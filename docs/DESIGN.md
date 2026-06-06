# Qualified Immunity — Behavior Spec

The theme: **a gang that happens to have badges.** Maximally competent at
violence, minimally interested in procedure, and they think it's all hilarious.

## Layer 1 — Dispatch (tactical competence)

These are *capabilities*, tuned in `data/dispatch.meta`:

- **Fast response.** Shorter spawn delays so they arrive quickly and in force.
- **Heavier numbers.** More peds/vehicles per wanted level than vanilla.
- **Coordinated escalation.** Helicopters earlier; NOOSE/SWAT and FIB at
  lower stars; army at WL5.
- **Roadblocks & PIT.** Road blocks enabled earlier and more aggressively.
- **They commit.** Longer parole/evasion times so they don't give up easily,
  and they search further.

## Layer 2 — Personality (the gang behavior)

These are *attitudes*, scripted in C#:

1. **Grudge system.** Flipping off a cop (or honking/aiming near one, or just
   getting too close) raises a hidden "annoyance" meter on nearby officers.
   Past a threshold, they go hostile *to you specifically* — even at 0 stars.

2. **Trigger-happy.** When hostile, cops skip warnings and open fire. Combat
   attributes set for max aggression, always-fight, no-flee, accurate.

3. **Collateral indifference.** Cops will fire even when civilians are in the
   line of fire, and occasionally pick off a nearby civilian who "looked at
   them wrong" (random low chance when annoyed). This is the dark-comedy core.

