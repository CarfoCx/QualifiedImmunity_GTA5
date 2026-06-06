using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // Caches world entities (peds and vehicles) within a large radius once per frame.
    // Minimizes expensive native calls in crowded areas across all scripts.
    internal static class WorldCache
    {
        private static int _lastFrame = -1;
        private static Ped[] _cachedPeds = null;
        private static Vehicle[] _cachedVehicles = null;
        private static Vector3 _cacheCenter = Vector3.Zero;
        private const float CacheRadius = 250f; // Wide enough to cover all scan radii (max is 160m)

        private static void UpdateCacheIfNeeded()
        {
            int currentFrame = Function.Call<int>(Hash.GET_FRAME_COUNT);
            if (currentFrame == _lastFrame && _cachedPeds != null && _cachedVehicles != null)
                return;

            Ped player = Game.Player.Character;
            if (player != null && player.Exists())
            {
                _cacheCenter = player.Position;
                _cachedPeds = World.GetNearbyPeds(player, CacheRadius);
                _cachedVehicles = World.GetNearbyVehicles(player, CacheRadius);
                _lastFrame = currentFrame;
            }
            else
            {
                _cachedPeds = new Ped[0];
                _cachedVehicles = new Vehicle[0];
            }
        }

        public static List<Ped> GetNearbyPeds(Vector3 pos, float radius)
        {
            UpdateCacheIfNeeded();
            List<Ped> results = new List<Ped>();

            // If the query exceeds the cached zone, fall back to direct query.
            if (pos.DistanceTo(_cacheCenter) + radius > CacheRadius)
            {
                return new List<Ped>(World.GetNearbyPeds(pos, radius));
            }

            float rSq = radius * radius;
            foreach (Ped p in _cachedPeds)
            {
                if (p != null && p.Exists() && p.Position.DistanceToSquared(pos) <= rSq)
                {
                    results.Add(p);
                }
            }
            return results;
        }

        public static List<Vehicle> GetNearbyVehicles(Vector3 pos, float radius)
        {
            UpdateCacheIfNeeded();
            List<Vehicle> results = new List<Vehicle>();

            // If the query exceeds the cached zone, fall back to direct query.
            if (pos.DistanceTo(_cacheCenter) + radius > CacheRadius)
            {
                return new List<Vehicle>(World.GetNearbyVehicles(pos, radius));
            }

            float rSq = radius * radius;
            foreach (Vehicle v in _cachedVehicles)
            {
                if (v != null && v.Exists() && v.Position.DistanceToSquared(pos) <= rSq)
                {
                    results.Add(v);
                }
            }
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
                if (d.PedType == PedType.Cop || d.PedType == PedType.Swat) continue;
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

                // Ignore police drivers or police vehicle models
                Ped d = v.Driver;
                if (d != null && d.Exists() && (d.PedType == PedType.Cop || d.PedType == PedType.Swat)) continue;
                
                switch ((VehicleHash)v.Model.Hash)
                {
                    case VehicleHash.Police:
                    case VehicleHash.Police2:
                    case VehicleHash.Police3:
                    case VehicleHash.Police4:
                    case VehicleHash.PoliceOld1:
                    case VehicleHash.PoliceOld2:
                    case VehicleHash.PoliceT:
                    case VehicleHash.Policeb:
                    case VehicleHash.Sheriff:
                    case VehicleHash.Sheriff2:
                    case VehicleHash.FBI:
                    case VehicleHash.FBI2:
                    case VehicleHash.Riot:
                    case VehicleHash.Pranger:
                    case VehicleHash.Polmav:
                        continue;
                }

                if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, victim, v, true))
                    return v;
            }
            return null;
        }
    }
}
