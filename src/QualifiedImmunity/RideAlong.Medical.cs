using System;
using GTA;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // -------------------------------------------------------------------
        // Field medicine -- tourniquet a wounded officer (PC + controller)
        // -------------------------------------------------------------------
        private void TourniquetTick(Ped player)
        {
            if (!_enableTourniquet) return;
            if (player == null || !player.Exists() || player.IsInVehicle()) return; // kneel beside them on foot

            Ped patient = FindInjuredFriendlyCop(player, 3.5f);
            if (patient == null) return;

            // ~INPUT_CONTEXT~ auto-renders the right glyph: the "E" key on PC, or the
            // matching face button on a controller -- so the same prompt fits both.
            ShowHelp("Press ~INPUT_CONTEXT~ to apply a ~b~tourniquet~w~ and stabilize the officer.");

            if (Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Context)
                && (DateTime.Now - _lastTourniquet).TotalSeconds > 1.0)
            {
                _lastTourniquet = DateTime.Now;
                ApplyTourniquet(player, patient);
            }
        }

        // Any wounded-but-alive officer near the player -- your own unit OR an
        // ambient cop who's down and bleeding out. (Not just ride-along officers.)
        private Ped FindInjuredFriendlyCop(Ped player, float radius)
        {
            Ped best = null;
            float bestD = radius;
            foreach (Ped c in WorldCache.GetNearbyPeds(player.Position, radius))
            {
                if (c == null || !c.Exists() || c.IsDead) continue;
                if (!IsCopPed(c)) continue;                 // only patch up the police
                if (!c.IsInjured) continue;                 // wounded but still alive -> savable
                float d = c.Position.DistanceTo(player.Position);
                if (d <= bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private void ApplyTourniquet(Ped player, Ped cop)
        {
            // Brief first-aid gesture; guarded so a missing anim never breaks anything.
            try
            {
                const string dict = "amb@medic@standing@kneel@base";
                Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                Function.Call(Hash.TASK_PLAY_ANIM, player, dict, "base", 8f, -8f, 1200, 48, 0f, false, false, false);
            }
            catch { /* cosmetic only */ }

            int target = cop.MaxHealth > 100 ? cop.MaxHealth : 200;
            Function.Call(Hash.SET_ENTITY_HEALTH, cop, target);
            Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, cop);
            Function.Call(Hash.CLEAR_PED_TASKS, cop);            // get them off the ground and back up
            CopBark(cop, "GENERIC_THANKS");
            Notify("~g~Tourniquet applied.~w~ " + CopNames.For(cop) + " is back in the fight.");
        }
    }
}
