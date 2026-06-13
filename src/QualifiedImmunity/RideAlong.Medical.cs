using System;
using GTA;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // -------------------------------------------------------------------
        // Field medicine -- tourniquet a wounded officer (PC + controller)
        // -------------------------------------------------------------------
        private void TourniquetTick(Ped player)
        {
            if (!_enableTourniquet) return;
            if (player == null || !player.Exists() || player.IsInVehicle()) return; // kneel beside them on foot

            Ped patient = FindInjuredFriendlyCop(player, 3.5f);
            if (patient == null) return;

            // ~INPUT_CONTEXT~ auto-renders the right glyph: the "E" key on PC, or the
            // matching face button on a controller -- so the same prompt fits both.
            ShowHelp("Press ~INPUT_CONTEXT~ to apply a ~b~tourniquet~w~ and stabilize the officer.");

            if (Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Context)
                && (DateTime.Now - _lastTourniquet).TotalSeconds > 1.0)
            {
                _lastTourniquet = DateTime.Now;
                ApplyTourniquet(player, patient);
            }
        }

        // Any wounded-but-alive officer near the player -- your own unit OR an
        // ambient cop who's down and bleeding out. (Not just ride-along officers.)
        private Ped FindInjuredFriendlyCop(Ped player, float radius)
        {
            Ped best = null;
            float bestD = radius;
            foreach (Ped c in WorldCache.GetNearbyPeds(player.Position, radius))
            {
                if (c == null || !c.Exists() || c.IsDead) continue;
                if (!IsCopPed(c)) continue;                 // only patch up the police
                if (!c.IsInjured) continue;                 // wounded but still alive -> savable
                float d = c.Position.DistanceTo(player.Position);
                if (d <= bestD) { bestD = d; best = c; }
            }
            return best;
        }

        // -------------------------------------------------------------------
        // Downed-officer window. Ride-along officers are set DiesWhenInjured=false
        // (MakeOfficerDownable), so a fatal hit drops them into a bleeding-out injured
        // state and keeps them ALIVE there instead of killing them. This tick gives
        // that state structure: alert the player, hold the officer stable (invincible
        // while down, so ongoing fire can't finish him) for a generous window, and only
        // let him bleed out for real if nobody applies a tourniquet in time.
        // -------------------------------------------------------------------
        private readonly System.Collections.Generic.Dictionary<int, DateTime> _downedSince =
            new System.Collections.Generic.Dictionary<int, DateTime>();
        private DateTime _lastDownedAlert = DateTime.MinValue;
        private const double BleedOutSeconds = 60.0;   // time to reach a downed officer before he's gone

        private void DownedOfficerTick()
        {
            foreach (Ped c in UnitOfficers())
            {
                if (c == null || !c.Exists()) continue;
                int h = c.Handle;
                if (c.IsDead) { _downedSince.Remove(h); continue; }

                if (c.IsInjured)   // injured-but-alive == bleeding out and savable
                {
                    DateTime since;
                    if (!_downedSince.TryGetValue(h, out since))
                    {
                        // Just went down: freeze him stable so he can't be finished, and
                        // sound the alarm so the player knows to run over with the kit.
                        _downedSince[h] = DateTime.Now;
                        Function.Call(Hash.SET_PED_KEEP_TASK, c, false);
                        Function.Call(Hash.SET_ENTITY_INVINCIBLE, c, true);   // protect the save window
                        if ((DateTime.Now - _lastDownedAlert).TotalSeconds > 5.0)
                        {
                            _lastDownedAlert = DateTime.Now;
                            Notify("~r~OFFICER DOWN!~w~ " + CopNames.For(c) + " is hit -- reach them and apply a ~b~tourniquet~w~ before they bleed out.");
                            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "Beep_Red", "DLC_HEIST_HACKING_SNAKE_SOUNDS", true);
                        }
                    }
                    else if ((DateTime.Now - since).TotalSeconds > BleedOutSeconds)
                    {
                        // Nobody got to him in time -> he bleeds out for real.
                        _downedSince.Remove(h);
                        Function.Call(Hash.SET_ENTITY_INVINCIBLE, c, false);
                        Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, c, true);
                        Function.Call(Hash.SET_ENTITY_HEALTH, c, 0);
                        Notify("~r~" + CopNames.For(c) + " bled out. Couldn't reach them in time.");
                    }
                }
                else _downedSince.Remove(h);   // healed and back up
            }
        }

        private void ApplyTourniquet(Ped player, Ped cop)
        {
            // Brief first-aid gesture; guarded so a missing anim never breaks anything.
            try
            {
                const string dict = "amb@medic@standing@kneel@base";
                Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                Function.Call(Hash.TASK_PLAY_ANIM, player, dict, "base", 8f, -8f, 1200, 48, 0f, false, false, false);
            }
            catch { /* cosmetic only */ }

            // Lift the downed-state protection and patch them back up to full.
            _downedSince.Remove(cop.Handle);
            Function.Call(Hash.SET_ENTITY_INVINCIBLE, cop, false);
            int target = cop.MaxHealth > 100 ? cop.MaxHealth : 200;
            Function.Call(Hash.SET_ENTITY_HEALTH, cop, target);
            Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, cop);
            Function.Call(Hash.CLEAR_PED_TASKS, cop);            // get them off the ground and back up
            CopBark(cop, "GENERIC_THANKS");
            Notify("~g~Tourniquet applied.~w~ " + CopNames.For(cop) + " is back in the fight.");
        }

        // -------------------------------------------------------------------
        // Officers patch up their own. When a squadmate is down wounded (incl.
        // bleeding out -- savable, not dead), a free officer runs over, kneels,
        // and works a tourniquet. Combat always comes first: nobody abandons an
        // active gunfight to play medic, and the rescue aborts if shooting starts.
        // -------------------------------------------------------------------
        private Ped _medicCop, _medicPatient;
        private int _medicStage;                       // 0 run over, 1 kneel and work
        private DateTime _medicStageAt = DateTime.MinValue;
        private DateTime _lastCrewMedic = DateTime.MinValue;

        private static readonly string[] MedicLines =
        {
            "Stay with me! You still owe me twenty bucks!",
            "Tourniquet's on! It's department issue, so... fifty-fifty.",
            "You're fine! That's mostly other people's blood!",
            "No dying on shift - the overtime paperwork is BRUTAL.",
            "Walk it off, champ. That's official LSPD medical advice."
        };

        private void CrewMedicTick()
        {
            if (_phase == Phase.Idle || _phase == Phase.EnRoute || _phase == Phase.Boarding) return;

            if (_medicCop != null) { UpdateCrewMedic(); return; }
            if ((DateTime.Now - _lastCrewMedic).TotalSeconds < 12) return;

            // A wounded unit officer who needs help (on the ground, not in a seat)...
            Ped patient = null;
            foreach (Ped c in UnitOfficers())
            {
                if (!Valid(c) || !c.IsInjured || c.IsInVehicle()) continue;
                patient = c; break;
            }
            if (patient == null) return;

            // ...and a healthy squadmate who's free to help.
            Ped medic = null;
            foreach (Ped c in UnitOfficers())
            {
                if (!Valid(c) || c == patient || c.IsInjured) continue;
                if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, c, 0)) continue;
                medic = c; break;
            }
            if (medic == null) return;

            _medicCop = medic; _medicPatient = patient;
            _medicStage = 0; _medicStageAt = DateTime.Now;
            _lastCrewMedic = DateTime.Now;
            PrepForScene(medic);   // release the driving/combat locks so the go-to takes
            Function.Call(Hash.TASK_GO_TO_ENTITY, medic, patient, -1, 1.4f, 2.2f, 1073741824.0f, 0);
            Notify("~b~" + CopNames.For(medic) + ":~w~ Officer down! Hang on, " + CopNames.For(patient) + " - I'm coming!");
        }

        private void UpdateCrewMedic()
        {
            Ped m = _medicCop, p = _medicPatient;
            if (!Valid(m) || p == null || !p.Exists() || p.IsDead || !p.IsInjured)
            { EndCrewMedic(); return; }
            // Shooting starts -> drop the kit, raise the gun. Retry after the cooldown.
            if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, m, 0)) { EndCrewMedic(); return; }

            double s = (DateTime.Now - _medicStageAt).TotalSeconds;
            switch (_medicStage)
            {
                case 0: // running over
                    if (m.Position.DistanceTo(p.Position) < 2.2f)
                    {
                        Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, m, p, 800);
                        _medicStage = 1; _medicStageAt = DateTime.Now;
                    }
                    else if (s > 12) EndCrewMedic();   // can't reach -> give up for now
                    break;

                case 1: // kneeling over them, working the wound
                    if (s > 4)
                    {
                        ApplyTourniquet(m, p);   // plays the kneel gesture on the medic + heals
                        Notify("~b~" + CopNames.For(m) + ":~w~ " + MedicLines[_rng.Next(MedicLines.Length)]);
                        EndCrewMedic();
                    }
                    break;
            }
        }

        private void EndCrewMedic()
        {
            // Walk the medic back to his post if the unit is just cruising; in the
            // action phases the regroup/clearing logic re-boards everyone anyway.
            if (Valid(_medicCop) && _phase == Phase.Riding && !_medicCop.IsInVehicle(_copCar))
                ReboardCop(_medicCop, SeatOf(_medicCop));
            _medicCop = null; _medicPatient = null;
        }

        private System.Collections.Generic.IEnumerable<Ped> UnitOfficers()
        {
            yield return _driver;
            yield return _partner;
            foreach (Ped sq in _squad) yield return sq;
        }

        private int SeatOf(Ped c)
        {
            if (Valid(_driver) && c == _driver) return -1;
            if (Valid(_partner) && c == _partner) return 0;
            for (int i = 0; i < _squad.Count; i++)
                if (_squad[i] == c) return _squadSeats[i];
            return 0;
        }
    }
}
