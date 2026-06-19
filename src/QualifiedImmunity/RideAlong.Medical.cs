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
        // Downed-officer tracking. The KEY design point (and the reason earlier versions
        // showed no prompt): we do NOT decide who's savable by reading Ped.IsInjured at
        // prompt time. A downed officer is held SET_ENTITY_INVINCIBLE, and an invincible
        // bleeding ped does not reliably report IsInjured -- so walking up to one found
        // "no injured cop" and never prompted. Instead we keep a STICKY set of officers we
        // put into the downed state; it only clears on revive, bleed-out, death, or despawn.
        // The prompt, the blip and the marker all key off that set.
        private sealed class Downed
        {
            public DateTime Since;
            public Blip Blip;
            public bool Unit;   // ride-along unit officer (vs an ambient-scene cop)
        }
        private readonly System.Collections.Generic.Dictionary<int, Downed> _downed =
            new System.Collections.Generic.Dictionary<int, Downed>();
        private readonly System.Collections.Generic.List<int> _downedReconcile =
            new System.Collections.Generic.List<int>();
        private DateTime _lastDownedAlert = DateTime.MinValue;
        private const double BleedOutSeconds = 60.0;   // time to reach a downed officer before he's gone
        private const float TourniquetReach = 4.5f;    // how close (m) you must be to apply it

        // Show the "apply tourniquet" prompt when you reach a downed officer, and draw a
        // marker over every downed officer so you're guided to them. Runs in ALL phases.
        private void TourniquetTick(Ped player)
        {
            if (!_enableTourniquet) return;
            if (!Valid(player) || player.IsInVehicle()) return;   // kneel beside them on foot
            if (_downed.Count == 0) return;

            Ped patient = null; float best = float.MaxValue;
            _downedReconcile.Clear();
            foreach (int h in _downed.Keys) _downedReconcile.Add(h);
            foreach (int h in _downedReconcile)
            {
                Ped p = (Ped)Entity.FromHandle(h);
                if (p == null || !p.Exists() || p.IsDead) continue;
                DrawDownedMarker(p);                                   // "go here to save them"
                float d = p.Position.DistanceTo(player.Position);
                if (d < best) { best = d; patient = p; }
            }
            if (patient == null || best > TourniquetReach) return;     // the marker leads you in

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

        // A pulsing chevron above a downed officer so the player is visibly directed to them.
        private void DrawDownedMarker(Ped p)
        {
            var a = p.Position;
            Function.Call(Hash.DRAW_MARKER, 2, a.X, a.Y, a.Z + 1.3f,
                0f, 0f, 0f, 0f, 0f, 0f, 0.6f, 0.6f, 0.6f, 220, 40, 40, 160,
                true, true, 2, false, 0, 0, false);
        }

        // -------------------------------------------------------------------
        // Downed-officer window. The mod's cops are SET_PED_DIES_WHEN_INJURED=false, so a
        // fatal hit drops them into a bleeding-out state ALIVE instead of killing them. This
        // tick detects that transition (unit officers AND ambient-scene cops), pins them in a
        // stable, savable downed state (invincible + flashing blip + alarm) for a generous
        // window, and only lets them bleed out for real if nobody reaches them in time.
        // -------------------------------------------------------------------
        private void DownedTick(Ped player)
        {
            if (!_enableTourniquet) return;

            // 1) Unit officers going down (a ride-along officer who's hit).
            foreach (Ped c in UnitOfficers())
            {
                if (!Valid(c)) continue;
                if (!_downed.ContainsKey(c.Handle) && c.IsInjured) MarkDowned(c, true);
            }

            // 2) The mod's ambient-scene cops: keep them armed to bleed out (not insta-die),
            //    and catch them when they go down. Only OUR cops; vanilla police are untouched.
            foreach (Ped c in WorldCache.GetNearbyPeds(player.Position, 70f))
            {
                if (!Valid(c)) continue;
                if (!RideAlongRegistry.FriendlyCops.Contains(c.Handle)) continue;
                if (IsUnitOfficer(c) || _downed.ContainsKey(c.Handle)) continue;
                if (c.IsInjured) MarkDowned(c, false);
                else
                {
                    Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, c, false);
                    Function.Call(Hash.SET_PED_SUFFERS_CRITICAL_HITS, c, false);
                }
            }

            // 3) Maintain / reconcile everyone currently down.
            if (_downed.Count == 0) return;
            _downedReconcile.Clear();
            foreach (int h in _downed.Keys) _downedReconcile.Add(h);
            foreach (int h in _downedReconcile)
            {
                Downed d = _downed[h];
                Ped p = (Ped)Entity.FromHandle(h);

                // Gone, dead, or no longer ours -> drop tracking + blip, restore mortality.
                if (p == null || !p.Exists() || p.IsDead) { ReleaseDowned(h, p, false); continue; }
                if (d.Unit && (_phase == Phase.Idle || !IsUnitOfficer(p))) { ReleaseDowned(h, p, false); continue; }
                if (!d.Unit && !RideAlongRegistry.FriendlyCops.Contains(h)) { ReleaseDowned(h, p, false); continue; }

                // Hold the save window open: re-assert protection in case something cleared it.
                Function.Call(Hash.SET_ENTITY_INVINCIBLE, p, true);

                if ((DateTime.Now - d.Since).TotalSeconds > BleedOutSeconds)
                {
                    bool unit = d.Unit;
                    ReleaseDowned(h, p, false);                  // restores DiesWhenInjured + drops blip
                    Function.Call(Hash.SET_ENTITY_HEALTH, p, 0); // bled out for real
                    if (unit) Notify("~r~" + CopNames.For(p) + " bled out. Couldn't reach them in time.");
                }
            }
        }

        // Pin a cop into the savable downed state: hold him stable, blip him, sound the alarm.
        private void MarkDowned(Ped c, bool unit)
        {
            Function.Call(Hash.SET_PED_KEEP_TASK, c, false);
            Function.Call(Hash.SET_ENTITY_INVINCIBLE, c, true);

            Blip b = c.AddBlip();
            if (b != null && b.Exists())
            {
                b.Color = BlipColor.Red;
                b.IsFlashing = true;
                try { b.Name = "Officer Down"; } catch { /* naming is best-effort */ }
            }
            _downed[c.Handle] = new Downed { Since = DateTime.Now, Blip = b, Unit = unit };

            if ((DateTime.Now - _lastDownedAlert).TotalSeconds > 5.0)
            {
                _lastDownedAlert = DateTime.Now;
                if (unit)
                {
                    Notify("~r~OFFICER DOWN!~w~ " + CopNames.For(c) + " is hit -- get to them and apply a ~b~tourniquet~w~ before they bleed out.");
                    Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "Beep_Red", "DLC_HEIST_HACKING_SNAKE_SOUNDS", true);
                }
                else
                {
                    Notify("~r~Officer down nearby.~w~ Reach them on foot and apply a ~b~tourniquet~w~ to save them.");
                }
            }
        }

        // Drop a cop out of the downed state: remove the blip + tracking, lift invincibility,
        // and (unless they were revived) restore normal mortality so a kill actually sticks.
        private void ReleaseDowned(int h, Ped p, bool revived)
        {
            Downed d;
            if (_downed.TryGetValue(h, out d))
            {
                if (d.Blip != null && d.Blip.Exists()) d.Blip.Delete();
                _downed.Remove(h);
            }
            if (p != null && p.Exists())
            {
                Function.Call(Hash.SET_ENTITY_INVINCIBLE, p, false);
                if (!revived) Function.Call(Hash.SET_PED_DIES_WHEN_INJURED, p, true);
            }
        }

        private bool IsUnitOfficer(Ped c)
        {
            foreach (Ped u in UnitOfficers()) if (u == c) return true;
            return false;
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

            // Clear downed tracking + its blip, lift protection, patch them up. Leave them
            // DiesWhenInjured=false so they can be saved again if they're hit later.
            Downed d;
            if (_downed.TryGetValue(cop.Handle, out d) && d.Blip != null && d.Blip.Exists()) d.Blip.Delete();
            _downed.Remove(cop.Handle);
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

            // A downed unit officer who needs help (on the ground, not in a seat)...
            Ped patient = null;
            foreach (Ped c in UnitOfficers())
            {
                if (!Valid(c) || c.IsInVehicle()) continue;
                if (!_downed.ContainsKey(c.Handle)) continue;   // keyed off the sticky downed set
                patient = c; break;
            }
            if (patient == null) return;

            // ...and a healthy squadmate who's free to help.
            Ped medic = null;
            foreach (Ped c in UnitOfficers())
            {
                if (!Valid(c) || c == patient || _downed.ContainsKey(c.Handle)) continue;
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
            if (!Valid(m) || p == null || !p.Exists() || p.IsDead || !_downed.ContainsKey(p.Handle))
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
            // Fallen officers whose slot was overwritten (a promoted-over dead driver):
            // they're all dead, so the live-unit ticks (XP, medic, AnyOfficerAlive) skip
            // them on their Valid/IsDead guards, but the HUD still draws their K.I.A. row.
            foreach (Ped f in _fallen) yield return f;
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
