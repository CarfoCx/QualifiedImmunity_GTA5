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

        // A downed officer's monitor: the flatline holds on screen while any
        // squadmate is still alive; once the whole unit is down it lingers a
        // beat and then fades away.
        private readonly Dictionary<int, DateTime> _flatlineAt = new Dictionary<int, DateTime>();
        private const double FlatlineHoldSeconds = 5.0;
        private const double FlatlineFadeSeconds = 2.0;

        private bool AnyOfficerAlive()
        {
            if (_driver != null && _driver.Exists() && !_driver.IsDead) return true;
            if (_partner != null && _partner.Exists() && !_partner.IsDead) return true;
            return false;
        }

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
            _flatlineAt.Clear();
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

            // Downed officer: the red flatline STAYS on the monitor as long as a
            // squadmate is still alive and the ride is going -- a permanent reminder
            // of the fallen. Only once the whole unit is down does the countdown
            // start: hold the flatline a beat, then fade the row out.
            float fade = 1f;
            bool dead = c.IsDead;
            if (dead)
            {
                DateTime at;
                if (AnyOfficerAlive() || !_flatlineAt.TryGetValue(c.Handle, out at))
                { at = DateTime.Now; _flatlineAt[c.Handle] = at; }
                double s = (DateTime.Now - at).TotalSeconds;
                if (s > FlatlineHoldSeconds + FlatlineFadeSeconds) return y;
                if (s > FlatlineHoldSeconds)
                    fade = 1f - (float)((s - FlatlineHoldSeconds) / FlatlineFadeSeconds);
            }

            const float rowH = 0.056f;
            Rect(x + w / 2f, y + rowH / 2f, w, rowH, 0, 0, 0, (int)(100 * fade));

            // Compact name on the left, level chip right-aligned to the box edge.
            // (The full "Sergeant Sam \"Leadspitter\" Tucker" name ran under -- and
            // pushed the Lv text past -- the right edge of the box.)
            DrawMenuText(CopNames.HudFor(c), x + 0.006f, y + 0.002f, 0.24f, 0, 235, 235, 235, false, true, (int)(255 * fade));
            if (dead)
                DrawHudTextRight("K.I.A.", x + w - 0.006f, y + 0.002f, 0.24f, 235, 60, 60, (int)(255 * fade));
            else
                DrawHudTextRight("Lv " + LevelOf(c), x + w - 0.006f, y + 0.002f, 0.24f, 235, 200, 80, 255);

            float maxHp = Math.Max(1, c.MaxHealth);
            float hp = Math.Max(0f, Math.Min(1f, c.Health / maxHp));
            DrawEcg(x + 0.006f, y + 0.035f, w - 0.012f, 0.010f, c, hp, fade);

            // Thin XP progress strip under the heart monitor (gold). Pointless on
            // a corpse, so flatline rows drop it.
            if (!dead)
            {
                float xpFrac = LevelOf(c) >= MaxLevel ? 1f : (XpOf(c) % XpPerLevel) / (float)XpPerLevel;
                DrawBarColored(x + 0.006f, y + 0.050f, w - 0.012f, 0.003f, xpFrac, 235, 200, 80);
            }

            return y + rowH + 0.004f;
        }

        // Right-justified HUD text: anchored to a right edge so a chip can never
        // spill outside its box no matter how wide the value renders.
        private void DrawHudTextRight(string text, float rightX, float y, float scale, int r, int g, int b, int alpha)
        {
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, alpha);
            Function.Call(Hash.SET_TEXT_DROP_SHADOW);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.SET_TEXT_JUSTIFICATION, 2);      // right-justify...
            Function.Call(Hash.SET_TEXT_WRAP, 0.0f, rightX);    // ...against this edge
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.0f, y, 0);
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

        private void DrawEcg(float left, float cy, float w, float halfH, Ped c, float hpFrac, float fade)
        {
            Rect(left + w / 2f, cy, w, halfH * 2f + 0.003f, 0, 0, 0, (int)(130 * fade));   // monitor backing

            bool dead = c.IsDead;
            int a = (int)(245 * fade);
            if (dead)
            {
                // FLATLINE: one unbroken bright-red line straight across the monitor.
                Rect(left + w / 2f, cy, w, 0.0022f, 235, 45, 45, a);
                return;
            }

            int r = (int)(200 - 140 * hpFrac);   // green when healthy...
            int g = (int)(60 + 140 * hpFrac);    // ...red when critical
            const int b = 60;

            // Heart rate climbs as health drops: ~65 bpm healthy -> ~150 bpm critical.
            int period = 400 + (int)(520 * hpFrac);
            float phase = (Game.GameTime % period) / (float)period;

            // A CONNECTED scrolling trace: sample the beat at each column
            // (interpolated, so the QRS spike is a clean stroke instead of stair
            // steps) and bridge every sample to the next with a vertical segment.
            // The old renderer drew disconnected center-anchored bars, which read
            // as a flickering bar chart rather than a monitor line.
            const int segs = 56;
            const float line = 0.0018f;              // trace stroke thickness
            float segW = w / segs;
            float amp = 0.45f + 0.55f * hpFrac;      // weaker spikes as the officer fades
            float prevY = cy - SampleEcg(phase) * amp * halfH;
            for (int i = 1; i <= segs; i++)
            {
                // ~2 beats across the strip, scrolling with the phase.
                float yv = cy - SampleEcg((float)i / segs * 2f + phase) * amp * halfH;
                float top = Math.Min(prevY, yv);
                float h = Math.Max(line, Math.Abs(yv - prevY));
                Rect(left + segW * (i - 0.5f), top + h / 2f, segW * 1.15f, h, r, g, b, a);
                prevY = yv;
            }
        }

        // The beat waveform at time t (in beats), linearly interpolated.
        private static float SampleEcg(float t)
        {
            float ft = ((t % 1f) + 1f) % 1f * EcgBeat.Length;
            int i0 = (int)ft % EcgBeat.Length;
            int i1 = (i0 + 1) % EcgBeat.Length;
            float fr = ft - (float)Math.Floor(ft);
            return EcgBeat[i0] * (1f - fr) + EcgBeat[i1] * fr;
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
