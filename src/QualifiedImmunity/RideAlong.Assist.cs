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
            // Same rule as pursuits: shooting from the windows is for moving cars.
            // If the cruiser has stopped anywhere near the threat, get out and fight.
            bool stoppedNearby = _copCar.Speed < 2.0f && gap < 50f && CarStalled();
            if (gap < 32f || stoppedNearby)
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
                t.X, t.Y, t.Z, 22.0f, DRIVE_STYLE, 10.0f); // lawful response driving
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
            // Make them actually FIGHT on foot. The driving phase leaves the event-block on
            // and CanLeaveVehicle off -- so when they pile out to back up local PD they just
            // stand there frozen. Clear the block and allow leaving/advancing first.
            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, c, false);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanLeaveVehicle, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_AlwaysFight, true);
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, c, 2); // advance
            if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, c, t)) return;   // already on it
            Function.Call(Hash.TASK_COMBAT_PED, c, t, 0, 16);
        }

        // Engagement's over -> hold position and "confirm" the scene for 5-10s.
        // Only if WE actually fought, though: if the threat died before the unit
        // ever engaged, announcing "confirming the threat is clear" two seconds
        // after "rolling in to back them up!" read as a bug. In that case the
        // unit just shrugs and goes back to patrol.
        private void BeginClearing()
        {
            bool fought = _assistEngaged;
            _threat = null;
            _assistEngaged = false;
            ReleaseCalloutPerps();   // staged radio-call perps go back to the engine

            if (!fought)
            {
                string who = Valid(_driver) ? CopNames.For(_driver) : "Officer";
                Notify("~y~" + who + ":~w~ Locals finished it before we got there. Glory hogs.");
                ResumePatrol();
                return;
            }

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

            // Fallback: warp a stuck officer back in so the scene-clear can never hang -- but
            // only after giving the walk-in animation real time to play.
            if (Valid(_driver) && !_driver.IsInVehicle(_copCar) && SecondsInPhase > 16)
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, _copCar, -1);
            if (Valid(_partner) && !_partner.IsInVehicle(_copCar) && SecondsInPhase > 18)
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _partner, _copCar, 0);

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
                // Same whole-crew rule as Regroup: never leave the partner behind.
                if (Valid(_partner) && !_partner.IsInVehicle(_copCar)) return;
                Notify("~g~Officer:~w~ Scene's clear. Back on patrol.");
                ResumePatrol();
            }
        }

        // After a firefight the ride continues IF you choose to stay - you're never forced.
        private void HandleRegroup(Ped player)
        {
            DespawnPursuitProps();

            // Re-board the OFFICERS once on entry, then refresh only every ~9s -- long
            // enough that a re-issue doesn't interrupt an in-progress walk-in animation.
            bool refresh = SecondsInPhase < 0.3 || (DateTime.Now - _lastReboardPrompt).TotalSeconds > 9.0;
            if (refresh)
            {
                _lastReboardPrompt = DateTime.Now;
                ReboardCop(_driver, -1);
                ReboardCop(_partner, 0);
                if (!player.IsInVehicle(_copCar))
                    Notify("~b~Dispatch:~w~ Hop back in for another run - or walk away to call it.");
            }

            // Fallback: warp a genuinely stuck officer back in so the unit is never stranded.
            // Give the animated walk-in real time first (a cop can be several meters away
            // after a foot chase). With the door unlocked + police-AI off, the walk-in
            // normally finishes well before this, so the warp only fires if they're truly
            // stuck -- you see the animation, not an instant teleport.
            if (Valid(_driver) && !_driver.IsInVehicle(_copCar) && SecondsInPhase > 16)
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, _copCar, -1);
            if (Valid(_partner) && !_partner.IsInVehicle(_copCar) && SecondsInPhase > 18)
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _partner, _copCar, 0);

            // YOU decide: get back in on your own to keep going, or leave and it ends. Never forced.
            if (!player.IsInVehicle(_copCar))
            {
                // Keep the doors enterable (police cars re-lock for the player) so stepping
                // out mid-ride never traps you outside a "locked" cruiser.
                LockCarForRide();
                // Hold the car so it waits for you instead of driving off and stranding you.
                if (Valid(_driver) && _driver.IsInVehicle(_copCar) && _copCar.Speed > 1f)
                    Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 2000);
                // End only if you actually walk away (more time/room than before so a
                // fumble at the door doesn't cancel the ride).
                if (_copCar.Position.DistanceTo(player.Position) > 45f || SecondsInPhase > 60)
                { Notify("~y~Ride-along ended."); Cleanup(); }
                return;
            }

            // Player chose to stay aboard and a driver is ready -> resume patrol.
            // Wait for the WHOLE crew: driving off while the partner was still
            // walking back stranded him at the scene (the 18s warp fallback above
            // bounds how long this can hold things up).
            if (Valid(_driver) && _driver.IsInVehicle(_copCar))
            {
                if (Valid(_partner) && !_partner.IsInVehicle(_copCar)) return;
                Notify("~g~Back on patrol.");
                ResumePatrol();
            }
        }

        private void ReboardCop(Ped c, int seat)
        {
            if (!Valid(c) || c.IsInVehicle(_copCar)) return;
            // Take them off police dispatch AI first -- otherwise the game culls our
            // enter-vehicle task and the cop never actually walks in (which then trips the
            // warp fallback and looks like an instant teleport instead of an animation).
            Function.Call(Hash.SET_PED_AS_COP, c, false);
            // CRITICAL: the combat "finish them" task was locked on with KEEP_TASK; while
            // that lock is on, the ped REJECTS the enter-vehicle task and just stands there.
            // Release it first so they accept the re-board. (Also re-enable vehicle use,
            // which ForceOutAndFight had turned off for the on-foot gunfight.)
            Function.Call(Hash.SET_PED_KEEP_TASK, c, false);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanUseVehicles, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanLeaveVehicle, false);
            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, c, true); // don't bail out to react
            // flag 1 = walk to the door and play the proper entry animation (not a warp).
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
