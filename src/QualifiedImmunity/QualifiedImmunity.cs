using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // Layer 2 — "a gang that happens to have badges."
    // Tactical competence comes from dispatch.meta (Layer 1). This script adds
    // the attitude: grudges, trigger-happy combat, collateral indifference,
    // taunts/bragging, and executing the surrendering. Single-player satire.
    //
    // Targets .NET Framework 4.8 (see QualifiedImmunity.csproj). Build with
    // `dotnet build -c Release` against the SHVDNE reference assemblies in libs\.
    public class QualifiedImmunity : Script
    {
        // ---- Config (loaded from QualifiedImmunity.ini) --------------------
        private float _annoyanceThreshold = 100f;
        private float _annoyanceDecayPerSec = 8f;
        private float _provokeProximityHit = 35f;        // honk/bump/aim near a cop
        private float _civilianCollateralChance = 0.06f; // per annoyed cop / decision tick
        private float _executeSurrenderingChance = 0.85f;
        private float _tauntCooldownSeconds = 4f;
        private bool _enableCivilianTargeting = true;
        private Keys _provokeKey = Keys.B;               // manual "flip 'em off" key

        // Vehicular assault on police: an NPC bumping/ramming a cruiser is treated
        // as a hostile act, and the badges hunt the offending driver down.
        private bool _enableVehicleAssaultResponse = true;
        private bool _enableOfficerAssaultResponse = true;
        private bool _enableWitnessedCrimeResponse = true; // cops pursue an NPC who runs someone down
        private float _vehicleAssaultRadius = 60f;       // cops within this of the incident respond
        private float _copCarScanRadius = 80f;           // how far from the player we watch cops/cruisers
        private float _vehicleThreatMemorySeconds = 30f; // how long they keep hunting the offender
        private DateTime _lastWitnessScan = DateTime.MinValue;

        // ---- State ---------------------------------------------------------
        // Hidden annoyance meter, keyed by ped handle. The player earns it.
        private readonly Dictionary<int, float> _annoyance = new Dictionary<int, float>();
        private DateTime _lastTaunt = DateTime.MinValue;
        private DateTime _lastUnrest = DateTime.MinValue;
        private readonly HashSet<int> _armedCivs = new HashSet<int>();
        // NPCs who assaulted police (rammed a cruiser or hit an officer) -> when
        // they were flagged. Every badge in the area hunts them until it expires.
        private readonly Dictionary<int, DateTime> _threats = new Dictionary<int, DateTime>();
        private DateTime _lastEnforce = DateTime.MinValue;  // throttle for re-siccing threats

        // ---- City Settlement Fund (satire ticker) --------------------------
        // Nobody is ever disciplined; the city just cuts a check. Every civilian
        // the police kill adds a payout to a running shift total -- the mod's
        // whole thesis in one number.
        private bool _enableSettlementFund = true;
        private long _settlementTotal;
        private int _settlementBodies;
        private readonly HashSet<int> _settledBodies = new HashSet<int>();
        private DateTime _lastSettlementScan = DateTime.MinValue;
        private DateTime _lastSettlementTicker = DateTime.MinValue;

        // ---- Internal Affairs gag ------------------------------------------
        // A few seconds after an execution, IA announces the investigation is
        // already complete. Queued so the punchline lands on a comedic delay.
        private DateTime _iaVerdictDue = DateTime.MinValue;
        private DateTime _lastIaVerdict = DateTime.MinValue;
        private readonly Random _rng = new Random();
        // Cops we've already configured. The combat/driver natives are idempotent
        // and persist on the ped, so we apply them once instead of every tick on
        // every nearby officer (which got expensive in crowded firefights).
        private readonly HashSet<int> _combatProfiled = new HashSet<int>();
        private readonly HashSet<int> _driverProfiled = new HashSet<int>();

        private static readonly string[] Taunts =
        {
            "Resisting arrest, your honor.",
            "Qualified immunity, baby!",
            "He was reaching for something. Probably.",
            "Paperwork's gonna LOVE this one.",
            "Shouldn'ta flipped me off.",
            "Brotherhood protects its own.",
            "I felt threatened. From over there.",
            "Administrative leave, here I come!"
        };

        // Keep in sync (order + count) with $settlement in tools/gen_audio.ps1.
        private static readonly string[] SettlementQuips =
        {
            "Your taxes at work!",
            "Still cheaper than retraining!",
            "The officer has been placed on PAID leave.",
            "No wrongdoing was found. It never is.",
            "The city disputes the family's account.",
            "Budget line item: 'Oopsies'.",
            "Thoughts, prayers, and a non-disclosure agreement.",
            "Don't worry, the pension is safe."
        };

        // Keep in sync (order + count) with $ia in tools/gen_audio.ps1.
        private static readonly string[] IaVerdicts =
        {
            "Investigation complete. The officer acted within policy. (Elapsed: 6 seconds.)",
            "We have reviewed the footage. There is no footage.",
            "After carefully reading the officer's own statement, the officer is cleared.",
            "The deceased had a record: jaywalking (2014). Use of force justified.",
            "Finding: the bullets acted independently of the officer.",
            "Case closed. The officer has been nominated for Employee of the Month."
        };

        public QualifiedImmunity()
        {
            LoadConfig();
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Interval = 200; // run ~5x/sec; cheap and responsive enough
        }

        private void LoadConfig()
        {
            // Falls back to defaults if the .ini is missing.
            ScriptSettings s = ScriptSettings.Load(@"scripts\QualifiedImmunity.ini");
            _annoyanceThreshold        = s.GetValue("Grudge", "AnnoyanceThreshold", _annoyanceThreshold);
            _annoyanceDecayPerSec      = s.GetValue("Grudge", "AnnoyanceDecayRate", _annoyanceDecayPerSec);
            _civilianCollateralChance  = s.GetValue("Chaos",  "CivilianCollateralChance", _civilianCollateralChance);
            _executeSurrenderingChance = s.GetValue("Chaos",  "ExecuteSurrenderingChance", _executeSurrenderingChance);
            _tauntCooldownSeconds      = s.GetValue("Chaos",  "TauntCooldownSeconds", _tauntCooldownSeconds);
            _enableCivilianTargeting   = s.GetValue("Chaos",  "EnableCivilianTargeting", _enableCivilianTargeting);
            _enableSettlementFund      = s.GetValue("Chaos",  "EnableSettlementFund", _enableSettlementFund);

            _enableVehicleAssaultResponse = s.GetValue("Assault", "EnableVehicleAssaultResponse", _enableVehicleAssaultResponse);
            _enableOfficerAssaultResponse = s.GetValue("Assault", "EnableOfficerAssaultResponse", _enableOfficerAssaultResponse);
            _enableWitnessedCrimeResponse = s.GetValue("Assault", "EnableWitnessedCrimeResponse", _enableWitnessedCrimeResponse);
            _vehicleAssaultRadius         = s.GetValue("Assault", "ResponseRadius", _vehicleAssaultRadius);
            _vehicleThreatMemorySeconds   = s.GetValue("Assault", "ThreatMemorySeconds", _vehicleThreatMemorySeconds);

            _provokeKey = s.GetValue("Keys", "ProvokeKey", _provokeKey);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == _provokeKey)
                ProvokeNearbyCops(60f);
        }

        private void OnTick(object sender, EventArgs e)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) return;

            float dt = Interval / 1000f;

            // Passive provocation: honking, aiming at, or crowding a cop.
            DetectPassiveProvocation(player);

            // Controller provoke chord: hold Take-Cover (RB) + tap Reload.
            PollControllerProvoke();

            // Someone ram a cruiser or lay hands on an officer? The badges take
            // it personally and the whole area swarms the offender.
            DetectVehicleAssaults(player);
            DetectOfficerAssaults(player);
            DetectWitnessedCrimes(player);
            EnforceThreats();

            foreach (Ped cop in WorldCache.GetNearbyPeds(player.Position, 60f))
            {
                if (!IsCop(cop) || cop.IsDead) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(cop.Handle)) continue; // your ride-along hosts

                float meter = GetAnnoyance(cop.Handle);

                // Decay over time — they cool off if you leave them alone.
                meter = Math.Max(0f, meter - _annoyanceDecayPerSec * dt);
                _annoyance[cop.Handle] = meter;

                // Make every cop a max-aggression, never-flee shooter.
                ApplyGangCombatProfile(cop);

                // Grudge boils over -> they come for the player specifically. Only (re)issue
                // the combat task if they aren't already fighting the player -- re-tasking
                // every tick restarts the behavior and makes the AI stutter (same guard
                // SicCopsOn uses for assault responders).
                if (meter >= _annoyanceThreshold)
                {
                    if (!Function.Call<bool>(Hash.IS_PED_IN_COMBAT, cop, player))
                        cop.Task.Combat(player);
                    MaybeCollateral(cop, player);
                }

                // Finish the surrendering instead of cuffing them.
                MaybeExecuteSurrendering(cop);
            }

            CivilianUnrest(player);
            SettlementWatch(player);
            IaVerdictTick();
            CleanupStaleHandles();
        }

        // -------------------------------------------------------------------
        // Provocation
        // -------------------------------------------------------------------
        private void DetectPassiveProvocation(Ped player)
        {
            bool honking = player.IsInVehicle() && Game.IsControlPressed(GTA.Control.VehicleHorn);
            bool aiming  = Game.IsControlPressed(GTA.Control.Aim);

            foreach (Ped cop in WorldCache.GetNearbyPeds(player.Position, 12f))
            {
                if (!IsCop(cop) || cop.IsDead) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(cop.Handle)) continue;

                float dist = player.Position.DistanceTo(cop.Position);
                float add = 0f;

                if (honking) add += 6f;                            // honking in their face
                if (aiming && IsAimingAt(cop)) add += 40f;         // pointing a gun = instant
                if (dist < 2.0f) add += 4f;                        // crowding / bumping

                if (add > 0f) AddAnnoyance(cop.Handle, add);
            }
        }

        private void PollControllerProvoke()
        {
            if (!Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, (int)GTA.Control.Cover)) return;
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Reload, true);
            if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Reload))
                ProvokeNearbyCops(60f);
        }

        private void ProvokeNearbyCops(float radius)
        {
            Ped player = Game.Player.Character;
            foreach (Ped cop in WorldCache.GetNearbyPeds(player.Position, radius))
            {
                if (!IsCop(cop) || cop.IsDead) continue;
                AddAnnoyance(cop.Handle, _provokeProximityHit);
                CopSpeak(cop, "GENERIC_INSULT_HIGH");
            }
            // hook point: a middle-finger gesture mod could call this directly.
        }

        // -------------------------------------------------------------------
        // Combat personality
        // -------------------------------------------------------------------
        // CombatAttributes indices, verified against GTA.CombatAttributes in SHVDNE.
        private const int CA_CanUseCover           = 0;
        private const int CA_CanUseVehicles        = 1;
        private const int CA_UseDynamicStrafe      = 4;
        private const int CA_AlwaysFight           = 5;
        private const int CA_BlindFireInCover      = 12;
        private const int CA_CanFlank              = 42;
        private const int CA_FightArmedWhenUnarmed = 46;
        private const int CA_PreferNavmeshInChase  = 69;
        private const int CM_WillAdvance           = 2;   // CombatMovement.WillAdvance

        private void ApplyGangCombatProfile(Ped cop)
        {
            // Configure the static combat personality once per ped; it persists.
            if (_combatProfiled.Add(cop.Handle))
            {
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_AlwaysFight, true);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_FightArmedWhenUnarmed, true);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_CanUseVehicles, true);
                Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, cop, 0, false);    // never flee
                Function.Call(Hash.SET_PED_COMBAT_ABILITY, cop, 2);            // professional
                Function.Call(Hash.SET_PED_COMBAT_RANGE, cop, 2);              // engage at range
                Function.Call(Hash.SET_PED_ACCURACY, cop, 75);                 // scary-good aim
                Function.Call(Hash.SET_PED_SEEING_RANGE, cop, 90f);
                Function.Call(Hash.SET_PED_FIRING_PATTERN, cop, unchecked((int)0xC6EE6B4C)); // full auto

                ApplyCrossfireDiscipline(cop);
            }

            // Driver tuning is applied the first time we see them behind a wheel.
            if (cop.IsInVehicle()) MakeProficientDriver(cop);
        }

        // "Crossfire": officers flank and keep moving so they engage from spread
        // angles instead of stacking up in each other's line of fire. Stops the
        // brotherhood from clipping each other in a firefight. (Civilians caught
        // in the gap are still fair game — that's a separate, deliberate cruelty.)
        private void ApplyCrossfireDiscipline(Ped cop)
        {
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_CanUseCover, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_CanFlank, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_UseDynamicStrafe, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_BlindFireInCover, false); // no blind spray near buddies
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, cop, CM_WillAdvance);
        }

        // Competent wheelman: full skill, urgent-but-controlled (not reckless),
        // steers around walls, props, peds and — crucially — other cruisers, and
        // prefers navmesh pathing in chases so they stop plowing into scenery.
        private void MakeProficientDriver(Ped cop)
        {
            if (!_driverProfiled.Add(cop.Handle)) return; // already tuned; settings persist
            Function.Call(Hash.SET_DRIVER_ABILITY, cop, 1.0f);
            // Low aggression is what keeps them from barging through traffic: at
            // full skill they still drive fast, they just thread cars instead of
            // ramming them.
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, cop, 0.3f);
            Function.Call(Hash.SET_PED_STEERS_AROUND_VEHICLES, cop, true);
            Function.Call(Hash.SET_PED_STEERS_AROUND_OBJECTS, cop, true);
            Function.Call(Hash.SET_PED_STEERS_AROUND_PEDS, cop, true);
            // Follow the ROAD network, not navmesh shortcuts across sidewalks/terrain --
            // keeps even ambient cops driving like real drivers instead of off-roading.
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, cop, CA_PreferNavmeshInChase, false);
        }

        private void MaybeCollateral(Ped cop, Ped player)
        {
            if (!_enableCivilianTargeting) return;
            if (_rng.NextDouble() > _civilianCollateralChance) return;

            // Pick a hapless nearby civilian and let the badge sort it out.
            foreach (Ped civ in WorldCache.GetNearbyPeds(cop.Position, 18f))
            {
                if (civ == player || IsCop(civ) || civ.IsDead) continue;
                if (civ.PedType == PedType.CivMale || civ.PedType == PedType.CivFemale)
                {
                    cop.Task.ShootAt(civ, 1500);
                    Taunt(cop);
                    break;
                }
            }
        }

        private void MaybeExecuteSurrendering(Ped cop)
        {
            // GTA peds don't truly "surrender" without a behavior mod, so we
            // treat ragdolled/stunned nearby peds as the cop's helpless target.
            Ped player = Game.Player.Character;
            bool useNonLethal = player != null && player.Weapons.Current.Hash == WeaponHash.StunGun && player.IsAiming;

            foreach (Ped p in WorldCache.GetNearbyPeds(cop.Position, 6f))
            {
                if (p == cop || IsCop(p) || p.IsDead) continue;
                bool surrendering = p.IsRagdoll
                                    || Function.Call<bool>(Hash.IS_PED_BEING_STUNNED, p, 0);
                if (surrendering)
                {
                    if (useNonLethal)
                    {
                        Function.Call(Hash.TASK_ARREST_PED, cop, p);
                        break;
                    }

                    // One roll decides the fate, so the execute rate matches the
                    // config exactly (the old code rolled twice, which silently
                    // diluted ExecuteSurrenderingChance to ~0.8x its value).
                    double roll = _rng.NextDouble();
                    if (roll < _executeSurrenderingChance)
                    {
                        cop.Task.ShootAt(p, 800);
                        Taunt(cop);
                        QueueIaVerdict();
                        break;
                    }
                    else if (roll < _executeSurrenderingChance + 0.10) // taser-flavor slice
                    {
                        cop.Weapons.Give(WeaponHash.StunGun, 100, true, true);
                        cop.Task.ShootAt(p, 2000);
                        Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, cop, "GENERIC_CURSE_MED", "SPEECH_PARAMS_FORCE_SHOUTED", 1);
                        GTA.UI.Notification.PostTicker("~b~" + CopNames.For(cop) + ":~w~ STOP RESISTING!", false);
                        break;
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // Vehicular assault on police
        // -------------------------------------------------------------------
        // If an NPC's vehicle hits a cop car -- fender-bender or full ram -- the
        // officers treat it as an attack and gun the driver down. Player-caused
        // bumps are intentionally ignored here; those run through the grudge meter.
        private void DetectVehicleAssaults(Ped player)
        {
            if (!_enableVehicleAssaultResponse) return;

            // Watch every cruiser near the player for a fresh collision.
            foreach (Vehicle copCar in WorldCache.GetNearbyVehicles(player.Position, _copCarScanRadius))
            {
                if (copCar == null || !copCar.Exists() || !IsPoliceVehicle(copCar)) continue;

                foreach (Vehicle other in WorldCache.GetNearbyVehicles(copCar.Position, 12f))
                {
                    if (other == null || !other.Exists() || other == copCar) continue;
                    if (IsPoliceVehicle(other)) continue;          // cop-on-cop bumps don't count
                    if (player.IsInVehicle(other)) continue;       // the player runs through the grudge meter
                    if (!Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, copCar, other, true)) continue;

                    Ped offender = other.Driver;
                    // Reset the record so a single shunt doesn't re-fire every tick.
                    Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, copCar);

                    if (!IsValidOffender(offender, player)) continue;
                    if (FlagThreat(offender, copCar.Position))
                        AnnounceAssault(copCar.Driver, copCar.Position, "Vehicular assault on an officer! Light him up!");
                    break;                                             // one offender per cruiser per tick
                }
            }
        }

        // An officer shot, beaten, or run down by an NPC: every badge in the area
        // treats that NPC as hostile and converges on them.
        private void DetectOfficerAssaults(Ped player)
        {
            if (!_enableOfficerAssaultResponse) return;

            foreach (Ped cop in WorldCache.GetNearbyPeds(player.Position, _copCarScanRadius))
            {
                if (!IsCop(cop) || cop.IsDead) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(cop.Handle)) continue;

                bool byPed     = Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ANY_PED, cop);
                bool byVehicle = Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ANY_VEHICLE, cop);
                if (!byPed && !byVehicle) continue;

                Ped offender = FindOffender(cop, player, byVehicle);
                // Reset so a single hit doesn't re-fire every tick (and so a player
                // shootout doesn't keep re-scanning the same officer).
                Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, cop);

                if (!IsValidOffender(offender, player)) continue;
                if (FlagThreat(offender, cop.Position))
                    AnnounceAssault(cop, cop.Position, "Assault on an officer -- weapons free, take him down!");
            }
        }

        // Who hurt this officer? Prefer the NPC who shot or struck them; failing
        // that, the driver of the vehicle that ran them down.
        private Ped FindOffender(Ped cop, Ped player, bool checkVehicles)
        {
            foreach (Ped p in WorldCache.GetNearbyPeds(cop.Position, _vehicleAssaultRadius))
            {
                if (p == null || !p.Exists() || p == cop || p == player || IsCop(p)) continue;
                if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, cop, p, true))
                    return p;
            }
            if (checkVehicles)
            {
                foreach (Vehicle v in WorldCache.GetNearbyVehicles(cop.Position, 30f))
                {
                    if (v == null || !v.Exists() || IsPoliceVehicle(v)) continue;
                    if (player.IsInVehicle(v)) continue;
                    if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, cop, v, true))
                    {
                        Ped d = v.Driver;
                        if (d != null && d.Exists()) return d;
                    }
                }
            }
            return null;
        }

        private bool IsValidOffender(Ped offender, Ped player)
        {
            if (offender == null || !offender.Exists() || offender.IsDead) return false;
            if (offender == player || IsCop(offender)) return false;       // player keeps the grudge system
            if (RideAlongRegistry.FriendlyCops.Contains(offender.Handle)) return false;
            return true;
        }

        // An NPC vehicle runs a pedestrian down in view of the police -> the badges
        // treat the driver as a hostile and hunt them (a hit-and-run pursuit). This
        // also lights up nearby officers' combat, which a ride-along will then join.
        private void DetectWitnessedCrimes(Ped player)
        {
            if (!_enableWitnessedCrimeResponse) return;
            if ((DateTime.Now - _lastWitnessScan).TotalSeconds < 1.0) return; // throttle the ped sweep
            _lastWitnessScan = DateTime.Now;

            foreach (Ped victim in WorldCache.GetNearbyPeds(player.Position, _copCarScanRadius))
            {
                if (victim == null || !victim.Exists() || victim == player) continue;
                if (IsCop(victim) || victim.IsInVehicle()) continue;
                // Only a real strike: the pedestrian was hit by a vehicle and is down/hurt.
                if (!Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ANY_VEHICLE, victim)) continue;
                if (!victim.IsDead && !victim.IsInjured && !victim.IsRagdoll) continue;

                Vehicle car = FindVehicleThatHit(victim, player);
                Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, victim); // don't re-fire on the same hit
                if (car == null || IsPoliceVehicle(car)) continue;          // a cop running someone over is "fine"

                Ped driver = car.Driver;
                if (!IsValidOffender(driver, player)) continue;
                if (!CopWitnessNear(victim.Position)) continue;             // a badge has to actually see it

                if (FlagThreat(driver, car.Position))
                    AnnounceAssault(NearestCop(victim.Position), victim.Position,
                        "Hit and run! Driver just mowed down a pedestrian - PURSUE AND TERMINATE!");
            }
        }

        // The non-police vehicle that struck the victim (blamed on its driver).
        private Vehicle FindVehicleThatHit(Ped victim, Ped player)
        {
            return NPCVehicleFinder.FindVehicleThatHit(victim, player, 25f);
        }

        private bool CopWitnessNear(Vector3 pos)
        {
            foreach (Ped cop in WorldCache.GetNearbyPeds(pos, _vehicleAssaultRadius))
                if (IsCop(cop) && !cop.IsDead && !RideAlongRegistry.FriendlyCops.Contains(cop.Handle)) return true;
            return false;
        }

        private Ped NearestCop(Vector3 pos)
        {
            foreach (Ped cop in WorldCache.GetNearbyPeds(pos, _vehicleAssaultRadius))
                if (IsCop(cop) && !cop.IsDead) return cop;
            return null;
        }

        // Flag (or refresh) an offender and immediately sic nearby cops on them.
        // Returns true only the first time this offender is flagged.
        private bool FlagThreat(Ped offender, Vector3 origin)
        {
            bool firstOffense = !_threats.ContainsKey(offender.Handle);
            _threats[offender.Handle] = DateTime.Now;
            SicCopsOn(offender, origin, false);
            return firstOffense;
        }

        // Keep the badges actively hunting flagged offenders, and forget them once
        // they're dead, gone, or the grudge has cooled.
        private void EnforceThreats()
        {
            if (_threats.Count == 0) return;

            // Expiry runs every tick, but RE-siccing is throttled to ~3s. The in-combat
            // guard inside SicCopsOn doesn't cover TASK_VEHICLE_CHASE (a vehicle task,
            // not "combat"), so re-siccing every 200ms tick was ClearAll+re-chasing the
            // pursuing drivers each tick -- the same restart-the-task-every-tick stutter
            // fixed everywhere else in the mod.
            bool resic = (DateTime.Now - _lastEnforce).TotalSeconds > 3.0;
            if (resic) _lastEnforce = DateTime.Now;

            List<int> expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> kv in _threats)
            {
                Ped offender = (Ped)Entity.FromHandle(kv.Key);
                if (offender == null || !offender.Exists() || offender.IsDead
                    || (DateTime.Now - kv.Value).TotalSeconds > _vehicleThreatMemorySeconds)
                {
                    expired.Add(kv.Key);
                    continue;
                }
                if (resic) SicCopsOn(offender, offender.Position, true);
            }
            foreach (int h in expired) _threats.Remove(h);
        }

        // Task every nearby (non-friendly) officer to engage the offender. The
        // in-combat guard avoids re-issuing the task each tick, which would
        // otherwise stutter the AI mid-fight. isReissue distinguishes the periodic
        // EnforceThreats refresh from the first sic: a refreshed driver whose car is
        // already MOVING is presumed to be chasing (vehicle-chase never reads as
        // "in combat") and is left alone, so the working chase isn't restarted.
        private void SicCopsOn(Ped offender, Vector3 origin, bool isReissue)
        {
            bool offenderInVehicle = offender.IsInVehicle();

            foreach (Ped cop in WorldCache.GetNearbyPeds(origin, _vehicleAssaultRadius))
            {
                if (!IsCop(cop) || cop.IsDead) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(cop.Handle)) continue;
                ApplyGangCombatProfile(cop);

                if (offenderInVehicle && cop.IsInVehicle())
                {
                    Vehicle copCar = cop.CurrentVehicle;
                    if (copCar != null && copCar.Exists())
                    {
                        if (copCar.Driver == cop)
                        {
                            if (isReissue && copCar.Speed > 2.0f)
                            {
                                // moving = the chase is live; don't restart it
                            }
                            else if (!Function.Call<bool>(Hash.IS_PED_IN_COMBAT, cop, offender))
                            {
                                // No ClearAll first: a re-issued chase task replaces the
                                // old one on its own; clearing just stalls the car a beat.
                                Function.Call(Hash.TASK_VEHICLE_CHASE, cop, offender);
                                Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, cop, 16.0f);
                                MakeProficientDriver(cop);
                            }
                        }
                        else
                        {
                            if (!Function.Call<bool>(Hash.IS_PED_IN_COMBAT, cop, offender))
                            {
                                cop.Task.Combat(offender);
                            }
                        }
                        continue;
                    }
                }

                if (!Function.Call<bool>(Hash.IS_PED_IN_COMBAT, cop, offender))
                {
                    cop.Task.Combat(offender);
                }
            }
        }

        private void AnnounceAssault(Ped announcer, Vector3 pos, string line)
        {
            Ped responder = announcer;
            if (responder == null || !responder.Exists() || !IsCop(responder))
            {
                responder = null;
                foreach (Ped cop in WorldCache.GetNearbyPeds(pos, _vehicleAssaultRadius))
                {
                    if (IsCop(cop) && !cop.IsDead) { responder = cop; break; }
                }
            }
            string who = (responder != null && responder.Exists()) ? CopNames.For(responder) : "Officer";
            GTA.UI.Notification.PostTicker("~r~" + who + ": ~w~" + line, false);
            if (responder != null && responder.Exists()) Taunt(responder);
        }

        // True for marked LSPD/LSSD/NOOSE vehicles, or anything a cop is driving.
        private static bool IsPoliceVehicle(Vehicle v) { return Cops.IsPoliceVehicle(v); }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------
        // A little civilian backlash: some bystanders who watch a cop gun someone
        // down arm up and fight back. A little, not a lot.
        private void CivilianUnrest(Ped player)
        {
            if ((DateTime.Now - _lastUnrest).TotalSeconds < 2.0) return;
            _lastUnrest = DateTime.Now;

            Ped shootingCop = null;
            foreach (Ped cop in WorldCache.GetNearbyPeds(player.Position, 50f))
            {
                if (!IsCop(cop) || cop.IsDead) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(cop.Handle)) continue;
                if (Function.Call<bool>(Hash.IS_PED_SHOOTING, cop)) { shootingCop = cop; break; }
            }
            if (shootingCop == null) return;

            foreach (Ped civ in WorldCache.GetNearbyPeds(shootingCop.Position, 25f))
            {
                if (civ.IsDead) continue;
                if (civ.PedType != PedType.CivMale && civ.PedType != PedType.CivFemale) continue;
                if (_armedCivs.Contains(civ.Handle)) continue;
                
                int react = _rng.Next(100);
                if (react < 15) // 15% chance to record on phone
                {
                    _armedCivs.Add(civ.Handle);
                    Function.Call(Hash.TASK_USE_MOBILE_PHONE_TIMED, civ, 10000); // pull out phone
                    // Instantly trigger police response!
                    shootingCop.Task.ShootAt(civ, 3000);
                    Taunt(shootingCop);
                }
                else if (react < 27) // 12% chance to fight back
                {
                    _armedCivs.Add(civ.Handle);
                    Function.Call(Hash.GIVE_WEAPON_TO_PED, civ, unchecked((int)0x1B06D571), 60, false, true); // pistol
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, civ, 46, true);
                    Function.Call(Hash.SET_PED_ACCURACY, civ, 25);
                    Function.Call(Hash.TASK_COMBAT_PED, civ, shootingCop, 0, 16);
                }
            }
        }

        // -------------------------------------------------------------------
        // Settlement fund + Internal Affairs
        // -------------------------------------------------------------------
        // Tally civilians killed by police near the player and post the running
        // taxpayer bill. Each corpse is counted once (handle-set), blame is checked
        // against nearby badges/cruisers via the engine's damage records.
        private void SettlementWatch(Ped player)
        {
            if (!_enableSettlementFund) return;
            if ((DateTime.Now - _lastSettlementScan).TotalSeconds < 2.0) return;
            _lastSettlementScan = DateTime.Now;

            foreach (Ped civ in WorldCache.GetNearbyPeds(player.Position, 70f))
            {
                if (civ == null || !civ.Exists() || !civ.IsDead) continue;
                if (civ.PedType != PedType.CivMale && civ.PedType != PedType.CivFemale) continue;
                if (_settledBodies.Contains(civ.Handle)) continue;
                if (!KilledByPolice(civ)) continue;

                _settledBodies.Add(civ.Handle);
                _settlementBodies++;
                // $250k-$3m a body, rounded to a tidy lawyer-friendly figure.
                long payout = 250000L + (long)(_rng.NextDouble() * 2750000.0);
                payout = (payout / 50000L) * 50000L;
                _settlementTotal += payout;

                if ((DateTime.Now - _lastSettlementTicker).TotalSeconds > 12)
                {
                    _lastSettlementTicker = DateTime.Now;
                    int quip = _rng.Next(SettlementQuips.Length);
                    QIAudio.PlaySettlement(quip);
                    GTA.UI.Notification.PostTicker(string.Format(
                        "~g~CITY SETTLEMENT FUND:~w~ +${0:N0} -- ${1:N0} this shift ({2} payouts). {3}",
                        payout, _settlementTotal, _settlementBodies, SettlementQuips[quip]), false);
                }
            }

            // Prune handles of despawned corpses so the counted-set can't grow forever.
            if (_settledBodies.Count > 96) PruneDeadOrGone(_settledBodies);
        }

        private bool KilledByPolice(Ped civ)
        {
            foreach (Ped cop in WorldCache.GetNearbyPeds(civ.Position, 55f))
            {
                if (!IsCop(cop)) continue;
                if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, civ, cop, true)) return true;
            }
            foreach (Vehicle v in WorldCache.GetNearbyVehicles(civ.Position, 55f))
            {
                if (!IsPoliceVehicle(v)) continue;
                if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, civ, v, true)) return true;
            }
            return false;
        }

        // The punchline lands a few seconds after the shot: Internal Affairs has
        // somehow already wrapped up its investigation.
        private void QueueIaVerdict()
        {
            if ((DateTime.Now - _lastIaVerdict).TotalSeconds < 45) return; // a gag, not a stream
            _lastIaVerdict = DateTime.Now;
            _iaVerdictDue = DateTime.Now.AddSeconds(6.0);
        }

        private void IaVerdictTick()
        {
            if (_iaVerdictDue == DateTime.MinValue || DateTime.Now < _iaVerdictDue) return;
            _iaVerdictDue = DateTime.MinValue;
            int i = _rng.Next(IaVerdicts.Length);
            QIAudio.PlayIaVerdict(i);
            GTA.UI.Notification.PostTicker("~b~Internal Affairs:~w~ " + IaVerdicts[i], false);
        }

        // Drop set entries whose entity no longer exists (corpses can despawn/be hauled off).
        private static void PruneDeadOrGone(HashSet<int> set)
        {
            List<int> gone = new List<int>();
            foreach (int h in set)
            {
                Ped p = (Ped)Entity.FromHandle(h);
                if (p == null || !p.Exists()) gone.Add(h);
            }
            foreach (int h in gone) set.Remove(h);
        }

        private void Taunt(Ped cop)
        {
            if ((DateTime.Now - _lastTaunt).TotalSeconds < _tauntCooldownSeconds) return;
            _lastTaunt = DateTime.Now;
            CopSpeak(cop, "GENERIC_INSULT_HIGH");
            int i = _rng.Next(Taunts.Length);
            QIAudio.PlayTaunt(i);
            GTA.UI.Notification.PostTicker("~r~" + CopNames.For(cop) + ": ~w~" + Taunts[i], false);
        }

        private void CopSpeak(Ped cop, string speech)
        {
            Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, cop, speech,
                          "SPEECH_PARAMS_FORCE_SHOUTED", 1);
        }

        private static bool IsCop(Ped p) { return Cops.IsCop(p); }

        private static bool IsAimingAt(Ped target)
        {
            return Function.Call<bool>(Hash.IS_PLAYER_FREE_AIMING_AT_ENTITY, Game.Player, target);
        }

        private float GetAnnoyance(int handle)
        {
            float v;
            return _annoyance.TryGetValue(handle, out v) ? v : 0f;
        }

        private void AddAnnoyance(int handle, float amount)
        {
            _annoyance[handle] = GetAnnoyance(handle) + amount;
        }

        private void CleanupStaleHandles()
        {
            // Drop dead/despawned cops so the dictionary doesn't grow forever.
            List<int> dead = new List<int>();
            foreach (KeyValuePair<int, float> kv in _annoyance)
            {
                Ped ped = (Ped)Entity.FromHandle(kv.Key);
                if (ped == null || !ped.Exists() || ped.IsDead) dead.Add(kv.Key);
            }
            foreach (int h in dead) { _annoyance.Remove(h); CopNames.Forget(h); }

            // Prune the armed-civilian set so it doesn't grow forever.
            PruneDeadHandles(_armedCivs);

            // Drop profiled cops once they're gone, so a recycled ped handle gets
            // (re)configured the next time it shows up as an officer.
            PruneDeadHandles(_combatProfiled);
            PruneDeadHandles(_driverProfiled);

            // Drop assault offenders who've despawned or died.
            List<int> goneThreats = new List<int>();
            foreach (KeyValuePair<int, DateTime> kv in _threats)
            {
                Ped ped = (Ped)Entity.FromHandle(kv.Key);
                if (ped == null || !ped.Exists() || ped.IsDead) goneThreats.Add(kv.Key);
            }
            foreach (int h in goneThreats) _threats.Remove(h);
        }

        // Remove handles whose peds are dead or despawned from a tracking set.
        private static void PruneDeadHandles(HashSet<int> set)
        {
            if (set.Count == 0) return;
            List<int> gone = new List<int>();
            foreach (int h in set)
            {
                Ped ped = (Ped)Entity.FromHandle(h);
                if (ped == null || !ped.Exists() || ped.IsDead) gone.Add(h);
            }
            foreach (int h in gone) set.Remove(h);
        }
    }
}
