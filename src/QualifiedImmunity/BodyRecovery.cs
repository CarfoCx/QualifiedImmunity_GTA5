using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // Keeps the dead where they fell. No corpse despawns on its own -- each one is
    // pinned in place until an ambulance rolls up, a paramedic walks over and loads
    // it, and the meat wagon carts it off. Single-player; the ambulances and medics
    // are script-spawned, so this works with or without the rest of the mod.
    //
    // C# 5-compatible (in-box csc), API names verified against SHVDNE.
    public class BodyRecovery : Script
    {
        // ---- Config (loaded from QualifiedImmunity.ini) --------------------
        private bool _enabled = true;
        private float _scanRadius = 160f;   // pin/recover bodies within this of the player
        private int _maxAmbulances = 3;     // concurrent meat wagons
        private int _maxBodies = 48;        // safety cap so we never blow the ped budget

        private enum Stage { EnRoute, OnFoot, Leaving }

        private class Recovery
        {
            public int Body;
            public Vehicle Van;
            public Ped Driver;
            public Ped Attendant;     // walks over and loads the body (falls back to driver)
            public Stage Stage;
            public DateTime Since;
        }

        // Pinned bodies, oldest first so the safety cap can release the oldest.
        private readonly List<int> _bodies = new List<int>();
        private readonly HashSet<int> _bodySet = new HashSet<int>();
        // Bodies the safety cap handed back to the engine. Kept out of re-pinning so
        // a released corpse that's still near the player doesn't bounce right back in
        // (which would defeat the cap). Pruned once the ped is gone.
        private readonly HashSet<int> _released = new HashSet<int>();
        private readonly List<Recovery> _jobs = new List<Recovery>();
        private DateTime _lastScan = DateTime.MinValue;
        private readonly Random _rng = new Random();

        // Brisk but careful driving: avoid traffic, steer around obstacles.
        private const int AMBULANCE_DRIVE_STYLE = 786468; // DrivingStyle.AvoidTraffic

        public BodyRecovery()
        {
            LoadConfig();
            Tick += OnTick;
            Aborted += OnAborted;
            Interval = 400;
        }

        private void OnAborted(object sender, EventArgs e)
        {
            foreach (int handle in _bodies)
            {
                Ped p = (Ped)Entity.FromHandle(handle);
                if (p != null && p.Exists())
                {
                    p.MarkAsNoLongerNeeded();
                }
            }
            _bodies.Clear();
            _bodySet.Clear();
            _released.Clear();

            foreach (Recovery r in _jobs)
            {
                ReleaseCrew(r);
            }
            _jobs.Clear();
        }

        private void LoadConfig()
        {
            ScriptSettings s = ScriptSettings.Load(@"scripts\QualifiedImmunity.ini");
            _enabled       = s.GetValue("Bodies", "KeepBodiesUntilAmbulance", _enabled);
            _maxAmbulances = s.GetValue("Bodies", "MaxAmbulances", _maxAmbulances);
            _maxBodies     = s.GetValue("Bodies", "MaxPersistentBodies", _maxBodies);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_enabled) return;
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;

            // Scanning the world for corpses is the expensive part -- do it ~1x/sec.
            if ((DateTime.Now - _lastScan).TotalSeconds >= 1.0)
            {
                _lastScan = DateTime.Now;
                PinNearbyBodies(player);
                PruneGoneBodies();
                EnforceBodyCap();
            }

            // DispatchAmbulances and UpdateRecoveries
            DispatchAmbulances(player);
            UpdateRecoveries();
        }

        // -------------------------------------------------------------------
        // Pinning the dead
        // -------------------------------------------------------------------
        private void PinNearbyBodies(Ped player)
        {
            foreach (Ped p in WorldCache.GetNearbyPeds(player.Position, _scanRadius))
            {
                if (p == null || !p.Exists() || p == player) continue;
                if (!IsCollectibleBody(p)) continue;
                if (_bodySet.Contains(p.Handle) || _released.Contains(p.Handle)) continue;

                // Claim ownership so the engine won't sweep the body up, and keep it
                // from sinking through / being cleaned with collision unloaded.
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, p, true, true);
                Function.Call(Hash.SET_ENTITY_LOAD_COLLISION_FLAG, p, true);
                _bodies.Add(p.Handle);
                _bodySet.Add(p.Handle);

                // Add crime scene flare on 30% of bodies
                if (_rng.NextDouble() < 0.3)
                {
                    Function.Call(Hash.SHOOT_SINGLE_BULLET_BETWEEN_COORDS,
                        p.Position.X, p.Position.Y, p.Position.Z + 0.1f,
                        p.Position.X, p.Position.Y, p.Position.Z - 0.1f,
                        0, true, unchecked((int)0x497FACC3), 0, true, false, 100f);
                }
            }
        }

        private void PruneGoneBodies()
        {
            for (int i = _bodies.Count - 1; i >= 0; i--)
            {
                Ped p = (Ped)Entity.FromHandle(_bodies[i]);
                if (p == null || !p.Exists()) Forget(_bodies[i], i);
            }

            // Drop released handles whose peds have despawned so the blacklist can't
            // grow forever (and a recycled handle isn't wrongly skipped for good).
            if (_released.Count > 0)
            {
                List<int> gone = new List<int>();
                foreach (int h in _released)
                {
                    Ped p = (Ped)Entity.FromHandle(h);
                    if (p == null || !p.Exists()) gone.Add(h);
                }
                foreach (int h in gone) _released.Remove(h);
            }
        }

        // Hard safety valve: if corpses pile up past the cap, release the oldest
        // un-serviced ones so we never exhaust the ped budget and wedge spawns.
        private void EnforceBodyCap()
        {
            while (_bodies.Count > _maxBodies)
            {
                int handle = OldestUnserviced();
                if (handle == 0) break;   // everything left is actively being recovered
                Ped p = (Ped)Entity.FromHandle(handle);
                if (p != null && p.Exists()) p.MarkAsNoLongerNeeded();
                Forget(handle, _bodies.IndexOf(handle));
                _released.Add(handle);    // don't re-pin this one on the next scan
            }
        }

        // -------------------------------------------------------------------
        // Dispatch & recovery
        // -------------------------------------------------------------------
        private void DispatchAmbulances(Ped player)
        {
            if (_jobs.Count >= _maxAmbulances) return;

            int bodyHandle = NearestUnservicedBody(player);
            if (bodyHandle == 0) return;
            Ped body = (Ped)Entity.FromHandle(bodyHandle);
            if (body == null || !body.Exists()) return;

            Vector3 road = SnapToRoad(body.Position + Offset(35f));
            Vector3 spawn = new Vector3(road.X, road.Y, road.Z + 2.5f);
            Vehicle van = World.CreateVehicle(new Model(VehicleHash.Ambulance), spawn);
            if (van == null) return;
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, van);
            van.IsSirenActive = true;

            Model medic = new Model("s_m_m_paramedic_01");
            Ped driver = van.CreatePedOnSeat(VehicleSeat.Driver, medic);
            Ped attendant = van.CreatePedOnSeat(VehicleSeat.Passenger, medic);
            if (driver == null || !driver.Exists()) { van.MarkAsNoLongerNeeded(); return; }

            MakeCarefulDriver(driver);
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, driver, van,
                body.Position.X, body.Position.Y, body.Position.Z, 20.0f, AMBULANCE_DRIVE_STYLE, 8.0f);

            Recovery job = new Recovery
            {
                Body = bodyHandle, Van = van, Driver = driver, Attendant = attendant,
                Stage = Stage.EnRoute, Since = DateTime.Now
            };
            _jobs.Add(job);
            GTA.UI.Notification.PostTicker("~b~EMS:~w~ Ambulance dispatched to recover a body.", false);
        }

        private void UpdateRecoveries()
        {
            for (int i = _jobs.Count - 1; i >= 0; i--)
            {
                Recovery r = _jobs[i];
                Ped body = (Ped)Entity.FromHandle(r.Body);

                // Van wrecked or driver dead -> abandon. The body stays pinned so a
                // fresh ambulance can be dispatched for it later.
                if (r.Van == null || !r.Van.Exists()
                    || !Function.Call<bool>(Hash.IS_VEHICLE_DRIVEABLE, r.Van, false)
                    || !Valid(r.Driver))
                {
                    ReleaseCrew(r);
                    _jobs.RemoveAt(i);
                    continue;
                }

                // Body already gone (deleted elsewhere) -> just send the crew off.
                if (body == null || !body.Exists())
                {
                    DriveOffAndRelease(r);
                    _jobs.RemoveAt(i);
                    continue;
                }

                double secs = (DateTime.Now - r.Since).TotalSeconds;
                switch (r.Stage)
                {
                    case Stage.EnRoute:
                        if (r.Van.Position.DistanceTo(body.Position) < 14f || secs > 50)
                        {
                            Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, r.Driver, r.Van, 1, 6000); // park/brake
                            Ped fetch = Valid(r.Attendant) ? r.Attendant : r.Driver;
                            // 1073741824 = AF_RUN-ish nav flag used widely for "go there".
                            Function.Call(Hash.TASK_GO_TO_ENTITY, fetch, body, -1, 1.0f, 2.0f, 1073741824.0f, 0);
                            r.Stage = Stage.OnFoot;
                            r.Since = DateTime.Now;
                        }
                        break;

                    case Stage.OnFoot:
                    {
                        Ped fetch = Valid(r.Attendant) ? r.Attendant : r.Driver;
                        if (!Valid(fetch)) { DriveOffAndRelease(r); _jobs.RemoveAt(i); break; }

                        if (fetch.Position.DistanceTo(body.Position) < 2.5f || secs > 30)
                        {
                            // Loaded up -- the body is taken away.
                            RemoveBody(r.Body);
                            GTA.UI.Notification.PostTicker("~b~EMS:~w~ Body recovered.", false);
                            if (Valid(r.Attendant) && !r.Attendant.IsInVehicle(r.Van))
                                Function.Call(Hash.TASK_ENTER_VEHICLE, r.Attendant, r.Van, 20000, 0, 2.0f, 1, 0);
                            r.Stage = Stage.Leaving;
                            r.Since = DateTime.Now;
                        }
                        break;
                    }

                    case Stage.Leaving:
                        bool attendantAboard = !Valid(r.Attendant) || r.Attendant.IsInVehicle(r.Van);
                        if (attendantAboard || secs > 14)
                        {
                            DriveOffAndRelease(r);
                            _jobs.RemoveAt(i);
                        }
                        break;
                }
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------
        private void DriveOffAndRelease(Recovery r)
        {
            if (Valid(r.Driver) && r.Van != null && r.Van.Exists())
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, r.Driver, r.Van, 18.0f, AMBULANCE_DRIVE_STYLE);
            ReleaseCrew(r);
        }

        private void ReleaseCrew(Recovery r)
        {
            if (r.Van != null && r.Van.Exists()) r.Van.MarkAsNoLongerNeeded();
            if (r.Driver != null && r.Driver.Exists()) r.Driver.MarkAsNoLongerNeeded();
            if (r.Attendant != null && r.Attendant.Exists()) r.Attendant.MarkAsNoLongerNeeded();
        }

        private void RemoveBody(int handle)
        {
            int idx = _bodies.IndexOf(handle);
            Ped p = (Ped)Entity.FromHandle(handle);
            if (p != null && p.Exists()) p.Delete();
            Forget(handle, idx);
        }

        private void Forget(int handle, int idxHint)
        {
            _bodySet.Remove(handle);
            if (idxHint >= 0 && idxHint < _bodies.Count && _bodies[idxHint] == handle)
                _bodies.RemoveAt(idxHint);
            else
                _bodies.Remove(handle);
        }

        private int NearestUnservicedBody(Ped player)
        {
            int best = 0;
            float bestD = float.MaxValue;
            foreach (int h in _bodies)
            {
                if (IsServiced(h)) continue;
                Ped p = (Ped)Entity.FromHandle(h);
                if (p == null || !p.Exists()) continue;
                float d = p.Position.DistanceTo(player.Position);
                // Only actively recover bodies near the player; distant pinned
                // corpses wait their turn (and don't spawn off-screen ambulances).
                if (d > _scanRadius) continue;
                if (d < bestD) { bestD = d; best = h; }
            }
            return best;
        }

        private int OldestUnserviced()
        {
            foreach (int h in _bodies)
                if (!IsServiced(h)) return h;
            return 0;
        }

        private bool IsServiced(int bodyHandle)
        {
            foreach (Recovery r in _jobs)
                if (r.Body == bodyHandle) return true;
            return false;
        }

        private void MakeCarefulDriver(Ped p)
        {
            Function.Call(Hash.SET_DRIVER_ABILITY, p, 1.0f);
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, p, 0.35f);
            Function.Call(Hash.SET_PED_STEERS_AROUND_VEHICLES, p, true);
            Function.Call(Hash.SET_PED_STEERS_AROUND_OBJECTS, p, true);
            Function.Call(Hash.SET_PED_STEERS_AROUND_PEDS, p, true);
        }

        private Vector3 SnapToRoad(Vector3 p)
        {
            OutputArgument o = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE, p.X, p.Y, p.Z, o, 1, 3.0f, 0f))
                return o.GetResult<Vector3>();
            return p;
        }

        private Vector3 Offset(float r)
        {
            double a = _rng.NextDouble() * Math.PI * 2.0;
            return new Vector3((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r), 0f);
        }

        private static bool Valid(Ped p) { return p != null && p.Exists() && !p.IsDead; }

        // A body worth collecting: one that will end up a corpse -- already dead,
        // or fatally injured and bleeding out. Peds merely knocked down / stunned /
        // ragdolled (who will get back up) are left alone, as is anyone still in a car.
        private static bool IsCollectibleBody(Ped p)
        {
            if (p == null || !p.Exists()) return false;
            if (p.IsInVehicle()) return false;
            return p.IsDead || Function.Call<bool>(Hash.IS_PED_FATALLY_INJURED, p);
        }
    }
}
