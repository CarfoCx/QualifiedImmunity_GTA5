using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // Start an individual pursuit -- but never by spawning a suspect. We pick
        // an EXISTING nearby vehicle and designate its occupants as the suspects
        // (hostile to police). Returns false if there's nothing around to chase.
        private bool StartPursuit()
        {
            EnsureRelationships();

            // Restore the player-tunable values so D-pad drift never carries between pursuits.
            _idealFollowDistance     = _baseIdealFollowDistance;
            _engageDistanceThreshold = _baseEngageDistanceThreshold;
            _engageSpeedThreshold    = _baseEngageSpeedThreshold;

            Vehicle target = FindPursuitTarget();
            if (target == null) return false;   // no traffic to designate -> keep cruising
            _suspectCar = target;

            // Pin the designated car (and its crew, in DesignateSuspects) as mission
            // entities for the length of the pursuit. These are AMBIENT entities --
            // without the pin, the engine's population manager could cull them the
            // moment they drifted out of streaming focus, which ended the pursuit one
            // breath after the siren flipped on ("sirens flash, instant suspect-down").
            // Released via MarkAsNoLongerNeeded in DespawnPursuitProps/Cleanup.
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _suspectCar, true, true);

            // Variety: Configurable innocent chance vs armed.
            _suspectsInnocent = _rng.Next(100) < _innocentChance;
            if (_suspectsInnocent)
            {
                _suspectThreat = 0;
            }
            else
            {
                int r = _rng.Next(100);
                // e.g. _threat1Chance (e.g. 45), _threat2Chance (e.g. 35) -> threat 1 if < 45, threat 2 if < 80, else threat 3
                _suspectThreat = r < _threat1Chance ? 1 : (r < (_threat1Chance + _threat2Chance) ? 2 : 3);
            }

            _suspectPeds.Clear();
            DesignateSuspects(_suspectCar, _suspectThreat, _suspectPeds);
            _suspect = Valid(_suspectCar.Driver) ? _suspectCar.Driver
                                                 : (_suspectPeds.Count > 0 ? _suspectPeds[0] : null);
            if (!Valid(_suspect)) { _suspect = null; _suspectCar = null; return false; }

            // Top-threat suspects rope a second EXISTING vehicle in as a backup crew.
            if (_suspectThreat >= 3) BuildSuspectBackup();

            Function.Call(Hash.SET_DRIVER_ABILITY, _suspect, 1.0f);
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, _suspect, 1.0f);
            Function.Call(Hash.SET_PED_KEEP_TASK, _suspect, true);
            Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, _suspect, 0, false); // don't bail the car early
            // Drive off fast. WANDER reliably keeps the suspect MOVING (the mission-flee
            // native left them sitting still, which killed the chase); the cruiser chases it.
            Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, _suspect, _suspectCar, _suspectsInnocent ? 35.0f : 50.0f, FLEE_DRIVE_STYLE);
            Notify(SuspectThreatLine());

            // Your unit: into the pursuit-cop group, armed, lights on, chasing.
            AssignCop(_driver);
            AssignCop(_partner);
            MakeRideAlongDriver(_driver);   // re-assert the off-policy driving profile
            EnsureDriverSeated();
            _copCar.IsSirenActive = true;
            Function.Call(Hash.TASK_VEHICLE_CHASE, _driver, _suspect);
            Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, _driver, _idealFollowDistance);
            Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, _driver, RIDE_DRIVE_STYLE); // avoid traffic while chasing
            if (Valid(_partner)) DriveBy(_partner, _suspect);

            _engaged = false;
            _backupCount = 0;
            _pitting = false; _lastPit = DateTime.MinValue; _lastCollateral = DateTime.MinValue;
            _swatCalled = false; _heliCalled = false; _swatWaves = 0; _lastSwat = DateTime.Now;
            // Don't dump the whole dispatch script the instant the siren flips on. The
            // single SuspectThreatLine() above is the only start-of-pursuit announcement;
            // the first radio quip waits the full interval so it isn't a wall of text.
            _lastRadio = DateTime.Now;
            _radioDelay = _radioIntervalMin + _rng.NextDouble() * Math.Max(0f, _radioIntervalMax - _radioIntervalMin);
            _lastBackup = DateTime.Now;       // first backup after the interval
            _lastReissue = DateTime.Now;
            _lastCarMoving = DateTime.Now;    // give the fresh chase task time to spool up
            _escapeTimerStarted = DateTime.MinValue; // reset escape timer
            _suspectStoppedSince = DateTime.MinValue; // reset the "car has stopped" tracker
            _lastPursuitStart = DateTime.Now; // track UI timer

            CopBark(_driver, "GENERIC_WAR_CRY"); // a single voiced bark, no extra ticker line
            SetPhase(Phase.Pursuit);
            return true;
        }

        // An existing nearby vehicle with an NPC driver, to designate as the suspect.
        // Widened search (was 70m) so a pursuit reliably finds a target even when traffic
        // is sparse -- the #1 reason "no pursuits ever happen". Min distance dropped so a
        // car just ahead still qualifies.
        private Vehicle FindPursuitTarget()
        {
            if (!Valid(_copCar)) return null;
            return NPCVehicleFinder.FindNearbyNPCDrivenVehicle(_copCar.Position, 130f, 10f, _copCar, null);
        }

        // Flag an existing vehicle's occupants as suspects: hostile to police, geared
        // to the threat level, and committed to the getaway.
        private void DesignateSuspects(Vehicle v, int threat, List<Ped> into)
        {
            if (!Valid(v)) return;
            Ped player = Game.Player.Character;
            foreach (Ped ped in v.Occupants)
            {
                if (!Valid(ped) || IsCopPed(ped) || ped == player) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(ped.Handle)) continue;
                SetupSuspectPed(ped, threat);                 // sets _suspGroup -> cops go hostile
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, ped, true, true); // don't cull mid-chase
                Function.Call(Hash.SET_PED_KEEP_TASK, ped, true);
                into.Add(ped);
            }
        }

        // Rope a second EXISTING nearby vehicle in as a backup crew (no spawning).
        // If there's nothing suitable around, the top-threat stop just runs without it.
        private void BuildSuspectBackup()
        {
            if (!Valid(_suspectCar)) return;
            Vehicle v2 = NPCVehicleFinder.FindNearbyNPCDrivenVehicle(_suspectCar.Position, 55f, 0f, _suspectCar, _copCar);
            if (v2 == null) return;     // no second vehicle around -> no backup crew

            _suspectCar2 = v2;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _suspectCar2, true, true); // don't cull mid-chase
            DesignateSuspects(_suspectCar2, 3, _suspectPeds);   // counted as suspects -> fight ends when ALL are down
            Ped d2 = _suspectCar2.Driver;
            if (Valid(d2))
            {
                Function.Call(Hash.SET_DRIVER_ABILITY, d2, 1.0f);
                Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, d2, 0.9f);
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, d2, _suspectCar2, 40.0f, DRIVE_STYLE);
            }
        }

        private void PursuitTick()
        {
            // Keep the chase locked onto a LIVE suspect. If the designated suspect dies but
            // others are still running (multi-suspect / gang pursuits), re-point at one that's
            // actually alive and force a fresh chase task -- otherwise the driver keeps trying
            // to chase a corpse and looks confused.
            if (!_engaged && !Valid(_suspect))
            {
                Ped a = AliveSuspect();
                if (a != null)
                {
                    _suspect = a;
                    if (Valid(a.CurrentVehicle)) _suspectCar = a.CurrentVehicle;
                    _lastReissue = DateTime.MinValue;   // re-issue the chase at the new target
                }
            }

            // Unhinged radio chatter -- sparse so it's an occasional punchline, not a
            // constant stream. Interval is tunable in the .ini (defaults 30-55s).
            if ((DateTime.Now - _lastRadio).TotalSeconds > _radioDelay)
            {
                _lastRadio = DateTime.Now;
                _radioDelay = _radioIntervalMin + _rng.NextDouble() * Math.Max(0f, _radioIntervalMax - _radioIntervalMin);
                RadioChatter();
            }
            // Backup waves (capped so it doesn't snowball forever).
            if ((DateTime.Now - _lastBackup).TotalSeconds > _backupIntervalSeconds) { _lastBackup = DateTime.Now; SpawnBackup(); }
            // Suspect-driven escalation: SWAT, then a helicopter, for geared crews.
            EscalateForThreat();
            
            // Indiscriminate SWAT Flashbangs
            if (_suspectThreat >= 3 && _rng.NextDouble() < 0.05 && Valid(_suspect))
            {
                Vector3 origin = _suspect.Position + new Vector3(0, 0, 10f);
                Function.Call(Hash.SHOOT_SINGLE_BULLET_BETWEEN_COORDS, 
                    origin.X, origin.Y, origin.Z, 
                    _suspect.Position.X, _suspect.Position.Y, _suspect.Position.Z, 
                    0, true, unchecked((int)(uint)WeaponHash.BZGas), 
                    0, true, false, 100f);
            }

            // PIT maneuver (with the "permission? lol no" chatter) + collateral one-liners.
            TryPit();
            CheckCollateral();

            float gap = _copCar.Position.DistanceTo(_suspectCar.Position);
            float suspSpeed = _suspectCar.Speed;

            if (_engaged)
            {
                Ped aliveSusp = AliveSuspect();
                if (aliveSusp != null && aliveSusp.IsInVehicle())
                {
                    _engaged = false;
                    _suspectCar = aliveSusp.CurrentVehicle;
                    _suspect = aliveSusp;
                    Notify("~r~Dispatch:~w~ Suspect jacked a vehicle! RESUME PURSUIT!");

                    // Make the runner actually RUN: without a fresh drive task he'd
                    // sit parked in his new ride and the officers would just walk
                    // back up and shoot him in the seat.
                    Function.Call(Hash.SET_DRIVER_ABILITY, aliveSusp, 1.0f);
                    Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, aliveSusp, 1.0f);
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, aliveSusp, _suspectCar, 50.0f, FLEE_DRIVE_STYLE);
                    Function.Call(Hash.SET_PED_KEEP_TASK, aliveSusp, true);

                    // ForceOutAndFight set CA_CanUseVehicles=false on both officers.
                    // Reset their vehicle-chase profile BEFORE re-boarding, otherwise
                    // the driver bails out of the cruiser the instant it enters the seat
                    // and the resumed chase never gets off the ground.
                    MakeRideAlongDriver(_driver);
                    if (Valid(_partner)) MakeRideAlongDriver(_partner);

                    // Release the combat KEEP_TASK lock first, or they reject the re-board.
                    if (Valid(_driver)) Function.Call(Hash.SET_PED_KEEP_TASK, _driver, false);
                    if (Valid(_partner)) Function.Call(Hash.SET_PED_KEEP_TASK, _partner, false);
                    if (Valid(_driver)) Function.Call(Hash.TASK_ENTER_VEHICLE, _driver, _copCar, -1, -1, 2.0f, 1, 0);
                    if (Valid(_partner)) Function.Call(Hash.TASK_ENTER_VEHICLE, _partner, _copCar, -1, -1, 2.0f, 1, 0);
                    return;
                }
            }

            // Driver dead but occupants remain -> force the on-foot fight.
            if (!_engaged && !Valid(_suspect)) { _engaged = true; Engage(); return; }

            // CHASE until the vehicle actually STOPS -- don't turn it into an instant
            // shootout. A pursuit should be: chase the car, run drive-bys, attempt PITs, and
            // only bail out on foot once the suspect's vehicle has come to a real stop (PITted,
            // crashed, or boxed in) and STAYED stopped for a beat. Tracking a sustained stop
            // (not just "slowed") stops the officers from piling out every time the suspect
            // taps the brakes in traffic.
            if (suspSpeed < _engageSpeedThreshold)
            {
                if (_suspectStoppedSince == DateTime.MinValue) _suspectStoppedSince = DateTime.Now;
            }
            else _suspectStoppedSince = DateTime.MinValue;

            bool chasedLongEnough = (DateTime.Now - _lastPursuitStart).TotalSeconds > 6.0;
            bool suspectParked = _suspectStoppedSince != DateTime.MinValue
                                 && (DateTime.Now - _suspectStoppedSince).TotalSeconds > 1.5;
            // Drive-bys are for DRIVING. If both cars have come to a stop near each
            // other, the officers get out and handle it on foot instead of sitting
            // in their seats shooting through the windows forever (the "standoff"
            // case: the chase AI stops a few meters outside the engage threshold).
            bool standoff = _copCar.Speed < 2.0f && gap < 45f;
            if (!_engaged && chasedLongEnough && suspectParked
                && (gap < _engageDistanceThreshold || standoff))
            {
                _engaged = true;
                Engage();
            }
            else if (!_engaged && !_pitting)
            {
                // Re-kick the chase on a timer (the task goes inert otherwise on this engine).
                bool driverAboard = Valid(_driver) && _driver.IsInVehicle(_copCar);
                if (CarStalled() && (DateTime.Now - _lastReissue).TotalSeconds > 2.5)
                {
                    _lastReissue = DateTime.Now;
                    if (driverAboard)
                    {
                        Function.Call(Hash.TASK_VEHICLE_CHASE, _driver, _suspect);
                        Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, _driver, _idealFollowDistance);
                        Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, _driver, RIDE_DRIVE_STYLE);
                    }
                    else if (Valid(_driver))
                    {
                        Function.Call(Hash.TASK_ENTER_VEHICLE, _driver, _copCar, -1, -1, 2.0f, 1, 0);
                    }
                    if (Valid(_partner) && _partner.IsInVehicle(_copCar)) DriveBy(_partner, _suspect);
                    else if (Valid(_partner) && !_partner.IsInVehicle(_copCar)) Function.Call(Hash.TASK_ENTER_VEHICLE, _partner, _copCar, -1, -1, 2.0f, 1, 0);
                }
            }
        }

        // Caught up -> bail out and turn it into a gunfight (or a one-sided execution).
        private void Engage()
        {
            string lead = "~r~" + CopNames.For(_driver) + ":~w~ ";
            Notify(_suspectsInnocent ? lead + "He's unarmed and begging - PERFECT. LIGHT HIM UP!"
                                     : lead + "He's boxed in - OUT OF THE CAR, LIGHT HIM UP!");
            CopBark(_driver, "GENERIC_WAR_CRY");
            CopBark(_partner, "GENERIC_INSULT_HIGH");
            _pitting = false;
            ForceOutAndFight(_driver);
            ForceOutAndFight(_partner);
            foreach (Entity ent in _backupEntities) { Ped p = ent as Ped; if (Valid(p)) ForceOutAndFight(p); }

            // The suspects respond: armed crews fight back, innocents run for their lives.
            foreach (Ped s in _suspectPeds)
            {
                if (!Valid(s)) continue;
                bool carjack = _rng.Next(100) < 40;
                Vehicle targetVehicle = null;
                if (carjack)
                {
                    foreach (Vehicle v in WorldCache.GetNearbyVehicles(s.Position, 30f))
                    {
                        if (v == null || !v.Exists() || !v.IsEngineRunning || v.HasSiren) continue;
                        if (v == _copCar || v == _suspectCar || v == _suspectCar2) continue; // not our cruiser or their own getaway cars
                        if (Cops.IsPoliceVehicle(v)) continue;                                // don't "carjack" a police unit
                        targetVehicle = v;
                        break;
                    }
                }

                if (targetVehicle != null)
                {
                    Function.Call(Hash.TASK_ENTER_VEHICLE, s, targetVehicle, -1, -1, 2.0f, 1, 0);
                    Notify("~y~Dispatch:~w~ Suspect is attempting a carjacking!");
                }
                else if (_suspectsInnocent)
                    Function.Call(Hash.TASK_SMART_FLEE_PED, s, _driver, 250.0f, -1, false, false);
                else
                {
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, s, CA_AlwaysFight, true);
                    Function.Call(Hash.TASK_COMBAT_PED, s, _driver, 0, 16);
                }
            }
        }

        private void CombatSuspect(Ped c)
        {
            Ped t = AliveSuspect();
            if (!Valid(c) || t == null) return;
            // Make sure they're armed, hostile to the suspect, and COMMIT to the kill (keep
            // the combat task so it isn't dropped) -- this is the "shoot them dead" finish.
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, c, _copsGroup);
            if (!Function.Call<bool>(Hash.IS_PED_ARMED, c, 7))
                Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)WeaponHash.CombatPistol), 250, false, true);
            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, c, false);
            Function.Call(Hash.TASK_COMBAT_PED, c, t, 0, 16);
            Function.Call(Hash.SET_PED_KEEP_TASK, c, true);
        }

        // Aggressive engage: drop the vehicle and open fire immediately instead of
        // loitering. CanUseVehicles=false forces a fast bail-out to an on-foot fight.
        private void ForceOutAndFight(Ped c)
        {
            if (!Valid(c)) return;
            // Lift the event-block from the driving phase FIRST. While it's on, a ped that
            // bails out just stands there frozen for a beat instead of reacting/advancing --
            // that's the "glitch, don't move for a couple seconds" on exit. Off = they move.
            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, c, false);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanUseVehicles, false);   // CanUseVehicles=false -> get OUT now
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanLeaveVehicle, true);    // CanLeaveVehicle
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_AlwaysFight, true);    // always fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_FightArmedWhenUnarmed, true);   // fight even unarmed targets
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, c, 2);            // advance
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, c, 2);            // professional
            Function.Call(Hash.SET_PED_FIRING_PATTERN, c, unchecked((int)0xC6EE6B4C)); // full auto, no hesitation
            CombatSuspect(c);
        }

        // -------------------------------------------------------------------
        // PIT maneuver -- ask "permission" (lol), then ram the suspect anyway
        // -------------------------------------------------------------------
        private void TryPit()
        {
            if (_pitting)
            {
                // End the PIT burst and fall back into the normal chase.
                if ((DateTime.Now - _pitSince).TotalSeconds > 2.5)
                {
                    _pitting = false;
                    if (Valid(_driver) && _driver.IsInVehicle(_copCar) && Valid(_suspect))
                    {
                        Function.Call(Hash.TASK_VEHICLE_CHASE, _driver, _suspect);
                        Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, _driver, _idealFollowDistance);
                        Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, _driver, RIDE_DRIVE_STYLE);
                    }
                }
                return;
            }
            if (_engaged || !Valid(_driver) || !_driver.IsInVehicle(_copCar) || !Valid(_suspectCar) || !Valid(_suspect)) return;
            if ((DateTime.Now - _lastPit).TotalSeconds < _pitCooldownSeconds) return;       // cooldown between PITs
            float gap = _copCar.Position.DistanceTo(_suspectCar.Position);
            if (gap > _pitDistanceThreshold || _suspectCar.Speed < _pitMinSpeed) return;             // need to be close + suspect still rolling

            _pitting = true; _pitSince = DateTime.Now; _lastPit = DateTime.Now;
            // A RAM vehicle mission is the PIT itself.
            Function.Call(Hash.TASK_VEHICLE_MISSION_PED_TARGET, _driver, _copCar, _suspect,
                (int)VehicleMissionType.Ram, 75.0f, RIDE_DRIVE_STYLE, 0.0f, 0.0f, false);
            
            // Tactical Vehicle Containment (Backup Pileups)
            foreach (Entity ent in _backupEntities)
            {
                Vehicle bv = ent as Vehicle;
                if (Valid(bv) && Valid(bv.Driver))
                {
                    Function.Call(Hash.TASK_VEHICLE_MISSION_PED_TARGET, bv.Driver, bv, _suspect,
                        (int)VehicleMissionType.Ram, 75.0f, RIDE_DRIVE_STYLE, 0.0f, 0.0f, false);
                }
            }

            PitChatter();
        }

        private void PitChatter()
        {
            Ped speaker = Valid(_driver) ? _driver : _partner;
            if (!Valid(speaker)) return;
            int idx = _rng.Next(PitLines.Length);
            Notify("~b~" + CopNames.For(speaker) + ":~w~ " + PitLines[idx]);
            QIAudio.PlayPit(idx); // Play voice line
            CopBark(speaker, "GENERIC_WAR_CRY");
        }

        // A bystander caught a stray -> the partner frets, the lead jokes it off.
        private void CheckCollateral()
        {
            if ((DateTime.Now - _lastCollateral).TotalSeconds < 18) return;
            Vector3 center = Valid(_suspectCar) ? _suspectCar.Position
                                                : (Valid(_copCar) ? _copCar.Position : Vector3.Zero);
            Ped player = Game.Player.Character;
            foreach (Ped p in WorldCache.GetNearbyPeds(center, 28f))
            {
                if (p == null || !p.Exists()) continue;
                if (!p.IsDead && p.Health == p.MaxHealth) continue; // Must be injured or dead
                if (p.PedType != PedType.CivMale && p.PedType != PedType.CivFemale) continue;
                if (p == player || IsCopPed(p) || IsSuspectPed(p)) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(p.Handle)) continue;
                
                // Only trigger if hit by a cop car
                bool hitByCop = false;
                if (Valid(_copCar) && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, p, _copCar, true)) hitByCop = true;
                foreach (Entity ent in _backupEntities)
                {
                    Vehicle bv = ent as Vehicle;
                    if (Valid(bv) && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, p, bv, true)) hitByCop = true;
                }
                
                if (!hitByCop) continue;

                _lastCollateral = DateTime.Now;
                CollateralChatter();
                return;
            }
        }

        private void CollateralChatter()
        {
            Ped joker = Valid(_driver) ? _driver : _partner;
            if (!Valid(joker)) return;
            Ped asker = (Valid(_partner) && _partner != joker) ? _partner : null;
            string askerName = asker != null ? CopNames.For(asker) : "Dispatch";
            
            int qIdx = _rng.Next(CollateralQ.Length);
            int aIdx = _rng.Next(CollateralA.Length);

            Notify("~y~" + askerName + ":~w~ " + CollateralQ[qIdx]);
            Notify("~r~" + CopNames.For(joker) + ":~w~ " + CollateralA[aIdx]);
            
            QIAudio.PlayCollateral(qIdx, aIdx); // Play background sequence voices
            CopBark(joker, "GENERIC_INSULT_HIGH");
        }

        private bool IsSuspectPed(Ped p)
        {
            if (p == null || !p.Exists()) return false;
            foreach (Ped s in _suspectPeds) if (s != null && s.Exists() && s.Handle == p.Handle) return true;
            return false;
        }

        private void DriveBy(Ped shooter, Ped target)
        {
            if (!Valid(shooter) || !Valid(target)) return;
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, shooter, 2, true);                  // CanDoDrivebys
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, shooter, CA_CanLeaveVehicle, false); // ...but don't bail out of the car
            Function.Call(Hash.TASK_DRIVE_BY, shooter, target, 0, 0f, 0f, 0f, 50f, 100, true, unchecked((int)0xC6EE6B4C));
        }

        // -------------------------------------------------------------------
        // Undercover sting mission: the plainclothes unit drives the player to a
        // staged drug buy, stakes it out, then busts it. The dealers fight or run
        // for their car -- at which point this hands off to the EXISTING pursuit
        // machinery (engaged fight, vehicle chase resume, backup, wrap-up).
        // -------------------------------------------------------------------
        private bool StartUndercoverMission(Ped player)
        {
            _undercover = false;   // one sting per ride; afterwards it patrols normally
            EnsureRelationships();

            Vector3 scene = FindHiddenSpawn(player.Position, 180f, 300f);
            if (scene == Vector3.Zero || scene.DistanceTo(player.Position) < 60f) return false;
            _ucScene = scene;

            // Stage the deal: a getaway car parked at the spot + three armed dealers.
            _suspectPeds.Clear();
            _suspectCar = World.CreateVehicle(new Model(VehicleHash.Buccaneer),
                new Vector3(scene.X + 2.5f, scene.Y + 2.5f, scene.Z + 0.5f));
            if (_suspectCar != null)
            {
                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _suspectCar);
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _suspectCar, true, true);
            }
            for (int i = 0; i < 3; i++)
            {
                Model m = new Model(i == 0 ? PedHash.Dealer01SMY : PedHash.StrPunk01GMY);
                Ped d = World.CreatePed(m, scene + RandomOffset(2.0f + i));
                if (d == null || !d.Exists()) continue;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, d, true, true);
                SetupSuspectPed(d, 2);
                // Hold the loitering pose until the bust kicks the door in.
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, d, true);
                Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, d,
                    i == 0 ? "WORLD_HUMAN_DRUG_DEALER" : "WORLD_HUMAN_HANG_OUT_STREET", 0, true);
                _suspectPeds.Add(d);
            }
            if (_suspectPeds.Count == 0)
            {
                if (Valid(_suspectCar)) _suspectCar.MarkAsNoLongerNeeded();
                _suspectCar = null;
                return false;   // staging failed -> fall back to a normal patrol
            }
            _suspect = _suspectPeds[0];
            _suspectThreat = 2;
            _suspectsInnocent = false;

            // Roll there like a civilian car -- no siren, normal traffic driving.
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, _driver, _copCar,
                scene.X, scene.Y, scene.Z, 17.0f, DRIVE_STYLE, 12.0f);
            _lastReissue = DateTime.Now;
            _lastCarMoving = DateTime.Now;

            string street = World.GetStreetName(scene);
            Notify("~b~" + CopNames.For(_driver) + ":~w~ Buy's going down on " + street +
                   ". We sit on it, then we hit it. You're my backup. Act natural.");
            CopBark(_driver, "GENERIC_HOWS_IT_GOING");
            SetPhase(Phase.UCDrive);
            return true;
        }

        private void HandleUCDrive(Ped player)
        {
            if (!player.IsInVehicle(_copCar))
            { Notify("~y~You left the unit mid-operation. Sting's blown. Ride over."); Cleanup(); return; }

            float d = _copCar.Position.DistanceTo(_ucScene);
            if (d < 30f)
            {
                Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 6000); // park it
                Notify("~b~" + CopNames.For(_driver) + ":~w~ There they are. Eyes forward. Wait for my signal...");
                SetPhase(Phase.UCStake);
                return;
            }

            // Same stall re-kick used everywhere else.
            if (CarStalled() && (DateTime.Now - _lastReissue).TotalSeconds > 3.0)
            {
                _lastReissue = DateTime.Now;
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, _driver, _copCar,
                    _ucScene.X, _ucScene.Y, _ucScene.Z, 17.0f, DRIVE_STYLE, 12.0f);
            }

            if (SecondsInPhase > 150)
            {
                Notify("~y~" + CopNames.For(_driver) + ":~w~ Deal's a bust. The boring kind. Back to patrol.");
                DespawnPursuitProps();
                ResumePatrol();
            }
        }

        private void HandleUCStake(Ped player)
        {
            if (!player.IsInVehicle(_copCar))
            { Notify("~y~You left the unit mid-operation. Sting's blown. Ride over."); Cleanup(); return; }

            // A short, tense beat... then the signal.
            if (SecondsInPhase > 8) TriggerBust();
        }

        private void TriggerBust()
        {
            Notify("~r~" + CopNames.For(_driver) + ":~w~ That's the signal - GO GO GO! LSPD! HANDS! ALL THE HANDS!");
            CopBark(_driver, "GENERIC_WAR_CRY");
            if (Valid(_copCar)) _copCar.IsSirenActive = true;   // Police4's hidden flashers
            AssignCop(_driver);
            AssignCop(_partner);

            // One dealer makes for the getaway car; the rest stand and fight. The
            // runner is the designated _suspect so the existing engaged-resume logic
            // turns it into a proper vehicle pursuit when he gets the door open.
            bool runnerSet = false;
            foreach (Ped s in _suspectPeds)
            {
                if (!Valid(s)) continue;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, s, false);
                Function.Call(Hash.CLEAR_PED_TASKS, s);
                if (!runnerSet && Valid(_suspectCar))
                {
                    runnerSet = true;
                    _suspect = s;
                    Function.Call(Hash.TASK_ENTER_VEHICLE, s, _suspectCar, -1, -1, 2.0f, 1, 0);
                    Function.Call(Hash.SET_PED_KEEP_TASK, s, true);
                }
                else if (Valid(_driver))
                {
                    Function.Call(Hash.TASK_COMBAT_PED, s, _driver, 0, 16);
                }
            }

            _engaged = true;
            ForceOutAndFight(_driver);
            ForceOutAndFight(_partner);

            // Prime the pursuit-phase state the same way StartPursuit does.
            _backupCount = 0;
            _pitting = false; _lastPit = DateTime.MinValue; _lastCollateral = DateTime.MinValue;
            _swatCalled = false; _heliCalled = false; _swatWaves = 0; _lastSwat = DateTime.Now;
            _lastRadio = DateTime.Now;
            _radioDelay = _radioIntervalMin + _rng.NextDouble() * Math.Max(0f, _radioIntervalMax - _radioIntervalMin);
            _lastBackup = DateTime.Now;
            _lastReissue = DateTime.Now;
            _lastCarMoving = DateTime.Now;
            _escapeTimerStarted = DateTime.MinValue;
            _suspectStoppedSince = DateTime.MinValue;
            _lastPursuitStart = DateTime.Now;
            SetPhase(Phase.Pursuit);
        }

        // -------------------------------------------------------------------
        // Post-pursuit scene wrap-up: real officers don't shrug and drive off.
        // The cruiser parks (lights stay on -- it's a scene now), one officer
        // covers while the other approaches the downed suspect and works the
        // body (cuff/inspect), the scene holds a beat, and only THEN does the
        // unit regroup and roll out. Suspect bodies aren't despawned until
        // Regroup, so BodyRecovery's ambulance often arrives mid-scene.
        // -------------------------------------------------------------------
        private void BeginWrapup()
        {
            _engaged = false;   // release the D-pad pursuit commands

            // Find a body to work: prefer one on the ground over one still in a car.
            _wrapBody = null;
            foreach (Ped s in _suspectPeds)
                if (s != null && s.Exists() && !s.IsInVehicle()) { _wrapBody = s; break; }
            if (_wrapBody == null)
                foreach (Ped s in _suspectPeds)
                    if (s != null && s.Exists()) { _wrapBody = s; break; }

            if (_wrapBody == null || (!Valid(_driver) && !Valid(_partner)))
            {
                EndSirens();
                SetPhase(Phase.Regroup);
                return;
            }

            // Park the cruiser at the scene; the lights stay on until Regroup.
            if (Valid(_driver) && _driver.IsInVehicle(_copCar))
                Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 5000);

            _wrapStage = 0;
            _wrapStageAt = DateTime.Now;
            SetPhase(Phase.Wrapup);
        }

        private void HandleWrapup(Ped player)
        {
            if (_wrapBody == null || !_wrapBody.Exists()) { FinishWrapup(); return; }
            Ped lead  = Valid(_partner) ? _partner : _driver;   // partner works the body...
            Ped cover = (Valid(_partner) && Valid(_driver)) ? _driver : null; // ...driver covers
            if (!Valid(lead)) { FinishWrapup(); return; }

            double inStage = (DateTime.Now - _wrapStageAt).TotalSeconds;
            switch (_wrapStage)
            {
                case 0: // dismount and approach; the cover officer holds an aim on the body
                    PrepForScene(lead);
                    Function.Call(Hash.TASK_GO_TO_ENTITY, lead, _wrapBody, -1, 1.6f, 1.6f, 1073741824.0f, 0);
                    if (Valid(cover))
                    {
                        PrepForScene(cover);
                        Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, cover, _wrapBody, -1, false);
                    }
                    Notify("~b~" + CopNames.For(lead) + ":~w~ Cover me. I'm gonna go 'check his pulse'.");
                    _wrapStage = 1; _wrapStageAt = DateTime.Now;
                    break;

                case 1: // reached the body (or took too long) -> cuff/inspect beat
                    if (lead.Position.DistanceTo(_wrapBody.Position) < 2.2f || inStage > 10)
                    {
                        if (!_wrapBody.IsDead)
                        {
                            Function.Call(Hash.TASK_ARREST_PED, lead, _wrapBody);
                            Notify("~b~" + CopNames.For(lead) + ":~w~ You're under arrest for surviving!");
                        }
                        else
                        {
                            Function.Call(Hash.TASK_TURN_PED_TO_FACE_ENTITY, lead, _wrapBody, 1200);
                            Function.Call(Hash.TASK_START_SCENARIO_IN_PLACE, lead,
                                "CODE_HUMAN_MEDIC_TEND_TO_DEAD", 0, true);
                            Notify("~b~" + CopNames.For(lead) + ":~w~ Yep. He's done resisting.");
                        }
                        _wrapStage = 2; _wrapStageAt = DateTime.Now;
                    }
                    break;

                case 2: // hold the scene a beat, then wrap and regroup
                    if (inStage > 9) FinishWrapup();
                    break;
            }

            if (SecondsInPhase > 40) FinishWrapup();   // hard safety cap
        }

        // Take an officer off the driving/combat locks so the scene tasks apply:
        // KEEP_TASK rejected new tasks, the event-block froze them on exit, and
        // CanLeaveVehicle=false kept them pinned in the seat.
        private void PrepForScene(Ped c)
        {
            if (!Valid(c)) return;
            Function.Call(Hash.SET_PED_KEEP_TASK, c, false);
            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, c, false);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanLeaveVehicle, true);
            Function.Call(Hash.CLEAR_PED_TASKS, c);
        }

        private void FinishWrapup()
        {
            _wrapBody = null;
            // Drop the tend/aim tasks so the Regroup re-board tasks apply cleanly.
            if (Valid(_driver)) Function.Call(Hash.CLEAR_PED_TASKS, _driver);
            if (Valid(_partner)) Function.Call(Hash.CLEAR_PED_TASKS, _partner);
            EndSirens();
            SetPhase(Phase.Regroup);
        }

        // Drop the suspect + backup units but keep your unit intact.
        private void DespawnPursuitProps()
        {
            foreach (Entity ent in _backupEntities)
            {
                Ped p = ent as Ped;
                if (p != null) { RideAlongRegistry.FriendlyCops.Remove(p.Handle); CopNames.Forget(p.Handle); }
                if (ent != null && ent.Exists()) ent.MarkAsNoLongerNeeded();
            }
            _backupEntities.Clear();
            _backupCount = 0;
            // Release designated suspects back to the engine (they're existing peds).
            foreach (Ped s in _suspectPeds) { if (Valid(s)) { Function.Call(Hash.SET_PED_KEEP_TASK, s, false); s.MarkAsNoLongerNeeded(); } }
            _suspectPeds.Clear();
            if (Valid(_suspect)) _suspect.MarkAsNoLongerNeeded();
            if (Valid(_suspectCar)) _suspectCar.MarkAsNoLongerNeeded();
            if (Valid(_suspectCar2)) _suspectCar2.MarkAsNoLongerNeeded();
            ReleaseHeli();
            _suspect = null; _suspectCar = null; _suspectCar2 = null;
        }
    }
}
