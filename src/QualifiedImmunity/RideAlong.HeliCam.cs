using System;
using GTA;
using GTA.Math;
using GTA.Native;

namespace QualifiedImmunity
{
    public partial class RideAlong
    {
        // -------------------------------------------------------------------
        // Helicopter sensor camera ("AIR-1"). Instead of a flat top-down view, this is a
        // gimbal-style tracking cam -- mounted under the real police chopper when one is
        // airborne, otherwise a virtual ship slowly orbiting overhead -- with selectable
        // optics (EO / night-vision / thermal), optical zoom, and a sensor-operator HUD
        // (reticle, target lock box, live telemetry). Toggled with D-pad Left during a
        // pursuit; while it's up, the D-pad drives the camera (Up/Down zoom, Right optics).
        // -------------------------------------------------------------------

        private float _heliCamZoom = 2f;        // 1..8 optical zoom
        private int _heliCamMode;               // 0 = EO/day, 1 = night-vision, 2 = thermal/IR
        private DateTime _heliCamSince = DateTime.Now;
        private Vector3 _heliCamPos = Vector3.Zero;   // last camera position (for telemetry)

        private static readonly string[] OpticsLong = { "EO / DAYLIGHT", "NIGHT-VISION", "THERMAL / IR" };
        private static readonly string[] OpticsShort = { "EO", "NV", "IR" };

        private void ToggleNewsChopperCamera()
        {
            if (_newsCam != null)
            {
                StopNewsCam();
                Notify("~g~You:~w~ Cutting the feed - back to bodycam.");
                return;
            }

            Vector3 focus = HeliCamFocus();
            if (focus == Vector3.Zero) return;

            _heliCamSince = DateTime.Now;
            _heliCamZoom = 2f;
            _heliCamMode = 0;
            _heliCamPos = HeliCamComputePos(focus);

            _newsCam = Camera.Create(ScriptedCameraNameHash.DefaultScriptedCamera,
                                     _heliCamPos, Vector3.Zero, ZoomToFov(_heliCamZoom), true, EulerRotationOrder.YXZ);
            if (_newsCam == null) return;
            if (Valid(_suspect)) _newsCam.PointAt(_suspect); else _newsCam.PointAt(focus);
            ScriptCameraDirector.StartRendering();
            ApplyHeliCamVision();
            Notify("~b~AIR-1:~w~ Sensor feed LIVE. ~y~D-pad:~w~ Up/Down zoom - Right optics - Left to cut.");
        }

        // Stop rendering the heli cam, tear it down, and clear any optics post-fx.
        private void StopNewsCam()
        {
            if (_newsCam == null) return;
            ScriptCameraDirector.StopRendering(false);
            if (_newsCam.Exists()) _newsCam.Delete();
            _newsCam = null;
            Function.Call(Hash.SET_NIGHTVISION, false);
            Function.Call(Hash.SET_SEETHROUGH, false);
            Function.Call(Hash.CLEAR_TIMECYCLE_MODIFIER);
        }

        private void HeliCamZoomStep(int dir)
        {
            _heliCamZoom += dir;
            if (_heliCamZoom < 1f) _heliCamZoom = 1f;
            if (_heliCamZoom > 8f) _heliCamZoom = 8f;
        }

        private void HeliCamCycleOptics()
        {
            _heliCamMode = (_heliCamMode + 1) % 3;
            ApplyHeliCamVision();
            Notify("~b~AIR-1:~w~ Optics -> " + OpticsLong[_heliCamMode]);
        }

        private void ApplyHeliCamVision()
        {
            Function.Call(Hash.SET_NIGHTVISION, false);
            Function.Call(Hash.SET_SEETHROUGH, false);
            Function.Call(Hash.CLEAR_TIMECYCLE_MODIFIER);
            if (_heliCamMode == 1)
            {
                Function.Call(Hash.SET_NIGHTVISION, true);
            }
            else if (_heliCamMode == 2)
            {
                Function.Call(Hash.SET_SEETHROUGH, true);
            }
            else
            {
                // EO/day: a gun-cam timecycle for the washed-out long-lens sensor look.
                Function.Call(Hash.SET_TIMECYCLE_MODIFIER, "heliGunCam");
                Function.Call(Hash.SET_TIMECYCLE_MODIFIER_STRENGTH, 0.7f);
            }
        }

        // Where the camera looks: the live suspect, else the getaway car.
        private Vector3 HeliCamFocus()
        {
            if (Valid(_suspect)) return _suspect.Position;
            if (Valid(_suspectCar)) return _suspectCar.Position;
            Ped a = AliveSuspect();
            return a != null ? a.Position : Vector3.Zero;
        }

