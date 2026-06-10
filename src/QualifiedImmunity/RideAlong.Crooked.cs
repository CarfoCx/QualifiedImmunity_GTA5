using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // -------------------------------------------------------------------
        // The crooked FIB agent: a rare FIB ride variant where the "unit" is a
        // single agent who does not chase criminals -- he SUPPLIES them. The
        // ride is a string of "errands": drive somewhere quiet, meet a few
        // gang buyers at the window, move seized guns and product, roll out.
        // The player rides shotgun for all of it. Allegedly.
        // -------------------------------------------------------------------

        // The agent explaining himself. He has thought about this a lot.
        private static readonly string[] CrookedDeal =
        {
            "Bureau pay is an insult. THIS is my real pension plan.",
            "It's not corruption. It's 'community-based asset redistribution'.",
            "These fell off an evidence truck. I was driving it. Still counts.",
            "Confiscate it Monday, sell it Friday. The system WORKS, kid.",
            "Relax. The paperwork says this gun doesn't exist. Neither do we.",
            "I'm technically still undercover. Nine years now. It's going great."
        };

        // The satisfied customer base.
        private static readonly string[] CrookedBuyer =
        {
            "Yo, this one's still got the evidence tag on it.",
            "You SURE you a fed? ...You take Maze Bank transfers?",
            "Best prices in Los Santos. The badge discount is real.",
            "My guy! Same corner next week?"
        };

        // Stage an errand: a couple of buyers loitering a few blocks away, and the
        // agent drives over -- quiet, lawful, unremarkable. No sirens on a sale.
        private bool StartDeal(Ped player)
        {
            EnsureRelationships();
            Vector3 spot = FindHiddenSpawn(player.Position, 120f, 220f);
            if (spot == Vector3.Zero || spot.DistanceTo(player.Position) < 60f) return false;
            _dealSpot = spot;

            _dealPeds.Clear();
            int n = 2 + _rng.Next(2);   // 2-3 buyers
            for (int i = 0; i < n; i++)
            {
                Ped b = World.CreatePed(new Model(i % 2 == 0 ? PedHash.BallaOrig01GMY : PedHash.Lost01GMY),
                    spot + RandomOffset(2.0f + i));
                if (b == null || !b.Exists()) continue;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, b, true, true);
                // Wait calmly for the man -- no ambient panic, no wandering off.
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, b, true);
                Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, b, "WORLD_HUMAN_HANG_OUT_STREET", 0, true);
                _dealPeds.Add(b);
            }
            if (_dealPeds.Count == 0) return false;

            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, _driver, _copCar,
                spot.X, spot.Y, spot.Z, 17.0f, DRIVE_STYLE, 10.0f);
            _lastReissue = DateTime.Now;
            _lastCarMoving = DateTime.Now;

            Notify("~b~" + CopNames.For(_driver) + ":~w~ Quick stop. Meeting some... confidential informants. You saw NOTHING.");
            CopBark(_driver, "GENERIC_HOWS_IT_GOING");
            SetPhase(Phase.DealDrive);
            return true;
        }

        private void HandleDealDrive(Ped player)
        {
            if (!player.IsInVehicle(_copCar))
            { Notify("~y~You walked out on a federal 'errand'. Ride over. You were never here."); Cleanup(); return; }

            if (_copCar.Position.DistanceTo(_dealSpot) < 25f)
            {
                Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 8000); // park it
                foreach (Ped b in _dealPeds)
                {
                    if (!Valid(b)) continue;
                    Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, b, false);
                    Function.Call(Hash.CLEAR_PED_TASKS, b);
                    Function.Call(Hash.TASK_GO_TO_ENTITY, b, _copCar, -1, 2.5f, 1.0f, 1073741824.0f, 0);
                }
                Notify("~b~" + CopNames.For(_driver) + ":~w~ Gentlemen! Step into my office. Window's down, inventory's fresh.");
                _dealStage = 0; _dealStageAt = DateTime.Now;
                SetPhase(Phase.Dealing);
                return;
            }

            // Same stall re-kick used everywhere else.
            if (CarStalled() && (DateTime.Now - _lastReissue).TotalSeconds > 3.0)
            {
                _lastReissue = DateTime.Now;
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, _driver, _copCar,
                    _dealSpot.X, _dealSpot.Y, _dealSpot.Z, 17.0f, DRIVE_STYLE, 10.0f);
            }

            if (SecondsInPhase > 150)
            {
                Notify("~y~" + CopNames.For(_driver) + ":~w~ Buyers got cold feet. Typical. Errand's cancelled.");
                ReleaseDealPeds();
                ResumePatrol();
            }
        }

        // The deal itself, in beats: buyers gather at the window, the merchandise
        // changes hands (they visibly walk away strapped), pleasantries, roll out.
        private void HandleDealing(Ped player)
        {
            double inStage = (DateTime.Now - _dealStageAt).TotalSeconds;
            switch (_dealStage)
            {
                case 0: // buyers walking up to the window
                    if (inStage > 6)
                    {
                        foreach (Ped b in _dealPeds)
                        {
                            if (!Valid(b)) continue;
                            WeaponHash wh = _rng.Next(2) == 0 ? WeaponHash.MicroSMG : WeaponHash.SawnOffShotgun;
                            Function.Call(Hash.GIVE_WEAPON_TO_PED, b, unchecked((int)(uint)wh), 120, false, true);
                        }
                        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "PURCHASE", "HUD_LIQUOR_STORE_SOUNDSET", true);
                        Notify("~b~" + CopNames.For(_driver) + ":~w~ " + CrookedDeal[_rng.Next(CrookedDeal.Length)]);
                        _dealStage = 1; _dealStageAt = DateTime.Now;
                    }
                    break;

                case 1: // the satisfied-customer beat
                    if (inStage > 4)
                    {
                        Notify("~o~Buyer:~w~ " + CrookedBuyer[_rng.Next(CrookedBuyer.Length)]);
                        _dealStage = 2; _dealStageAt = DateTime.Now;
                    }
                    break;

                case 2: // wrap it up and roll out whistling
                    if (inStage > 4)
                    {
                        Notify("~b~" + CopNames.For(_driver) + ":~w~ Pleasure doing business. Stay dangerous out there.");
                        CopBark(_driver, "GENERIC_BYE");
                        foreach (Ped b in _dealPeds)
                            if (Valid(b)) Function.Call(Hash.TASK_WANDER_STANDARD, b, 10.0f, 10);
                        ReleaseDealPeds();
                        ResumePatrol();   // until the next "errand" comes in
                    }
                    break;
            }

            if (SecondsInPhase > 45) { ReleaseDealPeds(); ResumePatrol(); }   // hard safety cap
        }

        // Buyers go back to the engine (armed -- that's the joke and the problem).
        private void ReleaseDealPeds()
        {
            foreach (Ped b in _dealPeds)
                if (b != null && b.Exists()) b.MarkAsNoLongerNeeded();
            _dealPeds.Clear();
        }
    }
}
