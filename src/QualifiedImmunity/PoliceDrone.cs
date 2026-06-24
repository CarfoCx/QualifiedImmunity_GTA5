using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    // -------------------------------------------------------------------
    // Police drones -- two separate aircraft, each on its own key, modeled on the
    // cheap, lethal drones seen in recent conflicts:
    //
    //   * FPV STRIKE (default F7) -- a first-person flying mobile bomb. Pilot it
    //                onto the target and detonate ON THE KEY (LMB); it does NOT
    //                auto-detonate. The airframe is consumed; relaunch for another.
    //   * BIRD'S-EYE BOMBER (default F8) -- a top-down (not FPV) loiter cam that
    //                drops bombs straight down until BINGO, then recalls to restock.
    //
    // The HUD is a realistic FPV OSD: artificial horizon, heading tape,
    // battery, altitude, ground speed, range/link (RSSI) that degrades with
    // distance until signal is lost, GPS, flight timer, VTX band, warhead/
    // grenade status, plus analog scanlines/static. Day / night-vision /
    // thermal optics.
    //
    // Standalone Script (its own tick/keys) so it stays out of the RideAlong
    // state machine and can be flown anytime. SHVDNE3, .NET Framework 4.8.
    // -------------------------------------------------------------------
    public class PoliceDrone : Script
    {
        // ---- Config (loaded from QualifiedImmunity.ini) -------------------
        private bool _enabled = true;
        private Keys _deployKey = Keys.F7;        // launch / recall the FPV strike drone
        private Keys _bomberKey = Keys.F8;        // launch / recall the bird's-eye bomber
        private float _maxFlightSeconds = 1800f;  // battery endurance at cruise (30 min)
        private int _bombLoadout = 8;             // bird's-eye bomber drops per sortie
        private float _maxSpeed = 20f;            // m/s horizontal top speed (DJI-sport ~19 m/s)
        private float _maxVertSpeed = 6f;         // m/s climb/descend (DJI is gentle on the vertical)
        private float _responsiveness = 3.0f;     // how briskly velocity chases the stick command (DJI smoothness)
        private float _yawRate = 110f;            // deg/s at full stick
        private float _pitchRate = 90f;           // deg/s gimbal tilt at full stick
        private float _signalRange = 1500f;       // m before the link drops out
        private float _bomberAltitude = 70f;      // m above ground the bird's-eye bomber spawns/loiters
        private int _strikeExplosion = 4;         // ADD_EXPLOSION type: kamikaze (rocket)
        private int _bombExplosion = 1;           // ADD_EXPLOSION type: dropped bomb (grenade-launcher)
        private float _strikeDamage = 1.0f;
        private float _bombDamage = 1.0f;

        // ---- Shop / inventory ([DroneShop] in QualifiedImmunity.ini) ------
        // When ShopEnabled, the drones are EXPENDABLE munitions you buy at any Ammu-Nation
        // gun store. You must own an airframe to launch it; a clean recall returns it to
        // your inventory, but detonating or losing it consumes it. Shop disabled => the old
        // behavior (F7/F8 launch for free, unlimited).
        private bool _shopEnabled = true;
        private int _strikePrice = 7500;
        private int _bomberPrice = 12000;
        private int _budgetPrice = 2000;          // dirt-cheap low-res FPV
        private float _shopRange = 30f;           // m from an Ammu-Nation to reach the "counter" (covers the shop floor)
        private Keys _shopKey = Keys.E;           // open the UAS counter when stood in a gun store
        private Keys _budgetKey = Keys.F6;        // launch / recall the low-res budget FPV
        private int _strikeStock;                 // FPV strike airframes owned (this session)
        private int _bomberStock;                 // bird's-eye bomber airframes owned (this session)
        private int _budgetStock;                 // low-res budget FPV airframes owned (this session)

        private bool _shopOpen;                   // buy menu showing
        private int _shopSel;                     // highlighted row (0 = strike, 1 = bomber, 2 = low-res)
        private bool _nearStore;                  // player currently within reach of a gun-store counter
        private DateTime _lastStoreScan = DateTime.MinValue;
        private Prop _shopDrone;                  // the drone displayed on the counter while buying

        // ---- Flight state -------------------------------------------------
        // Per-FLIGHT (active) envelope. Copied from the config defaults at launch and then
        // degraded for the low-res budget rig (cheap motors / weak VTX / tiny battery), so
        // one airframe can fly worse than another without mutating the shared config.
        private float _aSpeed, _aVert, _aResp, _aYaw, _aPitch, _aRange, _aFlightSecs;
        private int _budgetExplosion = 1;   // cheap warhead: grenade-launcher pop, not a rocket
        private float _budgetDamage = 0.7f;

        private bool _flying;
        private Camera _cam;
        private Vector3 _pos;                      // drone world position
        private Vector3 _vel;                      // drone world velocity (m/s)
        private Vector3 _home;                     // launch point (for range/RTH telemetry)

        // Async world-collision probe so the drone can't fly through buildings. Cast one
        // frame, read it the next; results are validated to ignore this build's bogus
        // "hit at the world origin" so a bad read can never teleport the bird.
        private int _probe;
        private Vector3 _probeStart;
        private Vector3 _probeDir;
        private float _probeLen;
        private const float DroneRadius = 0.85f;   // swept-sphere body radius: keep the camera this far off walls
        private float _yaw, _pitch, _roll;         // camera/airframe attitude (deg)
        private DateTime _launchedAt = DateTime.MinValue;
        private float _battery = 100f;             // %, drains with flight + throttle
        private float _link = 100f;                // RSSI/link quality %, drops with range
        private int _type;                         // 0 = FPV strike (kamikaze), 1 = bird's-eye bomber, 2 = low-res budget FPV
        private string _callsign = "STRIKE-1";
        private Prop _phoneProp;                   // handset in the pilot's hand while flying
        private int _optics;                       // 0 = day/EO, 1 = night-vision, 2 = thermal
        private int _bombs_left;                   // bomber ordnance remaining this sortie
        private DateTime _lastDrop = DateTime.MinValue;
        private const double DropCooldownSeconds = 0.8;

        // Falling grenades dropped by the bomber, tracked until they impact.
        private sealed class FallingBomb
        {
            public Prop Obj;
            public Vector3 Pos;                    // last known position (fallback if prop is gone)
            public DateTime ArmedUntil;            // fuse/timeout backstop
        }
        private readonly List<FallingBomb> _bombs = new List<FallingBomb>();

        // The visible drone airframe the pilot sees lift off and fly around.
        private Prop _droneProp;
        // Launch "establishing shot": a brief 3rd-person view of the pilot holding the
        // phone while the drone rises overhead, before we cut to the onboard feed.
        private bool _cine;
        private DateTime _cineUntil = DateTime.MinValue;
        private const double CineSeconds = 2.6;

        // Player-restore snapshot (so recall puts the pilot back exactly as they were).
        private bool _restorePending;

        private bool _announced;
        private readonly Random _rng = new Random();

        private static readonly string[] OpticsShort = { "EO/DAY", "NV GEN3", "IR WHOT" };

        public PoliceDrone()
        {
            LoadConfig();
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;
            Interval = 0;   // every frame -- needed for smooth FPV control while flying
        }

        private void LoadConfig()
        {
            ScriptSettings s = ScriptSettings.Load(@"scripts\QualifiedImmunity.ini");
            _enabled          = s.GetValue("Drone", "Enabled", _enabled);
            _maxFlightSeconds = s.GetValue("Drone", "FlightSeconds", _maxFlightSeconds);
            _bombLoadout      = s.GetValue("Drone", "BombLoadout", _bombLoadout);
            _maxSpeed         = s.GetValue("Drone", "MaxSpeed", _maxSpeed);
            _maxVertSpeed     = s.GetValue("Drone", "VerticalSpeed", _maxVertSpeed);
            _responsiveness   = s.GetValue("Drone", "Responsiveness", _responsiveness);
            _yawRate          = s.GetValue("Drone", "YawRate", _yawRate);
            _pitchRate        = s.GetValue("Drone", "PitchRate", _pitchRate);
            _signalRange      = s.GetValue("Drone", "SignalRange", _signalRange);
            _bomberAltitude   = s.GetValue("Drone", "BomberAltitude", _bomberAltitude);
            _deployKey        = s.GetValue("Keys",  "DroneKey", _deployKey);
            _bomberKey        = s.GetValue("Keys",  "BomberKey", _bomberKey);

            _shopEnabled      = s.GetValue("DroneShop", "ShopEnabled", _shopEnabled);
            _strikePrice      = s.GetValue("DroneShop", "StrikePrice", _strikePrice);
            _bomberPrice      = s.GetValue("DroneShop", "BomberPrice", _bomberPrice);
            _budgetPrice      = s.GetValue("DroneShop", "BudgetPrice", _budgetPrice);
            _shopRange        = s.GetValue("DroneShop", "StoreRange", _shopRange);
            _strikeStock      = s.GetValue("DroneShop", "StartingStrikeStock", 0);
            _bomberStock      = s.GetValue("DroneShop", "StartingBomberStock", 0);
            _budgetStock      = s.GetValue("DroneShop", "StartingBudgetStock", 0);
            _shopKey          = s.GetValue("Keys",  "DroneShopKey", _shopKey);
            _budgetKey        = s.GetValue("Keys",  "BudgetDroneKey", _budgetKey);
        }

        // Inventory accessors so the three drone types share one consume/refund/buy path.
        private int StockFor(int type) { return type == 0 ? _strikeStock : type == 1 ? _bomberStock : _budgetStock; }
        private void AddStock(int type, int delta)
        {
            if (type == 0) _strikeStock += delta;
            else if (type == 1) _bomberStock += delta;
            else _budgetStock += delta;
        }
        private Keys LaunchKeyFor(int type) { return type == 0 ? _deployKey : type == 1 ? _bomberKey : _budgetKey; }
        private static string DroneName(int type) { return type == 0 ? "FPV strike drone" : type == 1 ? "bird's-eye bomber" : "low-res FPV drone"; }

        private void OnAborted(object sender, EventArgs e)
        {
            // A script reload mid-flight must not strand the camera rendering or
            // leave the pilot frozen/invincible. Tear everything down hard.
            EndFlight(false, "reload");
            CloseShop();   // never leave the pilot frozen at the counter on a reload
            foreach (FallingBomb b in _bombs)
                if (b.Obj != null && b.Obj.Exists()) b.Obj.Delete();
            _bombs.Clear();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!_enabled) return;

            // Buy menu owns the keyboard while it's up.
            if (_shopOpen) { HandleShopKey(e.KeyCode); return; }

            // Stood at a gun-store counter, on foot -> open the UAS purchase menu.
            if (_shopEnabled && !_flying && _nearStore && e.KeyCode == _shopKey
                && !Game.Player.Character.IsInVehicle())
            {
                OpenShop();
                return;
            }

            // Each key toggles its own drone. While one is airborne, the other key
            // (or its own) recalls it; you fly one drone at a time.
            if (e.KeyCode == _deployKey)
            {
                if (_flying) EndFlight(true, "recalled");
                else TryLaunch(0);
            }
            else if (e.KeyCode == _bomberKey)
            {
                if (_flying) EndFlight(true, "recalled");
                else TryLaunch(1);
            }
            else if (e.KeyCode == _budgetKey)
            {
                if (_flying) EndFlight(true, "recalled");
                else TryLaunch(2);
            }
        }

        // Launch gated by inventory when the shop is on: you must own the airframe.
        private void TryLaunch(int type)
        {
            if (!_shopEnabled) { Launch(type); return; }   // shop off -> unlimited, as before
            if (StockFor(type) <= 0)
            {
                GTA.UI.Notification.PostTicker(
                    "~r~UAS:~w~ No " + DroneName(type) + " in inventory. Buy one at any ~y~Ammu-Nation~w~ gun store.", false);
                return;
            }
            Launch(type);   // LaunchCore draws it from stock once it's actually airborne
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!_announced)
            {
                _announced = true;
                if (_enabled)
                {
                    if (_shopEnabled)
                        GTA.UI.Notification.PostTicker(
                            "~b~Police UAS online.~w~ Buy drones at any ~y~Ammu-Nation~w~ gun store, then ~y~" + _deployKey + "~w~/~y~" + _bomberKey + "~w~/~y~" + _budgetKey + "~w~ to launch.", false);
                    else
                        GTA.UI.Notification.PostTicker(
                            "~b~Police UAS online.~w~ ~y~" + _deployKey + "~w~ FPV strike   ~y~" + _bomberKey + "~w~ bomber   ~y~" + _budgetKey + "~w~ low-res FPV", false);
                }
            }

            // Falling grenades keep ticking even after the drone lands.
            TickBombs();

            if (_flying) { FlightTick(); return; }

            // On foot: run the gun-store buy counter (proximity prompt + purchase menu).
            if (_shopEnabled) ShopTick();
        }

        // -------------------------------------------------------------------
        // Launch / recover
        // -------------------------------------------------------------------
        // type 0 = FPV strike (kamikaze, first-person), 1 = bird's-eye bomber (top-down).
        private void Launch(int type)
        {
            try
            {
                LaunchCore(type);
            }
            catch (Exception ex)
            {
                // Surface the failure instead of letting the script die silently, and
                // make sure the pilot isn't left frozen with the camera half-engaged.
                GTA.UI.Notification.PostTicker("~r~UAS launch error:~w~ " + ex.Message, false);
                EndFlight(false, "error");
            }
        }

        // Copy the config flight envelope into the active per-flight one, degrading it hard
        // for the low-res budget rig: slow cheap motors, floaty laggy control, a weak video
        // transmitter that drops out at short range, and a tiny battery.
        private void SetFlightParams(int type)
        {
            if (type == 2)
            {
                _aSpeed      = _maxSpeed * 0.60f;
                _aVert       = _maxVertSpeed * 0.70f;
                _aResp       = _responsiveness * 0.55f;
                _aYaw        = _yawRate * 0.80f;
                _aPitch      = _pitchRate * 0.80f;
                _aRange      = _signalRange * 0.40f;
                _aFlightSecs = _maxFlightSeconds * 0.40f;
            }
            else
            {
                _aSpeed = _maxSpeed; _aVert = _maxVertSpeed; _aResp = _responsiveness;
                _aYaw = _yawRate; _aPitch = _pitchRate; _aRange = _signalRange;
                _aFlightSecs = _maxFlightSeconds;
            }
        }

        private void LaunchCore(int type)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) return;
            if (player.IsInVehicle())
            {
                GTA.UI.Notification.PostTicker("~r~UAS:~w~ Step out of the vehicle to launch the drone.", false);
                return;
            }

            _type = type;
            _home = player.Position;
            _yaw = player.Heading;
            _roll = 0f;
            _vel = Vector3.Zero;
            _probe = 0;
            _battery = 100f;
            _link = 100f;
            _optics = 0;
            _launchedAt = DateTime.Now;

            // Ground height under the pilot, used for the loiter altitude.
            float gz = player.Position.Z;
            OutputArgument oz = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, player.Position.X, player.Position.Y, player.Position.Z + 5f, oz, false))
                gz = oz.GetResult<float>();

            if (type == 1)
            {
                // Bird's-eye bomber: climbs overhead to loiter altitude, camera straight down.
                _callsign = "ANVIL-2";
                _bombs_left = _bombLoadout;
                _pitch = -89f;    // looking straight down
                _pos = new Vector3(player.Position.X, player.Position.Y, gz + _bomberAltitude);
            }
            else
            {
                // FPV (strike OR low-res budget): lifts off to just above head height, nose forward.
                _callsign = type == 2 ? "CAM-1" : "STRIKE-1";
                _pitch = -8f;     // a touch nose-down so you see the ground ahead
                _pos = player.Position + new Vector3(0f, 0f, 2.5f) + DirFromYawPitch(_yaw, 0f) * 1.5f;
            }

            // Per-flight envelope: the budget rig flies markedly worse (see SetFlightParams).
            SetFlightParams(type);

            // The pilot stays put and untouchable while jacked into the goggles.
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, false, 0);
            player.IsInvincible = true;
            player.CanRagdoll = false;
            Function.Call(Hash.FREEZE_ENTITY_POSITION, player, true);
            // Put the phone in the pilot's hand and have them stare at the SCREEN (not to
            // the ear) -- they're "flying it off the handset". Reads as piloting in the
            // 3rd-person establishing shot and any time the pilot is on camera.
            PutPlayerOnPhone(player);

            // A visible drone that lifts off the pilot and flies around (best-effort:
            // if no drone model is available, the camera still flies as before).
            SpawnDroneProp(player.Position + new Vector3(0f, 0f, 1.2f));

            // Start the camera as a 3rd-person establishing shot of the pilot + drone.
            Vector3 behind = DirFromYawPitch(_yaw + 180f, 0f);
            Vector3 camPos = player.Position + behind * 3.2f + new Vector3(0f, 0f, 1.6f);
            float fov = type == 1 ? 55f : (type == 2 ? 92f : 80f);   // bomber tight, budget a cheap wide fisheye
            _cam = Camera.Create(ScriptedCameraNameHash.DefaultScriptedCamera,
                                 camPos, Vector3.Zero, 50f, true, EulerRotationOrder.YXZ);
            if (_cam == null) { EndFlight(true, "camera failed"); return; }
            _cam.PointAt(player);
            ScriptCameraDirector.StartRendering();
            ApplyOptics();

            // Single-use: draw the airframe from inventory the moment it's committed to the
            // air. Launching IS the use -- it does not come back, whether you detonate it,
            // lose it, or recall it. Run another sortie -> buy another at the counter.
            if (_shopEnabled) AddStock(type, -1);

            _restorePending = true;
            _flying = true;
            _cine = true;
            _cineUntil = DateTime.Now.AddSeconds(CineSeconds);
            // Stash the onboard FOV so the cine->onboard cut can restore it.
            _onboardFov = fov;

            GTA.UI.Notification.PostTicker(
                (type == 0 ? "~b~UAS STRIKE-1 lifting off (FPV).~w~ Taking the controls..."
                           : "~b~UAS ANVIL-2 climbing to station (bird's-eye).~w~ Taking the controls..."), false);
        }

        private float _onboardFov = 80f;

        // Spawn the visible drone airframe. We move it ourselves each frame, so it's
        // collision-free and frozen (purely cosmetic). Tries a few model names and
        // quietly does nothing if none exist in this game build.
        private void SpawnDroneProp(Vector3 at)
        {
            string[] models = { "prop_drone_01", "prop_drone_02", "hei_prop_drone_01" };
            foreach (string name in models)
            {
                Model m = new Model(name);
                if (!m.IsValid) continue;
                m.Request(250);
                if (!m.IsLoaded) { m.MarkAsNoLongerNeeded(); continue; }
                _droneProp = World.CreateProp(m, at, false, false);
                m.MarkAsNoLongerNeeded();
                if (_droneProp != null && _droneProp.Exists())
                {
                    Function.Call(Hash.SET_ENTITY_COLLISION, _droneProp, false, false);
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, _droneProp, true);
                    _droneProp.IsPersistent = true;
                    return;
                }
            }
            _droneProp = null;   // no visible airframe; the camera still flies
        }

        // Pose the pilot holding a phone up and reading the screen (the drone's "remote"),
        // plus an actual handset prop in the right hand. All best-effort: a missing anim or
        // prop just means a plainer pose, never a launch failure.
        private void PutPlayerOnPhone(Ped player)
        {
            try
            {
                Model pm = new Model("prop_npc_phone_02");
                pm.Request(300);
                if (pm.IsLoaded)
                {
                    _phoneProp = World.CreateProp(pm, player.Position, false, false);
                    pm.MarkAsNoLongerNeeded();
                    if (_phoneProp != null && _phoneProp.Exists())
                    {
                        int bone = Function.Call<int>(Hash.GET_PED_BONE_INDEX, player, 28422); // SKEL_R_Hand
                        Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, _phoneProp, player, bone,
                            0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, false, false, false, false, 2, true);
                    }
                }
                // Looping upper-body "reading the phone" pose. Flag 49 = looping + upper-body
                // + secondary, so it holds while the rest of the body stays frozen in place.
                const string dict = "cellphone@";
                Function.Call(Hash.REQUEST_ANIM_DICT, dict);
                Function.Call(Hash.TASK_PLAY_ANIM, player, dict, "cellphone_text_read_base",
                    4f, -4f, -1, 49, 0f, false, false, false);
            }
            catch { /* cosmetic only */ }
        }

        private void RemovePlayerPhone(Ped player)
        {
            if (_phoneProp != null)
            {
                if (_phoneProp.Exists()) _phoneProp.Delete();
                _phoneProp = null;
            }
            if (player != null && player.Exists() && !player.IsDead)
                Function.Call(Hash.STOP_ANIM_TASK, player, "cellphone@", "cellphone_text_read_base", 3f);
        }

        // detonateSafe: an orderly recall (drone survives conceptually); we just
        // tear down the feed. A kamikaze hit calls EndFlight after its explosion.
        private void EndFlight(bool notify, string why)
        {
            _cine = false;
            if (_cam != null)
            {
                ScriptCameraDirector.StopRendering(false);
                if (_cam.Exists()) _cam.Delete();
                _cam = null;
            }
            if (_droneProp != null)
            {
                if (_droneProp.Exists()) _droneProp.Delete();
                _droneProp = null;
            }
            // Drop the handset prop + its "looking at phone" pose. (The CLEAR_PED_TASKS in
            // the restore block below also lowers the anim; this guarantees the prop is gone.)
            RemovePlayerPhone(Game.Player.Character);
            Function.Call(Hash.SET_NIGHTVISION, false);
            Function.Call(Hash.SET_SEETHROUGH, false);
            Function.Call(Hash.CLEAR_TIMECYCLE_MODIFIER);

            if (_restorePending)
            {
                _restorePending = false;
                Ped player = Game.Player.Character;
                if (player != null && player.Exists())
                {
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, player, false);
                    Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, true, 0);
                    player.IsInvincible = false;
                    player.CanRagdoll = true;
                    if (!player.IsDead) Function.Call(Hash.CLEAR_PED_TASKS, player); // lower the controller
                }
            }

            // (Single-use: nothing to refund -- the airframe was spent at launch.)

            bool wasFlying = _flying;
            _flying = false;
            if (notify && wasFlying)
                GTA.UI.Notification.PostTicker("~b~UAS:~w~ Feed closed (" + why + ").", false);
        }

        // -------------------------------------------------------------------
        // Gun-store buy counter (Ammu-Nation). The real shop UI can't be extended from
        // a script, so we detect proximity to an Ammu-Nation (by its map blip) and run
        // our own native-styled purchase menu. Drones are expendable stock you buy here.
        // -------------------------------------------------------------------
        private void ShopTick()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) { CloseShop(); return; }

            // Refresh "am I at a counter?" a few times a second (cheap; blips are static).
            if ((DateTime.Now - _lastStoreScan).TotalSeconds > 0.25)
            {
                _lastStoreScan = DateTime.Now;
                _nearStore = !player.IsInVehicle() && NearAmmuNation(player.Position);
            }

            if (_shopOpen)
            {
                // Walking out of the store (or into a car) closes the counter.
                if (!_nearStore) { CloseShop(); return; }
                // Slowly spin the drone sitting on the counter so you can look it over.
                if (_shopDrone != null && _shopDrone.Exists())
                    _shopDrone.Heading = (Environment.TickCount * 0.05f) % 360f;
                DrawShopMenu(player);
                return;
            }

            if (_nearStore)
                Text("~y~[" + _shopKey + "]~w~ UAS counter -- buy a drone",
                     0.5f, 0.86f, 0.42f, 245, 245, 245, true);
        }

        // Every Ammu-Nation store counter in GTA V. The blip-only scan we used before
        // didn't reliably flag a store you were standing in (tight radius, and store blips
        // aren't always enumerable), so the UAS counter never showed. This fixed list is
        // the reliable primary check; the blip scan stays as a fallback for added stores.
        private static readonly Vector3[] AmmuNations =
        {
            new Vector3(21.7f,    -1107.3f, 29.8f),    // Little Seoul
            new Vector3(810.2f,   -2157.6f, 29.6f),    // Cypress Flats
            new Vector3(1693.4f,   3760.2f, 34.7f),    // Sandy Shores
            new Vector3(-330.2f,   6083.9f, 31.5f),    // Paleto Bay
            new Vector3(252.6f,     -50.0f, 69.9f),    // Downtown Vinewood
            new Vector3(-662.1f,   -935.3f, 21.8f),    // Pillbox Hill
            new Vector3(-1305.3f,  -394.0f, 36.7f),    // Morningwood
            new Vector3(-1117.6f,  2698.6f, 18.6f),    // Route 68 (Tongva)
            new Vector3(2567.9f,    294.4f, 108.7f),   // Tataviam Mountains
            new Vector3(-3172.5f,  1085.0f, 20.8f),    // Chumash
        };

        // Standing in (or right outside) any Ammu-Nation? Hardcoded counters first --
        // reliable regardless of blip state -- then the map-blip scan as a fallback so
        // DLC/added stores still work. A generous radius so you trigger anywhere inside
        // the shop floor, not only on the exact counter tile.
        private bool NearAmmuNation(Vector3 from)
        {
            foreach (Vector3 store in AmmuNations)
                if (store.DistanceTo(from) <= _shopRange) return true;

            foreach (Blip b in World.GetAllBlips())
            {
                if (b == null || !b.Exists()) continue;
                if (b.Sprite != BlipSprite.AmmuNation) continue;
                if (b.Position.DistanceTo(from) <= _shopRange) return true;
            }
            return false;
        }

        private void OpenShop()
        {
            _shopOpen = true;
            _shopSel = 0;
            // Hold the player at the counter while the menu is up.
            Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, false, 0);
            SpawnShopDrone();   // put the actual airframe on the counter to look at
        }

        private void CloseShop()
        {
            if (!_shopOpen) return;
            _shopOpen = false;
            if (_shopDrone != null)
            {
                if (_shopDrone.Exists()) _shopDrone.Delete();
                _shopDrone = null;
            }
            if (!_flying) Function.Call(Hash.SET_PLAYER_CONTROL, Game.Player, true, 0);
        }

        // The display airframe shown on the counter while shopping -- the actual drone prop,
        // collision-free and frozen in front of the pilot (we spin it in ShopTick). Best-
        // effort: if no drone model exists in this build, the menu just shows without it.
        private void SpawnShopDrone()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;
            Vector3 at = player.Position + player.ForwardVector * 1.1f + new Vector3(0f, 0f, 0.55f);
            string[] models = { "prop_drone_01", "prop_drone_02", "hei_prop_drone_01" };
            foreach (string name in models)
            {
                Model m = new Model(name);
                if (!m.IsValid) continue;
                m.Request(250);
                if (!m.IsLoaded) { m.MarkAsNoLongerNeeded(); continue; }
                _shopDrone = World.CreateProp(m, at, false, false);
                m.MarkAsNoLongerNeeded();
                if (_shopDrone != null && _shopDrone.Exists())
                {
                    Function.Call(Hash.SET_ENTITY_COLLISION, _shopDrone, false, false);
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, _shopDrone, true);
                    _shopDrone.IsPersistent = true;
                    return;
                }
            }
            _shopDrone = null;   // no model available; the menu still works
        }

        private const int ShopRows = 3;   // strike, bomber, low-res budget

        private void HandleShopKey(Keys k)
        {
            if (k == Keys.Up || k == Keys.W) _shopSel = (_shopSel + ShopRows - 1) % ShopRows;   // wrap
            else if (k == Keys.Down || k == Keys.S) _shopSel = (_shopSel + 1) % ShopRows;
            else if (k == Keys.Enter || k == Keys.Return) BuySelected();
            else if (k == Keys.Escape || k == Keys.Back || k == _shopKey) CloseShop();
        }

        private void BuySelected()
        {
            int type = _shopSel;   // 0 strike, 1 bomber, 2 low-res budget
            int price = type == 0 ? _strikePrice : type == 1 ? _bomberPrice : _budgetPrice;
            string name = DroneName(type);

            if (Game.Player.Money < price)
            {
                GTA.UI.Notification.PostTicker("~r~UAS counter:~w~ Insufficient funds for the " + name + " ($" + price.ToString("N0") + ").", false);
                return;
            }
            Game.Player.Money -= price;
            AddStock(type, 1);
            GTA.UI.Notification.PostTicker(
                "~g~Purchased~w~ " + name + " -- $" + price.ToString("N0") + ". Now holding "
                + StockFor(type) + ". Launch with ~y~" + LaunchKeyFor(type) + "~w~.", false);
        }

        // Native-styled buy panel (mirrors the dispatch phone menu's look).
        private void DrawShopMenu(Ped player)
        {
            Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);

            const float x = 0.13f, top = 0.30f, w = 0.34f, rowH = 0.046f;
            int money = Game.Player.Money;

            // Title + cash header.
            Rect(x + w / 2f, top + 0.022f, w, 0.052f, 0, 0, 0, 225);
            Text("AMMU-NATION  --  UAS COUNTER", x + 0.010f, top + 0.004f, 0.40f, 245, 245, 245);
            Rect(x + w / 2f, top + 0.064f, w, 0.034f, 20, 20, 20, 200);
            Text("Cash: $" + money.ToString("N0"), x + 0.010f, top + 0.050f, 0.30f, 120, 235, 120);

            string[] names = { "FPV Strike Drone", "Bird's-eye Bomber", "Low-Res FPV Drone" };
            int[] prices = { _strikePrice, _bomberPrice, _budgetPrice };
            int[] stock = { _strikeStock, _bomberStock, _budgetStock };

            float ry = top + 0.092f;
            for (int i = 0; i < ShopRows; i++)
            {
                bool sel = _shopSel == i;
                bool afford = money >= prices[i];
                Rect(x + w / 2f, ry + rowH / 2f, w, rowH, sel ? 245 : 0, sel ? 245 : 0, sel ? 245 : 0, sel ? 205 : 140);
                int tc = sel ? 0 : 230;
                int pr = sel ? 0 : (afford ? 235 : 235), pg = sel ? 0 : (afford ? 210 : 90), pb = sel ? 0 : (afford ? 90 : 90);
                Text(names[i], x + 0.012f, ry + 0.008f, 0.34f, tc, tc, tc);
                Text("$" + prices[i].ToString("N0"), x + 0.205f, ry + 0.008f, 0.32f, pr, pg, pb);
                Text("owned x" + stock[i], x + 0.285f, ry + 0.008f, 0.28f, tc, tc, tc);
                ry += rowH + 0.004f;
            }

            Text("Up/Down: Move    Enter: Buy    " + _shopKey + "/Esc: Close",
                 x + 0.010f, ry + 0.010f, 0.26f, 200, 200, 200);
        }

        // -------------------------------------------------------------------
        // Per-frame flight
        // -------------------------------------------------------------------
        private void FlightTick()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead) { EndFlight(false, "pilot down"); return; }
            if (_cam == null || !_cam.Exists()) { EndFlight(false, "camera lost"); return; }

            float dt = Game.LastFrameTime;
            if (dt <= 0f) dt = 1f / 60f;
            if (dt > 0.1f) dt = 0.1f;   // clamp big hitches so the drone can't teleport

            // Take over every input this frame; we read raw stick/mouse below.
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);

            // Establishing shot: 3rd-person of the pilot holding the phone while the
            // drone climbs overhead, then cut to the onboard feed.
            if (_cine) { CineTick(player, dt); return; }

            // ---- gimbal aim: look = mouse / right stick. On a DJI the camera is
            // gimbal-stabilized, so this only points the CAMERA (yaw/pitch); it does
            // NOT tilt the airframe or steer where "forward" goes. ----
            float lookX = Norm(GTA.Control.LookLeftRight);
            float lookY = Norm(GTA.Control.LookUpDown);
            // GTA heading increases counter-clockwise, so looking right LOWERS yaw.
            _yaw -= lookX * _aYaw * dt;
            _yaw = Wrap360(_yaw);
            if (_type == 0)
            {
                // FPV: the gimbal tilts up/down with look.
                _pitch -= lookY * _aPitch * dt;
                if (_pitch > 85f) _pitch = 85f;
                if (_pitch < -85f) _pitch = -85f;
            }
            else
            {
                // Bird's-eye bomber: camera locked straight down (the whole point of it).
                _pitch = -89f;
            }

            // ---- sticks command a VELOCITY, not an acceleration (DJI GPS/normal
            // mode). Release the sticks and the bird brakes to a stop and holds a
            // GPS hover; no vertical input holds altitude. ----
            float fwd = -Norm(GTA.Control.MoveUpDown);     // W (up axis = negative) -> forward
            float strafe = Norm(GTA.Control.MoveLeftRight);
            float lift = 0f;
            if (Pressed(GTA.Control.Jump)) lift += 1f;      // Space / pad A : climb
            if (Pressed(GTA.Control.Duck)) lift -= 1f;      // L-Ctrl / pad B : descend

            // Movement is HORIZONTAL in the heading direction (gimbal pitch is decoupled),
            // so you can stare straight down while still cruising forward, like a DJI.
            Vector3 fwdH = DirFromYawPitch(_yaw, 0f);
            Vector3 rightH = DirFromYawPitch(_yaw - 90f, 0f);
            Vector3 desiredVel = fwdH * (fwd * _aSpeed)
                               + rightH * (strafe * _aSpeed)
                               + new Vector3(0f, 0f, lift * _aVert);
            // Cap the horizontal command so diagonals aren't faster than straight.
            float hMag = (float)Math.Sqrt(desiredVel.X * desiredVel.X + desiredVel.Y * desiredVel.Y);
            if (hMag > _aSpeed)
            {
                float k = _aSpeed / hMag;
                desiredVel.X *= k; desiredVel.Y *= k;
            }

            // Smoothly chase the commanded velocity -> brisk but never twitchy
            // acceleration, and an active brake-to-hover when you let go.
            float follow = 1f - (float)Math.Exp(-dt * _aResp);
            _vel += (desiredVel - _vel) * follow;

            Vector3 nextPos = _pos + _vel * dt;

            // ---- world collision: stop at solid geometry instead of clipping through
            // buildings. Uses a validated async probe (see WorldCollide) -- the warhead
            // still only detonates on the key, so contact just halts/slides the airframe. ----
            WorldCollide(ref nextPos);

            // Ground clamp (always-on; GET_GROUND_Z is reliable in this build).
            OutputArgument groundZ = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, nextPos.X, nextPos.Y, nextPos.Z + 2f, groundZ, false))
            {
                float floor = groundZ.GetResult<float>() + 0.5f;   // don't sink into the ground
                if (nextPos.Z < floor) { nextPos.Z = floor; if (_vel.Z < 0f) _vel.Z = 0f; }
            }
            _pos = nextPos;

            // ---- gimbal keeps the horizon level (a DJI stays stable, it doesn't bank
            // the view into turns). Any residual roll eases back to level. ----
            _roll += (0f - _roll) * (1f - (float)Math.Exp(-dt * 6.0));

            // ---- battery + link telemetry ----
            float throttleLoad = Math.Min(1f, (Math.Abs(fwd) + Math.Abs(strafe) + Math.Abs(lift)) * 0.6f);
            float drainPerSec = (100f / _aFlightSecs) * (0.7f + 0.6f * throttleLoad);
            _battery -= drainPerSec * dt;
            if (_battery < 0f) _battery = 0f;

            float homeDist = _pos.DistanceTo(_home);
            float linkTarget = 100f * (1f - homeDist / _aRange);
            if (linkTarget < 0f) linkTarget = 0f;
            _link += (linkTarget - _link) * (1f - (float)Math.Exp(-dt * 3.0));

            // Power dead or out of range -> we lose the bird.
            if (_battery <= 0f) { LoseSignal("BATTERY DEPLETED"); return; }
            if (_link <= 3f || homeDist > _aRange) { LoseSignal("SIGNAL LOST"); return; }

            // ---- push the camera (the onboard lens IS the drone) ----
            _cam.Position = _pos;
            _cam.Rotation = new Vector3(_pitch, _roll, _yaw);

            // Keep the airframe under the camera but out of the lens: hidden onboard
            // (you're looking THROUGH it), still tracked for a clean recall/teardown.
            if (_droneProp != null && _droneProp.Exists())
            {
                _droneProp.Position = _pos - new Vector3(0f, 0f, 0.4f);
                _droneProp.Heading = _yaw;
                _droneProp.IsVisible = false;
            }

            HandleActions(player);
            if (_type == 1) DrawBomberHud(homeDist);
            else if (_type == 2) DrawRetroHud(homeDist);
            else DrawFpvHud(homeDist);
        }

        // The launch establishing shot: hold a 3rd-person view of the pilot (phone in hand,
        // reading the screen) while the visible drone climbs from over their head up to its
        // start position, then cut to the onboard feed and hand over the controls.
        private void CineTick(Ped player, float dt)
        {
            double remain = (_cineUntil - DateTime.Now).TotalSeconds;
            float prog = (float)Math.Max(0.0, Math.Min(1.0, 1.0 - remain / CineSeconds));

            // Drone rises from just above the pilot's head to its onboard start point.
            Vector3 liftFrom = player.Position + new Vector3(0f, 0f, 1.2f);
            Vector3 dronePos = Vector3.Lerp(liftFrom, _pos, prog);
            if (_droneProp != null && _droneProp.Exists())
            {
                _droneProp.IsVisible = true;
                _droneProp.Position = dronePos;
                _droneProp.Heading = _yaw + prog * 360f;   // a little spin-up as it climbs
            }

            // 3rd-person camera: behind/above the pilot, easing in to frame the climb.
            Vector3 behind = DirFromYawPitch(_yaw + 180f, 0f);
            Vector3 camPos = player.Position + behind * 3.2f + new Vector3(0f, 0f, 1.7f + prog * 0.6f);
            _cam.Position = camPos;
            _cam.PointAt(dronePos);   // follow the drone up

            // Caption.
            string title = _type == 1 ? "DEPLOYING BIRD'S-EYE BOMBER"
                         : _type == 2 ? "DEPLOYING LOW-RES FPV DRONE"
                         : "DEPLOYING FPV STRIKE DRONE";
            Text(title, 0.5f, 0.86f, 0.42f, 120, 235, 160, true);
            Text("acquiring video link...", 0.5f, 0.90f, 0.30f, 200, 200, 200, true);

            if (remain <= 0.0)
            {
                // Cut to the onboard feed and unlock the controls.
                _cine = false;
                _cam.StopPointing();
                _cam.FieldOfView = _onboardFov;
                _cam.Position = _pos;
                _cam.Rotation = new Vector3(_pitch, _roll, _yaw);
                if (_type == 1)
                    GTA.UI.Notification.PostTicker(
                        "~b~UAS ANVIL-2 on station (bird's-eye).~w~ ~y~LMB~w~ drop bomb  ~y~D-pad Right~w~ optics  ~y~" + _bomberKey + "~w~ recall (single-use).", false);
                else if (_type == 2)
                    GTA.UI.Notification.PostTicker(
                        "~b~UAS CAM-1 airborne (low-res FPV).~w~ Cheap rig -- short range, weak battery. Fly it onto the target, ~y~LMB~w~ to detonate.  ~y~" + _budgetKey + "~w~ recall.", false);
                else
                    GTA.UI.Notification.PostTicker(
                        "~b~UAS STRIKE-1 airborne (FPV).~w~ Fly it onto the target, ~y~LMB~w~ to detonate.  ~y~D-pad Right~w~ optics  ~y~" + _deployKey + "~w~ recall.", false);
            }
        }

        // A drone we can no longer command (dead battery / out of range): the bird is
        // simply lost. Nothing auto-detonates -- the FPV warhead only goes off on the
        // detonate key, so a lost link just ends the flight.
        private void LoseSignal(string reason)
        {
            GTA.UI.Notification.PostTicker("~r~UAS " + _callsign + ": " + reason + ".~w~ Airframe lost.", false);
            EndFlight(false, reason);
        }

        private void HandleActions(Ped player)
        {
            // Fire: FPV detonates the warhead; bomber drops a bomb straight down.
            // LMB (Attack) ONLY -- the old set also listened to VehicleFlyAttack/Detonate,
            // which share keybinds with Jump (Space), so climbing the FPV bird blew it up.
            if (JustPressed(GTA.Control.Attack))
            {
                if (_type == 1) { DropBomb(); return; }   // bomber drops; FPV types detonate
                Detonate(_pos, true);
            }

            // Cycle optics (day / NV / thermal) -- D-pad Right, like the heli cam.
            if (JustPressed(GTA.Control.PhoneRight))
            {
                _optics = (_optics + 1) % 3;
                ApplyOptics();
            }
        }

        private void ApplyOptics()
        {
            Function.Call(Hash.SET_NIGHTVISION, false);
            Function.Call(Hash.SET_SEETHROUGH, false);
            Function.Call(Hash.CLEAR_TIMECYCLE_MODIFIER);
            // The budget rig has no fancy optics -- cheapest parts. Instead it runs a grimy
            // security-cam timecycle (a no-op if the build lacks the modifier; the heavy HUD
            // overlay sells the look regardless), and ignores the optics cycle entirely.
            if (_type == 2)
            {
                Function.Call(Hash.SET_TIMECYCLE_MODIFIER, "scanline_cam_cheap");
                return;
            }
            if (_optics == 1) Function.Call(Hash.SET_NIGHTVISION, true);
            else if (_optics == 2) Function.Call(Hash.SET_SEETHROUGH, true);
        }

        // -------------------------------------------------------------------
        // Ordnance
        // -------------------------------------------------------------------
        // Kamikaze: blow up at a point and consume the airframe.
        private void Detonate(Vector3 at, bool endFlight)
        {
            // The low-res budget rig carries a cheap, weaker warhead, not the strike rocket.
            int ex = _type == 2 ? _budgetExplosion : _strikeExplosion;
            float dmg = _type == 2 ? _budgetDamage : _strikeDamage;
            Explode(at, ex, dmg);
            GTA.UI.Notification.PostTicker("~r~UAS " + _callsign + ":~w~ Warhead detonated. Splash on target.", false);
            if (endFlight) EndFlight(false, "warhead expended");
        }

        // Bird's-eye bomber: release a bomb that falls under gravity and explodes on
        // impact. A real falling prop so you watch it drop from altitude. When the
        // last one is gone the bird is BINGO and must recall to restock.
        private void DropBomb()
        {
            if (_bombs_left <= 0)
            {
                GTA.UI.Notification.PostTicker("~y~UAS " + _callsign + ":~w~ BINGO ordnance. Recall (~y~" + _bomberKey + "~w~); buy another bomber at the counter for more.", false);
                return;
            }
            if ((DateTime.Now - _lastDrop).TotalSeconds < DropCooldownSeconds) return;
            _lastDrop = DateTime.Now;
            _bombs_left--;

            Vector3 dropAt = _pos + new Vector3(0f, 0f, -0.6f);
            FallingBomb bomb = new FallingBomb { Pos = dropAt, ArmedUntil = DateTime.Now.AddSeconds(12.0) };

            Model m = new Model("w_ex_grenadefrag");
            m.Request(200);
            if (m.IsLoaded)
            {
                Prop obj = World.CreateProp(m, dropAt, true, false);
                if (obj != null && obj.Exists())
                {
                    Function.Call(Hash.FREEZE_ENTITY_POSITION, obj, false);
                    Function.Call(Hash.SET_ENTITY_HAS_GRAVITY, obj, true);
                    // Drop STRAIGHT DOWN: no inherited horizontal momentum, so the bomb
                    // lands directly under the fixed nadir crosshair at the moment of
                    // release and the drone's movement afterward can't drag its path.
                    Function.Call(Hash.SET_ENTITY_VELOCITY, obj, 0f, 0f, -5f);
                    obj.IsCollisionEnabled = true;
                    Function.Call(Hash.SET_ENTITY_RECORDS_COLLISIONS, obj, true); // so HAS_ENTITY_COLLIDED fires
                    bomb.Obj = obj;
                }
                m.MarkAsNoLongerNeeded();
            }
            _bombs.Add(bomb);
            if (_bombs_left > 0)
                GTA.UI.Notification.PostTicker("~y~UAS " + _callsign + ":~w~ Bomb away. (" + _bombs_left + " left)", false);
            else
                GTA.UI.Notification.PostTicker("~r~UAS " + _callsign + ":~w~ Last bomb away - BINGO ordnance.", false);
        }

        // Watch each falling bomb; detonate the instant it lands -- no delay sitting on
        // the deck. Impact = touched anything OR reached the ground directly below it
        // (the ground test is the reliable one; HAS_ENTITY_COLLIDED alone can miss/lag).
        // The fuse backstop only catches a bomb that somehow falls into the void.
        private void TickBombs()
        {
            if (_bombs.Count == 0) return;
            for (int i = _bombs.Count - 1; i >= 0; i--)
            {
                FallingBomb b = _bombs[i];
                bool exists = b.Obj != null && b.Obj.Exists();
                if (exists) b.Pos = b.Obj.Position;

                bool impact = exists && Function.Call<bool>(Hash.HAS_ENTITY_COLLIDED_WITH_ANYTHING, b.Obj);
                if (exists && !impact)
                {
                    // Reached the ground under it? Detonate right there.
                    OutputArgument oz = new OutputArgument();
                    if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, b.Pos.X, b.Pos.Y, b.Pos.Z + 2f, oz, false))
                    {
                        float gz = oz.GetResult<float>();
                        if (b.Pos.Z - gz <= 0.8f) { impact = true; b.Pos = new Vector3(b.Pos.X, b.Pos.Y, gz); }
                    }
                }
                bool expired = DateTime.Now >= b.ArmedUntil;
                if (!exists || impact || expired)
                {
                    Explode(b.Pos, _bombExplosion, _bombDamage);
                    if (exists) b.Obj.Delete();
                    _bombs.RemoveAt(i);
                }
            }
        }

        private void Explode(Vector3 at, int type, float damage)
        {
            // Unowned explosion: no ped is blamed, so a police drone op doesn't
            // instantly pin five stars on a pilot who can't fight back.
            Function.Call(Hash.ADD_EXPLOSION, at.X, at.Y, at.Z, type, damage, true, false, 0.0f);
        }

        // -------------------------------------------------------------------
        // FPV OSD HUD
        // -------------------------------------------------------------------
        private void DrawFpvHud(float homeDist)
        {
            // Optics-tinted OSD colour: amber-green EO, bright green NV, white IR.
            int r, g, b;
            switch (_optics)
            {
                case 1: r = 140; g = 255; b = 140; break;
                case 2: r = 235; g = 235; b = 235; break;
                default: r = 120; g = 235; b = 120; break;
            }

            // ---- analog video texture: link-driven static, scanlines, a drifting
            // frame-roll band, and the goggle vignette. The grit IS the FPV look. ----
            float noise = (100f - _link) / 100f;
            if (noise > 0.05f) DrawStatic(noise);
            DrawScanlines();
            DrawRollBar(_link / 100f);
            DrawVignette();

            bool blink = DateTime.Now.Millisecond < 600;
            bool battLow = _battery < 20f;

            // Subtle artificial horizon + heading tape (betaflight-style OSD).
            DrawHorizon(r, g, b);
            DrawHeadingTape(r, g, b);

            // Military FPV aiming crosshair + target frame (frame pulses red = armed).
            DrawFpvCrosshair(r, g, b, blink);

            // ---- telemetry ----
            int spdKmh = (int)Math.Round(_vel.Length() * 3.6f);
            int altAgl = (int)Math.Max(0f, GroundAgl());
            float vspd = _vel.Z;
            int linkBars = (int)Math.Round(_link / 20f);   // 0..5
            double flightT = (DateTime.Now - _launchedAt).TotalSeconds;
            string timer = TimeSpan.FromSeconds(flightT).ToString(@"mm\:ss");
            double cell = 3.3 + 0.9 * _battery / 100.0;     // per-cell LiPo (3.3-4.2v)
            double volts = cell * 4.0;                       // 4S pack

            // ---- top-left: callsign + blinking REC + video/control channels ----
            Text(_callsign, 0.020f, 0.028f, 0.36f, r, g, b);
            if (blink) Rect(0.029f, 0.072f, 0.009f, 0.014f, 235, 45, 45, 240);
            Text("     REC", 0.020f, 0.060f, 0.30f, 235, 70, 70);
            Text("CH8 5917  RC 915", 0.020f, 0.090f, 0.25f, r, g, b);

            // ---- top-right: warhead arm state + optics ----
            Text("ARMED", 0.820f, 0.028f, 0.40f, blink ? 235 : 150, 70, 70);
            Text("DET LMB", 0.820f, 0.070f, 0.27f, r, g, b);
            Text(OpticsShort[_optics], 0.820f, 0.098f, 0.25f, r, g, b);

            // ---- left rail: altitude + climb rate ----
            Text(altAgl.ToString("000") + "m", 0.030f, 0.448f, 0.40f, r, g, b);
            Text("ALT", 0.030f, 0.496f, 0.24f, r, g, b);
            Text((vspd >= 0 ? "+" : "") + vspd.ToString("0.0"), 0.030f, 0.524f, 0.25f, r, g, b);

            // ---- right rail: ground speed ----
            Text(spdKmh.ToString("000"), 0.912f, 0.448f, 0.40f, r, g, b);
            Text("KM/H", 0.912f, 0.496f, 0.24f, r, g, b);

            // ---- bottom-left: pack voltage (prominent) + per-cell + GPS ----
            int br = battLow ? 235 : r, bg = battLow ? (blink ? 50 : 170) : g, bb = battLow ? 50 : b;
            Text(volts.ToString("00.0") + "V", 0.020f, 0.858f, 0.40f, br, bg, bb);
            Text(cell.ToString("0.00") + "/cell " + _battery.ToString("00") + "%", 0.020f, 0.906f, 0.24f, br, bg, bb);
            Text(GeoCoord(_pos), 0.020f, 0.936f, 0.22f, r, g, b);

            // ---- bottom-centre: flight mode + heading ----
            bool hovering = _vel.Length() < 0.4f;
            Text(hovering ? "* HOLD *" : "FLY", 0.5f, 0.872f, 0.26f,
                 hovering ? 120 : r, hovering ? 235 : g, hovering ? 160 : b, true);
            Text("HDG " + ((int)_yaw).ToString("000") + " " + Cardinal(_yaw), 0.5f, 0.905f, 0.30f, r, g, b, true);

            // ---- bottom-right: range + RSSI bars + flight timer ----
            Text("RNG " + ((int)homeDist).ToString("0000") + "m", 0.800f, 0.846f, 0.28f, r, g, b);
            DrawLinkBars(0.800f, 0.884f, linkBars, r, g, b);
            Text("RSSI " + ((int)_link).ToString("000"), 0.852f, 0.878f, 0.24f, r, g, b);
            Text("T " + timer, 0.800f, 0.908f, 0.28f, r, g, b);

            // ---- warnings ----
            if (battLow && blink)
                Text("! LOW VOLTAGE - RTH !", 0.5f, 0.150f, 0.40f, 235, 70, 70, true);
            if (_link < 25f && blink)
                Text("! SIGNAL WEAK !", 0.5f, 0.182f, 0.36f, 235, 160, 60, true);
        }

        // -------------------------------------------------------------------
        // Bird's-eye bomber HUD: a downward recon/targeting display -- map grid,
        // a centre crosshair, a CCIP "bomb will land here" marker, ordnance count,
        // and the same battery/link/range telemetry. No artificial horizon (the
        // camera stares straight down).
        // -------------------------------------------------------------------
        private void DrawBomberHud(float homeDist)
        {
            int r, g, b;
            switch (_optics)
            {
                case 1: r = 140; g = 255; b = 140; break;
                case 2: r = 235; g = 235; b = 235; break;
                default: r = 150; g = 220; b = 255; break;   // cold blue recon tint by day
            }

            float noise = (100f - _link) / 100f;
            if (noise > 0.05f) DrawStatic(noise);
            DrawScanlines();
            DrawRollBar(_link / 100f);
            DrawVignette();

            bool blink = DateTime.Now.Millisecond < 600;
            bool battLow = _battery < 20f;
            bool bingo = _bombs_left <= 0;

            // Faint targeting graticule (fixed screen overlay) for the sensor-feed feel.
            for (int i = 1; i < 6; i++) Rect(0.18f + i * 0.108f, 0.5f, 0.0012f, 0.72f, r, g, b, 28);
            for (int i = 1; i < 5; i++) Rect(0.5f, 0.16f + i * 0.144f, 0.64f, 0.0012f, r, g, b, 28);

            // FIXED centre targeting reticle (nadir = straight below the drone). The camera
            // stares straight down, so this IS where a bomb dropped while hovering lands --
            // no jumpy projected CCIP marker chasing around the screen.
            DrawTargetReticle(r, g, b, !bingo, blink);
            // Compass rose (the top-down image yaws with the drone; show where north is).
            DrawNorthRose(r, g, b);

            int spdKmh = (int)Math.Round(_vel.Length() * 3.6f);
            int altAgl = (int)Math.Max(0f, GroundAgl());
            int altMsl = (int)Math.Max(0f, _pos.Z);
            int linkBars = (int)Math.Round(_link / 20f);
            double flightT = (DateTime.Now - _launchedAt).TotalSeconds;
            string timer = TimeSpan.FromSeconds(flightT).ToString(@"mm\:ss");
            double cell = 3.3 + 0.9 * _battery / 100.0;
            double volts = cell * 4.0;

            // ---- top-left: platform + sensor + blinking REC ----
            Text(_callsign + "  WESCAM MX", 0.020f, 0.028f, 0.31f, r, g, b);
            if (blink) Rect(0.029f, 0.070f, 0.009f, 0.014f, 235, 45, 45, 240);
            Text("     REC  " + DateTime.Now.ToString("HH:mm:ss"), 0.020f, 0.058f, 0.27f, 235, 70, 70);
            Text("SNSR " + OpticsShort[_optics] + "   FOV 2.3", 0.020f, 0.086f, 0.25f, r, g, b);

            // ---- top-right: weapon / ordnance status ----
            if (bingo)
            {
                if (blink) Text("BINGO ORDNANCE", 0.560f, 0.030f, 0.32f, 235, 70, 70);
                Text("RTB TO RESTOCK", 0.560f, 0.062f, 0.26f, 235, 160, 60);
            }
            else
            {
                Text("WPN  FRAG x" + _bombs_left, 0.560f, 0.030f, 0.33f, 235, 210, 90);
                Text("MASTER ARM   LMB RELEASE", 0.560f, 0.064f, 0.25f, r, g, b);
            }

            // ---- left rail: altitude (drop solution) + speed + track ----
            Text(altAgl.ToString("0000") + "m", 0.020f, 0.430f, 0.40f, r, g, b);
            Text("ALT AGL", 0.020f, 0.478f, 0.24f, r, g, b);
            Text("GS  " + spdKmh.ToString("000") + " KMH", 0.020f, 0.510f, 0.26f, r, g, b);
            Text("TRK " + ((int)_yaw).ToString("000") + " " + Cardinal(_yaw), 0.020f, 0.540f, 0.26f, r, g, b);

            // ---- right rail: MSL + laser code ----
            Text("MSL " + altMsl.ToString("0000"), 0.858f, 0.470f, 0.26f, r, g, b);
            Text("LZR 1688", 0.858f, 0.500f, 0.26f, r, g, b);

            // ---- bottom-left: pack voltage + GPS ----
            int br = battLow ? 235 : r, bg = battLow ? (blink ? 50 : 170) : g, bb = battLow ? 50 : b;
            Text(volts.ToString("00.0") + "V  " + _battery.ToString("00") + "%", 0.020f, 0.886f, 0.28f, br, bg, bb);
            Text(GeoCoord(_pos), 0.020f, 0.916f, 0.23f, r, g, b);

            // ---- bottom-centre: range to launch point ----
            Text("RNG " + ((int)homeDist).ToString("0000") + "m", 0.5f, 0.918f, 0.26f, r, g, b, true);

            // ---- bottom-right: link / timer ----
            DrawLinkBars(0.800f, 0.884f, linkBars, r, g, b);
            Text("RSSI " + ((int)_link).ToString("000"), 0.852f, 0.878f, 0.24f, r, g, b);
            Text("T " + timer, 0.800f, 0.908f, 0.28f, r, g, b);

            // ---- warnings ----
            if (battLow && blink) Text("! LOW VOLTAGE - RTB !", 0.5f, 0.150f, 0.38f, 235, 70, 70, true);
            if (_link < 25f && blink) Text("! SIGNAL WEAK !", 0.5f, 0.182f, 0.34f, 235, 160, 60, true);
        }

        // Artificial horizon: a roll-tilted line offset vertically by pitch, with
        // a couple of pitch-ladder rungs. Built from short DRAW_RECT segments
        // (the only 2D primitive available), which reads as a continuous tilted line.
        private void DrawHorizon(int r, int g, int b)
        {
            const float aspect = 1.7777f;          // 16:9 so the tilt looks correct
            float rollRad = _roll * (float)Math.PI / 180f;
            float slope = (float)Math.Tan(rollRad) * aspect;
            float pitchOff = (_pitch / 45f) * 0.30f; // pitch up -> horizon slides down
            if (pitchOff > 0.34f) pitchOff = 0.34f;
            if (pitchOff < -0.34f) pitchOff = -0.34f;
            float cy = 0.5f + pitchOff;

            DrawTiltedLine(0.5f, cy, 0.20f, slope, 0.0030f, r, g, b, 200);
            // pitch ladder rungs above/below (fixed screen spacing)
            DrawTiltedLine(0.5f, cy - 0.10f, 0.05f, slope, 0.0022f, r, g, b, 110);
            DrawTiltedLine(0.5f, cy + 0.10f, 0.05f, slope, 0.0022f, r, g, b, 110);
        }

        private void DrawTiltedLine(float cx, float cy, float halfW, float slope, float thick, int r, int g, int b, int a)
        {
            const int seg = 26;
            for (int i = -seg; i <= seg; i++)
            {
                float t = (float)i / seg;
                float x = cx + t * halfW;
                float y = cy + t * halfW * slope;
                Rect(x, y, (halfW / seg) * 1.4f, thick, r, g, b, a);
            }
        }

        private void DrawHeadingTape(int r, int g, int b)
        {
            const float top = 0.052f, w = 0.36f, cx = 0.5f;
            Rect(cx, top, w, 0.0022f, r, g, b, 120);
            // ticks every 15 deg of heading sliding under a fixed centre caret
            for (int d = -60; d <= 60; d += 15)
            {
                float hdg = Wrap360(_yaw + d);
                float x = cx + (d / 120f) * w;
                bool major = ((int)Math.Round(hdg / 15f) % 6) == 0;
                Rect(x, top + (major ? 0.010f : 0.007f), 0.0016f, major ? 0.018f : 0.012f, r, g, b, 170);
            }
            Rect(cx, top - 0.010f, 0.004f, 0.012f, 235, 210, 90, 235); // centre caret
        }

        private void DrawLinkBars(float x, float y, int bars, int r, int g, int b)
        {
            for (int i = 0; i < 5; i++)
            {
                bool on = i < bars;
                Rect(x + i * 0.010f, y - i * 0.0016f, 0.007f, 0.006f + i * 0.0032f,
                     on ? r : 60, on ? g : 60, on ? b : 60, on ? 220 : 120);
            }
        }

        private void DrawScanlines()
        {
            // A few faint dark bands for an analog-video texture (cheap; not every line).
            for (float y = 0.14f; y < 0.88f; y += 0.012f)
                Rect(0.5f, y, 0.78f, 0.0016f, 0, 0, 0, 22);
        }

        // -------------------------------------------------------------------
        // Low-res budget FPV HUD: a 1990s camcorder OSD. A sickly green CRT wash, coarse
        // scanlines, heavy ever-present static and a tracking tear, a big blinking REC, a
        // tape-speed flag, a frozen-in-1996 date/clock stamp, chunky battery blocks and a
        // crude crosshair. Deliberately ugly -- you bought the cheapest parts.
        // -------------------------------------------------------------------
        private void DrawRetroHud(float homeDist)
        {
            Rect(0.5f, 0.5f, 1.0f, 1.0f, 40, 70, 40, 30);      // green CRT wash over everything
            DrawCoarseStatic(0.45f + (100f - _link) / 130f);    // heavy grain, always on
            DrawCoarseScanlines();
            DrawRollBar(0.35f);                                  // strong analog tracking band
            DrawVignette();

            bool blink = DateTime.Now.Millisecond < 500;
            bool battLow = _battery < 25f;
            double flightT = (DateTime.Now - _launchedAt).TotalSeconds;
            string timer = TimeSpan.FromSeconds(flightT).ToString(@"h\:mm\:ss");

            int r = 205, g = 235, b = 205;   // washed-out green-white phosphor

            // top-left: blinking REC dot + tape speed
            if (blink) Rect(0.040f, 0.066f, 0.013f, 0.020f, 235, 40, 40, 240);
            Text("  REC", 0.028f, 0.052f, 0.52f, 235, 80, 80);
            Text("SP", 0.030f, 0.098f, 0.42f, r, g, b);

            // top-right: camera id + frozen camcorder date stamp with a live clock
            Text("CAM 01", 0.760f, 0.052f, 0.42f, r, g, b);
            Text(RetroStamp(), 0.560f, 0.098f, 0.40f, r, g, b);

            // crude centre crosshair (just a fat plus)
            Rect(0.5f, 0.5f, 0.060f, 0.0045f, r, g, b, 205);
            Rect(0.5f, 0.5f, 0.0040f, 0.095f, r, g, b, 205);

            // bottom-left: BATT label + chunky blocks
            Text("BATT", 0.028f, 0.862f, 0.40f, battLow ? 235 : r, battLow ? (blink ? 40 : 170) : g, battLow ? 40 : b);
            int blocks = (int)Math.Round(_battery / 20f);   // 0..5
            for (int i = 0; i < 5; i++)
            {
                bool on = i < blocks;
                Rect(0.080f + i * 0.017f, 0.878f, 0.013f, 0.024f, on ? r : 45, on ? g : 45, on ? b : 45, 220);
            }

            // bottom-centre: big timecode
            Text(timer, 0.5f, 0.900f, 0.58f, r, g, b, true);

            // bottom-right: crude link + range
            Text("LNK " + ((int)_link).ToString("000"), 0.792f, 0.862f, 0.40f, r, g, b);
            Text("RNG " + ((int)homeDist).ToString("0000") + "M", 0.792f, 0.900f, 0.40f, r, g, b);

            // warnings
            if (battLow && blink) Text("BATT LOW", 0.5f, 0.150f, 0.52f, 235, 70, 70, true);
            if (_link < 30f && blink) Text("-- NO SIGNAL --", 0.5f, 0.195f, 0.50f, 235, 160, 60, true);
        }

        private void DrawCoarseScanlines()
        {
            for (float y = 0.14f; y < 0.88f; y += 0.006f)
                Rect(0.5f, y, 0.94f, 0.0024f, 0, 0, 0, 55);
        }

        private void DrawCoarseStatic(float intensity)
        {
            if (intensity < 0.02f) return;
            int flecks = (int)(intensity * 55);
            for (int i = 0; i < flecks; i++)
            {
                float x = (float)_rng.NextDouble();
                float y = 0.14f + (float)_rng.NextDouble() * 0.72f;
                int a = 45 + _rng.Next(150);
                int shade = 180 + _rng.Next(75);
                Rect(x, y, 0.009f, 0.012f, shade, shade, shade, a);   // big chunky low-res blocks
            }
            if (intensity > 0.6f && _rng.Next(2) == 0)   // occasional horizontal tracking tear
                Rect(0.5f, 0.14f + (float)_rng.NextDouble() * 0.72f, 0.94f, 0.010f, 210, 210, 210, 80);
        }

        private static string RetroStamp()
        {
            // Camcorder clock stuck in 1996, with a real running time-of-day.
            return "JAN 01 1996  " + DateTime.Now.ToString("hh:mm:ss tt");
        }

        private void DrawVignette()
        {
            Rect(0.5f, 0.012f, 1.0f, 0.05f, 0, 0, 0, 120);  // top
            Rect(0.5f, 0.988f, 1.0f, 0.05f, 0, 0, 0, 120);  // bottom
            Rect(0.02f, 0.5f, 0.06f, 1.0f, 0, 0, 0, 90);     // left
            Rect(0.98f, 0.5f, 0.06f, 1.0f, 0, 0, 0, 90);     // right
        }

        // Analog "frame roll": a soft bright band that drifts down the feed like an
        // un-synced analog video signal. It drifts faster and brighter as the link weakens.
        private void DrawRollBar(float linkQuality)
        {
            double t = (DateTime.Now - _launchedAt).TotalSeconds;
            float speed = 0.05f + (1f - linkQuality) * 0.22f;
            float y = 0.14f + (float)(((t * speed) % 0.72) + 0.72) % 0.72f;
            int a = 16 + (int)((1f - linkQuality) * 42f);
            Rect(0.5f, y, 0.78f, 0.018f, 235, 235, 235, a);
            Rect(0.5f, y + 0.011f, 0.78f, 0.004f, 0, 0, 0, a);
        }

        // Military FPV aiming reticle: a centre-gap crosshair with mil-style ranging
        // ticks down the lower arm, a centre pip, and four corner brackets framing the
        // target box. The frame pulses red while the warhead is armed -- the pip the
        // operator flies onto the target before detonating. Built from DRAW_RECTs;
        // vertical extents are scaled by ~16:9 so arms read the same visual length.
        private void DrawFpvCrosshair(int r, int g, int b, bool blink)
        {
            const float gap = 0.013f;     // open centre so the target stays visible
            const float armX = 0.026f;    // horizontal arm length
            const float armY = 0.046f;    // vertical arm length (aspect-scaled)
            const float th = 0.0016f;     // vertical line thickness
            const float thH = 0.0028f;    // horizontal line thickness

            // crosshair arms (left/right, up/down of the centre gap)
            Rect(0.5f - gap - armX / 2f, 0.5f, armX, thH, r, g, b, 235);
            Rect(0.5f + gap + armX / 2f, 0.5f, armX, thH, r, g, b, 235);
            Rect(0.5f, 0.5f - gap - armY / 2f, th, armY, r, g, b, 235);
            Rect(0.5f, 0.5f + gap + armY / 2f, th, armY, r, g, b, 235);
            Rect(0.5f, 0.5f, 0.0022f, 0.0040f, r, g, b, 250);   // centre pip

            // mil-style ranging ticks down the lower vertical arm
            for (int i = 1; i <= 3; i++)
                Rect(0.5f, 0.5f + gap + armY + i * 0.020f, 0.010f, th, r, g, b, 150);

            // corner target-frame brackets (pulse red when armed)
            const float fx = 0.052f, fy = 0.092f, len = 0.014f, lenV = 0.024f;
            int fa = blink ? 240 : 130;
            int cr = blink ? 235 : r, cg = blink ? 70 : g, cb = blink ? 70 : b;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                float x = 0.5f + sx * fx, y = 0.5f + sy * fy;
                Rect(x - sx * len / 2f, y, len, thH, cr, cg, cb, fa);   // horizontal leg
                Rect(x, y - sy * lenV / 2f, th, lenV, cr, cg, cb, fa);  // vertical leg
            }
        }

        // Bird's-eye targeting reticle, locked dead-centre (nadir). A centre-gap cross,
        // mil ranging ticks, and four corner brackets forming the target box -- the box
        // goes amber while ordnance is aboard, red BINGO when empty. EO/IR pod style.
        private void DrawTargetReticle(int r, int g, int b, bool armed, bool blink)
        {
            const float gap = 0.016f;
            const float armX = 0.028f, armY = 0.050f;
            const float th = 0.0016f, thH = 0.0028f;
            Rect(0.5f - gap - armX / 2f, 0.5f, armX, thH, r, g, b, 215);
            Rect(0.5f + gap + armX / 2f, 0.5f, armX, thH, r, g, b, 215);
            Rect(0.5f, 0.5f - gap - armY / 2f, th, armY, r, g, b, 215);
            Rect(0.5f, 0.5f + gap + armY / 2f, th, armY, r, g, b, 215);
            Rect(0.5f, 0.5f, 0.0024f, 0.0042f, r, g, b, 245);   // centre pip

            // corner target-box brackets
            const float fx = 0.058f, fy = 0.103f, len = 0.016f, lenV = 0.028f;
            const float t = 0.0018f, tH = 0.0030f;
            int cr = armed ? 235 : 235, cg = armed ? 210 : 90, cb = armed ? 90 : 90;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                float x = 0.5f + sx * fx, y = 0.5f + sy * fy;
                Rect(x - sx * len / 2f, y, len, tH, cr, cg, cb, 210);
                Rect(x, y - sy * lenV / 2f, t, lenV, cr, cg, cb, 210);
            }
            if (armed) Text("READY", 0.5f, 0.5f + fy + 0.006f, 0.22f, cr, cg, cb, true);
            else if (blink) Text("BINGO", 0.5f, 0.5f + fy + 0.006f, 0.22f, 235, 80, 80, true);
        }

        // Compass rose widget (top-right): the cardinals projected onto the rotating
        // top-down image so the operator can read true bearings. North is highlighted.
        private void DrawNorthRose(int r, int g, int b)
        {
            const float cx = 0.900f, cy = 0.205f, rad = 0.045f;
            float ax = rad / 1.777f;   // aspect-correct the horizontal radius
            double z = _yaw * Math.PI / 180.0;
            float cosz = (float)Math.Cos(z), sinz = (float)Math.Sin(z);
            Rect(cx, cy, 0.007f, 0.0016f, r, g, b, 150);
            Rect(cx, cy, 0.0016f, 0.012f, r, g, b, 150);
            string[] card = { "N", "E", "S", "W" };
            float[] vx = { 0f, 1f, 0f, -1f };
            float[] vy = { 1f, 0f, -1f, 0f };
            for (int k = 0; k < 4; k++)
            {
                float sx = vx[k] * cosz + vy[k] * sinz;
                float sy = vx[k] * sinz - vy[k] * cosz;
                int er = k == 0 ? 235 : r, eg = k == 0 ? 235 : g, eb = k == 0 ? 110 : b;
                Text(card[k], cx + sx * ax, cy + sy * rad - 0.012f, 0.22f, er, eg, eb, true);
            }
        }

        private void DrawStatic(float intensity)
        {
            int flecks = (int)(intensity * 40);
            for (int i = 0; i < flecks; i++)
            {
                float x = (float)_rng.NextDouble();
                float y = 0.14f + (float)_rng.NextDouble() * 0.72f;
                int a = 40 + _rng.Next(120);
                Rect(x, y, 0.004f, 0.004f, 230, 230, 230, a);
            }
            if (intensity > 0.85f && _rng.Next(3) == 0)
                Rect(0.5f, 0.14f + (float)_rng.NextDouble() * 0.72f, 0.78f, 0.006f, 200, 200, 200, 90);
        }

        // -------------------------------------------------------------------
        // Geometry / helpers
        // -------------------------------------------------------------------
        // Unit direction from a GTA heading (yaw, 0=N/+Y, CCW+) and pitch (deg).
        private static Vector3 DirFromYawPitch(float yawDeg, float pitchDeg)
        {
            double z = yawDeg * Math.PI / 180.0;
            double x = pitchDeg * Math.PI / 180.0;
            double cp = Math.Abs(Math.Cos(x));
            return new Vector3((float)(-Math.Sin(z) * cp), (float)(Math.Cos(z) * cp), (float)Math.Sin(x));
        }

        // Keep the drone out of solid geometry (buildings, walls, vehicles, objects). We
        // read the cast from LAST frame, push the bird back to just shy of any wall it
        // found, and cancel the velocity going INTO the surface so it slides instead of
        // sticking -- then cast fresh along this frame's path plus a look-ahead.
        //
        // The cast is a SWEPT SPHERE (capsule) of the drone's body radius, not a zero-width
        // ray. A thin ray slipped between/around geometry and let the camera punch through
        // building faces and thin props; a fat swept sphere collides like an actual airframe,
        // so the FPV now stops at walls the way a plane would. Near-horizontal hits (floor/
        // ceiling, |normal.Z| high) are ignored here -- vertical clamping is the ground
        // clamp's job, and treating the floor as a wall would freeze low flight.
        //
        // Still async + validated: in this build the shape-test occasionally reports a bogus
        // "hit at the world origin", so we reject hits off the swept path; the push-back is
        // bounded to this frame's travel, so a bad read can never fling the camera away.
        private void WorldCollide(ref Vector3 nextPos)
        {
            Vector3 seg = nextPos - _pos;
            float segLen = seg.Length();
            Vector3 dir = segLen > 0.0001f ? seg / segLen : DirFromYawPitch(_yaw, 0f);

            // 1) read last frame's swept-sphere result
            if (_probe != 0)
            {
                OutputArgument hit = new OutputArgument(), ep = new OutputArgument(),
                               en = new OutputArgument(), ent = new OutputArgument();
                int status = Function.Call<int>(Hash.GET_SHAPE_TEST_RESULT, _probe, hit, ep, en, ent);
                if (status != 1)   // 1 = still processing; 2 = ready; 0 = gone
                {
                    if (status == 2 && hit.GetResult<bool>())
                    {
                        Vector3 hp = ep.GetResult<Vector3>();
                        Vector3 nrm = en.GetResult<Vector3>();
                        float d = hp.DistanceTo(_probeStart);
                        // Reject the bogus origin hit, anything past the swept path, and
                        // near-horizontal surfaces (floor/ceiling) -- those are the ground
                        // clamp's domain and would otherwise wall off low/level flight.
                        if (hp != Vector3.Zero && d <= _probeLen + 0.6f && Math.Abs(nrm.Z) < 0.6f)
                        {
                            float stop = Math.Max(0f, d - DroneRadius);
                            // Only brake if the wall is closer than where we meant to move.
                            if (stop < nextPos.DistanceTo(_probeStart))
                                nextPos = _probeStart + _probeDir * stop;
                            float into = Vector3.Dot(_vel, nrm);
                            if (into < 0f) _vel -= nrm * into;   // slide along the wall
                        }
                    }
                    _probe = 0;
                }
            }

            // 2) cast a fresh swept sphere along this frame's travel + a look-ahead buffer
            if (_probe == 0)
            {
                _probeStart = _pos;
                _probeDir = dir;
                _probeLen = segLen + 2.0f;
                Vector3 to = _pos + dir * _probeLen;
                _probe = Function.Call<int>(Hash.START_SHAPE_TEST_CAPSULE,
                    _pos.X, _pos.Y, _pos.Z, to.X, to.Y, to.Z,
                    DroneRadius, 1 | 2 | 16, Game.Player.Character.Handle, 7);   // map + vehicles + objects
            }
        }

        // Height above the ground directly below the drone (for the AGL readout).
        private float GroundAgl()
        {
            OutputArgument oz = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, _pos.X, _pos.Y, _pos.Z, oz, false))
                return _pos.Z - oz.GetResult<float>();
            return _pos.Z;
        }

        private static float Wrap360(float a)
        {
            a %= 360f;
            if (a < 0f) a += 360f;
            return a;
        }

        private static string Cardinal(float az)
        {
            string[] pts = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return pts[(int)Math.Round(az / 45.0) % 8];
        }

        private static string GeoCoord(Vector3 p)
        {
            double lat = 34.0522 + p.Y / 111320.0;
            double lon = -118.2437 + p.X / 92385.0;
            return "N" + lat.ToString("0.0000") + " W" + Math.Abs(lon).ToString("0.0000");
        }

        // ---- control reads (everything is disabled while flying; read raw) ----
        private static float Norm(GTA.Control c)
        {
            return Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)c);
        }
        private static bool Pressed(GTA.Control c)
        {
            return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)c);
        }
        private static bool JustPressed(GTA.Control c)
        {
            return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)c);
        }

        // ---- draw primitives (mirrors the heli-cam HUD path) ----
        private static void Rect(float cx, float cy, float w, float h, int r, int g, int b, int a)
        {
            Function.Call(Hash.DRAW_RECT, cx, cy, w, h, r, g, b, a);
        }

        private void Text(string s, float x, float y, float scale, int r, int g, int b)
        {
            Text(s, x, y, scale, r, g, b, false);
        }

        private void Text(string s, float x, float y, float scale, int r, int g, int b, bool center)
        {
            Function.Call(Hash.SET_TEXT_FONT, 4);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, 255);
            Function.Call(Hash.SET_TEXT_DROP_SHADOW);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.SET_TEXT_CENTRE, center);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, s);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
        }
    }
}
