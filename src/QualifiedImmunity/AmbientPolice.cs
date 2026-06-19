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
    //   - Pursuit     : a suspect car flees; cruisers chase (sirens) then box it in and
    //                   either cuff the driver or shoot it out when it stops.
    //   - DrugBust    : officers raid a small dealer crew at a stash car -- surrender or shootout.
    //   - SwatRaid    : a NOOSE van of armoured operators breaches a dug-in, rifle-armed crew.
    //   - FootChase   : an armed suspect bails on foot; officers pursue into a running gun battle.
    // Dangerous scenes call for backup (extra units drive in). Dead bodies are left for
    // the BodyRecovery script to collect. C# 5-compatible; API names verified vs SHVDNE.
    public class AmbientPolice : Script
    {
        // ---- Config ([AmbientPolice] in QualifiedImmunity.ini) ----
        private bool _enabled = true;
        private int _maxEvents = 1;
        private float _spawnIntervalMin = 180f;
        private float _spawnIntervalMax = 330f;
        private float _spawnDistMin = 60f;
        private float _spawnDistMax = 130f;
        private float _startupGraceSeconds = 60f;  // stage NOTHING for this long after load
        private const float DespawnDist = 300f;   // events past this from the player are torn down

        private readonly DateTime _scriptStart = DateTime.Now; // when this script loaded
        private int _eventsSpawned;                            // first couple scenes are forced calm

        private enum EType { TrafficStop, Arrest, Resist, Gunfight, Gang, Pursuit, DrugBust, SwatRaid, FootChase }

        private class Ev
        {
            public EType Type;
            public int Stage;
            public DateTime Since = DateTime.Now;   // time the current stage began
            public DateTime Start = DateTime.Now;   // time the whole event began
            public DateTime LastRefresh = DateTime.Now; // last time tasks were re-issued (chase/combat)
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

        // Driving-style bitfield for cop drivers, mirroring the ride-along's proven
        // RIDE_DRIVE_STYLE (see RideAlong.cs): weave through traffic and run lights, but
        // with EVERY avoidance flag on -- swerve moving cars (4), steer around parked
        // cars (8), peds (16) and objects (32), plus wrong-way (512) so the AI holds
        // speed instead of snapping a lane into an obstacle. The plain "rushed" style
        // (786603) we used before only avoided moving cars, so cruisers clipped parked
        // cars and street furniture. Used for both backup response drives and pursuit.
        private const int DriveStyleAvoid = 787004;

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
            // Crooks never target their own: a group is NEUTRAL to itself by default,
            // so stray friendly fire flipped allied crooks into fighting each other.
            // Also befriend the sister crook groups from the other QI systems so a
            // mixed firefight stays crooks-vs-police. (ADD is idempotent; it just
            // guarantees the hashes exist before wiring.)
            OutputArgument c1 = new OutputArgument(), c2 = new OutputArgument();
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "QI_PURSUIT_SUSP", c1);
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "QI_CRIMEWATCH", c2);
            int[] crooks = { _suspGroup, c1.GetResult<int>(), c2.GetResult<int>() };
            foreach (int ga in crooks)
                foreach (int gb in crooks)
                    Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 0, ga, gb);
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
                case EType.Pursuit:
                    BuildPursuit(ev, spot, heading);
                    break;
                case EType.DrugBust:
                    BuildDrugBust(ev, spot, heading);
                    break;
                case EType.SwatRaid:
                    BuildSwatRaid(ev, spot, heading);
                    break;
                case EType.FootChase:
                    BuildFootChase(ev, spot, heading);
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

            // Heavily weighted toward routine, calm stops/arrests -- those are now the
            // dominant MAJORITY (~75%) so the world stays quiet. The spectacular scenes
            // share the rest, and the CHASES in particular (car pursuit + foot chase) are
            // deliberately the rarest of the bunch (5% combined) so they stay a rare
            // highlight you stumble onto, not a constant backdrop of sirens.
            int r = _rng.Next(100);
            if (r < 46) return EType.TrafficStop; // 46%  \
            if (r < 75) return EType.Arrest;      // 29%   } 75% calm
            if (r < 83) return EType.Resist;      //  8%   (taze-and-cuff, stationary)
            if (r < 88) return EType.DrugBust;    //  5%   raid on a dealer crew (stationary)
            if (r < 92) return EType.Gunfight;    //  4%   (stationary)
            if (r < 95) return EType.Pursuit;     //  3%   car chase
            if (r < 97) return EType.SwatRaid;    //  2%   NOOSE breach (stationary)
            if (r < 99) return EType.FootChase;   //  2%   bail-out foot pursuit
            return EType.Gang;                     //  1%
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

            // Despawn off a sensible anchor. Stationary scenes use their fixed origin;
            // moving scenes (a car/foot chase) track the live suspect, so following the
            // action doesn't tear it down and a chase that roams far still gets released.
            Vector3 anchor = ev.Where;
            if (ev.Type == EType.Pursuit || ev.Type == EType.FootChase)
            {
                Ped runner = First(ev.Suspects);
                if (Valid(runner)) anchor = runner.Position;
            }
            if (anchor.DistanceTo(player.Position) > DespawnDist) return false;
            double age = (DateTime.Now - ev.Start).TotalSeconds;

            switch (ev.Type)
            {
                case EType.TrafficStop: return UpdateStop(ev, age, false, false);
                case EType.Arrest:      return UpdateStop(ev, age, true, false);
                case EType.Resist:      return UpdateStop(ev, age, true, true);
                case EType.Gunfight:
                case EType.Gang:        return UpdateFight(ev, player, age);
                case EType.SwatRaid:    return UpdateFight(ev, player, age); // a breach is just a heavy fight
                case EType.Pursuit:     return UpdatePursuit(ev, player, age);
                case EType.DrugBust:    return UpdateDrugBust(ev, player, age);
                case EType.FootChase:   return UpdateFootChase(ev, player, age);
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

            // End-of-fight check FIRST: the EnsureFighting refresh below resets ev.Since
            // every ~3s, so if it ran before this check the 8s settle window could never
            // elapse and dead scenes lingered until the 150s safety timeout.
            if (suspAlive == 0 || copsAlive == 0)
            {
                if (ev.Stage == 0) { ev.Stage = 9; ev.Since = DateTime.Now; }
                return (DateTime.Now - ev.Since).TotalSeconds < 8; // let it settle, then release
            }

            if ((DateTime.Now - ev.Since).TotalSeconds > 3.0) { EnsureFighting(ev); ev.Since = DateTime.Now; }
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
                        ev.Where.X, ev.Where.Y, ev.Where.Z, 22.0f, DriveStyleAvoid, 12.0f);
                    TuneDriver(d);
                }
                if (Valid(g)) { GiveCopWeapon(g, ev.Type == EType.Gang); ev.Backup.Add(g); }
            }
        }

        // -------------------------------------------------------------------
        // Vehicle pursuit: a suspect car flees, cruiser(s) chase with sirens, then box
        // it in. Resolves to a cuffing (unarmed runner) or a shootout (armed driver).
        // -------------------------------------------------------------------
        private void BuildPursuit(Ev ev, Vector3 spot, float heading)
        {
            Vector3 fwd = HeadingToVector(heading);

            ev.SuspectCar = SpawnVehicle(VehiclePool(), spot, heading);
            ev.CopCar = SpawnVehicle(VehicleHash.Police3, spot - fwd * 13f, heading);
            if (ev.CopCar != null) ev.CopCar.IsSirenActive = true;

            // ~45% of fleeing drivers are unarmed panickers; the rest are armed and the
            // stop ends in gunfire once they're cornered.
            int threat = _rng.Next(100) < 45 ? 0 : 2;
            Ped driver = ev.SuspectCar != null
                ? SpawnSuspect(ev.SuspectCar, VehicleSeat.Driver, Vector3.Zero, threat) : null;
            if (driver != null) ev.Suspects.Add(driver);

            Ped d = ev.CopCar != null ? SpawnCop(ev.CopCar, VehicleSeat.Driver, Vector3.Zero) : null;
            Ped p = ev.CopCar != null ? SpawnCop(ev.CopCar, VehicleSeat.Passenger, Vector3.Zero) : null;
            if (Valid(d)) { GiveCopWeapon(d, false); ev.Cops.Add(d); }   // GiveCopWeapon enables CanLeaveVehicle
            if (Valid(p)) { GiveCopWeapon(p, false); ev.Cops.Add(p); }

            // Suspect floors it; the cruiser gives chase.
            if (Valid(driver) && Valid(ev.SuspectCar))
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, driver, ev.SuspectCar, 32.0f, 786603);
            StartChase(ev);
        }

        // Re-issue the chase task to whichever officer is driving the cruiser. TASK_VEHICLE_CHASE
        // drops its driving subtasks over time, so this is refreshed periodically.
        private void StartChase(Ev ev)
        {
            Ped susp = First(ev.Suspects);
            if (!Valid(susp) || ev.CopCar == null || !ev.CopCar.Exists()) return;
            Ped copDriver = ev.CopCar.Driver;
            if (!Valid(copDriver)) return;
            Function.Call(Hash.TASK_VEHICLE_CHASE, copDriver, susp);
            Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, copDriver, 12.0f);
            TuneDriver(copDriver);
        }

        // Make a cop driver a competent, collision-aware driver: max skill, calmer
        // aggression (less ramming/clipping), and a driving style that swerves around
        // moving traffic and pedestrians. SET_DRIVE_TASK_DRIVING_STYLE applies to whatever
        // drive/chase task is currently active, so this must run AFTER the task is issued.
        private void TuneDriver(Ped d)
        {
            if (!Valid(d)) return;
            Function.Call(Hash.SET_DRIVER_ABILITY, d, 1.0f);          // skilled -- handles traffic cleanly
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, d, 0.35f);  // calmer -- fewer collisions
            Function.Call(Hash.SET_DRIVE_TASK_DRIVING_STYLE, d, DriveStyleAvoid);
        }

        private bool UpdatePursuit(Ev ev, Ped player, double age)
        {
            int copsAlive = CountAlive(ev.Cops) + CountAliveEntities(ev.Backup);
            Ped driver = First(ev.Suspects);
            if (copsAlive == 0) return age > 4;
            if (!Valid(driver))
            {
                if (ev.Stage < 8) { ev.Stage = 8; ev.Since = DateTime.Now; }
                return (DateTime.Now - ev.Since).TotalSeconds < 6;
            }

            switch (ev.Stage)
            {
                case 0: // chasing
                    if ((DateTime.Now - ev.LastRefresh).TotalSeconds > 4.0) { StartChase(ev); ev.LastRefresh = DateTime.Now; }
                    bool stopped = !Valid(ev.SuspectCar)
                        || !Function.Call<bool>(Hash.IS_PED_IN_VEHICLE, driver, ev.SuspectCar, false)
                        || ev.SuspectCar.Speed < 2.5f;
                    if (stopped) { if ((DateTime.Now - ev.Since).TotalSeconds > 2.0) { ev.Stage = 1; ev.Since = DateTime.Now; } }
                    else ev.Since = DateTime.Now;  // keep resetting the "has stopped" timer while it's rolling
                    break;

                case 1: // cornered -> resolve
                    Ped lead = First(ev.Cops);
                    if (SuspectArmed(driver))
                    {
                        CopLine(lead, "Suspect's armed -- take cover!");
                        if (!ev.BackupCalled) { ev.BackupCalled = true; CallBackup(ev, 1); }
                        EnsureFighting(ev);
                        ev.Stage = 2; ev.Since = DateTime.Now; ev.LastRefresh = DateTime.Now;
                    }
                    else
                    {
                        // Get BOTH out of their cars first -- a seated ped can't put hands up,
                        // and TASK_ARREST_PED on someone still in a seat is unreliable.
                        if (Valid(ev.SuspectCar)
                            && Function.Call<bool>(Hash.IS_PED_IN_VEHICLE, driver, ev.SuspectCar, false))
                            Function.Call(Hash.TASK_LEAVE_VEHICLE, driver, ev.SuspectCar, 0);
                        if (Valid(lead)) Function.Call(Hash.TASK_LEAVE_VEHICLE, lead, ev.CopCar, 0);
                        CopLine(lead, "Out of the car -- hands up!");
                        ev.Stage = 3; ev.Since = DateTime.Now;
                    }
                    break;

                case 2: // shootout
                    if (CountAlive(ev.Suspects) == 0) { ev.Stage = 8; ev.Since = DateTime.Now; break; }
                    if ((DateTime.Now - ev.LastRefresh).TotalSeconds > 3.0) { EnsureFighting(ev); ev.LastRefresh = DateTime.Now; }
                    break;

                case 3: // runner is out of the car by now -> hands up, then cuff
                    if ((DateTime.Now - ev.Since).TotalSeconds > 2.5)
                    {
                        Ped cop = First(ev.Cops);
                        Function.Call(Hash.TASK_HANDS_UP, driver, 8000, cop, -1, false);
                        if (Valid(cop)) Function.Call(Hash.TASK_ARREST_PED, cop, driver);
                        ev.Stage = 4; ev.Since = DateTime.Now;
                    }
                    break;

                case 4: if ((DateTime.Now - ev.Since).TotalSeconds > 9) return false; break;
                case 8: return (DateTime.Now - ev.Since).TotalSeconds < 8;  // settle after a kill
            }
            return age < 150;
        }

        // -------------------------------------------------------------------
        // Drug bust: officers raid a small dealer crew working out of a stash car. They
        // move up; the crew either surrenders (cuffed) or opens fire (shootout + backup).
        // -------------------------------------------------------------------
        private void BuildDrugBust(Ev ev, Vector3 spot, float heading)
        {
            Vector3 fwd = HeadingToVector(heading);
            bool armed = _rng.Next(100) < 55;  // a little over half go loud

            ev.SuspectCar = SpawnVehicle(VehiclePool(), spot + RightOf(heading) * 3f, heading + 90f);

            int dealers = 2 + _rng.Next(2);  // 2-3
            for (int i = 0; i < dealers; i++)
            {
                Vector3 p = spot + RightOf(heading) * ((i - 1) * 1.4f);
                Ped s = SpawnSuspect(null, VehicleSeat.None, p, armed ? 2 : 0);
                if (s != null) ev.Suspects.Add(s);
            }

            ev.CopCar = SpawnVehicle(VehicleHash.Police3, spot - fwd * 14f, heading);
            if (ev.CopCar != null) ev.CopCar.IsSirenActive = true;
            Ped d = ev.CopCar != null ? SpawnCop(ev.CopCar, VehicleSeat.Driver, Vector3.Zero) : null;
            Ped p2 = ev.CopCar != null ? SpawnCop(ev.CopCar, VehicleSeat.Passenger, Vector3.Zero) : null;
            if (Valid(d)) { GiveCopWeapon(d, false); ev.Cops.Add(d); }
            if (Valid(p2)) { GiveCopWeapon(p2, false); ev.Cops.Add(p2); }

            // Officers move up on the crew. TASK_GO_TO_ENTITY pulls them out of the
            // cruiser on its own -- issuing TASK_LEAVE_VEHICLE first would just get
            // overridden by this in the same frame (see BuildStop for the same pattern).
            Ped target = First(ev.Suspects);
            foreach (Ped c in ev.Cops)
            {
                if (!Valid(c)) continue;
                if (Valid(target)) Function.Call(Hash.TASK_GO_TO_ENTITY, c, target, -1, 3.0f, 2.0f, 1073741824.0f, 0);
            }
        }

        private bool UpdateDrugBust(Ev ev, Ped player, double age)
        {
            int copsAlive = CountAlive(ev.Cops) + CountAliveEntities(ev.Backup);
            int suspAlive = CountAlive(ev.Suspects);
            if (copsAlive == 0 || suspAlive == 0)
            {
                if (ev.Stage < 8) { ev.Stage = 8; ev.Since = DateTime.Now; }
                return (DateTime.Now - ev.Since).TotalSeconds < 8;
            }

            Ped cop = First(ev.Cops);
            Ped lead = First(ev.Suspects);

            switch (ev.Stage)
            {
                case 0: // moving in
                    float gap = Valid(cop) && Valid(lead) ? cop.Position.DistanceTo(lead.Position) : 99f;
                    if (gap < 8f || age > 14)
                    {
                        if (SuspectArmed(lead))
                        {
                            CopLine(cop, "Police! Drop the weapon!");
                            ev.BackupCalled = true; CallBackup(ev, 2);
                            EnsureFighting(ev);
                            ev.Stage = 2; ev.Since = DateTime.Now; ev.LastRefresh = DateTime.Now;
                        }
                        else
                        {
                            CopLine(cop, "LSPD! On the ground, now!");
                            foreach (Ped s in ev.Suspects)
                                if (Valid(s)) Function.Call(Hash.TASK_HANDS_UP, s, 15000, cop, -1, false);
                            ev.Stage = 1; ev.Since = DateTime.Now;
                        }
                    }
                    break;

                case 1: // surrender -> cuff the lead dealer
                    if ((DateTime.Now - ev.Since).TotalSeconds > 2.0)
                    {
                        if (Valid(cop) && Valid(lead)) Function.Call(Hash.TASK_ARREST_PED, cop, lead);
                        ev.Stage = 5; ev.Since = DateTime.Now;
                    }
                    break;

                case 2: // shootout
                    if ((DateTime.Now - ev.LastRefresh).TotalSeconds > 3.0) { EnsureFighting(ev); ev.LastRefresh = DateTime.Now; }
                    break;

                case 5: if ((DateTime.Now - ev.Since).TotalSeconds > 10) return false; break;
            }
            return age < 150;
        }

        // -------------------------------------------------------------------
        // SWAT raid: a NOOSE van of armoured operators breaches a dug-in, rifle-armed crew.
        // Heavier than a gunfight; uses the shared UpdateFight loop once it's underway.
        // -------------------------------------------------------------------
        private void BuildSwatRaid(Ev ev, Vector3 spot, float heading)
        {
            Vector3 fwd = HeadingToVector(heading);

            int crew = 3 + _rng.Next(2);  // 3-4 heavily-armed targets
            for (int i = 0; i < crew; i++)
            {
                Vector3 p = spot + fwd * 5f + RightOf(heading) * ((i - crew / 2) * 1.6f);
                Ped s = SpawnSuspect(null, VehicleSeat.None, p, 3);
                if (s != null) ev.Suspects.Add(s);
            }

            ev.CopCar = SpawnVehicle(VehicleHash.Riot, spot - fwd * 12f, heading);
            if (ev.CopCar != null) ev.CopCar.IsSirenActive = true;
            VehicleSeat[] seats = { VehicleSeat.Driver, VehicleSeat.Passenger, VehicleSeat.LeftRear, VehicleSeat.RightRear };
            for (int i = 0; i < 4; i++)
            {
                Ped sw = ev.CopCar != null
                    ? SpawnSwat(ev.CopCar, seats[i], Vector3.Zero)
                    : SpawnSwat(null, VehicleSeat.None, spot - fwd * (5f + i));
                if (Valid(sw)) ev.Cops.Add(sw);
            }
            EnsureFighting(ev);
        }

        // -------------------------------------------------------------------
        // Foot chase: an armed suspect bails on foot, officers pursue, then it turns into
        // a running gun battle as the suspect spins around and opens fire.
        // -------------------------------------------------------------------
        private void BuildFootChase(Ev ev, Vector3 spot, float heading)
        {
            Vector3 fwd = HeadingToVector(heading);

            ev.CopCar = SpawnVehicle(VehicleHash.Police3, spot - fwd * 8f, heading);
            if (ev.CopCar != null) ev.CopCar.IsSirenActive = true;

            Ped susp = SpawnSuspect(null, VehicleSeat.None, spot + fwd * 6f, 2);
            if (susp != null) ev.Suspects.Add(susp);

            int copCount = 1 + _rng.Next(2);  // 1-2 officers on foot
            VehicleSeat[] seats = { VehicleSeat.Driver, VehicleSeat.Passenger };
            for (int i = 0; i < copCount; i++)
            {
                Ped c = ev.CopCar != null ? SpawnCop(ev.CopCar, seats[i], Vector3.Zero) : null;
                if (!Valid(c)) continue;
                GiveCopWeapon(c, false);
                ev.Cops.Add(c);
                if (Valid(ev.CopCar)) Function.Call(Hash.TASK_LEAVE_VEHICLE, c, ev.CopCar, 0);
            }

            Ped chaser = First(ev.Cops);
            if (Valid(susp) && Valid(chaser))
                Function.Call(Hash.TASK_SMART_FLEE_PED, susp, chaser, 200.0f, -1, false, false);
        }

        private bool UpdateFootChase(Ev ev, Ped player, double age)
        {
            int copsAlive = CountAlive(ev.Cops) + CountAliveEntities(ev.Backup);
            int suspAlive = CountAlive(ev.Suspects);
            if (copsAlive == 0 || suspAlive == 0)
            {
                if (ev.Stage < 8) { ev.Stage = 8; ev.Since = DateTime.Now; }
                return (DateTime.Now - ev.Since).TotalSeconds < 8;
            }

            Ped susp = First(ev.Suspects);

            switch (ev.Stage)
            {
                case 0: // foot pursuit
                    if ((DateTime.Now - ev.LastRefresh).TotalSeconds > 2.0)
                    {
                        foreach (Ped c in ev.Cops)
                            if (Valid(c) && Valid(susp))
                                Function.Call(Hash.TASK_GO_TO_ENTITY, c, susp, -1, 2.0f, 4.0f, 1073741824.0f, 0);
                        ev.LastRefresh = DateTime.Now;
                    }
                    // After a short run, or once a cop closes in, the suspect turns and fights.
                    if (age > 6 || NearestGap(ev.Cops, susp) < 12f)
                    {
                        CopLine(First(ev.Cops), "He's turning -- gun!");
                        if (!ev.BackupCalled) { ev.BackupCalled = true; CallBackup(ev, 1); }
                        EnsureFighting(ev);
                        ev.Stage = 2; ev.Since = DateTime.Now; ev.LastRefresh = DateTime.Now;
                    }
                    break;

                case 2: // shootout
                    if ((DateTime.Now - ev.LastRefresh).TotalSeconds > 3.0) { EnsureFighting(ev); ev.LastRefresh = DateTime.Now; }
                    break;
            }
            return age < 120;
        }

        private static float NearestGap(List<Ped> cops, Ped from)
        {
            if (!Valid(from)) return 999f;
            Ped n = NearestAlive(cops, from.Position, null);
            return Valid(n) ? n.Position.DistanceTo(from.Position) : 999f;
        }

        private static bool SuspectArmed(Ped p)
        {
            return Valid(p) && Function.Call<bool>(Hash.IS_PED_ARMED, p, 7);
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

        // A NOOSE operator: armoured, carbine, professional combat AI. Mirrors the SWAT
        // setup the ride-along escalation uses so a raid feels like the same outfit.
        private Ped SpawnSwat(Vehicle car, VehicleSeat seat, Vector3 footPos)
        {
            Ped c = car != null ? car.CreatePedOnSeat(seat, new Model(PedHash.Swat01SMY))
                                : World.CreatePed(new Model(PedHash.Swat01SMY), footPos);
            if (c == null || !c.Exists()) return null;
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, c, true, true);
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, c, _copGroup);
            Function.Call(Hash.SET_PED_AS_COP, c, false);
            Function.Call(Hash.SET_PED_ARMOUR, c, 100);
            Function.Call(Hash.SET_ENTITY_MAX_HEALTH, c, 300);
            Function.Call(Hash.SET_ENTITY_HEALTH, c, 300);
            WeaponHash wh = _rng.Next(2) == 0 ? WeaponHash.CarbineRifle : WeaponHash.SpecialCarbine;
            Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)wh), 300, false, true);
            Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)WeaponHash.Pistol), 200, false, false);
            Function.Call(Hash.SET_PED_ACCURACY, c, 72);
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, c, 2);          // professional
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 5, true);  // always fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 46, true); // fight even unarmed
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 0, true);  // use cover
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 3, true);  // leave vehicle to fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, 42, true); // flank
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, c, 2);          // advance
            Function.Call(Hash.SET_PED_COMBAT_RANGE, c, 1);            // medium
            RideAlongRegistry.FriendlyCops.Add(c.Handle);
            return c;
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
            if (p == null) return;
            // Remove the handle even if the ped despawned -- a stale registry entry
            // would mark whatever ped the engine recycles the handle onto as friendly.
            RideAlongRegistry.FriendlyCops.Remove(p.Handle);
            if (p.Exists()) p.MarkAsNoLongerNeeded();   // dead ones stay for BodyRecovery; live ones go ambient
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
