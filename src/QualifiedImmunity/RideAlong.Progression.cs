using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // -------------------------------------------------------------------
        // Officer XP / RANKS -- per ride. Shooting and kills earn XP; every level
        // is a PROMOTION up the real police ladder: everyone starts a rookie
        // Officer (Lv 1) and climbs Corporal, Sergeant, Lieutenant, Captain ... to
        // SHERIFF (Lv 10). Each rank sharpens accuracy, ego (combat ability),
        // tactics (cover/strafe/flank habits) and ARMAMENTS (a heavier primary),
        // and the officer's shown title updates to match -- but HP never changes
        // (rank is competence + firepower, not survivability). The rank ladder and
        // its stats/gear live in CopNames; the tactics layer is ApplyRankTactics.
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

        // K.I.A. roster for the CURRENT patrol. We capture a fallen officer's name the
        // instant they die, so their flatline row stays on the monitor for the rest of the
        // ride even after the body despawns (the unit drives off into the next pursuit and
        // the corpse is left behind -- which used to make the row vanish). Cleared when the
        // ride ends, so it always reflects THIS patrol's losses.
        private sealed class KiaRec { public int Handle; public string Hud; }
        private readonly List<KiaRec> _kia = new List<KiaRec>();

        private bool AnyOfficerAlive()
        {
            foreach (Ped c in UnitOfficers())
                if (c != null && c.Exists() && !c.IsDead) return true;
            return false;
        }

        // One level per rank in the ladder, so Level 1 = Officer ... top level = Sheriff.
        private static readonly int MaxLevel = CopNames.MaxRankLevel;
        private const int XpPerLevel = 100;
        private const int XpPerKill = 30;
        private const int XpPerShootTick = 4;   // per second spent actually firing

        private void UnitProgressTick()
        {
            if ((DateTime.Now - _lastXpTick).TotalSeconds < 1.0) return;
            _lastXpTick = DateTime.Now;

            foreach (Ped c in UnitOfficers()) AwardShootingXp(c);
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
                foreach (Ped c in UnitOfficers())
                {
                    if (!Valid(c)) continue;
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, p, c, true)) { killer = c; break; }
                }
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
                // A level-up IS a promotion: it advances the officer's RANK (Officer ->
                // Corporal -> ... -> Sheriff), which sharpens accuracy, ego, and armament,
                // and re-titles them. Tactics (cover/strafe/flank habits) are layered on
                // here. None of it touches HP -- rank never buys raw survivability.
                bool promoted = CopNames.PromoteTo(c, newLvl - 1);
                ApplyRankTactics(c, newLvl);
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "RANK_UP", "HUD_AWARDS_SOUNDSET", true);
                if (promoted)
                    Notify("~g~PROMOTION!~w~ " + CopNames.HudFor(c) + " is now a ~y~" + CopNames.TitleOf(c) + "~w~ (Lv " + newLvl + ").");
                else
                    Notify("~g~LEVEL UP!~w~ " + CopNames.HudFor(c) + " reached ~y~Level " + newLvl + "~w~ (+aim, +ego, +arms).");
            }
            else if (!_lvl.ContainsKey(h)) _lvl[h] = oldLvl;
        }

        // Start an officer at a MINIMUM rank (and matching level), so a higher-value unit
        // fields higher-ranked officers from the outset instead of rookies. Only ever raises
        // -- a persistent crew that already out-ranks the floor keeps their earned standing.
        // Keeps the HUD "Lv" chip in step with the rank title (Lv 3 == Sergeant, etc.).
        private void SetUnitRankFloor(Ped c, int floorRankIdx)
        {
            if (!Valid(c) || floorRankIdx <= 0) return;
            int h = c.Handle;
            int floorLvl = Math.Min(MaxLevel, floorRankIdx + 1);   // rank index N -> Level N+1
            int curLvl; if (!_lvl.TryGetValue(h, out curLvl)) curLvl = 1;
            if (curLvl >= floorLvl) return;                        // already at/above the floor

            _lvl[h] = floorLvl;
            int floorXp = floorRankIdx * XpPerLevel;
            int curXp; _xp.TryGetValue(h, out curXp);
            if (curXp < floorXp) _xp[h] = floorXp;

            CopNames.PromoteTo(c, floorRankIdx);
            ApplyRankTactics(c, floorLvl);
        }

        // The "tactics" half of a promotion: better habits in a fight (the accuracy,
        // ego/combat-ability and armament half lives in CopNames.PromoteTo). Strictly
        // no HP/armour here -- ranking up never makes an officer tougher to kill.
        private void ApplyRankTactics(Ped c, int lvl)
        {
            if (!Valid(c)) return;
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_AlwaysFight, true);
            if (lvl >= 2) Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanUseCover, true);
            if (lvl >= 3) Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, c, 2);   // advance, don't cower
            if (lvl >= 4) Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_UseDynamicStrafe, true);
            if (lvl >= 6) Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanFlank, true);
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
            _kia.Clear();
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

            // Tucked off to the side (bottom right) and less prominent. The panel's
            // BOTTOM edge is the anchor: elite calls (NOOSE van, FIB Granger -- incl.
            // the operators hanging off its sides) run up to seven officers deep, so
            // the list grows UPWARD instead of running off the bottom of the screen.
            // (With the usual two-man car this lands at the same y = 0.74 as before.)
            RecordKia();   // capture any newly-fallen officer's name before the body can despawn

            const float x = 0.850f, w = 0.140f;
            int rows = 0;
            foreach (Ped c in UnitOfficers()) if (OfficerRowVisible(c)) rows++;
            foreach (KiaRec rec in _kia) if (KiaRowVisible(rec)) rows++;
            float y = 0.932f - 0.038f - rows * 0.060f - 0.034f;

            // Banner = the agency of whatever vehicle actually showed up (set at spawn from
            // the unit's type/model), so a Sheriff car reads "SHERIFF DEPT" and an unmarked
            // sting "UNMARKED UNIT" -- never a blanket "LSPD PATROL". Labelling by type (not
            // a roof number) also can't disagree with the livery decal we can't read back.
            Rect(x + w / 2f, y + 0.015f, w, 0.030f, 12, 28, 56, 160);
            DrawMenuText(_unitTitle, x + 0.006f, y + 0.003f, 0.28f, 4, 235, 235, 235, false, true);
            y += 0.034f;

            foreach (Ped c in UnitOfficers())
                y = DrawOfficerRow(c, x, y, w);

            // Persistent K.I.A. rows for fallen officers whose body has despawned -- drawn
            // from the captured record so the flatline survives the next pursuit.
            foreach (KiaRec rec in _kia)
                if (!IsHandleLiveInUnit(rec.Handle)) y = DrawKiaRow(rec, x, y, w);

            // Cruiser body health (entity health, 0-1000).
            float vh = Math.Max(0f, Math.Min(1f, _copCar.Health / 1000f));
            Rect(x + w / 2f, y + 0.019f, w, 0.038f, 0, 0, 0, 100);
            DrawMenuText("CRUISER", x + 0.006f, y + 0.002f, 0.24f, 0, 215, 215, 215, false, true);
            DrawBar(x + 0.006f, y + 0.030f, w - 0.012f, 0.006f, vh);
        }

        // Mirrors DrawOfficerRow's early-outs so the bottom-anchored panel can
        // count its rows before drawing: alive rows always show; flatline rows
        // show until their hold+fade window has fully elapsed.
        private bool OfficerRowVisible(Ped c)
        {
            if (c == null || !c.Exists()) return false;
            if (!c.IsDead) return true;
            DateTime at;
            if (AnyOfficerAlive() || !_flatlineAt.TryGetValue(c.Handle, out at)) return true;
            return (DateTime.Now - at).TotalSeconds <= FlatlineHoldSeconds + FlatlineFadeSeconds;
        }

        // Capture the name of any officer that has just died, while their ped still exists,
        // so the K.I.A. flatline can be drawn from the record after the body despawns.
        private void RecordKia()
        {
            foreach (Ped c in UnitOfficers())
            {
                if (c == null || !c.Exists() || !c.IsDead) continue;
                int h = c.Handle;
                bool known = false;
                foreach (KiaRec r in _kia) if (r.Handle == h) { known = true; break; }
                if (!known) _kia.Add(new KiaRec { Handle = h, Hud = CopNames.HudFor(c) });
            }
        }

        private bool IsHandleLiveInUnit(int h)
        {
            foreach (Ped c in UnitOfficers())
                if (c != null && c.Exists() && c.Handle == h) return true;
            return false;
        }

        // A KIA record draws as its own row only once its ped is gone; same hold+fade window
        // as a dead-in-slot officer, so it persists while the patrol continues.
        private bool KiaRowVisible(KiaRec rec)
        {
            if (IsHandleLiveInUnit(rec.Handle)) return false;   // still drawn by its slot row
            DateTime at;
            if (AnyOfficerAlive() || !_flatlineAt.TryGetValue(rec.Handle, out at)) return true;
            return (DateTime.Now - at).TotalSeconds <= FlatlineHoldSeconds + FlatlineFadeSeconds;
        }

        private float DrawKiaRow(KiaRec rec, float x, float y, float w)
        {
            DateTime at;
            if (AnyOfficerAlive() || !_flatlineAt.TryGetValue(rec.Handle, out at))
            { at = DateTime.Now; _flatlineAt[rec.Handle] = at; }
            double s = (DateTime.Now - at).TotalSeconds;
            if (s > FlatlineHoldSeconds + FlatlineFadeSeconds) return y;
            float fade = 1f;
            if (s > FlatlineHoldSeconds) fade = 1f - (float)((s - FlatlineHoldSeconds) / FlatlineFadeSeconds);

            const float rowH = 0.056f;
            Rect(x + w / 2f, y + rowH / 2f, w, rowH, 0, 0, 0, (int)(100 * fade));
            DrawMenuText(rec.Hud, x + 0.006f, y + 0.002f, 0.24f, 0, 235, 235, 235, false, true, (int)(255 * fade));
            DrawHudTextRight("K.I.A.", x + w - 0.006f, y + 0.002f, 0.24f, 235, 60, 60, (int)(255 * fade));
            DrawFlatline(x + 0.006f, y + 0.035f, w - 0.012f, 0.010f, fade);
            return y + rowH + 0.004f;
        }

        // The flatline ECG for a fallen officer whose ped is gone (no live ped to read).
        private void DrawFlatline(float left, float cy, float w, float halfH, float fade)
        {
            Rect(left + w / 2f, cy, w, halfH * 2f + 0.003f, 0, 0, 0, (int)(130 * fade));  // monitor backing
            Rect(left + w / 2f, cy, w, 0.0022f, 235, 45, 45, (int)(245 * fade));           // flatline
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
