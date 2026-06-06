# Qualified Immunity

A single-player GTA V (Legacy + Enhanced) mod that turns the LSPD/LSSD into a
hyper-competent, hilariously corrupt gang with badges. They respond fast and
coordinated (roadblocks, helicopters, NOOSE/FIB escalation), but they're
trigger-happy, hold grudges, gun down civilians who annoy them, and crack jokes
and brag about it afterward.

> **SINGLE-PLAYER ONLY.** Never load this in GTA Online. You must disable
> BattlEye for SP modding, which locks you out of Online until you re-enable it.
> Mixing the two risks a ban.

---

## What's in the box

The mod is built in two layers:

| Layer | File(s) | What it does | Needs coding |
|-------|---------|--------------|--------------|
| 1. Dispatch overhaul | `data/dispatch.meta` | *Tactical competence* — fast aggressive response, more units, roadblocks, choppers, faster escalation | No (XML) |
| 2. Gang personality | `src/QualifiedImmunity/` | *The fun part* — react to being flipped off, shoot annoying civilians, taunt/brag dialogue, execute-instead-of-arrest | C# (SHVDNE) |

You can run Layer 1 by itself today. Layer 2 is the C# script and is where the
"gang with a badge" behavior lives, since `.meta` files can't read player
gestures or trigger dialogue.

---

## Requirements

Install these for **Enhanced edition** (all current as of early 2026):

1. **Script Hook V** — got a compatibility refresh on 2026-02-21 restoring
   single-player modding for Legacy *and* Enhanced. <http://www.dev-c.com/gtav/scripthookv/>
2. **Script Hook V .NET Enhanced (SHVDNE)** — runs .NET scripts on Enhanced.
   <https://github.com/Chiheb-Bacha/scripthookvdotnetenhanced>
3. **OpenIV** — to extract/repack the `.meta` files. <https://openiv.com>
4. A **mods folder** setup so you never edit original game files (OpenIV's
   "mods" folder feature).

---

## Layer 1 install (dispatch overhaul)

Because `dispatch.meta` varies between game versions, **do not blind-drop my
file**. Merge the tuned values into your own extracted copy:

1. Open OpenIV, enable Edit mode, and make a `mods` folder (Tools → "Create/Run
   a mods folder").
2. Navigate to `update\update.rpf\common\data\ai\dispatch.meta`.
3. Right-click → Copy to "mods" folder, then export the copy to disk as a
   backup.
4. Open `data/dispatch.meta` from this project — it's annotated with exactly
   which sections/values to change and why. Apply those changes to your
   extracted file (or use it as a reference diff).
5. Drag the edited file back into the `mods` folder copy.
6. Launch single-player and test. See `docs/TESTING.md`.

---

## Layer 2 install (gang personality script)

See `src/QualifiedImmunity/README.md` for build + install. In short: build the
C# project, drop the resulting `.dll` into your `scripts/` folder, launch SP.

---

## Project layout

```
QualifiedImmunity-Mod/
├─ README.md              ← you are here
├─ data/
│  └─ dispatch.meta       ← Layer 1 tuning (annotated)
├─ src/
│  └─ QualifiedImmunity/  ← Layer 2 C# script (SHVDNE)
└─ docs/
   ├─ DESIGN.md           ← the full behavior spec ("the bit")
   └─ TESTING.md          ← how to verify each feature in-game
```
