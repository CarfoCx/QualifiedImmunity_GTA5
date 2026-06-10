using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // Keeps NPC traffic from stampeding when gunfire erupts. Real drivers mostly
    // STOP and keep their heads down; only someone with the muzzle flash right
    // outside their window actually peels out. The engine's default is the
    // opposite -- every car in earshot floors it and drives like a maniac -- so
    // during scripted firefights we sweep the area and pin the far-away drivers
    // calmly in place until the scene clears.
    internal static class TrafficCalm
    {
        // Inside this range the threat is "right next to them" -- real panic is
        // allowed (the engine's own reaction, or PanicTraffic's bail/floor-it).
        private const float PanicRadius = 12f;
        private const float SweepRadius = 50f;

        private static readonly HashSet<int> Calmed = new HashSet<int>();
        private static DateTime _lastSweep = DateTime.MinValue;

        // Call every tick while a firefight is live; internally throttled.
        public static void Sweep(Vector3 fightAt)
        {
            if ((DateTime.Now - _lastSweep).TotalSeconds < 2.0) return;
            _lastSweep = DateTime.Now;

            Ped player = Game.Player.Character;
            foreach (Vehicle v in WorldCache.GetNearbyVehicles(fightAt, SweepRadius))
            {
                if (v == null || !v.Exists() || Cops.IsPoliceVehicle(v)) continue;
                Ped drv = v.Driver;
                if (drv == null || !drv.Exists() || drv.IsDead || drv == player) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(drv.Handle)) continue;
                if (Calmed.Contains(drv.Handle)) continue;
                if (drv.Position.DistanceTo(fightAt) < PanicRadius) continue;

                Calmed.Add(drv.Handle);
                // Block the shocking events FIRST so the engine can't hand them a
                // flee-in-vehicle response, drop any panic task they already
                // grabbed, then brake and sit tight until the scene clears.
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, drv, true);
                Function.Call(Hash.CLEAR_PED_TASKS, drv);
                Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, drv, v, 1, 30000); // brake & hold
            }
        }

        // Scene's over: give the becalmed drivers back to the engine, rolling again.
        public static void ReleaseAll()
        {
            foreach (int h in Calmed)
            {
                Ped drv = Entity.FromHandle(h) as Ped;
                if (drv == null || !drv.Exists() || drv.IsDead) continue;
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, drv, false);
                Function.Call(Hash.CLEAR_PED_TASKS, drv);
                Vehicle v = drv.CurrentVehicle;
                if (v != null && v.Exists())
                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, drv, v, 13.0f, 786603); // lawful cruise
            }
            Calmed.Clear();
        }
    }
}
