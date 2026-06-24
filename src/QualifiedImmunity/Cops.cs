using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace QualifiedImmunity
{
    // Single source of truth for "is this a cop / police vehicle". This logic (and the
    // police vehicle-model list in particular) used to be copy-pasted into QualifiedImmunity,
    // NPCVehicleFinder, and RideAlong, where the three lists could silently drift apart.
    // Centralizing it removes that hazard and gives one place to tune.
    internal static class Cops
    {
        // Marked law-enforcement vehicle models. HashSet lookup keyed on the same
        // (VehicleHash)Model.Hash representation the rest of the mod already uses.
        private static readonly HashSet<VehicleHash> PoliceModels = new HashSet<VehicleHash>
        {
            VehicleHash.Police, VehicleHash.Police2, VehicleHash.Police3, VehicleHash.Police4,
            VehicleHash.PoliceOld1, VehicleHash.PoliceOld2, VehicleHash.PoliceT, VehicleHash.Policeb,
            VehicleHash.Sheriff, VehicleHash.Sheriff2, VehicleHash.FBI, VehicleHash.FBI2,
            VehicleHash.Riot, VehicleHash.Pranger, VehicleHash.Polmav
        };

        public static bool IsCop(Ped p)
        {
            return p != null && p.Exists() && (p.PedType == PedType.Cop || p.PedType == PedType.Swat);
        }

        // True for a marked police vehicle MODEL only (ignores who's driving it).
        public static bool IsPoliceModel(Vehicle v)
        {
            return v != null && v.Exists() && PoliceModels.Contains((VehicleHash)v.Model.Hash);
        }

        // A police vehicle by model, OR anything a cop is currently driving.
        public static bool IsPoliceVehicle(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;
            if (PoliceModels.Contains((VehicleHash)v.Model.Hash)) return true;
            return IsCop(v.Driver);
        }

        // A vehicle a GROUND cruiser can actually pursue. Excludes anything that flies,
        // floats, or rides rails: GET_VEHICLE_CLASS 14 = boats, 15 = helicopters,
        // 16 = planes, 21 = trains. (THE "the suspect was a helicopter and the cops just
        // sat there" bug -- a fleeing aircraft must never be designated as a chase target.)
        public static bool IsGroundPursuitVehicle(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;
            int cls = Function.Call<int>(Hash.GET_VEHICLE_CLASS, v);
            return cls != 14 && cls != 15 && cls != 16 && cls != 21;
        }
    }
}