        // Camera position: mounted just under the real chopper if one is airborne, otherwise
        // a virtual ship slowly orbiting above and to the side of the target, with a little
        // handheld sway so it reads as a real airborne gimbal rather than a locked tripod.
        private Vector3 HeliCamComputePos(Vector3 focus)
        {
            // Mount under the real chopper only while it's actually flyable -- a wrecked heli
            // still "exists", and mounting the feed on a smoking wreck on the ground looks broken.
            if (Valid(_heli) && IsDriveable(_heli)) return _heli.Position + new Vector3(0f, 0f, -1.6f);

            double t = (DateTime.Now - _heliCamSince).TotalSeconds;
            double ang = t * 0.18;                 // slow orbit (~rad/s)
            const float R = 48f, H = 80f;          // standoff radius + altitude
            Vector3 pos = focus + new Vector3((float)(Math.Cos(ang) * R), (float)(Math.Sin(ang) * R), H);
            pos += new Vector3((float)(Math.Sin(t * 1.6) * 0.5), (float)(Math.Cos(t * 1.2) * 0.5), (float)(Math.Sin(t * 0.7) * 0.3));
            return pos;
        }

        private static float ZoomToFov(float zoom)
        {
            float fov = 55f / zoom;
            if (fov < 7f) fov = 7f;
            if (fov > 55f) fov = 55f;
            return fov;
        }

        // Re-aim/zoom the camera each frame and suppress the normal game HUD for clarity.
        private void UpdateHeliCam()
        {
            if (_newsCam == null || !_newsCam.Exists()) return;
            Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);
            Vector3 focus = HeliCamFocus();
            // Target momentarily gone (about to be torn down this tick) -> hold the last
            // framing instead of whipping the camera to the map origin for a frame.
            if (focus == Vector3.Zero) return;
            _heliCamPos = HeliCamComputePos(focus);
            _newsCam.Position = _heliCamPos;
            if (Valid(_suspect)) _newsCam.PointAt(_suspect); else _newsCam.PointAt(focus);
            _newsCam.FieldOfView = ZoomToFov(_heliCamZoom);
        }

        // -------------------------------------------------------------------
        // The sensor-operator overlay
        // -------------------------------------------------------------------
        private void DrawHeliCamHud()
        {
            // Optics-tinted HUD colour: green-white EO, bright green NV, white-hot IR.
            int r, g, b;
            switch (_heliCamMode)
            {
                case 1: r = 140; g = 255; b = 140; break;
                case 2: r = 230; g = 230; b = 230; break;
                default: r = 185; g = 230; b = 185; break;
            }

            Vector3 focus = HeliCamFocus();
            Ped tgt = Valid(_suspect) ? _suspect : null;

            // --- framing: corner brackets + a slow sensor sweep bar ---
            DrawSensorFrame(r, g, b);

            // --- centre reticle ---
            DrawReticle(r, g, b);

            // --- target lock box (project the suspect to screen) ---
            bool locked = DrawTargetBox(focus, r, g, b);

            // --- telemetry ---
            float spdMps = tgt != null
                ? (tgt.IsInVehicle() && tgt.CurrentVehicle != null ? tgt.CurrentVehicle.Speed : tgt.Speed)
                : (Valid(_suspectCar) ? _suspectCar.Speed : 0f);
            int mph = (int)Math.Round(spdMps * 2.23694f);
            int hdg = tgt != null ? (int)tgt.Heading : (Valid(_suspectCar) ? (int)_suspectCar.Heading : 0);
            int altFt = (int)Math.Max(0f, (_heliCamPos.Z - focus.Z) * 3.28084f);
            int rng = (int)_heliCamPos.DistanceTo(focus);
            string street = focus != Vector3.Zero ? World.GetStreetName(focus) : "UNKNOWN";
            string clock = DateTime.Now.ToString("HH:mm:ss");
            bool blink = DateTime.Now.Millisecond < 650;

            // top-left: unit ident + record state (the blinking dot is drawn red on its own)
            HeliText("LSPD AIR-1  //  CAM-1", 0.02f, 0.030f, 0.34f, r, g, b);
            if (blink) Rect(0.028f, 0.072f, 0.007f, 0.012f, 220, 45, 45, 235);
            HeliText("      REC   " + clock, 0.02f, 0.060f, 0.32f, r, g, b);

            // top-right: optics + zoom (kept inboard so the longest line stays on-screen)
            HeliText("OPTICS: " + OpticsShort[_heliCamMode], 0.74f, 0.030f, 0.32f, r, g, b);
            HeliText("ZOOM x" + _heliCamZoom.ToString("0") + "  FOV " + ((int)ZoomToFov(_heliCamZoom)), 0.74f, 0.060f, 0.32f, r, g, b);

            // top-centre: track/lock status
            if (locked) HeliText("* LOCK *", 0.5f, 0.085f, 0.36f, 120, 255, 120, true);
            else        HeliText("SCANNING", 0.5f, 0.085f, 0.34f, 235, 200, 90, true);

            // bottom-left: target telemetry
            HeliText("TGT SPD: " + mph + " MPH", 0.02f, 0.860f, 0.32f, r, g, b);
            HeliText("TGT HDG: " + hdg + " DEG", 0.02f, 0.890f, 0.32f, r, g, b);
            HeliText("LOC: " + street, 0.02f, 0.920f, 0.32f, r, g, b);

            // bottom-right: platform telemetry
            HeliText("ALT: " + altFt + " FT AGL", 0.74f, 0.860f, 0.32f, r, g, b);
            HeliText("RNG: " + rng + " M", 0.74f, 0.890f, 0.32f, r, g, b);
            HeliText("GPS: " + focus.X.ToString("0.0") + " / " + focus.Y.ToString("0.0"), 0.74f, 0.920f, 0.32f, r, g, b);
        }

