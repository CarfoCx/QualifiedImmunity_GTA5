using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // Caches world entities (peds and vehicles) within a large radius once per frame so the
    // many proximity scans across all four scripts don't each hammer the engine.
    //
    // The key cost this avoids: a naive scan calls entity.Exists() and entity.Position --
    // BOTH native calls -- for every entity on every query. With several scans per frame over
    // a crowded area that's thousands of native round-trips per frame. Here we validate each
    // entity and snapshot its position EXACTLY ONCE per frame at cache-build time; the
    // per-query filtering is then pure managed math (no natives), which is dramatically
    // cheaper when many queries hit the same frame.
    internal static class WorldCache
    {
        private struct PedRec { public Ped Ped; public Vector3 Pos; }
        private struct VehRec { public Vehicle Veh; public Vector3 Pos; }

        private static int _lastFrame = -1;
        // Persistent buffers, cleared and refilled once per frame so the snapshot itself
        // doesn't allocate fresh arrays every frame. foreach over List<struct> uses the
        // struct enumerator, so per-query iteration stays allocation-free too.
        private static readonly List<PedRec> _peds = new List<PedRec>();
        private static readonly List<VehRec> _vehicles = new List<VehRec>();
        private static Vector3 _cacheCenter = Vector3.Zero;
        // Wide enough to cover every scan radius in the mod (the largest is ~170m, centered
        // on the cruiser, which is itself next to the player). Kept tight so the once-per-frame
        // snapshot and the per-query loops stay small.
        private const float CacheRadius = 200f;

        private static void UpdateCacheIfNeeded()
        {
            int currentFrame = Function.Call<int>(Hash.GET_FRAME_COUNT);
            if (currentFrame == _lastFrame) return;
            _lastFrame = currentFrame;

            _peds.Clear();
            _vehicles.Clear();

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;

            _cacheCenter = player.Position;

            foreach (Ped p in World.GetNearbyPeds(player, CacheRadius))
                if (p != null && p.Exists()) _peds.Add(new PedRec { Ped = p, Pos = p.Position });

            foreach (Vehicle v in World.GetNearbyVehicles(player, CacheRadius))
                if (v != null && v.Exists()) _vehicles.Add(new VehRec { Veh = v, Pos = v.Position });
        }

        public static List<Ped> GetNearbyPeds(Vector3 pos, float radius)
        {
            UpdateCacheIfNeeded();

            // Query reaches outside the cached zone -> fall back to a direct (correct) query.
            if (pos.DistanceTo(_cacheCenter) + radius > CacheRadius)
                return new List<Ped>(World.GetNearbyPeds(pos, radius));

            float rSq = radius * radius;
            List<Ped> results = new List<Ped>();
            foreach (PedRec rec in _peds)
                if (rec.Pos.DistanceToSquared(pos) <= rSq) results.Add(rec.Ped);
            return results;
        }

        public static List<Vehicle> GetNearbyVehicles(Vector3 pos, float radius)
        {
            UpdateCacheIfNeeded();

            if (pos.DistanceTo(_cacheCenter) + radius > CacheRadius)
                return new List<Vehicle>(World.GetNearbyVehicles(pos, radius));

            float rSq = radius * radius;
            List<Vehicle> results = new List<Vehicle>();
            foreach (VehRec rec in _vehicles)
                if (rec.Pos.DistanceToSquared(pos) <= rSq) results.Add(rec.Veh);
            return results;
        }
    }

    // Shared vehicle-finding helpers.
    internal static class NPCVehicleFinder
    {
        // Finds a nearby driveable NPC-driven vehicle (not police, not player, not friendly).
        public static Vehicle FindNearbyNPCDrivenVehicle(Vector3 pos, float radius, float minDistance, Vehicle exclude1, Vehicle exclude2)
        {
            Ped player = Game.Player.Character;
            Vehicle best = null;
            float bestD = float.MaxValue;

            foreach (Vehicle v in WorldCache.GetNearbyVehicles(pos, radius))
            {
                if (v == null || !v.Exists()) continue;
                if (v == exclude1 || v == exclude2) continue;
                if (player != null && player.IsInVehicle(v)) continue;
                if (!Function.Call<bool>(Hash.IS_VEHICLE_DRIVEABLE, v, false)) continue;

                Ped d = v.Driver;
                if (d == null || !d.Exists() || d.IsDead || d == player) continue;
                if (Cops.IsCop(d)) continue;
                // Never designate emergency services -- this is how a pursuit ended up
                // arming BodyRecovery's own ambulance crew and chasing the meat wagon.
                if (d.PedType == PedType.Medic || d.PedType == PedType.Fire) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(d.Handle)) continue;

                float dist = v.Position.DistanceTo(pos);
                if (dist < minDistance) continue;
                if (dist < bestD)
                {
                    bestD = dist;
                    best = v;
                }
            }
            return best;
        }

        // Finds the non-police vehicle that struck a victim.
        public static Vehicle FindVehicleThatHit(Ped victim, Ped player, float radius)
        {
            foreach (Vehicle v in WorldCache.GetNearbyVehicles(victim.Position, radius))
            {
                if (v == null || !v.Exists()) continue;
                if (player != null && player.IsInVehicle(v)) continue;
                if (Cops.IsCop(v.Driver) || Cops.IsPoliceModel(v)) continue; // a cop running someone over is "fine"

                if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, victim, v, true))
                    return v;
            }
            return null;
        }
    }
}
