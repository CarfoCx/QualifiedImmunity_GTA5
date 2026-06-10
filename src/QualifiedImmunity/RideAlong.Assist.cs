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
            _assistUnits = 0;                 // each engagement earns its own backup waves
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
                    _rideHadFight = true;   // the banter can reference the shooting now
                    CopBark(_driver, "GENERIC_WAR_CRY");
                    Notify("~r~Officer:~w~ Contact! Backing up the unit - take the threat DOWN!");
                    // The moment OUR officers are trading fire, the assistance call
                    // goes out -- the first unit dispatches this very tick.
                    _lastAssistBackup = DateTime.MinValue;
                    Notify("~r~Dispatch (radio):~w~ Unit taking fire! All nearby units, code 3 - GO!");
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

            AssistBackupTick();
        }

        // Backup for OUR unit: while the assist firefight is live, waves of nearby
        // units roll up fast and pile in. Spawned much closer than pursuit backup
        // (60-120m vs 90-170m) so "responding" means seconds, not a road trip.
        private void AssistBackupTick()
        {
            if (!_assistEngaged || !Valid(_threat)) return;

            // Stop the surrounding traffic from stampeding while rounds are flying.
            TrafficCalm.Sweep(_threat.Position);

            if (_assistUnits < MAX_ASSIST_UNITS
                && (DateTime.Now - _lastAssistBackup).TotalSeconds > ASSIST_BACKUP_INTERVAL_SECONDS)
            {
                _lastAssistBackup = DateTime.Now;
                SpawnAssistUnit();
            }

            // Arriving units bail out and join the fight once they're on top of it.
            foreach (Entity ent in _backupEntities)
            {
                Ped p = ent as Ped;
                if (!Valid(p)) continue;
                if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, p, 0)) continue;
                if (p.Position.DistanceTo(_threat.Position) < 32f) CombatThreatPed(p, _threat);
            }
        }

        // One two-officer cruiser dispatched to an "officer needs assistance" call:
        // spawns close, drives code 3 straight at the fight, and engages on arrival
        // (AssistBackupTick flips them to on-foot combat once they've closed in).
        private void SpawnAssistUnit()
        {
            Vector3 road = FindHiddenSpawn(_threat.Position, 60f, 120f);
            bool isCounty = _threat.Position.Y > 1500f;
            VehicleHash cruiserHash = isCounty ? VehicleHash.Sheriff : VehicleHash.Police3;
            PedHash copHash = isCounty ? PedHash.Sheriff01SMY : PedHash.Cop01SMY;

            Vehicle v = World.CreateVehicle(new Model(cruiserHash), new Vector3(road.X, road.Y, road.Z + 2.5f));
            if (v == null) return;
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, v);
            v.IsSirenActive = true;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, v, true, true);

            Ped d = v.CreatePedOnSeat(VehicleSeat.Driver, new Model(copHash));
            Ped g = v.CreatePedOnSeat(VehicleSeat.Passenger, new Model(copHash));
            if (Valid(d)) Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, d, true, true);
            if (Valid(g)) Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, g, true, true);
            AssignCop(d);
            AssignCop(g);
            if (Valid(d)) RideAlongRegistry.FriendlyCops.Add(d.Handle);
            if (Valid(g)) RideAlongRegistry.FriendlyCops.Add(g.Handle);

            if (Valid(d))
            {
                Vector3 t = _threat.Position;
                SetPursuitAggression(d);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, d, v,
                    t.X, t.Y, t.Z, 28.0f, RIDE_DRIVE_STYLE, 10.0f); // code 3: lights, speed, no red lights
            }
            if (Valid(g)) DriveBy(g, _threat);

            _backupEntities.Add(v);
            if (Valid(d)) _backupEntities.Add(d);
            if (Valid(g)) _backupEntities.Add(g);
            _assistUnits++;
            Notify("~b~Dispatch:~w~ Unit " + (20 + _rng.Next(60)) + " rolling up to assist - hold the line!");
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
            // Code 3: sirens are on and officers are taking fire -- drive like it.
            // (This used the lawful patrol style before, so the "responding" cruiser
            // sat at red lights on the way to a gunfight.)
            SetPursuitAggression(_driver);
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, _driver, _copCar,
                t.X, t.Y, t.Z, 28.0f, RIDE_DRIVE_STYLE, 10.0f);
            if (Valid(_partner)) DriveBy(_partner, _threat);
        }

        private void EngageThreat(Ped threat)
        {
            _copCar.IsSirenActive = true;
            CombatThreatPed(_driver, threat);
            CombatThreatPed(_partner, threat);
            foreach (Ped sq in _squad) CombatThreatPed(sq, threat);   // the whole van empties
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
            TrafficCalm.ReleaseAll();   // shooting's over -- traffic rolls again
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
            // A lull is NOT the all-clear. If anyone hostile is still up and fighting
            // (the perp reloading behind cover reads as "not in combat" for a beat),
            // jump straight back into the engagement instead of standing by and then
            // abandoning the guy we were just shooting. _threat is null here, so
            // ResolveThreat scans engaged local cops + in-combat peds near the cruiser.
            Ped lurker = ResolveThreat();
            if (lurker != null)
            {
                Notify("~r~" + (Valid(_driver) ? CopNames.For(_driver) : "Officer") +
                       ":~w~ He's still up! Re-engaging!");
                StartAssist(lurker);
                return;
            }

            // Keep the officers aboard while they hold and "confirm" the scene.
            bool refresh = SecondsInPhase < 0.3 || (DateTime.Now - _lastReboardPrompt).TotalSeconds > 5.0;
            if (refresh)
            {
                _lastReboardPrompt = DateTime.Now;
                ReboardCop(_driver, -1);
                ReboardCop(_partner, 0);
                ReboardSquad();
                if (!player.IsInVehicle(_copCar))
                    Notify("~b~Dispatch:~w~ Hop back in - your unit's moving to the next call.");
            }
            if (Valid(_driver) && !_driver.IsInVehicle(_copCar))
                Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 2000);

            // Fallback: warp a stuck officer back in so the scene-clear can never hang -- but
            // only after giving the walk-in animation real time to play, NEVER while the
            // officer is actually fighting (yanking them out of combat mid-firefight is
            // the "instantly ported into the car and gave up" bug), and NEVER while the
            // walk-in task is live -- warping over an in-progress entry is the visible
            // teleport. A live entry task bounds itself (20s timeout), so no hang risk.
            if (Valid(_driver) && !_driver.IsInVehicle(_copCar) && SecondsInPhase > 16
                && !Function.Call<bool>(Hash.IS_PED_IN_COMBAT, _driver, 0)
                && !IsEnteringCruiser(_driver))
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, _copCar, -1);
            if (Valid(_partner) && !_partner.IsInVehicle(_copCar) && SecondsInPhase > 18
                && !Function.Call<bool>(Hash.IS_PED_IN_COMBAT, _partner, 0)
                && !IsEnteringCruiser(_partner))
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _partner, _copCar, 0);
            if (SecondsInPhase > 20) WarpStuckSquad();

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
                if (!SquadAboard()) return;
                Notify("~g~Officer:~w~ Scene's clear. Back on patrol.");
                // Hand the assist-backup units back to the engine -- without this they
                // stay pinned as mission entities and pile up across engagements.
                DespawnPursuitProps();
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
                ReboardSquad();
                if (!player.IsInVehicle(_copCar))
                    Notify("~b~Dispatch:~w~ Hop back in for another run - or walk away to call it.");
            }

            // Fallback: warp a genuinely stuck officer back in so the unit is never stranded.
            // Give the animated walk-in real time first (a cop can be several meters away
            // after a foot chase). With the door unlocked + police-AI off, the walk-in
            // normally finishes well before this, so the warp only fires if they're truly
            // stuck -- you see the animation, not an instant teleport. (Never warp an
            // officer who's still mid-combat, and never one whose walk-in task is live:
            // warping over an in-progress entry IS the teleport the player keeps seeing.)
            if (Valid(_driver) && !_driver.IsInVehicle(_copCar) && SecondsInPhase > 16
                && !Function.Call<bool>(Hash.IS_PED_IN_COMBAT, _driver, 0)
                && !IsEnteringCruiser(_driver))
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, _copCar, -1);
            if (Valid(_partner) && !_partner.IsInVehicle(_copCar) && SecondsInPhase > 18
                && !Function.Call<bool>(Hash.IS_PED_IN_COMBAT, _partner, 0)
                && !IsEnteringCruiser(_partner))
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _partner, _copCar, 0);
            if (SecondsInPhase > 20) WarpStuckSquad();

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
                if (!SquadAboard()) return;
                Notify("~g~Back on patrol.");
                ResumePatrol();
            }
        }

        private void ReboardCop(Ped c, int seat)
        {
            if (!Valid(c) || c.IsInVehicle(_copCar)) return;
            // Already walking to the door / climbing in? Let the animation finish.
            // The periodic refresh used to re-issue the task on top of an entry in
            // progress, restarting the walk-in from scratch every few seconds until
            // the warp fallback fired -- THE "instantly ported into the cruiser" bug.
            if (IsEnteringCruiser(c)) return;
            // Never yank an officer who's still trading fire toward the car.
            if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, c, 0)) return;
            // Take them off police dispatch AI first -- otherwise the game culls our
            // enter-vehicle task and the cop never actually walks in (which then trips the
            // warp fallback and looks like an instant teleport instead of an animation).
            Function.Call(Hash.SET_PED_AS_COP, c, false);
            // CRITICAL: the combat "finish them" task was locked on with KEEP_TASK; while
            // that lock is on, the ped REJECTS the enter-vehicle task and just stands there.
            // Release it first so they accept the re-board. (Also re-enable vehicle use,
            // which ForceOutAndFight had turned off for the on-foot gunfight.)
            Function.Call(Hash.SET_PED_KEEP_TASK, c, false);
            // Releasing the KEEP_TASK lock does NOT stop the combat task already
            // running -- and while it runs, TASK_ENTER_VEHICLE is rejected and the
            // officer just stands at the scene until the warp "teleports" them in.
            // Actually drop it (we're past the in-combat guard, so the fight is over).
            Function.Call(Hash.CLEAR_PED_TASKS, c);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanUseVehicles, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanLeaveVehicle, false);
            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, c, true); // don't bail out to react
            // flag 1 = walk to the door and play the proper entry animation (not a warp).
            Function.Call(Hash.TASK_ENTER_VEHICLE, c, _copCar, 20000, seat, 2.0f, 1, 0);
        }

        // True while a scripted enter-vehicle task is live on the ped (walking to the
        // door or playing the climb-in). 0x950B6492 = SCRIPT_TASK_ENTER_VEHICLE;
        // GET_SCRIPT_TASK_STATUS returns 7 when no such task is assigned.
        private bool IsEnteringCruiser(Ped c)
        {
            if (!Valid(c)) return false;
            return Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, c, unchecked((int)0x950B6492u)) != 7;
        }

        // Elite squad re-boarding: same walk-in/warp discipline as the driver and
        // partner, each operator back to his OWN seat (never the player's).
        private void ReboardSquad()
        {
            for (int i = 0; i < _squad.Count; i++) ReboardCop(_squad[i], _squadSeats[i]);
        }

        private bool SquadAboard()
        {
            foreach (Ped sq in _squad)
                if (Valid(sq) && !sq.IsInVehicle(_copCar)) return false;
            return true;
        }

        private void WarpStuckSquad()
        {
            for (int i = 0; i < _squad.Count; i++)
            {
                Ped sq = _squad[i];
                if (!Valid(sq) || sq.IsInVehicle(_copCar)) continue;
                if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, sq, 0)) continue;
                if (IsEnteringCruiser(sq)) continue;
                Function.Call(Hash.SET_PED_INTO_VEHICLE, sq, _copCar, _squadSeats[i]);
            }
        }

        private void BefriendRidealongCops()
        {
            // Keep the QualifiedImmunity gang behavior from turning your hosts on you.
            int grp = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, Game.Player.Character);
            if (Valid(_driver)) Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, _driver, grp);
            if (Valid(_partner)) Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, _partner, grp);
            foreach (Ped sq in _squad)
                if (Valid(sq)) Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, sq, grp);
        }

        private void EndSirens()
        {
            if (Valid(_copCar)) _copCar.IsSirenActive = false;
        }
    }
}
