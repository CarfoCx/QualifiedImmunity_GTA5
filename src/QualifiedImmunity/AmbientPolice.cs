using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // Ambient open-world police encounters, independent of the ride-along. Periodically
    // stages a scene near (but off-camera from) the player and plays it out:
    //   - TrafficStop : a cop has a civ pulled over; brief check, everyone leaves.
    //   - Arrest      : suspect gives up, hands up, gets cuffed.
    //   - Resist      : suspect resists, gets tazed (ragdoll) and cuffed.
    //   - Gunfight    : armed suspect(s) trade fire with the officers.
    //   - Gang        : a heavily-armed crew that can overpower the responders.
    // Dangerous scenes call for backup (extra units drive in). Dead bodies are left for
    // the BodyRecovery script to collect. C# 5-compatible; API names verified vs SHVDNE.
    public class AmbientPolice : Script
    {
        // ---- Config ([AmbientPolice] in QualifiedImmunity.ini) ----
        private bool _enabled = true;
        private int _maxEvents = 2;
        private float _spawnIntervalMin = 90f;
        private float _spawnIntervalMax = 170f;
        private float _spawnDistMin = 60f;
        private float _spawnDistMax = 130f;
        private float _startupGraceSeconds = 60f;  // stage NOTHING for this long after load
        private const float DespawnDist = 300f;   // events past this from the player are torn down

        private readonly DateTime _scriptStart = DateTime.Now; // when this script loaded
        private int _eventsSpawned;                            // first couple scenes are forced calm

        private enum EType { TrafficStop, Arrest, Resist, Gunfight, Gang }

        private class Ev
        {
            public EType Type;
            public int Stage;
            public DateTime Since = DateTime.Now;   // time the current stage began
            public DateTime Start = DateTime.Now;   // time the whole event began
            public Vehicle CopCar;
            public Vehicle SuspectCar;
            public readonly List<Ped> Cops = new List<Ped>();
            public readonly List<Ped> Suspects = new List<Ped>();
            public readonly List<Entity> Backup = new List<Entity>();
            public bool BackupCalled;
            public Vector3 Where;
        }

        private readonly List<Ev> _events = new List<Ev>();
        private readonly Random _rng = new Random();
        private DateTime _lastSpawn = DateTime.MinValue;
        private double _nextDelay = 12.0;
        private int _copGroup, _suspGroup;
        private bool _rels;

        public AmbientPolice()
        {
            LoadConfig();
            Tick += OnTick;
            Aborted += OnAborted;
            Interval = 350;
        }

        private void LoadConfig()
        {
            ScriptSettings s = ScriptSettings.Load(@"scripts\QualifiedImmunity.ini");
            _enabled          = s.GetValue("AmbientPolice", "Enabled", _enabled);
            _maxEvents        = s.GetValue("AmbientPolice", "MaxConcurrentEvents", _maxEvents);
            _spawnIntervalMin = s.GetValue("AmbientPolice", "SpawnIntervalMinSeconds", _spawnIntervalMin);
            _spawnIntervalMax = s.GetValue("AmbientPolice", "SpawnIntervalMaxSeconds", _spawnIntervalMax);
            _spawnDistMin     = s.GetValue("AmbientPolice", "SpawnDistanceMin", _spawnDistMin);
            _spawnDistMax     = s.GetValue("AmbientPolice", "SpawnDistanceMax", _spawnDistMax);
            _startupGraceSeconds = s.GetValue("AmbientPolice", "StartupGraceSeconds", _startupGraceSeconds);
        }

        private void OnAborted(object sender, EventArgs e)
        {
            foreach (Ev ev in _events) Release(ev);
            _events.Clear();
        }

        // -------------------------------------------------------------------
        private void OnTick(object sender, EventArgs e)
        {
            if (!_enabled) return;
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) return;

            EnsureRels();

            for (int i = _events.Count - 1; i >= 0; i--)
            {
                Ev ev = _events[i];
                bool keep;
                try { keep = Update(ev, player); }
                catch { keep = false; }       // never let a bad scene wedge the script
                if (!keep) { Release(ev); _events.RemoveAt(i); }
            }

            if (CanStageNow() && _events.Count < _maxEvents
                && (DateTime.Now - _lastSpawn).TotalSeconds > _nextDelay)
            {
                _lastSpawn = DateTime.Now;
                _nextDelay = _spawnIntervalMin + _rng.NextDouble() * Math.Max(1f, _spawnIntervalMax - _spawnIntervalMin);
                try { SpawnEvent(player); } catch { }
            }
        }

        // Gate for staging new scenes: nothing right after load or during a loading/
        // fade/switch screen. The player was spawning straight into gunfire and a
        // panicking crowd the instant the game started -- this holds it off until the
        // world is actually loaded in and under player control.
        private bool CanStageNow()
        {
            if ((DateTime.Now - _scriptStart).TotalSeconds < _startupGraceSeconds) return false;
            if (!Function.Call<bool>(Hash.IS_SCREEN_FADED_IN)) return false;
            if (Function.Call<bool>(Hash.IS_PLAYER_SWITCH_IN_PROGRESS)) return false;
            if (!Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player)) return false;
            return true;
        }

        private void EnsureRels()
        {
            if (_rels) return;
            OutputArgument a = new OutputArgument(), b = new OutputArgument();
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "QI_AMB_COPS", a);
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "QI_AMB_CROOKS", b);
            _copGroup = a.GetResult<int>();
            _suspGroup = b.GetResult<int>();
            // 5 = hate, mutually. Suspects are left neutral to the player so this doesn't
            // turn into the player getting jumped by every staged crook in the city.
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _copGroup, _suspGroup);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _suspGroup, _copGroup);
            _rels = true;
        }

        // -------------------------------------------------------------------
        // Spawning a scene
        // -------------------------------------------------------------------
        private void SpawnEvent(Ped player)
        {
            float heading;
            Vector3 spot = RoadNear(player.Position, _spawnDistMin, _spawnDistMax, out heading);
            if (spot == Vector3.Zero) return;

            Ev ev = new Ev { Type = PickType(), Where = spot };
            switch (ev.Type)
            {
                case EType.TrafficStop:
                case EType.Arrest:
                case EType.Resist:
                    BuildStop(ev, spot, heading);
                    break;
                case EType.Gunfight:
                    BuildFight(ev, spot, heading, 2, 1);
                    break;
                case EType.Gang:
                    BuildFight(ev, spot, heading, 2, 4);
                    break;
            }

            if (CountAlive(ev.Cops) == 0) { Release(ev); return; }  // spawn failed -> bail
            _events.Add(ev);
            _eventsSpawned++;
        }

        private EType PickType()
        {
            // The first couple of staged scenes after load are ALWAYS calm (a stop or a
            // routine arrest) so the world doesn't erupt into gunfire right as ambient
            // policing comes online.
            if (_eventsSpawned < 2)
                return _rng.Next(2) == 0 ? EType.TrafficStop : EType.Arrest;

            // Heavily weighted toward routine, calm stops/arrests. The active scenes a player
            // reads as "cops chasing/fighting an NPC" (resist, gunfight, gang) are now a small
            // minority so they stay rare and special -- toned WAY down per feedback that the
            // ambient cops were pursuing NPCs too often. Still well above vanilla (which stages
            // none of this on its own).
            int r = _rng.Next(100);
            if (r < 48) return EType.TrafficStop; // 48%
            if (r < 78) return EType.Arrest;      // 30%
            if (r < 90) return EType.Resist;      // 12%
            if (r < 97) return EType.Gunfight;    //  7%
            return EType.Gang;                     //  3%
        }

        // A pulled-over scene: civ vehicle at the node, cruiser behind with lights on, an
        // officer stepping up to the window. Resolution depends on the event type.
        private void BuildStop(Ev ev, Vector3 spot, float heading)
        {
            Vector3 fwd = HeadingToVector(heading);
            // Only a traffic stop keeps the subject in a vehicle. Arrest/Resist put them on
            // FOOT -- you can't taze-ragdoll or hands-up a ped that's sitting in a car seat.
            bool vehicleStop = (ev.Type == EType.TrafficStop);

            // Parked stop -> NO siren (lights/siren is what makes every pedestrian bolt).
            // A stop is a calm scene; only gunfights get sirens.
            ev.CopCar = SpawnVehicle(VehicleHash.Police3, spot - fwd * 8f, heading);

            Ped suspect;
            if (vehicleStop)
            {
                ev.SuspectCar = SpawnVehicle(VehiclePool(), spot, heading);
                suspect = ev.SuspectCar != null
                    ? SpawnSuspect(ev.SuspectCar, VehicleSeat.Driver, Vector3.Zero, 0) : null;
            }
            else
            {
                suspect = SpawnSuspect(null, VehicleSeat.None, spot + RightOf(heading) * 2.0f, 0);
            }
            if (suspect != null) ev.Suspects.Add(suspect);

            Ped cop = ev.CopCar != null ? SpawnCop(ev.CopCar, VehicleSeat.Driver, Vector3.Zero)
                                        : SpawnCop(null, VehicleSeat.None, spot - fwd * 4f);
            if (cop != null)
            {
                ev.Cops.Add(cop);
                // Step out and walk up to the subject.
                if (Valid(suspect))
                    Function.Call(Hash.TASK_GO_TO_ENTITY, cop, suspect, -1, 1.5f, 1.6f, 1073741824.0f, 0);
            }
        }

        // A combat scene: officers vs an armed crew. copCount officers, suspCount suspects.
        // gang (suspCount>=3) get rifles/armor and can genuinely overpower the responders.
        private void BuildFight(Ev ev, Vector3 spot, float heading, int copCount, int suspCount)
        {
            Vector3 fwd = HeadingToVector(heading);
            bool gang = suspCount >= 3;

            ev.CopCar = SpawnVehicle(VehicleHash.Police3, spot - fwd * 9f, heading);
            if (ev.CopCar != null) ev.CopCar.IsSirenActive = true;

            VehicleSeat[] seats = { VehicleSeat.Driver, VehicleSeat.Passenger };
            for (int i = 0; i < copCount; i++)
            {
                Ped c = ev.CopCar != null && i < seats.Length
                    ? SpawnCop(ev.CopCar, seats[i], Vector3.Zero)
                    : SpawnCop(null, VehicleSeat.None, spot - fwd * (5f + i));
                if (c == null) continue;
                ev.Cops.Add(c);
                GiveCopWeapon(c, gang);   // shotguns/carbines when it's a serious call
            }

            for (int i = 0; i < suspCount; i++)
            {
                Vector3 p = spot + fwd * 4f + RightOf(heading) * ((i - suspCount / 2) * 1.6f);
                Ped s = SpawnSuspect(null, VehicleSeat.None, p, gang ? 3 : 2);
                if (s != null) ev.Suspects.Add(s);
            }

            // Kick the fight off immediately.
            EnsureFighting(ev);
        }

        // -------------------------------------------------------------------
        // Per-tick event logic
        // -------------------------------------------------------------------
        private bool Update(Ev ev, Ped player)
        {
            // Keep the responding officers off the QualifiedImmunity gang-cop AI so staged
            // scenes (especially peaceful ones) aren't hijacked into executing the civ.
            foreach (Ped c in ev.Cops) if (Valid(c)) RideAlongRegistry.FriendlyCops.Add(c.Handle);
            foreach (Entity b in ev.Backup) { Ped bp = b as Ped; if (Valid(bp)) RideAlongRegistry.FriendlyCops.Add(bp.Handle); }

            if (ev.Where.DistanceTo(player.Position) > DespawnDist) return false;
            double age = (DateTime.Now - ev.Start).TotalSeconds;

            switch (ev.Type)
            {
                case EType.TrafficStop: return UpdateStop(ev, age, false, false);
                case EType.Arrest:      return UpdateStop(ev, age, true, false);
                case EType.Resist:      return UpdateStop(ev, age, true, true);
                case EType.Gunfight:
                case EType.Gang:        return UpdateFight(ev, player, age);
            }
            return false;
        }

        // Stop / Arrest / Resist share a flow; `arrest` adds a cuffing, `resist` adds a taze.
        private bool UpdateStop(Ev ev, double age, bool arrest, bool resist)
        {
            Ped cop = First(ev.Cops);
            Ped suspect = First(ev.Suspects);
            if (!Valid(cop) || !Valid(suspect)) return age > 6; // someone died/despawned -> wrap up

            float gap = cop.Position.DistanceTo(suspect.Position);

            switch (ev.Stage)
            {
                case 0: // walking up to the window
                    if (gap < 2.5f || age > 18)
                    {
                        if (resist)
                        {
                            // Suspect bails and the officer tazes them.
                            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, suspect, false);
                            Function.Call(Hash.SET_PED_TO_RAGDOLL, suspect, 4500, 4500, 0, true, true, false);
                            Function.Call(Hash.PLAY_SOUND_FROM_ENTITY, -1, "Tazer_Shot", cop, "Police_Tazer_Sounds", 0, 0);
                            CopLine(cop, "STOP RESISTING!");
                            ev.Stage = 2; ev.Since = DateTime.Now;
                        }
                        else if (arrest)
                        {
                            Function.Call(Hash.TASK_HANDS_UP, suspect, 10000, cop, -1, false);
                            ev.Stage = 2; ev.Since = DateTime.Now;
                        }
                        else
                        {
                            ev.Stage = 1; ev.Since = DateTime.Now; // just a chat
                        }
                    }
                    break;

                case 1: // routine stop -- chat a bit, then everyone leaves
                    if ((DateTime.Now - ev.Since).TotalSeconds > 9) return false;
                    break;

                case 2: // cuffing (after hands-up or taze)
                    if ((DateTime.Now - ev.Since).TotalSeconds > 1.5)
                    {
                        Function.Call(Hash.TASK_ARREST_PED, cop, suspect);
                        ev.Stage = 3; ev.Since = DateTime.Now;
                    }
                    break;

                case 3: // arrested -- hold the scene briefly then tear down
                    if ((DateTime.Now - ev.Since).TotalSeconds > 10) return false;
                    break;
            }
            return age < 75; // hard safety cap
        }

        private bool UpdateFight(Ev ev, Ped player, double age)
        {
            int copsAlive = CountAlive(ev.Cops) + CountAliveEntities(ev.Backup);
            int suspAlive = CountAlive(ev.Suspects);

            // Dispatch always calls for help on a shots-fired call.
            if (!ev.BackupCalled && age > 4 && suspAlive > 0)
            {
                ev.BackupCalled = true;
                CallBackup(ev, ev.Type == EType.Gang ? 3 : 2);
                CopLine(First(ev.Cops), "Shots fired! All units respond!");
            }

            if ((DateTime.Now - ev.Since).TotalSeconds > 3.0) { EnsureFighting(ev); ev.Since = DateTime.Now; }

            if (suspAlive == 0 || copsAlive == 0)
            {
                if (ev.Stage == 0) { ev.Stage = 9; ev.Since = DateTime.Now; }
                return (DateTime.Now - ev.Since).TotalSeconds < 8; // let it settle, then release
            }
            return age < 150; // safety timeout
        }

        // (Re)issue combat tasks so the fight doesn't go inert. Suspects fight the nearest
        // officer; officers fight the nearest suspect.
        private void EnsureFighting(Ev ev)
        {
            foreach (Ped s in ev.Suspects)
            {
                if (!Valid(s)) continue;
                Ped t = NearestAlive(ev.Cops, s.Position, ev.Backup);
                if (Valid(t) && !Function.Call<bool>(Hash.IS_PED_IN_COMBAT, s, t))
                    Function.Call(Hash.TASK_COMBAT_PED, s, t, 0, 16);
            }
            CombatList(ev.Cops, ev);
            // Backup only engages once it has actually ARRIVED -- otherwise it stops mid-drive
            // and shoots from across the map instead of responding to the scene.
            foreach (Entity b in ev.Backup)
            {
                Ped bp = b as Ped;
                if (Valid(bp) && bp.Position.DistanceTo(ev.Where) < 45f) CombatOne(bp, ev);
            }
        }

        private void CombatList(List<Ped> cops, Ev ev)
        {
            foreach (Ped c in cops) CombatOne(c, ev);
        }

        private void CombatOne(Ped c, Ev ev)
        {
            if (!Valid(c)) return;
            Ped t = NearestAlive(ev.Suspects, c.Position, null);
            if (Valid(t) && !Function.Call<bool>(Hash.IS_PED_IN_COMBAT, c, t))
                Function.Call(Hash.TASK_COMBAT_PED, c, t, 0, 16);
        }

        // Send extra cruisers in. They drive to the scene and their crews join the fight.
        private void CallBackup(Ev ev, int units)
        {
            for (int u = 0; u < units; u++)
            {
                float h;
                Vector3 road = RoadNear(ev.Where, 70f, 150f, out h);
                if (road == Vector3.Zero) continue;
                Vehicle v = SpawnVehicle(VehicleHash.Police3, road, h);
                if (v == null) continue;
                v.IsSirenActive = true;
                ev.Backup.Add(v);

                Ped d = SpawnCop(v, VehicleSeat.Driver, Vector3.Zero);
                Ped g = SpawnCop(v, VehicleSeat.Passenger, Vector3.Zero);
                if (Valid(d))
                {
                    GiveCopWeapon(d, ev.Type == EType.Gang);
                    ev.Backup.Add(d);
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, d, v,
                        ev.Where.X, ev.Where.Y, ev.Where.Z, 22.0f, 786603, 12.0f);
                }
                if (Valid(g)) { GiveCopWeapon(g, ev.Type == EType.Gang); ev.Backup.Add(g); }
            }
        }

        // -------------------------------------------------------------------
        // Spawning primitives
        // -------------------------------------------------------------------
        private Ped SpawnCop(Vehicle car, VehicleSeat seat, Vector3 footPos)
        {
            Ped c = car != null ? car.CreatePedOnSeat(seat, new Model(PedHash.Cop01SMY))
                                : World.CreatePed(new Model(PedHash.Cop01SMY), footPos);
            if (c == null || !c.Exists()) return null;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, c, true, true);
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, c, _copGroup);
            Function.Call(Hash.SET_PED_AS_COP, c, false);   // the SCRIPT owns these, not police dispatch
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 46, true);  // fight even unarmed
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 5, true);   // always fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 0, true);   // use cover
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, c, 2);
            Function.Call(Hash.SET_PED_ACCURACY, c, 60);
            Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)WeaponHash.Pistol), 200, false, true);
            RideAlongRegistry.FriendlyCops.Add(c.Handle);
            return c;
        }

        private void GiveCopWeapon(Ped c, bool heavy)
        {
            if (!Valid(c)) return;
            WeaponHash w = heavy
                ? (_rng.Next(2) == 0 ? WeaponHash.CarbineRifle : WeaponHash.PumpShotgun)
                : (_rng.Next(2) == 0 ? WeaponHash.PumpShotgun : WeaponHash.SMG);
            Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)w), 250, false, true);
            // Act like a real responding officer: pile out of the car, use cover, and
            // engage at a sensible range rather than charging blindly.
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 3, true);   // CanLeaveVehicle -> get out and fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 0, true);   // CanUseCover
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 42, true);  // CanFlank
            Function.Call(Hash.SET_PED_COMBAT_RANGE, c, 1);              // medium range
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, c, 2);           // professional
            if (heavy) { Function.Call(Hash.SET_PED_ARMOUR, c, 50); Function.Call(Hash.SET_PED_ACCURACY, c, 65); }
        }

        // threat: 0 unarmed civ, 2 armed, 3 heavily-armed gang.
        private Ped SpawnSuspect(Vehicle car, VehicleSeat seat, Vector3 footPos, int threat)
        {
            Model m = new Model(threat >= 3 ? PedHash.MexGang01GMY : PedHash.Hipster01AMY);
            Ped s = car != null ? car.CreatePedOnSeat(seat, m) : World.CreatePed(m, footPos);
            if (s == null || !s.Exists()) return null;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, s, true, true);
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, s, _suspGroup);
            if (threat <= 0) return s;  // unarmed -- a routine stop / compliant arrest

            WeaponHash w;
            if (threat >= 3) w = _rng.Next(2) == 0 ? WeaponHash.CarbineRifle : WeaponHash.AssaultRifle;
            else             w = _rng.Next(2) == 0 ? WeaponHash.Pistol : WeaponHash.MicroSMG;
            Function.Call(Hash.GIVE_WEAPON_TO_PED, s, unchecked((int)(uint)w), 250, false, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, s, 5, true);   // always fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, s, 46, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, s, 0, true);   // use cover
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, s, 2);
            if (threat >= 3)
            {
                // Gangs are a real threat: armoured, accurate, tanky -- they can win.
                Function.Call(Hash.SET_PED_ARMOUR, s, 100);
                Function.Call(Hash.SET_ENTITY_MAX_HEALTH, s, 260);
                Function.Call(Hash.SET_ENTITY_HEALTH, s, 260);
                Function.Call(Hash.SET_PED_ACCURACY, s, 55);
                Function.Call(Hash.SET_PED_COMBAT_ABILITY, s, 2);
            }
            else Function.Call(Hash.SET_PED_ACCURACY, s, 35);
            return s;
        }

        private Vehicle SpawnVehicle(VehicleHash hash, Vector3 pos, float heading)
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, pos.X, pos.Y, pos.Z);
            Vehicle v = World.CreateVehicle(new Model(hash), new Vector3(pos.X, pos.Y, pos.Z + 1.0f), heading);
            if (v == null) return null;
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, v);
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, v, true, true);
            return v;
        }

        private VehicleHash VehiclePool()
        {
            VehicleHash[] cars = { VehicleHash.Sultan, VehicleHash.Premier, VehicleHash.Asea,
                                   VehicleHash.Blista, VehicleHash.Futo, VehicleHash.Washington };
            return cars[_rng.Next(cars.Length)];
        }

        // -------------------------------------------------------------------
        // Teardown
        // -------------------------------------------------------------------
        private void Release(Ev ev)
        {
            foreach (Ped c in ev.Cops) Free(c);
            foreach (Ped s in ev.Suspects) Free(s);
            foreach (Entity b in ev.Backup)
            {
                Ped bp = b as Ped; if (bp != null) { RideAlongRegistry.FriendlyCops.Remove(bp.Handle); }
                if (b != null && b.Exists()) b.MarkAsNoLongerNeeded();
            }
            if (ev.CopCar != null && ev.CopCar.Exists()) ev.CopCar.MarkAsNoLongerNeeded();
            if (ev.SuspectCar != null && ev.SuspectCar.Exists()) ev.SuspectCar.MarkAsNoLongerNeeded();
        }

        private void Free(Ped p)
        {
            if (p == null || !p.Exists()) return;
            RideAlongRegistry.FriendlyCops.Remove(p.Handle);
            p.MarkAsNoLongerNeeded();   // dead ones stay for BodyRecovery; live ones go ambient
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------
        private Vector3 RoadNear(Vector3 center, float min, float max, out float heading)
        {
            heading = 0f;
            for (int i = 0; i < 14; i++)
            {
                double ang = _rng.NextDouble() * Math.PI * 2.0;
                float d = min + (float)_rng.NextDouble() * (max - min);
                Vector3 p = center + new Vector3((float)(Math.Cos(ang) * d), (float)(Math.Sin(ang) * d), 0f);
                OutputArgument o = new OutputArgument();
                OutputArgument oh = new OutputArgument();
                if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING, p.X, p.Y, p.Z, o, oh, 1, 3.0f, 0f))
                {
                    Vector3 node = o.GetResult<Vector3>();
                    if (node.DistanceTo(center) >= min * 0.7f
                        && !Function.Call<bool>(Hash.IS_SPHERE_VISIBLE, node.X, node.Y, node.Z, 5.0f))
                    {
                        heading = oh.GetResult<float>();
                        return node;
                    }
                }
            }
            return Vector3.Zero;
        }

        private static Vector3 HeadingToVector(float headingDeg)
        {
            double r = (headingDeg + 90.0) * Math.PI / 180.0;
            return new Vector3((float)Math.Cos(r), (float)Math.Sin(r), 0f);
        }

        private static Vector3 RightOf(float headingDeg)
        {
            double r = headingDeg * Math.PI / 180.0;
            return new Vector3((float)Math.Cos(r), (float)Math.Sin(r), 0f);
        }

        private void CopLine(Ped cop, string line)
        {
            if (!Valid(cop)) return;
            GTA.UI.Notification.PostTicker("~b~Officer:~w~ " + line, false);
        }

        private static Ped First(List<Ped> list)
        {
            foreach (Ped p in list) if (Valid(p)) return p;
            return null;
        }

        private static int CountAlive(List<Ped> list)
        {
            int n = 0;
            foreach (Ped p in list) if (Valid(p)) n++;
            return n;
        }

        private static int CountAliveEntities(List<Entity> list)
        {
            int n = 0;
            foreach (Entity e in list) { Ped p = e as Ped; if (Valid(p)) n++; }
            return n;
        }

        private static Ped NearestAlive(List<Ped> list, Vector3 from, List<Entity> also)
        {
            Ped best = null; float bd = float.MaxValue;
            if (list != null)
                foreach (Ped p in list) { if (!Valid(p)) continue; float d = p.Position.DistanceTo(from); if (d < bd) { bd = d; best = p; } }
            if (also != null)
                foreach (Entity e in also) { Ped p = e as Ped; if (!Valid(p)) continue; float d = p.Position.DistanceTo(from); if (d < bd) { bd = d; best = p; } }
            return best;
        }

        private static bool Valid(Ped p) { return p != null && p.Exists() && !p.IsDead; }
        private static bool Valid(Vehicle v) { return v != null && v.Exists(); }
    }
}