        // Corner brackets around the active sensor area + a slow downward refresh sweep.
        private void DrawSensorFrame(int r, int g, int b)
        {
            const float L = 0.12f, R = 0.88f, T = 0.14f, B = 0.86f; // frame extents
            DrawCorner(L, T, +1, +1, r, g, b);
            DrawCorner(R, T, -1, +1, r, g, b);
            DrawCorner(L, B, +1, -1, r, g, b);
            DrawCorner(R, B, -1, -1, r, g, b);

            float sweep = T + (float)(((DateTime.Now - _heliCamSince).TotalSeconds * 0.13) % 1.0) * (B - T);
            Rect(0.5f, sweep, (R - L), 0.0035f, r, g, b, 40);
        }

        private void DrawCorner(float x, float y, int sx, int sy, int r, int g, int b)
        {
            const float lx = 0.022f, ly = 0.038f, tx = 0.0016f, ty = 0.0028f;
            Rect(x + sx * lx / 2f, y, lx, ty, r, g, b, 170);   // horizontal arm
            Rect(x, y + sy * ly / 2f, tx, ly, r, g, b, 170);   // vertical arm
        }

        private void DrawReticle(int r, int g, int b)
        {
            // crosshair with a centre gap + a centre dot
            Rect(0.5f - 0.030f, 0.5f, 0.022f, 0.0030f, r, g, b, 210); // left
            Rect(0.5f + 0.030f, 0.5f, 0.022f, 0.0030f, r, g, b, 210); // right
            Rect(0.5f, 0.5f - 0.052f, 0.0017f, 0.040f, r, g, b, 210); // top
            Rect(0.5f, 0.5f + 0.052f, 0.0017f, 0.040f, r, g, b, 210); // bottom
            Rect(0.5f, 0.5f, 0.0020f, 0.0035f, r, g, b, 235);          // centre dot
        }

        // Project the target to screen and draw a bracketed lock box. Returns true when the
        // target is on screen and sitting near the reticle (a "lock").
        private bool DrawTargetBox(Vector3 world, int r, int g, int b)
        {
            if (world == Vector3.Zero) return false;
            OutputArgument ox = new OutputArgument(), oy = new OutputArgument();
            bool on = Function.Call<bool>(Hash.GET_SCREEN_COORD_FROM_WORLD_COORD, world.X, world.Y, world.Z, ox, oy);
            if (!on) return false;
            float sx = ox.GetResult<float>(), sy = oy.GetResult<float>();

            const float bw = 0.060f, bh = 0.100f;
            DrawCorner(sx - bw / 2f, sy - bh / 2f, +1, +1, r, g, b);
            DrawCorner(sx + bw / 2f, sy - bh / 2f, -1, +1, r, g, b);
            DrawCorner(sx - bw / 2f, sy + bh / 2f, +1, -1, r, g, b);
            DrawCorner(sx + bw / 2f, sy + bh / 2f, -1, -1, r, g, b);
            HeliText("SUSPECT", sx, sy - bh / 2f - 0.030f, 0.28f, r, g, b, true);

            return Math.Abs(sx - 0.5f) < 0.07f && Math.Abs(sy - 0.5f) < 0.10f;
        }

        private static void Rect(float cx, float cy, float w, float h, int r, int g, int b, int a)
        {
            Function.Call(Hash.DRAW_RECT, cx, cy, w, h, r, g, b, a);
        }

        // Thin wrapper over the native text path (drop-shadowed, condensed font) used by the
        // dispatch menu, so the heli HUD matches the game's own UI rendering.
        private void HeliText(string text, float x, float y, float scale, int r, int g, int b)
        {
            DrawMenuText(text, x, y, scale, 4, r, g, b, false);
        }

        private void HeliText(string text, float x, float y, float scale, int r, int g, int b, bool center)
        {
            DrawMenuText(text, x, y, scale, 4, r, g, b, center);
        }
    }
}
