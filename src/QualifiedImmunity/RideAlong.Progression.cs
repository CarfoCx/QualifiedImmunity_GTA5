using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // -------------------------------------------------------------------
        // Officer XP / levels -- per ride. Shooting and kills earn XP; levels
        // sharpen aim, decision-making (combat ability), cover/flank habits and
        // survivability. RANK (and the gear that comes with it) stays exactly as
        // rolled by CopNames -- you still draw a green Officer or a grizzled
        // Captain at random; levels are how THIS officer grows on THIS shift.
        // Plus a unit HUD: name + level, HP/XP bars, and cruiser health.
        // -------------------------------------------------------------------
        private readonly Dictionary<int, int> _xp = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _lvl = new Dictionary<int, int>();
        private readonly HashSet<int> _xpCorpses = new HashSet<int>();   // kills already credited
        private DateTime _lastXpTick = DateTime.MinValue;
        private bool _showUnitHud = true;   // [RideAlong] ShowUnitHud

        private const int MaxLevel = 10;
        private const int XpPerLevel = 100;
        private const int XpPerKill = 30;
        private const int XpPerShootTick = 4;   // per second spent actually firing

        private void UnitProgressTick()
        {
            if ((DateTime.Now - _lastXpTick).TotalSeconds < 1.0) return;
            _lastXpTick = DateTime.Now;

            AwardShootingXp(_driver);
            AwardShootingXp(_partner);
            AwardKillXp();
        }

        private void AwardShootingXp(Ped c)
        {
            if (!Valid(c)) return;
            if (Function.Call<bool>(Hash.IS_PED_SHOOTING, c)) GrantXp(c, XpPerShootTick);
        }

        // Credit kills: a fresh corpse near the unit that one of OUR officers
        // damaged counts once, for the officer who did it.
        private void AwardKillXp()
        {
            if (!Valid(_copCar)) return;
            foreach (Ped p in WorldCache.GetNearbyPeds(_copCar.Position, 70f))
            {
                if (p == null || !p.Exists() || !p.IsDead) continue;
                if (_xpCorpses.Contains(p.Handle)) continue;

                Ped killer = null;
                if (Valid(_driver) && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, p, _driver, true))
                    killer = _driver;
                else if (Valid(_partner) && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, p, _partner, true))
                    killer = _partner;
                if (killer == null) continue;

                _xpCorpses.Add(p.Handle);
                GrantXp(killer, XpPerKill);
            }
            if (_xpCorpses.Count > 192) _xpCorpses.Clear();   // cheap reset; re-credit is rare
        }

        private void GrantXp(Ped c, int amount)
        {
            int h = c.Handle;
            int xp;
            _xp.TryGetValue(h, out xp);
            xp += amount;
            _xp[h] = xp;

            int oldLvl;
            if (!_lvl.TryGetValue(h, out oldLvl)) oldLvl = 1;
            int newLvl = Math.Min(MaxLevel, 1 + xp / XpPerLevel);
            if (newLvl > oldLvl)
            {
                _lvl[h] = newLvl;
                ApplyLevelPerks(c, newLvl);
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "RANK_UP", "HUD_AWARDS_SOUNDSET", true);
                Notify("~g~LEVEL UP!~w~ " + CopNames.For(c) + " hit ~y~Level " + newLvl + "~w~ (+aim, +grit, +ego).");
            }
            else if (!_lvl.ContainsKey(h)) _lvl[h] = oldLvl;
        }

        // Levels sharpen the officer WITHOUT touching the rolled rank/equipment:
        // tighter aim on top of the rank's base, professional decision-making,
        // better cover/strafe/flank habits, and a survival patch-up per level.
        private void ApplyLevelPerks(Ped c, int lvl)
        {
            if (!Valid(c)) return;
            int acc = Math.Min(100, CopNames.BaseAccuracy(c) + (lvl - 1) * 4);
            Function.Call(Hash.SET_PED_ACCURACY, c, acc);
            if (lvl >= 2) Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanUseCover, true);
            if (lvl >= 3) Function.Call(Hash.SET_PED_COMBAT_ABILITY, c, 2);      // professional
            if (lvl >= 4) Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_UseDynamicStrafe, true);
            if (lvl >= 5) Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanFlank, true);
            int armour = Function.Call<int>(Hash.GET_PED_ARMOUR, c);
            Function.Call(Hash.SET_PED_ARMOUR, c, Math.Min(100, armour + 10));
            Function.Call(Hash.SET_ENTITY_HEALTH, c, c.MaxHealth);
        }

        private int LevelOf(Ped c)
        {
            if (!Valid(c)) return 1;
            int l;
            return _lvl.TryGetValue(c.Handle, out l) ? l : 1;
        }

        private int XpOf(Ped c)
        {
            if (!Valid(c)) return 0;
            int x;
            return _xp.TryGetValue(c.Handle, out x) ? x : 0;
        }

        private void ResetProgression()
        {
            _xp.Clear();
            _lvl.Clear();
            _xpCorpses.Clear();
        }

        // -------------------------------------------------------------------
        // Unit HUD (right side): each officer's name/level with HP + XP bars,
        // and the cruiser's health. Hidden while the heli feed owns the screen.
        // -------------------------------------------------------------------
        private void DrawUnitHud()
        {
            if (!_showUnitHud || _phase == Phase.Idle) return;
            if (_newsCam != null) return;
            if (!Valid(_copCar)) return;

            const float x = 0.800f, w = 0.180f;
            float y = 0.50f;

            string title = _eliteUnit == 1 ? "NOOSE UNIT"
                         : _eliteUnit == 2 ? "FIB UNIT"
                         : _eliteUnit == 3 ? "AGENCY UNIT" : "UNIT 23";
            Rect(x + w / 2f, y + 0.014f, w, 0.028f, 12, 28, 56, 220);
            DrawMenuText(title, x + 0.006f, y + 0.002f, 0.28f, 4, 235, 235, 235, false);
            y += 0.032f;

            y = DrawOfficerRow(_driver, x, y, w);
            y = DrawOfficerRow(_partner, x, y, w);

            // Cruiser body health (entity health, 0-1000).
            float vh = Math.Max(0f, Math.Min(1f, _copCar.Health / 1000f));
            Rect(x + w / 2f, y + 0.019f, w, 0.038f, 0, 0, 0, 150);
            DrawMenuText("CRUISER", x + 0.006f, y + 0.001f, 0.24f, 0, 215, 215, 215, false);
            DrawBar(x + 0.006f, y + 0.027f, w - 0.012f, 0.007f, vh);
        }

        private float DrawOfficerRow(Ped c, float x, float y, float w)
        {
            if (c == null || !c.Exists()) return y;
            const float rowH = 0.052f;
            Rect(x + w / 2f, y + rowH / 2f, w, rowH, 0, 0, 0, 150);

            string label = c.IsDead
                ? CopNames.For(c) + "  ~r~K.I.A."
                : CopNames.For(c) + "  ~y~Lv " + LevelOf(c);
            DrawMenuText(label, x + 0.006f, y + 0.001f, 0.24f, 0, 235, 235, 235, false);

            float maxHp = Math.Max(1, c.MaxHealth);
            float hp = Math.Max(0f, Math.Min(1f, c.Health / maxHp));
            DrawBar(x + 0.006f, y + 0.030f, w - 0.012f, 0.007f, hp);

            // Thin XP progress strip under the HP bar (gold).
            float xpFrac = LevelOf(c) >= MaxLevel ? 1f : (XpOf(c) % XpPerLevel) / (float)XpPerLevel;
            DrawBarColored(x + 0.006f, y + 0.041f, w - 0.012f, 0.004f, xpFrac, 235, 200, 80);

            return y + rowH + 0.004f;
        }

        // Left-anchored bar: dark backing + green-to-red fill by fraction.
        private static void DrawBar(float left, float cy, float w, float h, float frac)
        {
            int r = (int)(200 - 140 * frac);   // red when empty...
            int g = (int)(60 + 140 * frac);    // ...green when full
            DrawBarColored(left, cy, w, h, frac, r, g, 60);
        }

        private static void DrawBarColored(float left, float cy, float w, float h, float frac, int r, int g, int b)
        {
            if (frac < 0f) frac = 0f;
            if (frac > 1f) frac = 1f;
            Rect(left + w / 2f, cy, w, h, 25, 25, 25, 190);
            if (frac > 0.01f)
                Rect(left + (w * frac) / 2f, cy, w * frac, h, r, g, b, 235);
        }
    }
}
