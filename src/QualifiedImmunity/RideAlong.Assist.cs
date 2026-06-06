using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        private void StartAssist(Ped threat)
        {
            if (!Valid(threat)) return;
            EnsureRelationships();
            AssignCop(_driver);
            AssignCop(_partner);
            MakeRideAlongDriver(_driver);
            EnsureDriverSeated();
            _copCar.IsSirenActive = true;

            _threat = threat;
            _assistEngaged = false;
            _lastReissue = DateTime.MinValue;
            _lastCarMoving = DateTime.Now;    // give the fresh drive task time to spool up
            DriveToThreat();

            Notify("~b~Dispatch:~w~ Officers engaged nearby - your unit is rolling in to back them up!");
            CopBark(_driver, "GENERIC_WAR_CRY");
            SetPhase(Phase.Assist);
        }

        private void HandleAssist(Ped player)
        {
            // Stay connected to the engagement: hold on the current threat, or pick
            // up the next hostile still fighting, until the whole scene goes quiet.
            Ped threat = ResolveThreat();
            if (threat == null) { BeginClearing(); return; }
            _threat = threat;

            float gap = _copCar.Position.DistanceTo(_threat.Position);
            if (gap < 32f)
            {
                if (!_assistEngaged)
                {
                    _assistEngaged = true;
                    CopBark(_driver, "GENERIC_WAR_CRY");
                    Notify("~r~Officer:~w~ Contact! Backing up the unit - take the threat DOWN!");
                }
                EngageThreat(_threat);
            }
            else
            {
                EnsureDriverSeated();
                // Re-kick the drive only when the car has actually stalled (same guard
                // used in pursuit/en-route). Re-issuing DRIVE_TO_COORD on a moving car
                // restarts the task mid-frame and causes lurching / momentary braking.
                if (CarStalled() && (DateTime.Now - _lastReissue).TotalSeconds > 2.5)
                {
                    _lastReissue = DateTime.Now;
                    DriveToThreat();
                }
            }
        }

        // The live threat: the current one if it's still up, otherwise the next
        // hostile still in combat near the action. null => the engagement is over.
        private Ped ResolveThreat()
        {
            if (Valid(_threat) && Function.Call<bool>(Hash.IS_PED_IN_COMBAT, _threat, 0)) return _threat;
            if (Valid(_threat)) return _threat;   // alive but momentarily not shooting - keep on it

            Ped cop = FindNearbyEngagedCop(ASSIST_SCAN_RADIUS);
            if (cop != null) { Ped t = GetCombatThreat(cop); if (t != null) return t; }

            if (Valid(_copCar))
            {
                Ped player = Game.Player.Character;
                foreach (Ped p in WorldCache.GetNearbyPeds(_copCar.Position, 45f))
                {
                    if (p == null || !p.Exists() || p.IsDead) continue;
                    if (IsCopPed(p) || p == player) continue;
                    if (RideAlongRegistry.FriendlyCops.Contains(p.Handle)) continue;
                    if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, p, 0)) return p;
                }
            }
            return null;
        }

        private void DriveToThreat()
        {
            if (!Valid(_driver) || !Valid(_threat) || !_driver.IsInVehicle(_copCar)) return;
            Vector3 t = _threat.Position;
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, _driver, _copCar,
                t.X, t.Y, t.Z, 22.0f, RIDE_DRIVE_STYLE, 10.0f);
            if (Valid(_partner)) DriveBy(_partner, _threat);
        }

        private void EngageThreat(Ped threat)
        {
            _copCar.IsSirenActive = true;
            CombatThreatPed(_driver, threat);
            CombatThreatPed(_partner, threat);
        }

        private void CombatThreatPed(Ped c, Ped t)
        {
            if (!Valid(c) || !Valid(t)) return;
            if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, c, t)) return;   // already on it
            Function.Call(Hash.TASK_COMBAT_PED, c, t, 0, 16);
        }

        // Engagement's over -> hold position and "confirm" the scene for 5-10s.
        private void BeginClearing()
        {
            _threat = null;
            _assistEngaged = false;
            _clearDelay = 5.0 + _rng.NextDouble() * 5.0;   // 5-10s
            EndSirens();
            if (Valid(_driver) && _driver.IsInVehicle(_copCar))
                Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 3000); // stop
            Notify("~y~Officer:~w~ Hold position... confirming the threat is clear.");
            CopBark(_driver, "GENERIC_INSULT_HIGH");
            SetPhase(Phase.Clearing);
        }

        private void HandleClearing(Ped player)
        {
            // Keep the officers aboard while they hold and "confirm" the scene.
            bool refresh = SecondsInPhase < 0.3 || (DateTime.Now - _lastReboardPrompt).TotalSeconds > 5.0;
            if (refresh)
            {
                _lastReboardPrompt = DateTime.Now;
                ReboardCop(_driver, -1);
                ReboardCop(_partner, 0);
                if (!player.IsInVehicle(_copCar))
                    Notify("~b~Dispatch:~w~ Hop back in - your unit's moving to the next call.");
            }
            if (Valid(_driver) && !_driver.IsInVehicle(_copCar))
                Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 2000);

            // Wait out the confirm-clear pause before moving on.
            if (SecondsInPhase < _clearDelay) return;

            // Pause done -> next threat if there is one...
            Ped cop = FindNearbyEngagedCop(ASSIST_SCAN_RADIUS);
            if (cop != null)
            {
                Ped t = GetCombatThreat(cop);
                if (t != null) { Notify("~b~Officer:~w~ Clear. New contact - moving up!"); StartAssist(t); return; }
            }

            // ...otherwise resume patrol, or end if the player has wandered off.
            if (!player.IsInVehicle(_copCar))
            {
                if (!Valid(_driver) || _copCar.Position.DistanceTo(player.Position) > 35f
                    || SecondsInPhase > _clearDelay + 20)
                { Notify("~y~Ride-along ended."); Cleanup(); }
                return;
            }
            if (Valid(_driver) && _driver.IsInVehicle(_copCar))
            {
                Notify("~g~Officer:~w~ Scene's clear. Back on patrol.");
                ResetRideDelay();
                SetPhase(Phase.Riding);
            }
        }

        // After a firefight the ride continues IF you choose to stay - you're never forced.
        private void HandleRegroup(Ped player)
        {
            DespawnPursuitProps();

            // Re-board the OFFICERS once on entry, then refresh only every ~5s (no per-tick reset).
            bool refresh = SecondsInPhase < 0.3 || (DateTime.Now - _lastReboardPrompt).TotalSeconds > 5.0;
            if (refresh)
            {
                _lastReboardPrompt = DateTime.Now;
                ReboardCop(_driver, -1);
                ReboardCop(_partner, 0);
                if (!player.IsInVehicle(_copCar))
                    Notify("~b~Dispatch:~w~ Hop back in for another run - or walk away to call it.");
            }

            // Fallback: warp a stuck officer back in so the unit is never stranded.
            if (Valid(_driver) && !_driver.IsInVehicle(_copCar) && SecondsInPhase > 12)
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, _copCar, -1);
            if (Valid(_partner) && !_partner.IsInVehicle(_copCar) && SecondsInPhase > 14)
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _partner, _copCar, 0);

            // YOU decide: get back in on your own to keep going, or leave and it ends. Never forced.
            if (!player.IsInVehicle(_copCar))
            {
                if (_copCar.Position.DistanceTo(player.Position) > 35f || SecondsInPhase > 30)
                { Notify("~y~Ride-along ended."); Cleanup(); }
                return;
            }

            // Player chose to stay aboard and a driver is ready -> resume patrol.
            if (Valid(_driver) && _driver.IsInVehicle(_copCar))
            {
                ResetRideDelay();
                Notify("~g~Back on patrol.");
                SetPhase(Phase.Riding);
            }
        }

        private void ReboardCop(Ped c, int seat)
        {
            if (!Valid(c) || c.IsInVehicle(_copCar)) return;
            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, c, true); // don't bail out to react
            Function.Call(Hash.TASK_ENTER_VEHICLE, c, _copCar, 20000, seat, 2.0f, 1, 0);
        }

        private void BefriendRidealongCops()
        {
            // Keep the QualifiedImmunity gang behavior from turning your hosts on you.
            int grp = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, Game.Player.Character);
            if (Valid(_driver)) Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, _driver, grp);
            if (Valid(_partner)) Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, _partner, grp);
        }

        private void EndSirens()
        {
            if (Valid(_copCar)) _copCar.IsSirenActive = false;
        }
    }
}