4. **Taunts & bragging.** After a kill, cops play taunt/laugh speech lines and
   we float a comedic subtitle ("Resisting arrest, your honor." / "Qualified
   immunity, baby!"). Pulled from a configurable line pool. Every officer is
   tagged with a stable **rank + ridiculous nicknamed name** in
   `First "Nickname" Last` form (Officer Mike "BigBalls" Johnson, Sergeant
   Sam "Leadspitter" Tucker, Captain Wanda "Mag-Dump" Slaughter…) — randomly
   composed from first-name / nickname / surname pools so the squad never
   repeats — that fronts their radio chatter, taunts and bragging; they never
   read as a generic "Unit." Rank is random and weighted
   (most are rank-and-file, brass is rare), and it's not just cosmetic: **the
   higher the rank, the more HP, the better accuracy/fire-rate, and the deadlier
   the weapon**. Each officer rolls a specific gun from their rank's pool, so a
   squad shows variety — sidearms and shotguns at the bottom, SMGs and rifles in
   the middle, marksman/heavy weapons at the top — plus an assorted sidearm. When
   they catch a suspect they **bail out and open fire immediately**.

5. **Execute, don't arrest.** Downed/surrendering suspects get finished off
   with a one-liner instead of cuffed.

6. **Protected by the badge.** Other cops never turn on a cop for these acts;
   the relationship/aggression rules keep the "brotherhood" intact.

7. **Don't touch the cruiser.** If an NPC's vehicle hits a cop car — a careless
   fender-bender or a deliberate ram, no difference — every officer in range
   treats it as an assault on police and hunts the driver down until they're
   dead, gone, or the grudge cools off. (Player bumps still go through the
   grudge meter in #1, so this is NPC-on-cop only.)

8. **Competent wheelmen.** Officers drive at full skill but with low aggression,
   so even in a chase they thread around walls, props, peds, surrounding traffic
   and — crucially — each other's cruisers rather than ramming through them, and
   they use navmesh pathing instead of plowing into scenery. Pursuit follow
   distance is kept wide enough that they're not constantly bulling through cars
   to close the gap.
   *Exception:* the **ride-along host** drives off-policy on purpose — it never
   rides in formation, won't do coordinated blocking/PIT maneuvers, and ignores
   traffic lights — but it keeps full driver skill, steer-around and navmesh
   pathing, so it threads traffic cleanly rather than hitting cars for no reason.

9. **Crossfire discipline.** In a firefight officers flank, strafe and advance
   so they engage from spread angles rather than stacking up in one another's
   line of fire — they keep their guns off the brotherhood. (Civilians caught in
   the gap are still fair game; that cruelty in #3 is deliberate.)

10. **Hands off the officers.** If an NPC shoots, beats, or runs down a cop, every
    badge in the area instantly treats that NPC as hostile and converges on them
    until they're dead, gone, or the grudge cools — the same threat system as #7,
    extended from "rammed a cruiser" to "hurt an officer." Player attacks still
    run through the grudge meter in #1, so this is NPC-on-cop only.

11. **The dead stay put.** No body despawns on its own. Every body that's going to
    end up a corpse — dead, or fatally injured and bleeding out — is pinned where
    it fell, and each gets an ambulance dispatched to pick it up: a paramedic walks
    over, loads it, and the meat wagon hauls it off. Peds merely knocked down who
    will get back up are left alone. A safety cap releases the oldest un-recovered
    bodies if corpses ever pile up past the ped budget.

12. **Witnessed crimes.** If an NPC vehicle runs a pedestrian down in view of the
    police, the cops treat the driver as hostile and pursue/kill them — a
    hit-and-run never goes unpunished. (Lights up nearby officers' combat, which a
    ride-along unit will then join.)

### Ride-along extras

- **Connected to local PD (priority).** While patrolling, the ride-along unit
  sweeps a wide radius for other officers who are already in a fight and, as its
  *first priority*, rolls in to back them up — staying locked onto that engagement
  (re-targeting each hostile in turn) until the whole scene goes quiet. Then it
  holds for a 5–10s "confirming the threat is clear" beat before moving to the
  next engagement. Only when there's no local engagement to join does it start a
  pursuit of its own.
- **No spawned suspects.** Pursuits never conjure a suspect out of thin air. The
  unit **designates an existing nearby vehicle** — picks a real ambient car with
  an NPC driver and flags its occupants as suspects (hostile to police). If
  there's no traffic to designate, no pursuit starts; the unit just keeps cruising
  and looking for police to back up.
- **Suspect-driven escalation.** A designated suspect rolls a *threat level*.
  Innocents panic and flee; armed ones scale from a basic sidearm up to an
  armoured, rifle-toting crew — and at the top end a *second existing vehicle* is
  roped in as a backup crew. The tougher the suspect, the harder the coordinated
  response escalates: **NOOSE/SWAT** vans of armoured carbine operators and, at the
  top end, a **police helicopter** with a door gunner (both spawned off-camera and
  driven/flown in). Lighter stops stay low-key.
- **Welcome aboard.** When the player climbs into the cruiser, the driver throws
  out a random welcome line with a funny hint at the chaos to come ("Buckle up.
  Statistically, someone's getting tased today. Might be you.").
- **DMR loadout.** The ride-along officers carry a designated-marksman rifle as
  their primary (instead of spray-and-pray automatics) with a pistol secondary;
  the exact rifle and their HP/accuracy scale with rank (see #4).
- **Spaced-out radio chatter.** Officers crack wise on the radio during a
  pursuit, but only every 12–20s (randomized) from a ~20-line pool, so it stays
  funny instead of turning into a wall of text. Each line is fronted by the
  officer's rank + name (see #4) and backed by a TTS clip from `gen_audio.ps1`.
- **No pop-in.** Requested units and pursuit backup spawn on a road *off-camera*
  and 55–170m out, then drive into the scene — they never materialize in view.
- **Tourniquets.** Walk up to any wounded (not dead) officer on foot — your own
  unit *or* an ambient cop who's down and bleeding out — and a prompt appears;
  press it to apply a tourniquet and revive them. The prompt uses
  `~INPUT_CONTEXT~`, so it shows the right key on PC (E) or button on a
  controller, and the same input works on both.
- **You board on foot.** Calling a unit (F9) drives a cruiser to you and parks
  it — an "arrived" prompt appears and you walk up and press the enter-vehicle
  button yourself to start the ride-along; it no longer auto-walks you in. If the
  cruiser can't reach you (wedged or no path), you're told it couldn't get there
  and to try again elsewhere, and the ride-along cancels. While riding, the police
  ignore the player, so shooting a suspect to defend the unit never earns a wanted
  level.
- **PIT maneuvers.** Closing on a fleeing suspect, the officer "asks permission"
  to PIT on the radio, laughs, and rams them off the road anyway without it. If a
  bystander catches a stray, another officer frets about the body count and the
  lead jokes that the collateral damage was "worth it."

### Tunables (config file, Layer 2)
- `AnnoyanceThreshold`, `AnnoyanceDecayRate`
- `CivilianCollateralChance`
- `ExecuteSurrenderingChance`
- `TauntCooldownSeconds`
- `EnableCivilianTargeting` (master toggle for the spiciest behavior)
- `EnableVehicleAssaultResponse`, `EnableOfficerAssaultResponse`, `ResponseRadius`,
  `ThreatMemorySeconds` (the assault-on-police response, `[Assault]` section)
- `KeepBodiesUntilAmbulance`, `MaxAmbulances`, `MaxPersistentBodies`
  (the "dead stay put" body-recovery system, `[Bodies]` section)
- `EnableTourniquet` (ride-along field medicine, `[RideAlong]` section)

All comedic, all single-player, all satire — same register as base-game GTA V.
