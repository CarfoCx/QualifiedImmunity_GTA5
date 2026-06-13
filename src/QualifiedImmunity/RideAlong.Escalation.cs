using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // Suspect-driven escalation, paced over the pursuit: SWAT at threat >=2,
        // a helicopter at threat 3, plus extra SWAT waves for the worst offenders.
        private void EscalateForThreat()
        {
            if (_suspectThreat < 2) return;
            double t = SecondsInPhase;
            if (!_swatCalled && t > _swatDelaySeconds) { _swatCalled = true; SpawnSwat(); }
            if (_suspectThreat >= 3 && !_heliCalled && t > _heliDelaySeconds) { _heliCalled = true; SpawnHeli(); }
            if (_suspectThreat >= 3 && _swatCalled && _swatWaves < _maxSwatWaves
                && (DateTime.Now - _lastSwat).TotalSeconds > _swatIntervalSeconds)
                SpawnSwat();

            // Re-issue helicopter flight task periodically to keep it locked above the suspect (part 2)
            if (_heliCalled && (DateTime.Now - _lastHeliUpdate).TotalSeconds > 3.0)
            {
                _lastHeliUpdate = DateTime.Now;
                UpdateHeliFlight();
            }
        }

        // A NOOSE/SWAT van of armoured, carbine-toting operators that joins the chase.
        private void SpawnSwat()
        {
            if (!Valid(_suspect)) return;
            Vector3 road = FindHiddenSpawn(_suspect.Position, 80f, 150f);
            Vector3 spawn = new Vector3(road.X, road.Y, road.Z + 2.5f);
            Vehicle v = World.CreateVehicle(new Model(VehicleHash.Riot), spawn);
            if (v == null) return;
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, v);
            v.IsSirenActive = true;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, v, true, true);

            VehicleSeat[] seats = { VehicleSeat.Driver, VehicleSeat.Passenger, VehicleSeat.LeftRear, VehicleSeat.RightRear };
            int n = 2 + _rng.Next(3);   // 2-4 operators
            for (int s = 0; s < n && s < seats.Length; s++)
            {
                Ped sw = v.CreatePedOnSeat(seats[s], new Model(PedHash.Swat01SMY));
                if (!Valid(sw)) continue;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, sw, true, true);
                SetupSwat(sw);
                RideAlongRegistry.FriendlyCops.Add(sw.Handle);
                _backupEntities.Add(sw);
                if (_engaged) CombatSuspect(sw);
            }

            Ped drv = v.GetPedOnSeat(VehicleSeat.Driver);
            if (Valid(drv) && !_engaged)
            {
                SetPursuitAggression(drv);
                Function.Call(Hash.TASK_VEHICLE_CHASE, drv, _suspect);
                Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, drv, 18.0f);
                Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, drv, RIDE_DRIVE_STYLE);
            }

            _backupEntities.Add(v);
            _swatWaves++;
            _lastSwat = DateTime.Now;
            Notify("~b~Dispatch:~w~ NOOSE/SWAT on scene - breach and clear!");
        }

        // Armoured operator loadout: carbine primary, pistol backup, tanky and accurate.
        private void SetupSwat(Ped sw)
        {
            if (!Valid(sw)) return;
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, sw, _copsGroup);
            Function.Call(Hash.SET_PED_ARMOUR, sw, 100);
            Function.Call(Hash.SET_ENTITY_MAX_HEALTH, sw, 300);
            Function.Call(Hash.SET_ENTITY_HEALTH, sw, 300);
            WeaponHash wh = _rng.Next(2) == 0 ? WeaponHash.CarbineRifle : WeaponHash.SpecialCarbine;
            Function.Call(Hash.GIVE_WEAPON_TO_PED, sw, unchecked((int)(uint)wh), 300, false, true);
            Function.Call(Hash.GIVE_WEAPON_TO_PED, sw, unchecked((int)(uint)WeaponHash.Pistol), 200, false, false);
            Function.Call(Hash.SET_PED_ACCURACY, sw, 72);
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, sw, 2);              // professional
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, sw, CA_AlwaysFight, true);     // always fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, sw, CA_FightArmedWhenUnarmed, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, sw, CA_CanUseCover, true);     // cover
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, sw, CA_CanFlank, true);    // flank
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, sw, 2);             // advance
            MakeGoodDriver(sw);
        }

        // Police helicopter overhead with a pilot and a door gunner.
        private void SpawnHeli()
        {
            if (!Valid(_suspect)) return;
            // Spawn off-camera and up high so it flies in rather than popping into view.
            Vector3 ground = FindHiddenSpawn(_suspect.Position, 120f, 220f);
            Vector3 spawn = new Vector3(ground.X, ground.Y, _suspect.Position.Z + 60f);
            _heli = World.CreateVehicle(new Model(VehicleHash.Polmav), spawn);
            if (_heli == null) return;
            _heli.IsSirenActive = true;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _heli, true, true);

            _heliPilot = _heli.CreatePedOnSeat(VehicleSeat.Driver, new Model(PedHash.Cop01SMY));
            _heliGunner = _heli.CreatePedOnSeat(VehicleSeat.Passenger, new Model(PedHash.Swat01SMY));

            if (Valid(_heliPilot))
            {
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _heliPilot, true, true);
                RideAlongRegistry.FriendlyCops.Add(_heliPilot.Handle);
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, _heliPilot, _copsGroup);
                Function.Call(Hash.SET_PED_KEEP_TASK, _heliPilot, true);
                _lastHeliUpdate = DateTime.Now;
                UpdateHeliFlight();
            }
            if (Valid(_heliGunner))
            {
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _heliGunner, true, true);
                RideAlongRegistry.FriendlyCops.Add(_heliGunner.Handle);
                SetupSwat(_heliGunner);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, _heliGunner, 3, true); // drive-bys from the heli (CA_CanLeaveVehicle/CanDriveby)
                CombatSuspect(_heliGunner);
            }
            Notify("~b~Dispatch:~w~ AIR-1 is overhead - eyes on the suspect!");
        }

        private void UpdateHeliFlight()
        {
            if (!Valid(_heliPilot) || !Valid(_heli) || !Valid(_suspect)) return;

            // Fly to a coordinate 30m above the suspect (mission flag 4 = go to coord).
            Vector3 targetPos = _suspect.Position + new Vector3(0f, 0f, 30f);
            Function.Call(Hash.TASK_HELI_MISSION, _heliPilot, _heli, 0, 0,
                targetPos.X, targetPos.Y, targetPos.Z,
                4, 40.0f, 10.0f, -1.0f, 0.0f, 20.0f, 20.0f, 32);
        }

        // Drop the helicopter and its crew (used on pursuit end + ride-along end).
        private void ReleaseHeli()
        {
            // Bookkeeping removed unconditionally (stale handles poison recycled peds);
            // the entity itself is only released if it still exists.
            if (_heliPilot != null)
            { RideAlongRegistry.FriendlyCops.Remove(_heliPilot.Handle); CopNames.Forget(_heliPilot.Handle); if (_heliPilot.Exists()) _heliPilot.MarkAsNoLongerNeeded(); }
            if (_heliGunner != null)
            { RideAlongRegistry.FriendlyCops.Remove(_heliGunner.Handle); CopNames.Forget(_heliGunner.Handle); if (_heliGunner.Exists()) _heliGunner.MarkAsNoLongerNeeded(); }
            if (_heli != null && _heli.Exists()) _heli.MarkAsNoLongerNeeded();
            _heli = null; _heliPilot = null; _heliGunner = null;
        }

        private void SpawnBackup()
        {
            if (!Valid(_suspect) || _backupCount >= _maxBackupUnits) return;
            // Spawn well back and off-camera so backup drives into the pursuit.
            Vector3 road = FindHiddenSpawn(_suspect.Position, 90f, 170f);
            
            bool isCounty = _suspect.Position.Y > 1500f;
            VehicleHash cruiserHash = isCounty ? VehicleHash.Sheriff : VehicleHash.Police3;
            PedHash copHash = isCounty ? PedHash.Sheriff01SMY : PedHash.Cop01SMY;

            Vector3 spawn = new Vector3(road.X, road.Y, road.Z + 2.5f);
            Vehicle v = World.CreateVehicle(new Model(cruiserHash), spawn);
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

            // 25% chance for a K-9 unit
            bool isK9 = _rng.Next(100) < 25;
            Ped dog = null;
            if (isK9)
            {
                dog = v.CreatePedOnSeat(VehicleSeat.LeftRear, new Model(PedHash.Rottweiler));
                if (Valid(dog))
                {
                    Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, dog, true, true);
                    Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, dog, _copsGroup);
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, dog, CA_AlwaysFight, true);
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, dog, CA_CanUseVehicles, true); // can exit
                    Notify("~b~Dispatch:~w~ K-9 unit joining the pursuit. Suspect is dog food.");
                }
            }

            // ~35% chance this unit "decided the situation warrants" heavy ordnance.
            if (_rng.Next(100) < 35 && Valid(g))
            {
                bool rocket = _rng.Next(2) == 0;
                int wpn = rocket ? unchecked((int)0xB1CA77B1)   // WEAPON_RPG
                                 : unchecked((int)0xA284510B);  // WEAPON_GRENADELAUNCHER
                Function.Call(Hash.GIVE_WEAPON_TO_PED, g, wpn, 10, false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, g, wpn, true);
                string gunner = "~r~" + CopNames.For(g) + ":~w~ ";
                Notify(rocket ? gunner + "I brought the rocket launcher. For a traffic stop. Problem?"
                              : gunner + "Grenade launcher's hot! Collateral is just paperwork!");
            }

            if (_engaged) 
            { 
                CombatSuspect(d); CombatSuspect(g); 
                if (Valid(dog)) CombatSuspect(dog);
            }
            else
            {
                if (Valid(d))
                {
                    SetPursuitAggression(d);
                    Function.Call(Hash.TASK_VEHICLE_CHASE, d, _suspect);
                    Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, d, _idealFollowDistance);
                    Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, d, RIDE_DRIVE_STYLE); // avoid traffic while chasing
                }
                DriveBy(g, _suspect);
            }

            _backupEntities.Add(v);
            if (Valid(d)) _backupEntities.Add(d);
            if (Valid(g)) _backupEntities.Add(g);
            if (Valid(dog)) _backupEntities.Add(dog);
            _backupCount++;
        }
    }
}
