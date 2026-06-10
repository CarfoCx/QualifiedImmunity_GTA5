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

            // Tucked off to the side (bottom right) and less prominent
            const float x = 0.850f, w = 0.140f;
            float y = 0.74f;

            // Regular cruisers are pinned to livery 0 at spawn (SpawnUnit), whose roof
            // decal reads 32 -- so the HUD callsign matches the number on the roof.
            string title = _eliteUnit == 1 ? "NOOSE UNIT"
                         : _eliteUnit == 2 ? "FIB UNIT"
                         : _eliteUnit == 3 ? "AGENCY UNIT"
                         : _undercover ? "UNMARKED" : "UNIT 32";
            Rect(x + w / 2f, y + 0.015f, w, 0.030f, 12, 28, 56, 160);
            DrawMenuText(title, x + 0.006f, y + 0.003f, 0.28f, 4, 235, 235, 235, false, true);
            y += 0.034f;

            y = DrawOfficerRow(_driver, x, y, w);
            y = DrawOfficerRow(_partner, x, y, w);

            // Cruiser body health (entity health, 0-1000).
            float vh = Math.Max(0f, Math.Min(1f, _copCar.Health / 1000f));
            Rect(x + w / 2f, y + 0.019f, w, 0.038f, 0, 0, 0, 100);
            DrawMenuText("CRUISER", x + 0.006f, y + 0.002f, 0.24f, 0, 215, 215, 215, false, true);
            DrawBar(x + 0.006f, y + 0.030f, w - 0.012f, 0.006f, vh);
        }

        private float DrawOfficerRow(Ped c, float x, float y, float w)
        {
            if (c == null || !c.Exists()) return y;
            const float rowH = 0.056f;
            Rect(x + w / 2f, y + rowH / 2f, w, rowH, 0, 0, 0, 100);

            string label = c.IsDead
                ? CopNames.For(c) + "  ~r~K.I.A."
                : CopNames.For(c) + "  ~y~Lv " + LevelOf(c);
            DrawMenuText(label, x + 0.006f, y + 0.002f, 0.24f, 0, 235, 235, 235, false, true);

            float maxHp = Math.Max(1, c.MaxHealth);
            float hp = Math.Max(0f, Math.Min(1f, c.Health / maxHp));
            DrawEcg(x + 0.006f, y + 0.035f, w - 0.012f, 0.010f, c, hp);

            // Thin XP progress strip under the heart monitor (gold).
            float xpFrac = LevelOf(c) >= MaxLevel ? 1f : (XpOf(c) % XpPerLevel) / (float)XpPerLevel;
            DrawBarColored(x + 0.006f, y + 0.050f, w - 0.012f, 0.003f, xpFrac, 235, 200, 80);

            return y + rowH + 0.004f;
        }

        // -------------------------------------------------------------------
        // Hospital-style heart monitor in place of an HP bar: a scrolling ECG
        // trace whose rate climbs and spikes shrink as the officer fades --
        // and a dead officer reads as a red flatline.
        // -------------------------------------------------------------------

        // One heartbeat sampled into a lookup strip: baseline, P bump, QRS spike, T bump.
        private static readonly float[] EcgBeat = BuildEcgBeat();

        private static float[] BuildEcgBeat()
        {
            var s = new float[48];
            s[8] = 0.10f; s[9] = 0.16f; s[10] = 0.10f;                              // P wave
            s[14] = -0.18f;                                                          // Q dip
            s[15] = 0.55f; s[16] = 1.00f; s[17] = 0.45f;                             // R spike
            s[18] = -0.30f; s[19] = -0.12f;                                          // S dip
            s[26] = 0.10f; s[27] = 0.22f; s[28] = 0.26f; s[29] = 0.22f; s[30] = 0.10f; // T wave
            return s;
        }

        private void DrawEcg(float left, float cy, float w, float halfH, Ped c, float hpFrac)
        {
            Rect(left + w / 2f, cy, w, halfH * 2f + 0.003f, 0, 0, 0, 130);   // monitor backing

            bool dead = c.IsDead;
            int r, g, b;
            if (dead) { r = 230; g = 50; b = 50; }
            else
            {
                r = (int)(200 - 140 * hpFrac);   // green when healthy...
                g = (int)(60 + 140 * hpFrac);    // ...red when critical
                b = 60;
            }

            int offset = 0;
            if (!dead)
            {
                // Heart rate climbs as health drops: ~65 bpm healthy -> ~150 bpm critical.
                int period = 400 + (int)(520 * hpFrac);
                offset = (int)((long)(Game.GameTime % period) * EcgBeat.Length / period);
            }

            const int segs = 30;
            float segW = w / segs;
            for (int i = 0; i < segs; i++)
            {
                float amp = 0f;
                if (!dead)
                {
                    int idx = (i * EcgBeat.Length * 2 / segs + offset) % EcgBeat.Length; // ~2 beats across the strip
                    amp = EcgBeat[idx] * (0.35f + 0.65f * hpFrac);   // weaker spikes as they fade
                }
                float h = Math.Max(0.0016f, Math.Abs(amp) * halfH);
                Rect(left + segW * (i + 0.5f), cy - amp * halfH * 0.5f, segW, h, r, g, b, 235);
            }
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
