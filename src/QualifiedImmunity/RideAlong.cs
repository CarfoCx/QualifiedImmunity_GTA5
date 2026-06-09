using System;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // Police ride-along. Request a unit via configurable hotkey (default F9).
    // Board the passenger or rear seat, and the unit drops into pursuits or assists local PD.
    // C# 5-compatible (in-box csc), API names verified against SHVDNE.
    public partial class RideAlong : Script
    {
        private enum Phase { Idle, EnRoute, Boarding, Riding, Pursuit, Wrapup, Regroup, Assist, Clearing, UCDrive, UCStake }

        // ---- Keybinds (loaded from QualifiedImmunity.ini under [Keys]) ----
        private Keys _requestKey = Keys.F9;
        private Keys _cancelKey  = Keys.F10;

        // ---- Config constants (loaded from QualifiedImmunity.ini under [Pursuit]) ----
        private int _innocentChance = 35;
        private int _threat1Chance = 45;
        private int _threat2Chance = 35; // threat 3 chance is 100 - _threat1Chance - _threat2Chance
        private float _swatDelaySeconds = 7.0f;
        private float _heliDelaySeconds = 11.0f;
        private int _maxSwatWaves = 3;
        private int _maxBackupUnits = 4;
        private float _swatIntervalSeconds = 15.0f;
        private float _backupIntervalSeconds = 9.0f;
        private float _pitDistanceThreshold = 16.0f;
        private float _pitMinSpeed = 7.0f;
        private float _pitCooldownSeconds = 8.0f;
        private float _engageDistanceThreshold = 16.0f; // only bail out of the car when right on top
        private float _engageSpeedThreshold = 2.5f;     // ...of a suspect whose car has actually STOPPED
        private float _idealFollowDistance = 16.0f;
        private float _escapeDistance = 160.0f;
        private float _escapeTimeLimit = 25.0f;

        // Pacing the player can tune in the .ini without a rebuild.
        private float _pursuitDelayMin = 25.0f;   // gap before the next pursuit kicks off
        private float _pursuitDelayMax = 55.0f;
        private float _radioIntervalMin = 45.0f;  // gap between unhinged radio lines
        private float _radioIntervalMax = 85.0f;
        private bool _showDebugHud = false;        // the cyan QI diagnostic overlay (dev only)
        private float _driverAggressiveness = 0.3f; // 0=calm/road-following, 1=reckless
        private float _spawnDistanceMin = 75.0f;   // how far off the unit spawns to drive in
        private float _spawnDistanceMax = 130.0f;
        private int _maxReplacementUnits = 2;      // replacement units dispatched if yours is lost
        private int _eliteUnitChance = 6;          // % chance the unit is NOOSE/FIB/Agency
        private int _undercoverChance = 5;         // % chance the unit is an undercover sting

        // 0 = regular LSPD; 1 = NOOSE/SWAT; 2 = FIB; 3 = "the Agency" (CIA-flavored IAA).
        private int _eliteUnit;
        // Undercover sting ride: plainclothes officers in an unmarked Police4 who run
        // a staged drug-buy bust mission with the player as backup.
        private bool _undercover;
        private Vector3 _ucScene = Vector3.Zero;   // where the deal is staged

        // Unit-downed recovery state.
        private int _replacementsUsed;

        // Phone dispatch menu state (call dispatch on the cell phone instead of a raw key).
        private bool _phoneOpen;
        private int _phoneIndex;
        private DateTime _phoneOpenedAt = DateTime.MinValue;
        private int _phoneRingSound = -1;   // looping ring -- must be stopped explicitly
        private bool _phoneConnected;       // ring done, menu showing
        private const double PhoneRingSeconds = 1.8;

        // Configured baselines for the three values the player can nudge live via
        // the D-pad (follow distance, engage distance/speed). The live fields above
        // get mutated mid-pursuit, so we snapshot the .ini values and restore them
        // at the start of each pursuit -- otherwise the drift carries over forever.
        private float _baseIdealFollowDistance;
        private float _baseEngageDistanceThreshold;
        private float _baseEngageSpeedThreshold;

        // ---- State ----
        private Phase _phase = Phase.Idle;
        private DateTime _phaseSince = DateTime.MinValue;

        private Vehicle _copCar;
        private Blip _copBlip;    // map/radar marker so the player can see their unit
        private Ped _driver;
        private Ped _partner;     // null if it's a solo officer
        private Vehicle _suspectCar;
        private Ped _suspect;
        private int _playerSeat;  // -1 driver, 0 passenger, 1 LeftRear, 2 RightRear
        private readonly Random _rng = new Random();

        private bool _engaged;        // true once it turns into an on-foot firefight
        private bool _relsReady;
        private int _copsGroup, _suspGroup, _playerGroup;
        private int _backupCount;
        private DateTime _lastRadio = DateTime.MinValue;
        private DateTime _lastBackup = DateTime.MinValue;
        private DateTime _lastReissue = DateTime.MinValue;
        private DateTime _lastReboardPrompt = DateTime.MinValue;
        private double _ridePursuitDelay = 15.0;   // randomized 10-40s before each pursuit
        private double _radioDelay = 13.0;         // randomized gap between radio lines
        private bool _enableTourniquet = true;
        private DateTime _lastTourniquet = DateTime.MinValue;
        private DateTime _lastAssaultScan = DateTime.MinValue;
        private DateTime _lastCoverUp = DateTime.MinValue;   // throttle the cover-up lines
        private DateTime _lastBanter = DateTime.MinValue;    // partner-banter pacing
        private double _banterDelay = 50.0;

        // Connected to local PD: the live ambient engagement we're backing up.
        private Ped _threat;
        private bool _assistEngaged;
        private double _clearDelay = 7.0;             // 5-10s "scene clear" pause
        private DateTime _lastEngageScan = DateTime.MinValue;
        private double _rideCalloutDelay = 45.0;      // gap before a staged local radio call
        private readonly System.Collections.Generic.List<Ped> _calloutPerps =
            new System.Collections.Generic.List<Ped>();
        private readonly System.Collections.Generic.List<Entity> _backupEntities =
            new System.Collections.Generic.List<Entity>();
        private readonly System.Collections.Generic.List<Ped> _suspectPeds =
            new System.Collections.Generic.List<Ped>();
        private bool _suspectsInnocent;

        // Suspect-driven escalation state
        private int _suspectThreat;
        private Vehicle _suspectCar2;       // a second armed crew car at the top threat
        private Vehicle _heli;
        private Ped _heliPilot, _heliGunner;
        private bool _swatCalled, _heliCalled;
        private int _swatWaves;
        private DateTime _lastSwat = DateTime.MinValue;
        private DateTime _lastHeliUpdate = DateTime.MinValue; // periodic heli follow re-issue (part 2)

        // PIT maneuver + dark-comedy chatter state.
        private bool _pitting;
        private DateTime _pitSince = DateTime.MinValue;
        private DateTime _lastPit = DateTime.MinValue;
        private DateTime _suspectStoppedSince = DateTime.MinValue; // when the fleeing car actually stopped
        private DateTime _lastCollateral = DateTime.MinValue;
        private DateTime _lastProgress = DateTime.MinValue;   // en-route stuck/fail detection
        private float _lastEnRouteDist = float.MaxValue;       // track closing distance, not just speed
        private bool _copGrounded;                             // cruiser confirmed sitting on loaded collision
        private int _driveMethod;                              // en-route drive native: 0=longrange,1=mission GoTo,2=wander
        private bool _everMoved;                               // cruiser has registered real motion this en-route
        private DateTime _groundedAt = DateTime.MinValue;      // when the cruiser confirmed grounded (for escalation)
        private DateTime _driverOutSince = DateTime.MinValue;  // when the driver left the seat (debounce re-seating)
        private int _reseatCount;                              // diagnostic: how many times we've force-reseated the driver
        private bool _boardTaskIssued;                         // walk-in task issued once per boarding
        private DateTime _boardTaskAt = DateTime.MinValue;     // when the walk-in was issued (for fallback)
        private DateTime _lastWander = DateTime.MinValue;      // throttle re-issuing the patrol wander
        private DateTime _lastCarMoving = DateTime.MinValue;   // last time the cruiser was actually moving
        private bool _pullingOver;                             // close to the player -> curb pull-over issued

        // Escape resolution state (part 8)
        private DateTime _escapeTimerStarted = DateTime.MinValue;

        // Post-pursuit scene wrap-up: the unit holds the scene, covers, and works
        // the downed suspect instead of immediately driving off.
        private Ped _wrapBody;                              // the downed suspect being worked
        private int _wrapStage;                             // 0 approach, 1 cuff/inspect, 2 hold
        private DateTime _wrapStageAt = DateTime.MinValue;

        // Message bodies only; the speaking officer's name is prepended at runtime.
        // Keep in sync (order + count) with $radio in tools/gen_audio.ps1.
        private static readonly string[] Radio =
        {
            "Dispatch, suspect failed to signal a turn. Requesting LETHAL force.",
            "I have NOT read him his rights and I do NOT intend to!",
            "Be advised, I'm two coffees deep and I can see through time.",
            "Requesting backup, air support, and someone to hold my badge.",
            "Define 'excessive.' Asking for a friend. The friend is me.",
            "Best shift EVER! This is why I skipped the ethics module!",
            "Dispatch, vibes immaculate, suspect toast, paperwork pending.",
            "He looked at me funny back in 2019. It's personal now.",
            "Pursuit ongoing. I've decided the speed limit is a personal attack.",
            "Dispatch, can someone Google if this is legal? Asking mid-chase.",
            "Suspect signaled politely. Suspiciously polite. Floor it.",
            "I'm not angry, I'm just constitutionally unaccountable.",
            "Requesting a chopper, a tank, and a hug. In that order.",
            "He's doing the speed limit now. Cowardice. Open fire.",
            "Be advised, my body cam fell into my coffee. Again. Tragic.",
            "Reading him his rights would imply I can read. Negative.",
            "Suspect's crime? Vibes. Bad ones. Trust me, I'm trained.",
            "Backup, bring snacks. The only hostage here is my lunch.",
            "I haven't blinked since the academy and I'm not starting now.",
            "Dispatch, define 'unarmed.' He had FISTS. Two of them!"
        };

        // Spoken by the driver when the player boards. The player is ONE OF US: an
        // honorary deputy wrapped in the same magic force field -- never the butt of
        // the joke. Keep in sync (order + count) with $welcome in tools/gen_audio.ps1.
        private static readonly string[] Welcome =
        {
            "Welcome aboard, deputy! As of right now you're one of us. Legally? Don't worry about it.",
            "Climb in! Anything you do today is officially 'departmental procedure'.",
            "Good to have you, partner. Whatever happens out there, it was justified.",
            "Welcome to the brotherhood! Your immunity paperwork is verbal. It's verbal.",
            "Buckle up, partner. You're covered by the same magic force field we are.",
            "You ride with us, you're family. Family with plausible deniability.",
            "Welcome aboard! Rule one: we protect our own. That's you now. Congrats.",
            "New partner, huh? Here's your imaginary badge. Works exactly like a real one.",
            "Glad you're here, deputy. Whatever you see today, you also didn't see.",
            "Hop in! By riding with us you are now, legally, 'an ongoing investigation'."
        };

        // Partner banter: when the two officers are together they compliment each
        // other and trash the suspects. A/B are call-and-reply pairs by index.
        // Keep in sync (order + count) with $banter_a/$banter_b in tools/gen_audio.ps1.
        private static readonly string[] BanterA =
        {
            "Great shooting out there, partner.",
            "These suspects get dumber every year.",
            "You ever miss a shot?",
            "That perp ran like he had somewhere to be.",
            "You're glowing today. New body armor?",
            "Remember that guy who 'knew his rights'?"
        };
        private static readonly string[] BanterB =
        {
            "Great driving. Together we're basically ONE competent officer.",
            "Lucky for them, the paperwork can't read either.",
            "Only court dates.",
            "He did. The morgue. ...I'm hilarious.",
            "Crunches. And a suspect-funded confidence boost.",
            "HA! Classic. Anyway, he's community service now."
        };

        // Spoken when the ride-along player wings a civilian: you're one of us, so
        // the cover-up machine activates on your behalf. Keep in sync (order +
        // count) with $cover in tools/gen_audio.ps1.
        private static readonly string[] CoverUp =
        {
            "I saw the whole thing. The civilian assaulted your bullet.",
            "He was charging you. From behind. While fleeing. Textbook.",
            "Relax, deputy. The city has a guy for this.",
            "Nice grouping! I'll put 'resisting' in the report.",
            "Whoa there! ...Eh, he probably had warrants. Carry on.",
            "Don't sweat it. You're one of us -- that never happened."
        };

        // PIT-maneuver chatter: asks permission, laughs, does it anyway.
        private static readonly string[] PitLines =
        {
            "Requesting permission to PIT- ah, who am I kidding. *BONK*",
            "Dispatch, green light on the PIT? ...Too late! HAHA!",
            "Permission to PIT requested... denied... and ignored. PIT IT!",
            "I'll ask forgiveness, not permission - SENDING IT!",
            "Supervisor says negative on the PIT. Supervisor isn't HERE. *SLAM*",
            "Is a PIT authorized? Didn't ask, don't care, already did it!",
            "Permission to gently nudge him? *RAMS HIM OFF THE ROAD* Gentle enough!",
            "Filing the PIT request... and by 'filing' I mean DOING IT! Whoops!"
        };

        // Another officer raises the civilian body count; the answer is always the same.
        private static readonly string[] CollateralQ =
        {
            "Uh, dispatch... were there civilians in that crosswalk?",
            "Hey, were those... bystanders we just went through?",
            "Should we be worried about the body count back there?",
            "That was a LOT of pedestrians. We good on that?"
        };
        private static readonly string[] CollateralA =
        {
            "Collateral damage is the price of excellence. WORTH IT.",
            "Eggs, omelettes, you know the saying. Totally worth it!",
            "They go in the report as 'scenery.' Worth every penny.",
            "Acceptable losses! The suspect matters WAY more. Worth it!",
            "Suspect deliberately pushed a civilian into our path! Not our fault!",
            "Be advised, suspect just used a civilian as a human shield!",
            "Civilian was interfering with an active pursuit! Pushing through!"
        };

        // LAW-ABIDING style (DrivingStyle.Normal): stops at red lights, stops for traffic
        // and pedestrians, stays on the road. Used for all NORMAL driving -- en route,
        // patrol, responding -- so the AI drives like a real-life driver.
        private const int DRIVE_STYLE = 786603;

        // PURSUIT style for OUR officers: weave through traffic and run lights, but
        // with every avoidance flag on -- swerve moving cars (4), steer around
        // parked/empty cars (8), peds (16) and objects (32), and allow wrong-way
        // (512) so the AI holds speed instead of snapping lanes into obstacles.
        // (The old 786468 only avoided moving cars + objects, which is why chase
        // cruisers clipped parked cars and street furniture.)
        private const int RIDE_DRIVE_STYLE = 787004;

        // FLEE style for suspects: the old, sloppier avoidance set. Fleeing perps
        // are SUPPOSED to clip mirrors and eat fences; the precision is for cops.
        private const int FLEE_DRIVE_STYLE = 786468;

        // How far around the cruiser we look for other cops already in a fight.
        private const float ASSIST_SCAN_RADIUS = 90f;
        // Wider sweep used while patrolling to PRIORITIZE joining local engagements.
        private const float ENGAGEMENT_SEEK_RADIUS = 170f;

        // ---- Combat attribute constants (part 10) ----
        private const int CA_CanUseCover = 0;
        private const int CA_CanUseVehicles = 1;
        private const int CA_CanLeaveVehicle = 3;
        private const int CA_UseDynamicStrafe = 4;
        private const int CA_AlwaysFight = 5;
        private const int CA_FleeWhilstInVehicle = 6;
        private const int CA_BlindFireInCover = 12;
        private const int CA_CanFlank = 42;
        private const int CA_FightArmedWhenUnarmed = 46;
        private const int CA_DisableBlockFromPursueDuringVehicleChase = 64;
        private const int CA_DisableSpinOutDuringVehicleChase = 65;
        private const int CA_DisableCruiseInFrontDuringBlockDuringVehicleChase = 66;
        private const int CA_PreferNavmeshInChase = 69;
        private const int CA_DisablePullAlongsideDuringVehicleChase = 74;

        public RideAlong()
        {
            LoadConfig();
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted; // part 1
            Interval = 0; // run every frame: on-screen prompts/HUD must redraw per frame,
                          // and polled button presses must be sampled per frame to register.
                          // All heavy scans below are throttled by their own timers.
        }

        private void OnAborted(object sender, EventArgs e)
        {
            StopPhoneRing();   // the looping dispatch ring owns a sound id -> release it or it leaks on reload
            Cleanup();
        }

        private void LoadConfig()
        {
            ScriptSettings s = ScriptSettings.Load(@"scripts\QualifiedImmunity.ini");
            
            // Keys
            _requestKey = s.GetValue("Keys", "RequestRideAlongKey", _requestKey);
            _cancelKey  = s.GetValue("Keys", "CancelRideAlongKey", _cancelKey);

            // RideAlong options
            _enableTourniquet = s.GetValue("RideAlong", "EnableTourniquet", _enableTourniquet);
            _showDebugHud     = s.GetValue("RideAlong", "ShowDebugHud", _showDebugHud);
            _driverAggressiveness = s.GetValue("RideAlong", "DriverAggressiveness", _driverAggressiveness);
            _spawnDistanceMin = s.GetValue("RideAlong", "SpawnDistanceMin", _spawnDistanceMin);
            _spawnDistanceMax = s.GetValue("RideAlong", "SpawnDistanceMax", _spawnDistanceMax);
            _maxReplacementUnits = s.GetValue("RideAlong", "MaxReplacementUnits", _maxReplacementUnits);
            _eliteUnitChance  = s.GetValue("RideAlong", "EliteUnitChance", _eliteUnitChance);
            _undercoverChance = s.GetValue("RideAlong", "UndercoverChance", _undercoverChance);
            _showUnitHud      = s.GetValue("RideAlong", "ShowUnitHud", _showUnitHud);

            // Pursuit options
            _innocentChance       = s.GetValue("Pursuit", "InnocentChance", _innocentChance);
            _pursuitDelayMin      = s.GetValue("Pursuit", "PursuitDelayMinSeconds", _pursuitDelayMin);
            _pursuitDelayMax      = s.GetValue("Pursuit", "PursuitDelayMaxSeconds", _pursuitDelayMax);
            _radioIntervalMin     = s.GetValue("Pursuit", "RadioIntervalMinSeconds", _radioIntervalMin);
            _radioIntervalMax     = s.GetValue("Pursuit", "RadioIntervalMaxSeconds", _radioIntervalMax);
            _threat1Chance        = s.GetValue("Pursuit", "Threat1Chance", _threat1Chance);
            _threat2Chance        = s.GetValue("Pursuit", "Threat2Chance", _threat2Chance);
            _swatDelaySeconds     = s.GetValue("Pursuit", "SwatDelaySeconds", _swatDelaySeconds);
            _heliDelaySeconds     = s.GetValue("Pursuit", "HeliDelaySeconds", _heliDelaySeconds);
            _maxSwatWaves         = s.GetValue("Pursuit", "MaxSwatWaves", _maxSwatWaves);
            _maxBackupUnits       = s.GetValue("Pursuit", "MaxBackupUnits", _maxBackupUnits);
            _swatIntervalSeconds  = s.GetValue("Pursuit", "SwatIntervalSeconds", _swatIntervalSeconds);
            _backupIntervalSeconds = s.GetValue("Pursuit", "BackupIntervalSeconds", _backupIntervalSeconds);
            _pitDistanceThreshold = s.GetValue("Pursuit", "PitDistanceThreshold", _pitDistanceThreshold);
            _pitMinSpeed          = s.GetValue("Pursuit", "PitMinSpeed", _pitMinSpeed);
            _pitCooldownSeconds   = s.GetValue("Pursuit", "PitCooldownSeconds", _pitCooldownSeconds);
            _engageDistanceThreshold = s.GetValue("Pursuit", "EngageDistanceThreshold", _engageDistanceThreshold);
            _engageSpeedThreshold = s.GetValue("Pursuit", "EngageSpeedThreshold", _engageSpeedThreshold);
            _idealFollowDistance  = s.GetValue("Pursuit", "IdealFollowDistance", _idealFollowDistance);
            _escapeDistance       = s.GetValue("Pursuit", "EscapeDistance", _escapeDistance);
            _escapeTimeLimit      = s.GetValue("Pursuit", "EscapeTimeLimit", _escapeTimeLimit);

            // Snapshot the player-tunable values so each pursuit can start from them.
            _baseIdealFollowDistance    = _idealFollowDistance;
            _baseEngageDistanceThreshold = _engageDistanceThreshold;
            _baseEngageSpeedThreshold   = _engageSpeedThreshold;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == _requestKey)
            {
                // The request key now raises the cell phone to dispatch (toggles the menu).
                if (_phoneOpen) ClosePhone(); else OpenPhone();
                return;
            }
            if (e.KeyCode == _cancelKey && _phase != Phase.Idle) { Notify("~y~Ride-along ended."); Cleanup(); }
        }

        private void PollController()
        {
            if (!Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, (int)GTA.Control.Cover)) return;
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Jump, true);
            if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Jump))
            {
                if (_phoneOpen) ClosePhone(); else OpenPhone();
            }
        }

        private void SetPhase(Phase p) { _phase = p; _phaseSince = DateTime.Now; RideAlongRegistry.Active = (p != Phase.Idle); }
        private double SecondsInPhase { get { return (DateTime.Now - _phaseSince).TotalSeconds; } }

        // True when the cruiser hasn't moved for a few seconds -- our cue to (re)issue
        // a drive/chase task. A moving cruiser is left alone so its task isn't restarted.
        private bool CarStalled() { return (DateTime.Now - _lastCarMoving).TotalSeconds > 3.5; }

        // -------------------------------------------------------------------
        // Cell-phone dispatch menu -- "call" the ride-along instead of a raw key.
        // -------------------------------------------------------------------
        private void OpenPhone()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) return;
            StopPhoneRing();   // clear any prior ring so the sound id can't leak on a re-open
            _phoneOpen = true;
            _phoneConnected = false;
            _phoneIndex = 0;
            _phoneOpenedAt = DateTime.Now;
            // Put the phone to the player's ear and play a dial/ring so it reads as a call.
            Function.Call(Hash.TASK_USE_MOBILE_PHONE_TIMED, player, 30000);
            // Own the sound id so we can STOP the looping ring (the bug: it never stopped).
            _phoneRingSound = Function.Call<int>(Hash.GET_SOUND_ID);
            Function.Call(Hash.PLAY_SOUND_FRONTEND, _phoneRingSound, "Dial_and_Remote_Ring", "Phone_SoundSet_Default", true);
        }

        private void StopPhoneRing()
        {
            if (_phoneRingSound != -1)
            {
                Function.Call(Hash.STOP_SOUND, _phoneRingSound);
                Function.Call(Hash.RELEASE_SOUND_ID, _phoneRingSound);
                _phoneRingSound = -1;
            }
        }

        private void ClosePhone()
        {
            _phoneOpen = false;
            _phoneConnected = false;
            StopPhoneRing();
            Ped player = Game.Player.Character;
            if (player != null && player.Exists() && !player.IsInVehicle())
                Function.Call(Hash.CLEAR_PED_TASKS, player); // put the phone away
        }

        private void PhoneTick()
        {
            if (!_phoneOpen) return;
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) { ClosePhone(); return; }

            // Don't let the vanilla phone open over our menu while it's up.
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.Phone, true);

            // Brief "ringing" beat before the menu connects.
            double since = (DateTime.Now - _phoneOpenedAt).TotalSeconds;
            if (since < PhoneRingSeconds)
            {
                DrawPhoneBox("DISPATCH", new string[] { "...ringing..." }, -1);
                return;
            }
            // Connected -> kill the ring (once).
            if (!_phoneConnected) { _phoneConnected = true; StopPhoneRing(); }

            // Context-sensitive options: request when idle, cancel when a ride is active.
            bool active = _phase != Phase.Idle;
            string[] opts = active
                ? new string[] { "Cancel Ride-Along", "Close" }
                : new string[] { "Request Ride-Along Unit", "Close" };

            if (Game.IsControlJustPressed(GTA.Control.PhoneUp))
                _phoneIndex = (_phoneIndex - 1 + opts.Length) % opts.Length;
            if (Game.IsControlJustPressed(GTA.Control.PhoneDown))
                _phoneIndex = (_phoneIndex + 1) % opts.Length;
            if (Game.IsControlJustPressed(GTA.Control.PhoneCancel)) { PhoneBeep(false); ClosePhone(); return; }
            if (Game.IsControlJustPressed(GTA.Control.PhoneSelect))
            {
                PhoneBeep(true);
                bool chosePrimary = (_phoneIndex == 0);
                ClosePhone();
                if (chosePrimary)
                {
                    if (active) { Notify("~y~Ride-along ended."); Cleanup(); }
                    else RequestRideAlong();
                }
                return;
            }

            DrawPhoneBox("POLICE DISPATCH", opts, _phoneIndex);
        }

        private static void PhoneBeep(bool accept)
        {
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, accept ? "SELECT" : "BACK",
                "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
        }

        // Native-styled menu (GTA's own fonts/banner/highlight) so the dispatch UI looks
        // like it belongs in the game rather than a plain text overlay.
        private void DrawPhoneBox(string title, string[] options, int sel)
        {
            const float x = 0.118f;       // left edge (0-1 screen space), like the interaction menu
            const float w = 0.215f;       // panel width (wide enough for the longest option)
            const float top = 0.26f;      // top of the banner
            const float hdrH = 0.05f;     // banner height
            const float itemH = 0.035f;   // row height
            float cx = x + w / 2f;

            // Title banner (dark navy, like LSPD dispatch).
            Function.Call(Hash.DRAW_RECT, cx, top + hdrH / 2f, w, hdrH, 12, 28, 56, 235);
            DrawMenuText(title, x + 0.007f, top + 0.010f, 0.38f, 4, 245, 245, 245, false);

            for (int i = 0; i < options.Length; i++)
            {
                float ry = top + hdrH + itemH * i;
                bool s = (i == sel);
                // Selected row gets the white highlight bar with dark text (native style).
                if (s) Function.Call(Hash.DRAW_RECT, cx, ry + itemH / 2f, w, itemH, 240, 240, 240, 255);
                else   Function.Call(Hash.DRAW_RECT, cx, ry + itemH / 2f, w, itemH, 0, 0, 0, 160);
                int t = s ? 15 : 235;
                DrawMenuText(options[i], x + 0.009f, ry + 0.006f, 0.34f, 0, t, t, t, false);
            }

            // Footer hint strip (only once the menu is interactive, i.e. not during the ring).
            // Plain words -- ~INPUT~ glyph tokens don't render through this text path.
            if (sel >= 0)
            {
                float fy = top + hdrH + itemH * options.Length;
                Function.Call(Hash.DRAW_RECT, cx, fy + itemH / 2f, w, itemH, 0, 0, 0, 200);
                DrawMenuText("Up/Down: Move    Enter: Select    Esc: Hang up",
                    x + 0.009f, fy + 0.008f, 0.27f, 0, 200, 200, 200, false);
            }
        }

        // Draw text with the game's native text renderer (proper menu font + drop shadow),
        // so it matches GTA's own UI instead of the flat TextElement overlay.
        private void DrawMenuText(string text, float x, float y, float scale, int font, int r, int g, int b, bool center)
        {
            Function.Call(Hash.SET_TEXT_FONT, font);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, 255);
            Function.Call(Hash.SET_TEXT_DROP_SHADOW);
            Function.Call(Hash.SET_TEXT_CENTRE, center);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
        }

        // -------------------------------------------------------------------
        private void RequestRideAlong()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsInVehicle())
            {
                Notify("~r~Dispatch:~w~ Step out into the open and call again.");
                return;
            }
            _replacementsUsed = 0;       // fresh ride -> reset the replacement budget
            SpawnUnit(player, false);
        }

        // Your unit got wrecked or all its officers went down -> dispatch a fresh unit and
        // keep the ride going, instead of just ending it. Capped by MaxReplacementUnits.
        private void DispatchReplacementOrEnd(string reason)
        {
            // The replacement path leaves the Pursuit phase, so UpdateHeliCam stops running --
            // cut the feed here or it'd freeze with its optics (NV/thermal) stuck on screen.
            StopNewsCam();
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) { Cleanup(); return; }
            if (_replacementsUsed >= _maxReplacementUnits)
            {
                Notify(reason + " No units left in the area. Ride-along over.");
                Cleanup();
                return;
            }
            _replacementsUsed++;
            Notify(reason + " ~b~Dispatching a replacement unit...");
            ReleaseCurrentUnit();        // let the old wreck/bodies go back to the engine
            DespawnPursuitProps();       // drop any suspect/backup tied to the lost unit
            _engaged = false; _pitting = false;
            _threat = null; _assistEngaged = false;
            _wrapBody = null;
            ReleaseCalloutPerps();
            SpawnUnit(player, true);
        }

        // Release the current unit's entities (wreck, downed officers, blip) without ending
        // the ride, so a replacement can take over. (Cleanup() would end the whole ride.)
        private void ReleaseCurrentUnit()
        {
            if (_copBlip != null && _copBlip.Exists()) _copBlip.Delete();
            _copBlip = null;
            if (_driver != null && _driver.Exists())
            { RideAlongRegistry.FriendlyCops.Remove(_driver.Handle); CopNames.Forget(_driver.Handle); _driver.MarkAsNoLongerNeeded(); }
            if (_partner != null && _partner.Exists())
            { RideAlongRegistry.FriendlyCops.Remove(_partner.Handle); CopNames.Forget(_partner.Handle); _partner.MarkAsNoLongerNeeded(); }
            if (_copCar != null && _copCar.Exists())
            {
                Function.Call(Hash.SET_VEHICLE_EXCLUSIVE_DRIVER, _copCar, 0, 0); // release the seat claim
                _copCar.MarkAsNoLongerNeeded();
            }
            _driver = null; _partner = null; _copCar = null;
        }

        // Spawn a ride-along unit OUT OF SIGHT at a distance and send it en route to the
        // player. Shared by the initial F9 request and the unit-downed replacement dispatch.
        private void SpawnUnit(Ped player, bool isReplacement)
        {
            // Spawn the cruiser OUT OF SIGHT and at a (tunable) distance so it visibly drives
            // up to the player rather than popping into view.
            Vector3 roadPos = FindHiddenSpawn(player.Position, _spawnDistanceMin, _spawnDistanceMax);
            float minAccept = Math.Max(20f, _spawnDistanceMin * 0.8f);
            if (roadPos.DistanceTo(player.Position) < minAccept)
            {
                Vector3 fallback = SnapToRoad(player.Position + RandomOffset(_spawnDistanceMin + 10f));
                roadPos = fallback != Vector3.Zero ? fallback : player.Position + RandomOffset(_spawnDistanceMin);
            }

            // Stream collision in at the (far, off-screen) spawn point so the cruiser has
            // ground under it before it's tasked to drive.
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, roadPos.X, roadPos.Y, roadPos.Z);

            // Small chance dispatch sends a SPECIAL unit instead of a black-and-white:
            // a NOOSE tactical van, an FIB Granger, or an unmarked "Agency" Buffalo --
            // with matching crew and serious hardware (see OutfitEliteUnit).
            VehicleHash carModel = VehicleHash.Police3;
            PedHash copModel = PedHash.Cop01SMY;
            _eliteUnit = 0;
            // Rarest first: an undercover sting ride (plainclothes officers in an
            // unmarked Police4 -- it has the hidden flashers). Replacements are
            // always regular units. Otherwise, small chance of a special unit.
            _undercover = !isReplacement && _rng.Next(100) < _undercoverChance;
            if (_undercover)
            {
                carModel = VehicleHash.Police4;
                copModel = PedHash.Business01AMY;
            }
            else if (_rng.Next(100) < _eliteUnitChance)
            {
                switch (_rng.Next(3))
                {
                    case 0: _eliteUnit = 1; carModel = VehicleHash.Riot; copModel = PedHash.Swat01SMY;     break;
                    case 1: _eliteUnit = 2; carModel = VehicleHash.FBI2; copModel = PedHash.FibSec01SMM;   break;
                    default: _eliteUnit = 3; carModel = VehicleHash.FBI; copModel = PedHash.CiaSec01SMM;   break;
                }
            }

            // Spawn slightly in the air so the chassis doesn't clip into the road mesh
            Vector3 spawnPos = new Vector3(roadPos.X, roadPos.Y, roadPos.Z + 2.5f);
            _copCar = World.CreateVehicle(new Model(carModel), spawnPos);
            if (_copCar == null) { Notify("~r~Dispatch:~w~ No units available - try near a road."); Cleanup(); return; }
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _copCar);
            Function.Call(Hash.FREEZE_ENTITY_POSITION, _copCar, false); // ensure physics is active, not frozen
            if (_eliteUnit != 0) DeckOutVehicle(_copCar);

            _driver = _copCar.CreatePedOnSeat(VehicleSeat.Driver, new Model(copModel));
            // Elite units always roll two-deep; regular cars ~50% chance of a partner.
            bool hasPartner = _eliteUnit != 0 || _rng.Next(2) == 0;
            if (hasPartner) _partner = _copCar.CreatePedOnSeat(VehicleSeat.Passenger, new Model(copModel));

            _playerSeat = hasPartner ? 2 : 0;                    // rear-right if partnered, else front passenger

            // Keep the car enterable for the player at all times (they came for a RIDE) --
            // only the driver door is reserved. The driver/partner can't be jacked regardless
            // (LockIntoCar pins them in), so we DON'T lock the passenger door anymore: that
            // was blocking the player from getting back in after stepping out mid-ride.
            LockCarForRide();

            // The driver SEAT belongs to the AI, full stop -- the same mechanism taxis
            // use. Without it, the game's "enter nearest door" default dropped the player
            // into the EMPTY driver seat after a firefight (while the cop was still
            // walking back), with no way to move to a passenger seat afterwards. With an
            // exclusive driver set, entering routes the player to the open seats instead.
            if (Valid(_driver)) Function.Call(Hash.SET_VEHICLE_EXCLUSIVE_DRIVER, _copCar, _driver, 0);

            // Blip the cruiser so the player can watch it drive in and find it later.
            _copBlip = _copCar.AddBlip();
            if (_copBlip != null && _copBlip.Exists())
            {
                Function.Call(Hash.SET_BLIP_SPRITE, _copBlip, 56);   // police-car icon
                _copBlip.Color = BlipColor.Blue;
                _copBlip.Name = "Ride-Along Unit";
                Function.Call(Hash.SET_BLIP_AS_SHORT_RANGE, _copBlip, false);
            }

            _copCar.IsEngineRunning = true;
            _copCar.IsSirenActive = false; // roll in calm/unassuming -- the siren makes every NPC panic

            // NOTE: do NOT Clear() the registry here -- it's shared with AmbientPolice,
            // whose staged officers re-register only every ~350ms. Wiping it handed the
            // gang-cop AI a window to hijack a peaceful staged scene. Our own previous
            // unit's handles were already removed (ReleaseCurrentUnit / Cleanup).
            if (Valid(_driver)) RideAlongRegistry.FriendlyCops.Add(_driver.Handle);
            if (Valid(_partner)) RideAlongRegistry.FriendlyCops.Add(_partner.Handle);

            // Mark the unit as mission entities so the game keeps simulating them while
            // they're off-camera -- otherwise the cruiser sleeps at its spawn and never
            // drives in. Released again via MarkAsNoLongerNeeded in Cleanup.
            Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _copCar, true, true);
            if (Valid(_driver)) Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _driver, true, true);
            if (Valid(_partner)) Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, _partner, true, true);

            EnsureRelationships();   // wire cop/suspect/native-PD relations from the ride's start
            BefriendRidealongCops();
            LockIntoCar(_driver);
            if (Valid(_partner)) LockIntoCar(_partner);

            CopNames.Apply(_driver);
            if (Valid(_partner)) CopNames.Apply(_partner);

            // Elite gear AFTER CopNames.Apply so the operator loadout/health wins
            // over the rank-based one.
            if (_eliteUnit != 0)
            {
                OutfitEliteUnit(_driver);
                OutfitEliteUnit(_partner);
            }

            // Drive to the player's nearest road. The first task is issued the same
            // frame the driver spawns, which often doesn't "take", so EnRoute also
            // re-issues this periodically -- _lastReissue starts at MinValue so the
            // first OnTick re-tasks the unit promptly.
            MakeRideAlongDriver(_driver);
            IssueEnRouteDrive(player, true);

            // Stabilize the partner: take them out of police AI too (else they fight to
            // exit), clear their spawn scenario, lock them in the passenger seat, and
            // block ambient events so they stay put en-route.
            if (Valid(_partner))
            {
                Function.Call(Hash.SET_PED_AS_COP, _partner, false);
                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, _partner, true);
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, _partner);
                Function.Call(Hash.SET_PED_INTO_VEHICLE, _partner, _copCar, 0);
            }

            // Suppress the player's wanted system for the duration of the ride -- set ONCE
            // here, never per-frame. SET_MAX_WANTED_LEVEL(0) stops stars accruing at all, so
            // we don't need to CLEAR every tick; SET_POLICE_IGNORE_PLAYER is a sticky toggle.
            // (Hammering these every frame is what froze the cop driver -- see OnTick.)
            Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
            Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, true);

            if (_undercover)
                Notify("~b~Dispatch:~w~ Your unit is... a gray sedan. Plainclothes. Don't wave, don't point, don't blow their cover.");
            else if (_eliteUnit == 1)
                Notify("~b~Dispatch:~w~ All regular units are busy, so... NOOSE tactical is picking you up. Don't touch anything.");
            else if (_eliteUnit == 2)
                Notify("~b~Dispatch:~w~ A federal unit was 'in the area'. The FIB is your chauffeur today. Lucky you.");
            else if (_eliteUnit == 3)
                Notify("~b~Dispatch:~w~ Be advised: this unit does not exist, and neither does this ride-along. Enjoy.");
            else
                Notify(isReplacement ? "~b~Dispatch:~w~ Replacement unit en route. Stand by."
                                     : "~b~Dispatch:~w~ Unit en route for your ride-along. Stand by.");
            _lastProgress = DateTime.Now;
            _lastReissue = DateTime.MinValue;
            _copGrounded = false;
            _driveMethod = 0;
            _everMoved = false;
            _groundedAt = DateTime.MinValue;
            _driverOutSince = DateTime.MinValue;
            _reseatCount = 0;
            _boardTaskIssued = false;
            _lastEnRouteDist = float.MaxValue;
            _lastCarMoving = DateTime.Now;
            _pullingOver = false;
            SetPhase(Phase.EnRoute);
        }

        // Fallback when the cruiser can't path in: put it on a road right next to the
        // player (just behind them if there's no node close), engine on and upright,
        // so the arrival check trips next tick and the ride-along proceeds.
        private void WarpCruiserNearPlayer(Ped player)
        {
            if (!Valid(_copCar)) return;
            Vector3 spot = SnapToRoad(player.Position);
            if (spot == Vector3.Zero || spot.DistanceTo(player.Position) > 25f)
                spot = player.Position - player.ForwardVector * 5f; // just behind the player
            Function.Call(Hash.SET_ENTITY_COORDS, _copCar, spot.X, spot.Y, spot.Z + 0.5f, false, false, false, true);
            Function.Call(Hash.SET_ENTITY_HEADING, _copCar, player.Heading);
            Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _copCar);
            _copCar.IsEngineRunning = true;
            EnsureDriverSeated();
            Notify("~b~Dispatch:~w~ Unit's pulling up to you now.");
        }

        // Order the unit to drive to the player. Uses the long-range pathing native
        // (the same one the assist/ambulance code relies on) for reliable navigation.
        //
        // hardReset controls the one-time flush: on the FIRST issue we must clear the
        // freshly-spawned ped's ambient cop scenario (otherwise the drive task queues
        // behind it and the car never moves). On RE-issues we must NOT clear -- calling
        // CLEAR_PED_TASKS_IMMEDIATELY repeatedly wipes the in-progress drive task before
        // the car can accelerate, pinning the speed at 0.0. A re-issued drive task
        // naturally replaces the previous one on its own.
        private void IssueEnRouteDrive(Ped player, bool hardReset)
        {
            if (!Valid(_driver) || !Valid(_copCar)) return;

            // MINIMAL en-route drive. An ambient cop drives perfectly when the script
            // leaves it alone, so we mimic that: take it out of police AI, make sure the
            // engine's on, and issue ONE drive task -- nothing else. No KEEP_TASK (it
            // makes the ped reject later tasks / lock a non-moving state), no per-issue
            // re-seating or door/handbrake fiddling (each of those resets the in-progress
            // task), and no blocking during en-route (no player around to react to yet).
            Function.Call(Hash.SET_PED_AS_COP, _driver, false);
            _copCar.IsEngineRunning = true;

            // Gentle clear (NOT immediate) only on the first issue, to drop the spawn
            // scenario without ejecting the ped from the seat.
            if (hardReset) Function.Call(Hash.CLEAR_PED_TASKS, _driver);

            // Calm cruising speed (16 m/s) and a SMALL stop range (4m) so the unit drives in
            // like normal traffic and pulls right up next to the player, not 8m short.
            if (_driveMethod >= 2)
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, _driver, _copCar, 16.0f, DRIVE_STYLE);
            else if (_driveMethod == 1)
                Function.Call(Hash.TASK_VEHICLE_MISSION_PED_TARGET, _driver, _copCar, player,
                    4, 16.0f, DRIVE_STYLE, 2.0f, 5.0f, false); // 4 == MISSION_GOTO; small stop range -> pulls right up
            else
            {
                // Aim for the ROAD beside the player, not their exact coordinate. The
                // player is usually standing on a sidewalk/plaza; navigating at that raw
                // spot is what made the AI mount the curb at pedestrians on the way in
                // (mass panic) instead of arriving like a taxi. The curb-side pull-over
                // for the last stretch is handled in the EnRoute phase.
                Vector3 dest = SnapToRoad(player.Position);
                if (dest == Vector3.Zero || dest.DistanceTo(player.Position) > 40f) dest = player.Position;
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, _driver, _copCar,
                    dest.X, dest.Y, dest.Z, 16.0f, DRIVE_STYLE, 2.0f); // 2m stop range
            }
        }

        private bool _announcedLoad;

        private void OnTick(object sender, EventArgs e)
        {
            // One-time confirmation the script is alive this session.
            if (!_announcedLoad)
            {
                _announcedLoad = true;
                Notify("~g~Qualified Immunity V7.5:~w~ ride-along ready. Press ~b~" + _requestKey + "~w~ on foot to call dispatch.");
            }

            PollController();
            CheckCommands();
            PhoneTick();   // the cell-phone dispatch menu (works whether or not a ride is active)
            if (_phase == Phase.Idle) return;

            // Player went down -> end the ride cleanly NOW. Otherwise the unit keeps
            // patrolling with a dead player and (with the heli feed up) the camera and its
            // night-vision/thermal optics would stay stuck over the wasted screen. Cleanup
            // tears the cam/optics down and restores the wanted system.
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) { Cleanup(); return; }

            // Unit lost (wrecked cruiser, or all officers down) -> dispatch a replacement
            // and keep the ride going, until the replacement budget runs out.
            if (!IsDriveable(_copCar)) { DispatchReplacementOrEnd("~r~Dispatch:~w~ Unit's wrecked."); return; }
            PromoteDriverIfNeeded();
            if (!Valid(_driver)) { DispatchReplacementOrEnd("~r~Dispatch:~w~ Officers are down."); return; }

            // The ride-along player is ONE OF US -- an honorary deputy wrapped in the
            // same protection the badges enjoy. Wing a civilian and the cover-up
            // machine activates on your behalf. The ONE unforgivable crime is hurting
            // a fellow officer: the brotherhood turns on you instantly. (Civilians are
            // paperwork; cops are family.) 80m ped scan, throttled to ~1/sec.
            if ((DateTime.Now - _lastAssaultScan).TotalSeconds > 1.0)
            {
                _lastAssaultScan = DateTime.Now;
                Ped victim = FindPlayerVictim();
                if (victim != null)
                {
                    if (IsCopPed(victim))
                    {
                        Notify("~r~Officer:~w~ You shot a COP?! Your immunity is REVOKED!");
                        Cleanup();
                        // Cleanup() restored the wanted system, so the stars stick.
                        Notify("~b~Dispatch:~w~ Be advised: ride-along's deputy status is RESCINDED. All units respond.");
                        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, 2, false);
                        Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
                        return;
                    }

                    // A civilian. Clear the damage record so each incident is covered
                    // exactly once, then the driver writes the report for you.
                    Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, victim);
                    if ((DateTime.Now - _lastCoverUp).TotalSeconds > 20)
                    {
                        _lastCoverUp = DateTime.Now;
                        int i = _rng.Next(CoverUp.Length);
                        Notify("~b~" + (Valid(_driver) ? CopNames.For(_driver) : "Officer") + ":~w~ " + CoverUp[i]);
                        QIAudio.PlayCover(i);
                        CopBark(_driver, "GENERIC_HOWS_IT_GOING");
                    }
                }
            }
            // NOTE: police-ignore + wanted suppression are set ONCE in RequestRideAlong,
            // NOT here. Calling SET_POLICE_IGNORE_PLAYER every frame was THE bug: it resets
            // police-ped AI each call, which froze our own cop driver's task ~60x/sec (the
            // entire "won't drive / 0.0 mph" saga). They're sticky toggles -- set once, done.
            //
            // EXCEPTION: clearing the PLAYER's own wanted level is safe (it never touches cop
            // AI). Boarding/riding in a police cruiser can still slap a 1-star "stolen police
            // vehicle" flag on the player despite the one-time max-wanted suppression. Reading
            // the wanted level is cheap, so only re-clear when a star actually shows up.
            if (Game.Player.Wanted.WantedLevel > 0)
            {
                Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
            }

            TourniquetTick(player);
            UnitProgressTick();   // XP for shooting/kills, level-ups
            BanterTick();         // partner small talk: compliments + suspect slander
            DrawUnitHud();        // officer HP/XP bars + cruiser health

            if (_phase == Phase.Pursuit) DrawHUD();
            DrawDebug();

            // Track whether the cruiser is actually moving, so drive/chase tasks are
            // only re-issued when it has genuinely stalled -- re-tasking a moving car
            // is what makes it lurch, brake, and go nowhere.
            if (Valid(_copCar) && _copCar.Speed > 1.5f) { _lastCarMoving = DateTime.Now; _everMoved = true; }

            switch (_phase)
            {
                case Phase.EnRoute:
                    {
                        // ROOT CAUSE of the "stuck at 0.0 mph" bug: the cruiser spawns far
                        // away and off-screen, where the map collision often hasn't streamed
                        // in yet. A vehicle sitting in unloaded collision has no ground under
                        // it, so its AI driver physically cannot move it. We must wait for
                        // collision to load, drop the car onto the road, and ONLY THEN task
                        // the drive. (Pursuit driving always worked because the player is in
                        // the car -- collision is loaded around the player.)
                        if (!_copGrounded)
                        {
                            Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                                _copCar.Position.X, _copCar.Position.Y, _copCar.Position.Z);
                            if (Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, _copCar))
                            {
                                Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, _copCar);
                                Function.Call(Hash.FREEZE_ENTITY_POSITION, _copCar, false);
                                EnsureDriverSeated();
                                _copGrounded = true;
                                _groundedAt = DateTime.Now;
                                _lastReissue = DateTime.MinValue; // force a fresh hard-reset drive task
                                _lastCarMoving = DateTime.Now;
                                _lastProgress = DateTime.Now;
                            }
                            else
                            {
                                // Still streaming collision in -- hold the timeout clock and
                                // wait for the next tick rather than tasking a groundless car.
                                _lastProgress = DateTime.Now;
                                break;
                            }
                        }

                        float dist = _copCar.Position.DistanceTo(player.Position);
                        // Count "progress" as actually getting closer (it can drive in slowly).
                        if (dist < _lastEnRouteDist - 1f) { _lastEnRouteDist = dist; _lastProgress = DateTime.Now; }
                        double sinceClosing = (DateTime.Now - _lastProgress).TotalSeconds;
                        bool noProgress = sinceClosing > 30; // Increased to 30s to allow 3-point turns

                        // If the current drive native produced NO motion within a few seconds
                        // of grounding, escalate to the next one (longrange -> mission -> wander).
                        // Whichever finally moves the car identifies the working native.
                        if (!_everMoved && _copGrounded && _groundedAt != DateTime.MinValue)
                        {
                            double sinceGrounded = (DateTime.Now - _groundedAt).TotalSeconds;
                            if (_driveMethod == 0 && sinceGrounded > 5.0)
                            { _driveMethod = 1; _lastReissue = DateTime.MinValue; }
                            else if (_driveMethod == 1 && sinceGrounded > 10.0)
                            { _driveMethod = 2; _lastReissue = DateTime.MinValue; }
                        }

                        // Taxi-style arrival: once the cruiser is close and rolling, stop
                        // navigating at the player and PULL OVER at the curb beside them
                        // (TASK_VEHICLE_PARK mode 3 -- the same pull-over taxis use). This
                        // is what gives the clean "rolls up and stops at the curb" arrival
                        // instead of the AI nosing onto the sidewalk after its destination.
                        if (!_pullingOver && _everMoved && dist < 32f)
                        {
                            OutputArgument on = new OutputArgument(), oh = new OutputArgument();
                            if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                                player.Position.X, player.Position.Y, player.Position.Z, on, oh, 1, 3.0f, 0f))
                            {
                                Vector3 node = on.GetResult<Vector3>();
                                if (node.DistanceTo(player.Position) < 28f)
                                {
                                    _pullingOver = true;
                                    Function.Call(Hash.TASK_VEHICLE_PARK, _driver, _copCar,
                                        node.X, node.Y, node.Z, oh.GetResult<float>(),
                                        3 /* pull over at the curb */, 20.0f, true);
                                }
                            }
                        }
                        // Pull-over wedged short of the player -> drop back to the normal
                        // approach so the unit doesn't idle 20m+ away forever.
                        else if (_pullingOver && dist >= 22f && CarStalled())
                        {
                            _pullingOver = false;
                            _lastReissue = DateTime.MinValue;
                        }

                        // HANDS-OFF re-issue: issue once, then refresh only every 10s. Re-
                        // tasking too often restarts the drive before the car can accelerate
                        // (it never gets off 0.0). Leave the task alone so it can actually run.
                        // While the curb pull-over is in progress the drive task is NOT
                        // refreshed -- a re-issued drive would cancel the park maneuver.
                        bool isFirstIssue = (_lastReissue == DateTime.MinValue);
                        double sinceReissue = (DateTime.Now - _lastReissue).TotalSeconds;
                        // Keep refreshing the destination until it's right on top of the player,
                        // and refresh FASTER once it's close so it noses all the way up instead
                        // of coasting to a stop a few meters short and making you walk.
                        double reissueGap = dist < 35f ? 3.5 : 10.0;
                        if (!_pullingOver && dist >= 6f && (isFirstIssue || sinceReissue > reissueGap))
                        {
                            _lastReissue = DateTime.Now;
                            IssueEnRouteDrive(player, isFirstIssue);
                        }

                        // Arrival, in priority order:
                        //  - pulledUp : it got right next to you (the normal, desired case).
                        //  - parked   : the curb pull-over finished within hailing distance.
                        //  - stuckClose: stopped within 16m and can't improve for 9s -- accept it.
                        //  - bestEffort: genuinely can't path any closer (you're off the road
                        //    network), so after 12s stalled it stops as close as it can and you
                        //    cover the last few steps -- rather than failing the whole call.
                        bool pulledUp   = dist < 8f;
                        bool parked     = _pullingOver && _copCar.Speed < 0.6f && dist < 22f;
                        bool stuckClose = _copCar.Speed < 1f && dist < 16f && (DateTime.Now - _lastProgress).TotalSeconds > 9;
                        bool bestEffort = _copCar.Speed < 1f && dist < 45f && (DateTime.Now - _lastProgress).TotalSeconds > 12;
                        if (pulledUp || parked || stuckClose || bestEffort)
                        {
                            // Release the KEEP_TASK lock from the en-route drive so the
                            // upcoming boarding/patrol tasks apply cleanly (the car is now
                            // next to the player and on-screen, so task culling is moot).
                            Function.Call(Hash.SET_PED_KEEP_TASK, _driver, false);
                            // CRITICAL: Clear the CHASE task so the AI stops treating the player as a suspect
                            Function.Call(Hash.CLEAR_PED_TASKS, _driver);
                            if (Valid(_partner)) Function.Call(Hash.CLEAR_PED_TASKS, _partner);

                            Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 2000);
                            _copCar.IsSirenActive = false;
                            Notify(pulledUp || parked || stuckClose
                                ? "~g~Dispatch:~w~ Your ride-along has ARRIVED - get in (" + SeatName(_playerSeat) + ")."
                                : "~g~Dispatch:~w~ Unit's as close as it can get - walk over and get in (" + SeatName(_playerSeat) + ").");
                            _lastReboardPrompt = DateTime.MinValue;
                            _boardTaskIssued = false;
                            SetPhase(Phase.Boarding);
                        }
                        // The user hates the instant spawn / warp. If they are truly stuck,
                        // we let it timeout so they have to call again somewhere else.
                        else if (noProgress || SecondsInPhase > 120)
                        {
                            Notify("~r~Dispatch:~w~ Unit got stuck or couldn't reach your location. Try calling again.");
                            Cleanup();
                        }
                        break;
                    }

                case Phase.Boarding:
                    {
                        if (player.IsInVehicle(_copCar))
                        {
                            WelcomeAboard();
                            ResetRideDelay();
                            // Re-assert script ownership (off police AI) and block events so
                            // the player climbing in doesn't trigger the get-in/get-out bail.
                            Function.Call(Hash.SET_PED_AS_COP, _driver, false);
                            Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, _driver, true);
                            if (Valid(_partner))
                            {
                                Function.Call(Hash.SET_PED_AS_COP, _partner, false);
                                Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, _partner, true);
                            }
                            // Clear tasks to stop any fleeing/panicking induced by the player entering
                            Function.Call(Hash.CLEAR_PED_TASKS, _driver);
                            if (Valid(_partner)) Function.Call(Hash.CLEAR_PED_TASKS, _partner);

                            // An undercover ride goes straight into its sting mission;
                            // everyone else starts a normal patrol wander.
                            if (_undercover && StartUndercoverMission(player)) return;
                            Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, _driver, _copCar, 18.0f, DRIVE_STYLE);
                            _lastWander = DateTime.Now;
                            _lastCarMoving = DateTime.Now; // prevent immediate CarStalled triggering
                            SetPhase(Phase.Riding);
                            return;
                        }
                        EnsureDriverSeated();
                        if (_copCar.Speed > 1.5f)
                            Function.Call(Hash.TASK_VEHICLE_TEMP_ACTION, _driver, _copCar, 1, 2000);

                        if ((DateTime.Now - _lastReboardPrompt).TotalSeconds > 5.0)
                        {
                            _lastReboardPrompt = DateTime.Now;
                            Notify("~b~Dispatch:~w~ Your unit's waiting - get in to start the ride-along.");
                        }
                        if (!_boardTaskIssued && _copCar.Position.DistanceTo(player.Position) < 8f)
                        {
                            ShowHelp("Press ~INPUT_ENTER~ to get in and start the ride-along.");
                            if (Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, (int)GTA.Control.Enter)
                                || Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, (int)GTA.Control.Context))
                            {
                                // Walk in for the proper animation (issue once). If the path to the
                                // door is blocked and it hasn't completed shortly, we seat directly below.
                                _boardTaskIssued = true;
                                _boardTaskAt = DateTime.Now;
                                Function.Call(Hash.TASK_ENTER_VEHICLE, player, _copCar, 10000, _playerSeat, 2.0f, 1, 0);
                            }
                        }

                        // Fallback: if the walk-in didn't get the player seated within a few
                        // seconds (blocked door/path), just put them in so boarding never hangs.
                        if (_boardTaskIssued && !player.IsInVehicle(_copCar)
                            && (DateTime.Now - _boardTaskAt).TotalSeconds > 4.0)
                            Function.Call(Hash.SET_PED_INTO_VEHICLE, player, _copCar, _playerSeat);

                        if (SecondsInPhase > 180) { Notify("~r~Dispatch:~w~ You never got in. Ride-along cancelled."); Cleanup(); }
                        break;
                    }

                case Phase.Riding:
                    {
                        if (!player.IsInVehicle(_copCar)) { SetPhase(Phase.Regroup); return; }
                        // DEBOUNCED re-seat: warping the driver back every single frame is
                        // what turns a brief bail into the violent get-in/get-out loop (and
                        // resets the drive task each time). Only re-seat if they've actually
                        // been out for >2s, and keep them out of police AI so they don't bail.
                        if (Valid(_driver) && !_driver.IsInVehicle(_copCar))
                        {
                            if (_driverOutSince == DateTime.MinValue) _driverOutSince = DateTime.Now;
                            else if ((DateTime.Now - _driverOutSince).TotalSeconds > 2.0)
                            {
                                Function.Call(Hash.SET_PED_AS_COP, _driver, false);
                                Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, _copCar, -1);
                                _reseatCount++;
                                _driverOutSince = DateTime.MinValue;
                            }
                        }
                        else _driverOutSince = DateTime.MinValue;
                        // Patrol: re-kick the wander on a timer so it keeps driving (the task
                        // goes inert otherwise). 6s is long enough not to feel erratic.
                        if (CarStalled() && (DateTime.Now - _lastWander).TotalSeconds > 6.0)
                        {
                            _lastWander = DateTime.Now;
                            Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, _driver, _copCar, 18.0f, DRIVE_STYLE);
                        }

                        if ((DateTime.Now - _lastEngageScan).TotalSeconds > 1.5)
                        {
                            _lastEngageScan = DateTime.Now;
                            Ped engagedCop = FindNearbyEngagedCop(ENGAGEMENT_SEEK_RADIUS);
                            if (engagedCop != null)
                            {
                                Ped threat = GetCombatThreat(engagedCop);
                                if (threat != null) { StartAssist(threat); break; }
                            }

                            // Gunshots are a CALL, not ambience: any audible shooter in
                            // the area and the unit responds -- no line of sight needed,
                            // you can HEAR it.
                            Ped shooter = FindAudibleShooter(130f);
                            if (shooter != null)
                            {
                                Notify("~r~Dispatch (radio):~w~ Shots fired near " +
                                       World.GetStreetName(shooter.Position) + "! Closest unit, respond!");
                                Notify("~b~" + CopNames.For(_driver) + ":~w~ That's us! Hold onto something.");
                                StartAssist(shooter);
                                break;
                            }

                            // Visible street crime (carjackings, brawls) in the crew's
                            // line of sight gets the same enthusiasm.
                            Ped perp = FindVisibleCriminal(70f);
                            if (perp != null)
                            {
                                Notify("~b~" + CopNames.For(_driver) + ":~w~ You seeing this? Actual crime! In PUBLIC! Let's go!");
                                StartAssist(perp);
                                break;
                            }
                        }

                        // Between pursuits, dispatch occasionally raises a local radio
                        // call (shots fired nearby) for the unit to respond to.
                        if (SecondsInPhase > _rideCalloutDelay)
                        {
                            _rideCalloutDelay = double.MaxValue;   // one attempt per patrol stretch
                            if (TryStageCallout(player)) break;
                        }

                        if (SecondsInPhase > _ridePursuitDelay)
                        {
                            if (!StartPursuit()) { ResetRideDelay(); SetPhase(Phase.Riding); }
                        }
                        break;
                    }

                case Phase.Pursuit:
                    {
                        if (!_engaged && !player.IsInVehicle(_copCar)) { Notify("~y~You left the unit. Ride-along over."); Cleanup(); return; }
                        
                        Ped aliveSusp = AliveSuspect();
                        if (!Valid(_suspectCar) || aliveSusp == null)
                        {
                            if (_suspectsInnocent || _suspectThreat <= 0)
                            {
                                PlantEvidence();
                            }
                            else
                            {
                                Notify("~g~Dispatch:~w~ Suspect down. Now THAT'S community policing!");
                            }
                            StopNewsCam();   // pursuit's over -> don't leave the news cam stuck on a corpse
                            // Hold the scene like real officers (cover + work the body)
                            // instead of shrugging and driving off; falls through to
                            // Regroup when there's no body or no crew to do it.
                            BeginWrapup();
                            return;
                        }

                        // Escape Check (part 8)
                        float distToCops = _copCar.Position.DistanceTo(aliveSusp.Position);
                        if (distToCops > _escapeDistance)
                        {
                            if (_escapeTimerStarted == DateTime.MinValue)
                            {
                                _escapeTimerStarted = DateTime.Now;
                            }
                            else if ((DateTime.Now - _escapeTimerStarted).TotalSeconds > _escapeTimeLimit)
                            {
                                Notify("~y~Dispatch:~w~ Suspect got away! Call off the pursuit.");
                                EndSirens();
                                StopNewsCam();   // pursuit's over -> tear the news cam down
                                SetPhase(Phase.Regroup);
                                return;
                            }
                        }
                        else
                        {
                            _escapeTimerStarted = DateTime.MinValue;
                        }

                        PursuitTick();
                        break;
                    }

                case Phase.Wrapup:
                    HandleWrapup(player);
                    break;

                case Phase.UCDrive:
                    HandleUCDrive(player);
                    break;

                case Phase.UCStake:
                    HandleUCStake(player);
                    break;

                case Phase.Regroup:
                    HandleRegroup(player);
                    break;

                case Phase.Assist:
                    HandleAssist(player);
                    break;

                case Phase.Clearing:
                    HandleClearing(player);
                    break;
            }
        }

        // A nearby officer actively in a fight, EXCLUDING our own crew (driver, partner,
        // backup, heli). Note: this deliberately does NOT skip the whole FriendlyCops
        // registry -- AmbientPolice's staged officers live there too, and skipping them
        // made every real staged gunfight invisible to the assist scanner. The only
        // engagements it could find were momentary gang-AI executions, which were over
        // before the unit arrived ("rolling in!" -> instant "threat clear").
        private Ped FindNearbyEngagedCop(float radius)
        {
            if (!Valid(_copCar)) return null;
            foreach (Ped cop in WorldCache.GetNearbyPeds(_copCar.Position, radius))
            {
                if (!IsCopPed(cop) || cop.IsDead) continue;
                if (IsOwnUnit(cop)) continue;
                if (Function.Call<bool>(Hash.IS_PED_IN_COMBAT, cop, 0)) return cop;
            }
            return null;
        }

        // One of the ride-along's own peds: the unit, its backup waves, or the heli crew.
        private bool IsOwnUnit(Ped p)
        {
            if (p == null || !p.Exists()) return false;
            int h = p.Handle;
            if (Valid(_driver) && _driver.Handle == h) return true;
            if (Valid(_partner) && _partner.Handle == h) return true;
            if (_heliPilot != null && _heliPilot.Exists() && _heliPilot.Handle == h) return true;
            if (_heliGunner != null && _heliGunner.Exists() && _heliGunner.Handle == h) return true;
            foreach (Entity ent in _backupEntities)
            {
                Ped bp = ent as Ped;
                if (bp != null && bp.Exists() && bp.Handle == h) return true;
            }
            return false;
        }

        // A non-cop ped actively FIRING a weapon within earshot of the cruiser.
        // No line-of-sight requirement: gunshots are heard, not seen.
        private Ped FindAudibleShooter(float radius)
        {
            if (!Valid(_copCar)) return null;
            Ped player = Game.Player.Character;
            foreach (Ped p in WorldCache.GetNearbyPeds(_copCar.Position, radius))
            {
                if (p == null || !p.Exists() || p.IsDead || p == player) continue;
                if (IsCopPed(p) || RideAlongRegistry.FriendlyCops.Contains(p.Handle)) continue;
                if (IsSuspectPed(p)) continue;
                if (Function.Call<bool>(Hash.IS_PED_SHOOTING, p)) return p;
            }
            return null;
        }

        // A non-cop ped committing visible street crime (carjacking or brawling)
        // in the crew's actual line of sight.
        private Ped FindVisibleCriminal(float radius)
        {
            if (!Valid(_copCar) || !Valid(_driver)) return null;
            Ped player = Game.Player.Character;
            foreach (Ped p in WorldCache.GetNearbyPeds(_copCar.Position, radius))
            {
                if (p == null || !p.Exists() || p.IsDead || p == player) continue;
                if (IsCopPed(p) || RideAlongRegistry.FriendlyCops.Contains(p.Handle)) continue;
                if (IsSuspectPed(p)) continue;
                bool crime = Function.Call<bool>(Hash.IS_PED_JACKING, p)
                          || Function.Call<bool>(Hash.IS_PED_IN_MELEE_COMBAT, p);
                if (!crime) continue;
                if (!Function.Call<bool>(Hash.HAS_ENTITY_CLEAR_LOS_TO_ENTITY, _driver, p, 17)) continue;
                return p;
            }
            return null;
        }

        // Stage a local radio call between pursuits: a couple of armed troublemakers
        // firing off rounds a few blocks away. Dispatch raises it on the radio and
        // the unit responds through the normal assist flow (drive in, engage,
        // clear the scene). Returns false if no spot could be staged.
        private bool TryStageCallout(Ped player)
        {
            EnsureRelationships();
            Vector3 spot = FindHiddenSpawn(player.Position, 120f, 240f);
            if (spot == Vector3.Zero || spot.DistanceTo(player.Position) < 70f) return false;

            Ped first = null;
            int n = 2 + _rng.Next(2);   // 2-3 perps
            for (int i = 0; i < n; i++)
            {
                Ped p = World.CreatePed(new Model(i % 2 == 0 ? PedHash.StrPunk01GMY : PedHash.Lost01GMY),
                    spot + RandomOffset(1.5f + i));
                if (p == null || !p.Exists()) continue;
                Function.Call(Hash.SET_ENTITY_AS_MISSION_ENTITY, p, true, true);
                SetupSuspectPed(p, _rng.Next(100) < 30 ? 2 : 1);
                // Fire into the air so the call is AUDIBLE -- this is the gunfire the
                // radio is reporting (and it organically trips the crime-watch too).
                Function.Call(Hash.TASK_SHOOT_AT_COORD, p,
                    spot.X + 2f, spot.Y + 2f, spot.Z + 12f, 12000, unchecked((int)0xC6EE6B4C));
                _calloutPerps.Add(p);
                if (first == null) first = p;
            }
            if (first == null) return false;

            string street = World.GetStreetName(spot);
            Notify("~r~Dispatch (radio):~w~ All units: shots fired near " + street + ". Closest unit, respond!");
            Notify("~b~" + CopNames.For(_driver) + ":~w~ Unit 23 responding! That's us. We're Unit 23 today.");
            StartAssist(first);
            return true;
        }

        // Hand staged callout perps back to the engine (dead ones stay for EMS).
        private void ReleaseCalloutPerps()
        {
            foreach (Ped p in _calloutPerps)
                if (p != null && p.Exists()) p.MarkAsNoLongerNeeded();
            _calloutPerps.Clear();
        }

        private Ped GetCombatThreat(Ped engagedCop)
        {
            if (!Valid(engagedCop)) return null;
            Ped player = Game.Player.Character;
            // ONLY a ped that is actually FIGHTING qualifies as a threat. The old
            // nearest-non-combat fallback handed the unit whoever happened to be
            // standing (or dying) closest to the shooter -- usually the gang-AI cop's
            // own execution victim, who was dead a beat later. That's what produced
            // "backing them up!" followed instantly by "threat is clear": the unit
            // was dispatched at a corpse-to-be, not a combatant.
            Ped best = null;
            float bestD = float.MaxValue;
            foreach (Ped p in WorldCache.GetNearbyPeds(engagedCop.Position, 55f))
            {
                if (p == null || !p.Exists() || p.IsDead) continue;
                if (IsCopPed(p) || p == player) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(p.Handle)) continue;
                if (!Function.Call<bool>(Hash.IS_PED_IN_COMBAT, p, 0)) continue;
                float d = p.Position.DistanceTo(engagedCop.Position);
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        private Ped AliveSuspect()
        {
            foreach (Ped p in _suspectPeds) if (Valid(p)) return p;
            return null;
        }

        private void LockIntoCar(Ped p)
        {
            if (!Valid(p)) return;
            // The original mod creator used SET_PED_KEEP_TASK here, which permanently 
            // locked the AI into an IDLE state before the driving task was even issued,
            // causing them to reject all pathing commands and freeze with speed 0.0!
            Function.Call(Hash.SET_PED_CAN_BE_DRAGGED_OUT, p, false);
            Function.Call(Hash.SET_PED_STAY_IN_VEHICLE_WHEN_JACKED, p, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_CanLeaveVehicle, false); // stay in the car
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_FleeWhilstInVehicle, false); // Prevent fleeing in vehicle
        }

        private void EnsureDriverSeated()
        {
            if (!Valid(_driver) || _driver.IsInVehicle(_copCar)) return;
            // Never warp the cop onto a player who grabbed the wheel (legacy edge --
            // exclusive-driver normally prevents the player from being there at all).
            Ped seated = Function.Call<Ped>(Hash.GET_PED_IN_VEHICLE_SEAT, _copCar, -1);
            if (seated != null && seated.Exists() && seated == Game.Player.Character) return;
            Function.Call(Hash.SET_PED_INTO_VEHICLE, _driver, _copCar, -1);
        }

        // Keep the cruiser open for the player (passenger + rear seats) while reserving the
        // driver door for the AI. Police vehicles default to locked-for-player, so this is
        // re-asserted whenever the player is out, otherwise they can't climb back in.
        private void LockCarForRide()
        {
            if (!Valid(_copCar)) return;
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED, _copCar, 1);               // whole car enterable
            Function.Call(Hash.SET_VEHICLE_DOORS_LOCKED_FOR_PLAYER, _copCar, Game.Player.Handle, false);
            // Explicitly UNLOCK the driver door. We used to lock it to "reserve" the driver
            // seat (state 2, then 3), but locking it -- even lockout-player-only -- stopped the
            // AI driver from re-entering after he bailed out to fight: he'd walk to the door,
            // fail to open it, walk off, and retry forever (the get-in/get-out loop). The
            // driver can't be jacked (LockIntoCar pins him), so an open door is safe; on the
            // rare frame the player grabs an empty driver seat they can just hop out again.
            Function.Call(Hash.SET_VEHICLE_INDIVIDUAL_DOORS_LOCKED, _copCar, 0, 1); // driver door UNLOCKED
        }

        private void PromoteDriverIfNeeded()
        {
            if (Valid(_driver)) return;
            if (Valid(_partner))
            {
                _driver = _partner;
                _partner = null;
                MakeRideAlongDriver(_driver);
                if (Valid(_copCar))
                {
                    // Unlock front right passenger door now that it's vacant
                    Function.Call(Hash.SET_VEHICLE_INDIVIDUAL_DOORS_LOCKED, _copCar, 1, 1); // Front Right (Passenger) Unlocked
                    // Re-point the exclusive-driver slot at the promoted officer so the
                    // player still can't end up behind the wheel.
                    Function.Call(Hash.SET_VEHICLE_EXCLUSIVE_DRIVER, _copCar, _driver, 0);
                }
            }
        }

        private static bool IsDriveable(Vehicle v)
        {
            return v != null && v.Exists() && Function.Call<bool>(Hash.IS_VEHICLE_DRIVEABLE, v, false);
        }

        private void ResetRideDelay()
        {
            // Gap before the next pursuit (tunable in the .ini). Defaults 25-55s so
            // pursuits feel like events, not a constant back-to-back stream.
            float span = Math.Max(0f, _pursuitDelayMax - _pursuitDelayMin);
            _ridePursuitDelay = _pursuitDelayMin + _rng.NextDouble() * span;
            // Local radio calls land on their own clock; when this rolls shorter
            // than the pursuit delay, the patrol stretch gets a callout instead.
            _rideCalloutDelay = 30.0 + _rng.NextDouble() * 45.0;
        }

        private void AssignCop(Ped c)
        {
            if (!Valid(c)) return;
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, c, _copsGroup);
            CopNames.Apply(c);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_AlwaysFight, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_FightArmedWhenUnarmed, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanUseCover, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_CanFlank, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_UseDynamicStrafe, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, c, CA_BlindFireInCover, false);
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, c, 2); // advance
            MakeGoodDriver(c);
        }

        private void MakeGoodDriver(Ped p)
        {
            if (!Valid(p)) return;
            Function.Call(Hash.SET_DRIVER_ABILITY, p, 1.0f);
            // Lower aggression (tunable; default 0.3): high aggression is what makes the AI
            // barge off the road and plow through obstacles. At full ability + low aggression
            // they still drive fast, but thread the roads instead of ramming/cutting corners.
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, p, _driverAggressiveness);
            Function.Call(Hash.SET_PED_STEERS_AROUND_VEHICLES, p, true);
            Function.Call(Hash.SET_PED_STEERS_AROUND_OBJECTS, p, true);
            Function.Call(Hash.SET_PED_STEERS_AROUND_PEDS, p, true);
            // NOTE: deliberately NOT setting PreferNavmeshInChase -- on Enhanced it can
            // push the AI off the road grid (driving over sidewalks/terrain) instead of
            // following roads, which wrecks normal driving. Let it use the road network.
        }

        private void MakeRideAlongDriver(Ped p)
        {
            if (!Valid(p)) return;

            // THE core fix for "won't drive / get-in-get-out loop": take the ped OUT of
            // the game's police dispatch AI. As a PedType.Cop, GTA's law-enforcement AI
            // keeps re-asserting its own scenario/idle behavior -- it ignores our scripted
            // drive task (car sits at 0.0) and tries to make the ped exit to "do cop
            // things" (the get-in/get-out fight with our re-seat logic). With this off, the
            // script actually owns the ped and the drive task takes. (PedType is unchanged,
            // so IsCopPed and the friendly-cop registry still work.)
            Function.Call(Hash.SET_PED_AS_COP, p, false);

            MakeGoodDriver(p);
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, p, _driverAggressiveness); // calmer = stays on the road
            // Keep the driver in the car: with events unblocked it'll otherwise bail out
            // to react/fight, fighting our re-seat logic (the get-in/get-out loop).
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_CanUseVehicles, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_CanLeaveVehicle, false);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_FleeWhilstInVehicle, false); // Prevent fleeing in vehicle
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_DisableBlockFromPursueDuringVehicleChase, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_DisableSpinOutDuringVehicleChase, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_DisableCruiseInFrontDuringBlockDuringVehicleChase, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_DisablePullAlongsideDuringVehicleChase, true);
            // Force the chase to follow the ROAD network instead of cutting across navmesh
            // (sidewalks/terrain/props). Navmesh-in-chase is exactly what makes the cruiser
            // plow into objects with no real pathing during a pursuit.
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_PreferNavmeshInChase, false);
            Function.Call(Hash.SET_PED_STAY_IN_VEHICLE_WHEN_JACKED, p, true);
        }

        // Serious hardware for the rare special units: operator armor/health and a
        // proper long gun. Applied AFTER CopNames.Apply so this loadout wins.
        private void OutfitEliteUnit(Ped c)
        {
            if (!Valid(c) || _eliteUnit == 0) return;
            Function.Call(Hash.SET_PED_ARMOUR, c, _eliteUnit == 1 ? 100 : 75);
            Function.Call(Hash.SET_ENTITY_MAX_HEALTH, c, 300);
            Function.Call(Hash.SET_ENTITY_HEALTH, c, 300);
            WeaponHash primary = _eliteUnit == 1 ? WeaponHash.CarbineRifle
                               : _eliteUnit == 2 ? WeaponHash.SpecialCarbine
                                                 : WeaponHash.AdvancedRifle;
            WeaponHash sidearm = _eliteUnit == 3 ? WeaponHash.APPistol : WeaponHash.CombatPistol;
            Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)sidearm), 200, false, false);
            Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)primary), 400, false, true);
            if (_eliteUnit == 1) // NOOSE brings the toys
                Function.Call(Hash.GIVE_WEAPON_TO_PED, c, unchecked((int)(uint)WeaponHash.SmokeGrenade), 3, false, false);
            Function.Call(Hash.SET_PED_ACCURACY, c, 80);
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, c, 2);
        }

        // Performance + armor mods so the special unit's ride feels decked out.
        private static void DeckOutVehicle(Vehicle v)
        {
            if (v == null || !v.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0);
            Function.Call(Hash.SET_VEHICLE_MOD, v, 11, Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, v, 11) - 1, false); // engine
            Function.Call(Hash.SET_VEHICLE_MOD, v, 12, Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, v, 12) - 1, false); // brakes
            Function.Call(Hash.SET_VEHICLE_MOD, v, 13, Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, v, 13) - 1, false); // transmission
            Function.Call(Hash.SET_VEHICLE_MOD, v, 16, Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, v, 16) - 1, false); // armor
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, 18, true);  // turbo
            Function.Call(Hash.SET_VEHICLE_TYRES_CAN_BURST, v, false);
        }

        // Put a unit back into calm, road-following PATROL driving after a pursuit/assist.
        // A chase leaves the driver on an aggressive vehicle-chase/ram task (and the
        // AvoidTraffic chase style). If we don't CLEAR that and re-issue a normal drive, the
        // cruiser keeps the chase behavior -- cutting corners and plowing off the road into
        // buildings. That's the "AI breaks after one pursuit" bug.
        private void RestorePatrolDriving(Ped p)
        {
            if (!Valid(p)) return;
            Function.Call(Hash.SET_PED_AS_COP, p, false);   // script owns the ped, not police dispatch
            Function.Call(Hash.CLEAR_PED_TASKS, p);          // drop the leftover chase/ram task
            MakeGoodDriver(p);                               // full skill, low aggression, steer around stuff
            Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, p, _driverAggressiveness);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_PreferNavmeshInChase, false); // follow roads
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_CanLeaveVehicle, false);       // stay in the car
        }

        // Resume normal patrol after a pursuit/assist: clean both officers' driving, drop the
        // siren, and hand the driver a fresh lawful wander task right now (don't wait for the
        // stall timer, or the leftover chase task keeps driving the car off-road first).
        private void ResumePatrol()
        {
            RestorePatrolDriving(_driver);
            if (Valid(_partner)) RestorePatrolDriving(_partner);
            EndSirens();
            if (Valid(_driver) && Valid(_copCar))
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, _driver, _copCar, 18.0f, DRIVE_STYLE);
            _lastWander = DateTime.Now;
            _lastCarMoving = DateTime.Now;
            ResetRideDelay();
            SetPhase(Phase.Riding);
        }

        // The nearest ped (civilian or non-friendly cop) the player has injured or
        // killed, or null. Suspects and the ride's own officers don't count.
        private Ped FindPlayerVictim()
        {
            Ped player = Game.Player.Character;
            if (player == null) return null;

            foreach (Ped p in WorldCache.GetNearbyPeds(player.Position, 80f))
            {
                if (p == null || !p.Exists() || p == player) continue;
                if (RideAlongRegistry.FriendlyCops.Contains(p.Handle)) continue;
                if (IsSuspectPed(p)) continue;

                // Only count actual damage (injuring or killing them)
                if ((p.IsDead || p.Health < p.MaxHealth) &&
                    Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, p, player, true))
                {
                    return p;
                }
            }
            return null;
        }

        // When the two officers are together (in the cruiser or standing at a
        // scene), they swap a compliment and a shot at the suspects.
        private void BanterTick()
        {
            if (!Valid(_driver) || !Valid(_partner)) return;
            if (_phoneOpen) return;
            if ((DateTime.Now - _lastBanter).TotalSeconds < _banterDelay) return;
            if (_driver.Position.DistanceTo(_partner.Position) > 9f) return;

            _lastBanter = DateTime.Now;
            _banterDelay = 55.0 + _rng.NextDouble() * 55.0;   // every ~1-2 minutes

            int i = _rng.Next(BanterA.Length);
            Notify("~b~" + CopNames.For(_driver) + ":~w~ " + BanterA[i]);
            Notify("~b~" + CopNames.For(_partner) + ":~w~ " + BanterB[i]);
            QIAudio.PlayBanter(i);
            CopBark(_partner, "GENERIC_HOWS_IT_GOING");
        }

        private void RadioChatter()
        {
            Ped speaker = Valid(_driver) ? _driver : (Valid(_partner) ? _partner : null);
            if (speaker == null) return;
            string who = CopNames.For(speaker);
            int i = _rng.Next(Radio.Length);
            Notify("~b~" + who + ":~w~ " + Radio[i]);
            QIAudio.PlayRadio(i);
            CopBark(speaker, "GENERIC_CURSE_HIGH");
        }

        private void WelcomeAboard()
        {
            string who = Valid(_driver) ? CopNames.For(_driver) : "Officer";
            int i = _rng.Next(Welcome.Length);
            Notify("~g~" + who + ":~w~ " + Welcome[i]);
            QIAudio.PlayWelcome(i); // Play welcome voice clip
            CopBark(_driver, "GENERIC_HOWS_IT_GOING");
        }

        private void ShowHelp(string text)
        {
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);
        }

        private void CopBark(Ped p, string ctx)
        {
            if (Valid(p))
                Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, p, ctx, "SPEECH_PARAMS_FORCE_SHOUTED", 1);
        }

        private Vector3 RandomOffset(float r)
        {
            double ang = _rng.NextDouble() * Math.PI * 2.0;
            return new Vector3((float)(Math.Cos(ang) * r), (float)(Math.Sin(ang) * r), 0f);
        }

        private Vector3 SnapToRoad(Vector3 p)
        {
            float[] zOffsets = new float[] { 0f, 5f, -5f, 15f, -15f, 30f, -30f, 50f, -50f, 100f, -100f };
            foreach (float z in zOffsets)
            {
                Vector3 testPos = new Vector3(p.X, p.Y, p.Z + z);
                
                // Use OutputArgument to grab the native's Vector3 pointer
                try
                {
                    OutputArgument o = new OutputArgument();
                    if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE, testPos.X, testPos.Y, testPos.Z, o, 1, 3.0f, 0f)) return o.GetResult<Vector3>();
                    if (Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE, testPos.X, testPos.Y, testPos.Z, o, 0, 3.0f, 0f)) return o.GetResult<Vector3>();
                }
                catch { }
            }

            Vector3 builtIn = World.GetNextPositionOnStreet(p);
            if (builtIn != Vector3.Zero) return builtIn;

            return Vector3.Zero;
        }

        private Vector3 FindHiddenSpawn(Vector3 around, float minDist, float maxDist)
        {
            // Reject any result that is closer than 60f.
            float minAccept = 60f;

            // Pass 1: off-camera road node at the right distance.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                float dist = minDist + (float)_rng.NextDouble() * (maxDist - minDist);
                Vector3 node = SnapToRoad(around + RandomOffset(dist));
                if (node != Vector3.Zero
                    && node.DistanceTo(around) >= minAccept
                    && !Function.Call<bool>(Hash.IS_SPHERE_VISIBLE, node.X, node.Y, node.Z, 3.0f))
                    return node;
            }
            // Pass 2: visible OK, but must still be far enough away.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                float dist = minDist + (float)_rng.NextDouble() * (maxDist - minDist);
                Vector3 node = SnapToRoad(around + RandomOffset(dist));
                if (node != Vector3.Zero && node.DistanceTo(around) >= minAccept)
                    return node;
            }
            // Pass 3: Try a closer road node off-camera.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                float dist = 50f + (float)_rng.NextDouble() * 20f;
                Vector3 node = SnapToRoad(around + RandomOffset(dist));
                if (node != Vector3.Zero
                    && node.DistanceTo(around) >= 45f
                    && !Function.Call<bool>(Hash.IS_SPHERE_VISIBLE, node.X, node.Y, node.Z, 3.0f))
                    return node;
            }
            // Absolute last resort: snap a random direction offset.
            Vector3 rawOffset = around + RandomOffset(minDist);
            Vector3 snappedRaw = SnapToRoad(rawOffset);
            if (snappedRaw != Vector3.Zero && snappedRaw.DistanceTo(around) >= 45f)
                return snappedRaw;

            return rawOffset;
        }

        private void Cleanup()
        {
            try
            {
                if (Game.Player != null)
                {
                    Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, false);
                    Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5); // restore normal wanted system
                }
            }
            catch { }

            RideAlongRegistry.FriendlyCops.Clear();
            foreach (Entity ent in _backupEntities) { if (ent != null && ent.Exists()) ent.MarkAsNoLongerNeeded(); }
            _backupEntities.Clear();
            _backupCount = 0;
            foreach (Ped s in _suspectPeds) { if (Valid(s)) s.MarkAsNoLongerNeeded(); }
            _suspectPeds.Clear();
            if (Valid(_suspect)) _suspect.MarkAsNoLongerNeeded();
            if (Valid(_suspectCar)) _suspectCar.MarkAsNoLongerNeeded();
            if (Valid(_suspectCar2)) _suspectCar2.MarkAsNoLongerNeeded();
            ReleaseHeli();
            StopNewsCam();
            if (_copBlip != null && _copBlip.Exists()) _copBlip.Delete();
            _copBlip = null;
            if (Valid(_driver)) { CopNames.Forget(_driver.Handle); _driver.MarkAsNoLongerNeeded(); }
            if (Valid(_partner)) { CopNames.Forget(_partner.Handle); _partner.MarkAsNoLongerNeeded(); }
            if (Valid(_copCar))
            {
                Function.Call(Hash.SET_VEHICLE_EXCLUSIVE_DRIVER, _copCar, 0, 0); // release the seat claim
                _copCar.MarkAsNoLongerNeeded();
            }
            _suspect = null; _suspectCar = null; _suspectCar2 = null; _driver = null; _partner = null; _copCar = null;
            _engaged = false; _pitting = false;
            _threat = null; _assistEngaged = false;
            _wrapBody = null;
            ReleaseCalloutPerps();
            ResetProgression();   // fresh officers next ride start at Level 1
            SetPhase(Phase.Idle);
        }

        private Camera _newsCam = null;   // the heli gimbal cam (see RideAlong.HeliCam.cs)

        private void PlantEvidence()
        {
            // NOTE: this fires for a DEAD suspect, so we must check existence directly --
            // Valid() resolves to the Entity overload, which is false for any dead ped, so
            // using it here made the whole "plant a weapon on the body" beat never run.
            Ped deadSuspect = (_suspect != null && _suspect.Exists() && _suspect.IsDead) ? _suspect : null;
            if (deadSuspect == null)
            {
                foreach (Ped p in _suspectPeds) { if (p != null && p.Exists() && p.IsDead) { deadSuspect = p; break; } }
            }
            if (deadSuspect == null) { Notify("~g~Dispatch:~w~ Suspect dealt with. Good work."); return; }

            Ped closestCop = Valid(_driver) ? _driver : (Valid(_partner) ? _partner : null);
            if (Valid(closestCop) && !closestCop.IsInVehicle())
            {
                Function.Call(Hash.TASK_GO_TO_ENTITY, closestCop, deadSuspect, -1, 1.5f, 1.0f, 1073741824, 0);
            }
            Notify("~b~" + CopNames.For(closestCop) + ":~w~ Found a weapon on the suspect! Good shoot, everyone.");
            Notify("~g~Dispatch:~w~ Excellent work. Suspect is down.");
        }

        private static bool Valid(Entity ent) { return ent != null && ent.Exists() && !ent.IsDead; }
        private static bool Valid(Vehicle v) { return v != null && v.Exists(); }

        private static bool IsCopPed(Ped p) { return Cops.IsCop(p); }

        private static string SeatName(int s) { return s == 0 ? "front passenger" : "rear"; }

        private void EnsureRelationships()
        {
            if (_relsReady) return;
            OutputArgument a = new OutputArgument();
            OutputArgument b = new OutputArgument();
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "QI_PURSUIT_COPS", a);
            Function.Call(Hash.ADD_RELATIONSHIP_GROUP, "QI_PURSUIT_SUSP", b);
            _copsGroup = a.GetResult<int>();
            _suspGroup = b.GetResult<int>();
            _playerGroup = Function.Call<int>(Hash.GET_PED_RELATIONSHIP_GROUP_HASH, Game.Player.Character);
            // 5 = hate, 1 = respect
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _copsGroup, _suspGroup);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _suspGroup, _copsGroup);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 1, _copsGroup, _playerGroup);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 1, _playerGroup, _copsGroup);

            // --- Co-operate with the game's own police (issue 4) ---
            // Wire the NATIVE cop/SWAT relationship groups in too, so local PD actually
            // works WITH the ride-along instead of ignoring it:
            //   - native cops HATE our designated suspects -> they join the pursuit and
            //     open fire on the same target on sight, rather than standing around.
            //   - native cops RESPECT our unit -> no friendly fire between the ride-along
            //     officers and local PD; they fight side by side.
            // (Empty groups after a ride are harmless; these are global, set once.)
            int copHash  = Function.Call<int>(Hash.GET_HASH_KEY, "COP");
            int swatHash = Function.Call<int>(Hash.GET_HASH_KEY, "SWAT");
            int[] lawGroups = { copHash, swatHash };
            foreach (int g in lawGroups)
            {
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, g, _suspGroup);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 5, _suspGroup, g);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 1, g, _copsGroup);
                Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, 1, _copsGroup, g);
            }
            _relsReady = true;
        }

        private void SetupSuspectPed(Ped p, int threat)
        {
            if (!Valid(p)) return;
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, p, _suspGroup); // cops hate them either way
            if (threat <= 0) return;   // innocent: left unarmed; panics when the badge opens fire

            int armour = threat >= 3 ? 100 : (threat == 2 ? 50 : 0);
            if (armour > 0) Function.Call(Hash.SET_PED_ARMOUR, p, armour);

            WeaponHash wh;
            if (threat >= 3)      wh = _rng.Next(2) == 0 ? WeaponHash.CarbineRifle : WeaponHash.AdvancedRifle;
            else if (threat == 2) wh = _rng.Next(2) == 0 ? WeaponHash.AssaultRifle : WeaponHash.SMG;
            else                  wh = _rng.Next(2) == 0 ? WeaponHash.Pistol       : WeaponHash.MicroSMG;
            Function.Call(Hash.GIVE_WEAPON_TO_PED, p, unchecked((int)(uint)wh), 250, false, true);

            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_AlwaysFight, true);   // always fight
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_FightArmedWhenUnarmed, true);
            Function.Call(Hash.SET_PED_ACCURACY, p, 25 + threat * 12 + _rng.Next(10));
            if (threat >= 2)
            {
                int hp = 200 + threat * 30;
                Function.Call(Hash.SET_ENTITY_MAX_HEALTH, p, hp);
                Function.Call(Hash.SET_ENTITY_HEALTH, p, hp);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, CA_CanUseCover, true);  // use cover
            }
        }

        private string SuspectThreatLine()
        {
            string street = World.GetStreetName(_suspectCar.Position);
            string vehName = _suspectCar.LocalizedName;
            string baseMsg = string.Format("In pursuit of a {0} on {1}.", vehName, street);

            if (_suspectsInnocent) return string.Format("~r~Dispatch:~w~ {0} Suspect is... probably innocent. Engage anyway.", baseMsg);
            if (_suspectThreat >= 3) return string.Format("~r~Dispatch:~w~ {0} Heavily armed crew WITH backup - AIR SUPPORT AND SWAT INBOUND. This is a war now!", baseMsg);
            if (_suspectThreat == 2) return string.Format("~r~Dispatch:~w~ {0} Suspects are geared up and armoured - escalating the response!", baseMsg);
            return string.Format("~r~Dispatch:~w~ {0} Suspect armed and dangerous - weapons free!", baseMsg);
        }

        private void DrawHUD()
        {
            // Heli sensor feed active -> it replaces the normal overlay for visual clarity.
            if (_newsCam != null && _newsCam.Exists())
            {
                UpdateHeliCam();
                DrawHeliCamHud();
                return;
            }

            if (_phase == Phase.Pursuit || _engaged)
            {
                var time = DateTime.Now - _lastPursuitStart;
                string threatLevelStr = _suspectThreat == 0 ? "LOW (Innocent)" : _suspectThreat == 1 ? "MEDIUM" : _suspectThreat == 2 ? "HIGH (Armor)" : "CRITICAL (SWAT)";
                new GTA.UI.TextElement(string.Format("THREAT: {0}", threatLevelStr), new System.Drawing.PointF(10f, 10f), 0.45f, System.Drawing.Color.Red).Draw();
                new GTA.UI.TextElement(string.Format("BACKUP UNITS: {0}", _backupCount), new System.Drawing.PointF(10f, 30f), 0.45f, System.Drawing.Color.White).Draw();
                new GTA.UI.TextElement(string.Format("PURSUIT TIME: {0:D2}:{1:D2}", time.Minutes, time.Seconds), new System.Drawing.PointF(10f, 50f), 0.45f, System.Drawing.Color.Yellow).Draw();
                new GTA.UI.TextElement("COMMANDS: UP (Ram), DOWN (Back Off), RIGHT (Drive-by), LEFT (Heli Cam)", new System.Drawing.PointF(10f, 70f), 0.35f, System.Drawing.Color.LightGray).Draw();
            }
        }

        private DateTime _lastPursuitStart = DateTime.Now;

        private void CheckCommands()
        {
            if (_phase != Phase.Pursuit && !_engaged) return;
            if (Game.Player.Character == null || !Game.Player.Character.IsInVehicle(_copCar)) return;
            // Dispatch phone is up -> it owns the D-pad (menu nav). Don't also ram/zoom/toggle
            // the cam off the same presses.
            if (_phoneOpen) return;

            // D-Pad Left always toggles the heli sensor feed.
            bool camOn = _newsCam != null;
            if (Game.IsControlJustPressed(GTA.Control.PhoneLeft)) ToggleNewsChopperCamera();

            if (camOn)
            {
                // While the feed is up, the D-pad drives the CAMERA, not the cruiser:
                // Up/Down = optical zoom, Right = cycle EO/Night-Vision/Thermal optics.
                if (Game.IsControlJustPressed(GTA.Control.PhoneUp)) HeliCamZoomStep(+1);
                if (Game.IsControlJustPressed(GTA.Control.PhoneDown)) HeliCamZoomStep(-1);
                if (Game.IsControlJustPressed(GTA.Control.PhoneRight)) HeliCamCycleOptics();
                return;
            }

            // D-Pad Up: Ram
            if (Game.IsControlJustPressed(GTA.Control.PhoneUp))
            {
                if (Valid(_driver))
                {
                    Notify("~b~You:~w~ Ram them!");
                    _lastPit = DateTime.MinValue; // reset pit cooldown
                    _pitting = false;
                    TryPit();
                }
            }
            // D-Pad Down: Back off
            if (Game.IsControlJustPressed(GTA.Control.PhoneDown))
            {
                if (Valid(_driver))
                {
                    Notify("~b~You:~w~ Back off!");
                    _idealFollowDistance += 10f;
                    if (_idealFollowDistance > 50f) _idealFollowDistance = 50f;
                    Function.Call(Hash.SET_TASK_VEHICLE_CHASE_IDEAL_PURSUIT_DISTANCE, _driver, _idealFollowDistance);
                }
            }
            // D-Pad Right: Light 'em up
            if (Game.IsControlJustPressed(GTA.Control.PhoneRight))
            {
                if (Valid(_partner))
                {
                    Notify("~b~You:~w~ Light 'em up!");
                    _engageDistanceThreshold += 15f; // allow engaging earlier
                    if (_engageDistanceThreshold > 120f) _engageDistanceThreshold = 120f;
                    _engageSpeedThreshold += 5f;
                    if (_engageSpeedThreshold > 40f) _engageSpeedThreshold = 40f;
                    DriveBy(_partner, _suspect);
                }
            }
        }

        // TEMP diagnostic: shows the cruiser's live state so we can see whether the
        // drive tasks are taking effect (speed), whether the driver is in the seat,
        // and whether the engine is on. Top-left, cyan.
        private void DrawDebug()
        {
            if (_phase == Phase.Idle || !_showDebugHud) return;
            bool carOk = _copCar != null && _copCar.Exists();
            // Is _driver actually the one in the DRIVER seat (-1)? "seat" alone wasn't
            // proving that; show the real occupant so we can tell if the AI even has a
            // wheelman. "isDrv" = our driver is genuinely seat -1.
            bool isDrv = false;
            if (carOk && Valid(_driver))
            {
                Ped seated = Function.Call<Ped>(Hash.GET_PED_IN_VEHICLE_SEAT, _copCar, -1);
                isDrv = seated != null && seated.Handle == _driver.Handle;
            }
            string drv = !Valid(_driver) ? "DEAD" : (isDrv ? "DRV" : (_driver.IsInVehicle(_copCar) ? "pax" : "OUT"));
            float spd = carOk ? _copCar.Speed : -1f;
            bool eng = carOk && _copCar.IsEngineRunning;
            Ped pl = Game.Player.Character;
            float dist = (carOk && pl != null && pl.Exists()) ? _copCar.Position.DistanceTo(pl.Position) : -1f;
            bool col = carOk && Function.Call<bool>(Hash.HAS_COLLISION_LOADED_AROUND_ENTITY, _copCar);
            string method = _driveMethod == 2 ? "WANDER" : (_driveMethod == 1 ? "MISSION" : "LONGRANGE");
            string txt = string.Format("QI {0} | spd {1:F1} | drv {2} | eng {3} | dist {4:F0} | col {5} | grnd {6} | {7} | moved {8} | reseat {9}",
                _phase, spd, drv, eng ? "on" : "OFF", dist, col ? "Y" : "N", _copGrounded ? "Y" : "N",
                method, _everMoved ? "Y" : "N", _reseatCount);
            new GTA.UI.TextElement(txt, new System.Drawing.PointF(10f, 130f), 0.4f,
                System.Drawing.Color.Cyan).Draw();
        }

        private static void Notify(string msg) { GTA.UI.Notification.PostTicker(msg, false); }
    }

    internal static class RideAlongRegistry
    {
        public static readonly System.Collections.Generic.HashSet<int> FriendlyCops =
            new System.Collections.Generic.HashSet<int>();
        public static bool Active = false;
    }

    internal static class CopNames
    {
        private class Cop
        {
            public int RankIdx; public string Name; public string Display;
            public WeaponHash Primary; public WeaponHash Secondary;
        }

        private static readonly System.Collections.Generic.Dictionary<int, Cop> Assigned =
            new System.Collections.Generic.Dictionary<int, Cop>();
        private static readonly System.Collections.Generic.HashSet<string> UsedNames =
            new System.Collections.Generic.HashSet<string>();
        private static readonly Random Rng = new Random();
        private static int _badge = 1337;

        // Names are composed at random as First "Nickname" Last (e.g.
        // Officer Mike "BigBalls" Johnson, Sergeant Sam "Leadspitter" Tucker).
        // Three pools give tens of thousands of unique combos; PickName tracks
        // what's in play so two cops on screen never share the exact same one.
        private static readonly string[] FirstNames =
        {
            "Mike", "Sam", "Chad", "Brad", "Hank", "Duke", "Rex", "Gus", "Roy",
            "Earl", "Cole", "Wade", "Buck", "Dale", "Cliff", "Vince", "Lou",
            "Sal", "Moe", "Stan", "Karl", "Bart", "Chip", "Gary", "Randy",
            "Dwayne", "Butch", "Rod", "Tank", "Big Tony", "Darlene", "Brenda",
            "Wanda", "Cheryl", "Peggy", "Donna", "Sandy", "Bobbi", "Connie", "Trish"
        };

        private static readonly string[] Nicknames =
        {
            "BigBalls", "Leadspitter", "Trigger", "Knuckles", "Boomstick",
            "No-Knock", "Two-Taze", "Lights-Out", "Overtime", "Paperwork",
            "Warrant", "Chokehold", "Friendly-Fire", "Reachin'", "Felt-Threatened",
            "Qualified", "Pension", "Donut", "Nightstick", "Mag-Dump",
            "Cam-Off", "Lawsuit", "Settlement", "Probable-Cause", "Stop-Resistin'",
            "Roid-Rage", "Hair-Trigger", "Collateral", "Whoopsie", "Tasey",
            "Buckshot", "Skullcracker", "Loose-Cannon", "Maverick", "Itchy",
            "Sledge", "Ramrod", "Voltage", "Bonecrusher", "Cuffs",
            "Pepper-Spray", "Warning-Shot", "Discount-Rambo", "The-Hammer", "Splatter"
        };

        private static readonly string[] LastNames =
        {
            "Johnson", "Tucker", "McGraw", "Bronson", "Kowalski", "Hammergren",
            "Rodriguez", "Petrov", "O'Malley", "Banks", "Cruz", "Steele",
            "Boyd", "Hardin", "Vance", "Mercer", "Whitaker", "Doyle", "Briggs",
            "Hauser", "Calhoun", "Romano", "Fletcher", "Dunlap", "Stone",
            "Becker", "Knox", "Crowe", "Mathis", "Sully", "Pruitt", "Garcia",
            "Holloway", "Vasquez", "Decker", "Burns", "Hayes", "Webb", "Cross", "Slaughter"
        };

        private struct RankDef
        {
            public string Title; public int Health; public int Accuracy; public int ShootRate;
            public WeaponHash[] Primaries;
            public RankDef(string t, int h, int a, int s, WeaponHash[] w)
            { Title = t; Health = h; Accuracy = a; ShootRate = s; Primaries = w; }
        }

        private static readonly WeaponHash[] Pistols =
        {
            WeaponHash.Pistol, WeaponHash.CombatPistol, WeaponHash.SNSPistol,
            WeaponHash.HeavyPistol, WeaponHash.VintagePistol, WeaponHash.Pistol50
        };

        private static readonly RankDef[] Ranks =
        {
            new RankDef("Officer",    200, 45, 550,  new[] { WeaponHash.Pistol, WeaponHash.CombatPistol, WeaponHash.PumpShotgun }),
            new RankDef("Corporal",   260, 55, 650,  new[] { WeaponHash.PumpShotgun, WeaponHash.SawnOffShotgun, WeaponHash.MicroSMG }),
            new RankDef("Sergeant",   340, 66, 800,  new[] { WeaponHash.AssaultShotgun, WeaponHash.SMG, WeaponHash.CarbineRifle }),
            new RankDef("Lieutenant", 440, 78, 950,  new[] { WeaponHash.CarbineRifle, WeaponHash.SpecialCarbine, WeaponHash.MarksmanRifle }),
            new RankDef("Captain",    560, 90, 1000, new[] { WeaponHash.MarksmanRifleMk2, WeaponHash.HeavySniper, WeaponHash.CombatMG })
        };

        private static int RollRank()
        {
            int r = Rng.Next(100);
            if (r < 50) return 0;
            if (r < 75) return 1;
            if (r < 90) return 2;
            if (r < 98) return 3;
            return 4;
        }

        private static Cop Ensure(Ped p)
        {
            Cop c;
            if (Assigned.TryGetValue(p.Handle, out c)) return c;
            string name = PickName();
            int rank = RollRank();
            RankDef rd = Ranks[rank];
            c = new Cop
            {
                RankIdx = rank,
                Name = name,
                Display = rd.Title + " " + name,
                Primary = rd.Primaries[Rng.Next(rd.Primaries.Length)],
                Secondary = Pistols[Rng.Next(Pistols.Length)]
            };
            Assigned[p.Handle] = c;
            return c;
        }

        private static string PickName()
        {
            // Roll a fresh First "Nickname" Last; retry a handful of times if the
            // exact combo is already on an active cop. With ~40^3 combinations a
            // collision is rare, so a few tries is plenty before we bail to a badge #.
            for (int attempt = 0; attempt < 24; attempt++)
            {
                string cand = FirstNames[Rng.Next(FirstNames.Length)] + " \"" +
                              Nicknames[Rng.Next(Nicknames.Length)] + "\" " +
                              LastNames[Rng.Next(LastNames.Length)];
                if (!UsedNames.Contains(cand)) { UsedNames.Add(cand); return cand; }
            }
            return "Badge #" + (_badge++);
        }

        public static string For(Ped p)
        {
            if (p == null || !p.Exists()) return "Officer";
            return Ensure(p).Display;
        }

        public static void Apply(Ped p)
        {
            if (p == null || !p.Exists()) return;
            Cop c = Ensure(p);
            RankDef r = Ranks[c.RankIdx];

            Function.Call(Hash.SET_ENTITY_MAX_HEALTH, p, r.Health);
            Function.Call(Hash.SET_ENTITY_HEALTH, p, r.Health);
            Function.Call(Hash.SET_PED_ACCURACY, p, r.Accuracy);
            Function.Call(Hash.SET_PED_SHOOT_RATE, p, r.ShootRate);

            Function.Call(Hash.GIVE_WEAPON_TO_PED, p, unchecked((int)(uint)c.Secondary), 250, false, false);
            Function.Call(Hash.GIVE_WEAPON_TO_PED, p, unchecked((int)(uint)c.Primary), 350, false, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 54, true);  // AlwaysEquipBestWeapon
        }

        public static void Forget(int handle)
        {
            Cop c;
            if (Assigned.TryGetValue(handle, out c)) { Assigned.Remove(handle); UsedNames.Remove(c.Name); }
        }

        // The rolled rank's baseline accuracy -- the XP system stacks its level
        // bonus on top of this without ever changing the rank itself.
        public static int BaseAccuracy(Ped p)
        {
            if (p == null || !p.Exists()) return 50;
            return Ranks[Ensure(p).RankIdx].Accuracy;
        }
    }

    internal static class QIAudio
    {
        private static readonly string Dir =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "QI_Audio");
        private static System.Media.SoundPlayer _player;

        public static void PlayRadio(int i) { Play("radio_" + i + ".wav"); }
        public static void PlayTaunt(int i) { Play("taunt_" + i + ".wav"); }
        public static void PlayWelcome(int i) { Play("welcome_" + i + ".wav"); }
        public static void PlayPit(int i) { Play("pit_" + i + ".wav"); }
        public static void PlayCollateralQ(int i) { Play("collateral_q_" + i + ".wav"); }
        public static void PlayCollateralA(int i) { Play("collateral_a_" + i + ".wav"); }
        public static void PlayIaVerdict(int i) { Play("ia_" + i + ".wav"); }
        public static void PlayCover(int i) { Play("cover_" + i + ".wav"); }
        public static void PlayBanter(int i) { PlaySequence("banter_a_" + i + ".wav", "banter_b_" + i + ".wav", 350); }

        private static void Play(string file)
        {
            try
            {
                string path = System.IO.Path.Combine(Dir, file);
                if (!System.IO.File.Exists(path)) return;
                if (_player != null) _player.Dispose(); // release the previous one-shot
                _player = new System.Media.SoundPlayer(path);
                _player.Play();
            }
            catch { }
        }

        public static void PlaySequence(string file1, string file2, int delayMs)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string path1 = System.IO.Path.Combine(Dir, file1);
                    if (System.IO.File.Exists(path1))
                    {
                        using (System.Media.SoundPlayer p1 = new System.Media.SoundPlayer(path1))
                        {
                            p1.PlaySync();
                        }
                    }
                    System.Threading.Thread.Sleep(delayMs);
                    string path2 = System.IO.Path.Combine(Dir, file2);
                    if (System.IO.File.Exists(path2))
                    {
                        using (System.Media.SoundPlayer p2 = new System.Media.SoundPlayer(path2))
                        {
                            p2.PlaySync();
                        }
                    }
                }
                catch { }
            });
        }

        public static void PlayCollateral(int qIdx, int aIdx)
        {
            PlaySequence("collateral_q_" + qIdx + ".wav", "collateral_a_" + aIdx + ".wav", 300);
        }
    }
}
