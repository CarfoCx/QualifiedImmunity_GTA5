# Layer 2 — Gang Personality (C# / SHVDNE)

## Build

1. Install **Script Hook V .NET Enhanced (SHVDNE)** into your game.
2. Copy its `ScriptHookVDotNet3.dll` into `src/QualifiedImmunity/libs/`
   (so the `.csproj` reference resolves).
3. From this folder:
   ```
   dotnet build -c Release
   ```
4. Copy `bin/Release/net48/QualifiedImmunity.dll` **and**
   `QualifiedImmunity.ini` into your game's `scripts/` folder.

## Use

- Launch single-player. The script auto-loads.
- Press **B** to "flip 'em off" (provoke nearby cops). Rebind in the source
  (`_provokeKey`) or wire it to a middle-finger gesture mod via
  `ProvokeNearbyCops`.
- Honking near cops, aiming at them, or crowding them also builds their grudge.
- Edit `QualifiedImmunity.ini` to dial the chaos up or down; reload with the
  SHVDN reload key (default Insert).

## Status / known rough edges (need in-game verification)

I can't run GTA V here, so these are the spots to confirm/tune on your machine:

- **Combat attribute indices** (`5`, `46`, `1`) and the firing-pattern hash are
  build-dependent — verify against the SHVDNE native DB.
- **Surrender detection** is approximate (ragdoll/stunned heuristic). GTA peds
  don't truly "surrender" without a behavior mod; we fake it.
- **"Flip off" has no vanilla control** — hence the hotkey + provocation
  heuristics. A dedicated gesture mod would make it feel native.
- Tune `_annoyanceThreshold` and decay so cops don't go feral instantly.
