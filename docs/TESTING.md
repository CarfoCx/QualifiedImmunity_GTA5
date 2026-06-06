# Testing checklist

Single-player only. Make a save first. Use a trainer (Menyoo/Simple Trainer)
to spawn cops on demand and to reset wanted level between tests.

## Layer 1 — dispatch.meta
- [ ] Game loads to SP without an infinite-load hang (a malformed `.meta`
      usually shows as a black/infinite load → check OpenIV repacked cleanly).
- [ ] 1★: two cars instead of one, and they drive in rather than pop in behind.
- [ ] 2★: a helicopter shows up (vanilla waits for 3★).
- [ ] 3★: roadblocks + SWAT appear.
- [ ] 4–5★: heavy units, fast successive waves, slow to lose them.

## Layer 2 — personality
- [ ] Script loads (SHVDN log shows `QualifiedImmunity` with no exception).
- [ ] Press B near a cop → he insults you; repeat → he turns hostile at 0 stars.
- [ ] Aim a gun at a cop → near-instant hostility.
- [ ] When hostile, cops open fire without warning and don't flee.
- [ ] Collateral: with `EnableCivilianTargeting=true`, occasionally a cop pops a
      bystander and a taunt notification appears.
- [ ] Down a suspect near a cop → he finishes them + taunts (most of the time).
- [ ] Other cops never turn on the shooting cop (brotherhood intact).

## If something breaks
- Check `ScriptHookVDotNet.log` / `.error.log` in the game root for stack traces.
- Comment out one feature method in `OnTick` at a time to isolate.
- Revert the `mods/` dispatch.meta to your backup to rule Layer 1 in/out.
