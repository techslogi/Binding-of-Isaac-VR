using System;
using System.IO;
using StereoKit;

namespace IsaacDiorama;

/// <summary>
/// Phase 2b — real Isaac sprites. Each entity is drawn from its actual anm2 +
/// current animation frame, composited from the game's spritesheets, standing
/// at a fixed angle with its pivot on the floor (so tears/flies sit at the
/// right height automatically). Anything that can't be resolved falls back to
/// the Phase 2a coloured quad, so the diorama always renders.
///
/// Resources: pass the Binding of Isaac install folder as the first argument,
/// or put the path on the first line of resources_root.txt next to the app.
/// The base sprites must be UNPACKED first with the game's Resource Extractor
/// (tools/ResourceExtractor) — packed .a archives can't be read directly.
/// </summary>
internal static class Program
{
    // Rooms scale by a FIXED world->table ratio, so a 2x2 room is genuinely
    // twice the size of a 1x1 instead of being squashed to the same footprint.
    // Shown in the hidden Debug page and logged at startup, so a bug report can be pinned to
    // a build without asking the reporter to find a log file.
    public const string Version = "0.9.0-beta";

    const float RoomPxToM     = 0.00136f;  // game px -> metres (1x1 room ~= 0.6m)

    // ---------------- Options menu / passthrough ----------------
    // Passthrough is done by rendering a flat key colour that Virtual Desktop chroma-keys
    // away, so "passthrough on" is literally just the clear colour. With it OFF we paint
    // black instead and build the room Isaac is standing in AROUND the player at life size,
    // so the diorama board floats inside a virtual version of his room instead of yours.
    static bool _passthrough = false;        // OFF by default (the 1:1 room replaces it)
    enum MenuPage { Main, Options, ConfirmExit, Debug }
    static MenuPage _menuPage = MenuPage.Main;
    static Pose _menuPose    = Pose.Identity;
    static readonly Color PassthroughKey = new Color(0f, 0f, 180f/255f);
    // Metres per Isaac TILE for the life-size room. A tile is 40 game px and a 1x1 room's
    // play area is 13x7 tiles, so 1.0 puts you in a ~13m x 7m hall.
    static float RoomMetersPerTile = 2.00f;  // Tab->RoomMPerTile (baked from VR)
    const  float TilePx = 40f;
    // Menu placement in RIG space — the same space HudRenderer.Origin lives in — so the
    // menu travels with the board when you grab it. Viewer looks down -z, so -x is left.
    static float MenuX = -0.520f;            // Tab->MenuX (baked from VR)
    static float MenuY =  0.000f;            // Tab->MenuY (baked from VR)
    // World Y of the life-size room's floor. NOT 0: this app's tracking origin sits at head
    // height rather than on the real floor, so a room built at y=0 puts the floor through
    // your eyes. Roughly negative eye height. Tab->RoomFloorY to bake, and there is a live
    // slider in the options menu because it is the one value you can only judge standing up.
    static float RoomFloorY = -1.600f;       // Tab->RoomFloorY
    // Push the life-size room away from the anchor along world -z. The anchor sits under the
    // headset, which centres the room on you exactly — but the diorama board is in front of
    // you, so the far wall wants to sit further back than dead centre.
    static float RoomZ      = -3.000f;       // Tab->RoomZ (baked from VR; negative = further away)
    // Info panel, left of the diorama under the options menu. Two INDEPENDENT toggles so
    // either can run alone.
    static bool  _showStats = false;
    static bool  _showEid   = false;
    // Each block is placed INDEPENDENTLY — they are two different readouts, not one panel.
    // Defaults sit them near the HUD's dark square rather than out at the menu's column.
    static float StatsX = -0.200f, StatsY = -0.140f, StatsZ = -0.740f;   // baked from VR
    static float EidX   = -0.100f, EidY   = -0.120f, EidZ   = -0.740f;   // baked from VR
    // The debug/tuner page is hidden until a controller face button asks for it, so it can
    // never be opened by a stray ray during play.
    static bool _debugUnlocked = false;
    const  float MenuZ = -0.740f;            // same depth as the HUD panel
    // World centre of the life-size room. Anchored under the headset when passthrough is
    // switched off (and once on the first frame), NOT followed every frame — walls that
    // chase your head are unusable.
    static Vec3 _roomAnchor = Vec3.Zero;
    static bool _roomAnchored = false;

    // ---------------- Persisted settings ----------------
    // Only USER-FACING values live here: the toggles, and the numbers that describe THIS
    // player's physical setup (scale, floor height, panel placement). Art calibration —
    // entity lifts, water, reflections — is deliberately NOT persisted, because those are
    // baked into constants and a saved file would silently shadow every future default.
    //
    // A MISSING KEY KEEPS THE COMPILED DEFAULT, so shipping a new default still reaches
    // anything the file does not already mention.
    //
    // Values are written with InvariantCulture on purpose: this machine's locale uses a
    // COMMA decimal separator, and a file written as "0,090" then parsed as invariant reads
    // back as 90.
    sealed class Setting
    {
        public string Key = "";
        public Func<float> Get = () => 0f;
        public Action<float> Set = _ => { };
        public float Default;
    }
    static readonly System.Collections.Generic.List<Setting> _settings = new();
    static bool  _settingsDirty;
    static float _settingsSaveAt;
    // Next to the exe when that folder is writable (portable / unzipped installs), otherwise
    // %APPDATA%\IsaacDiorama. Under Program Files the first one fails for a normal user, and
    // the save is try/caught — so without this the settings would silently never persist.
    // Probed by actually WRITING, not by inspecting ACLs: virtualisation and OneDrive
    // redirection both make the permission bits lie.
    static string? _settingsPath;
    static string SettingsPath
    {
        get
        {
            if (_settingsPath != null) return _settingsPath;
            string local = Path.Combine(AppContext.BaseDirectory, "diorama-settings.txt");
            try
            {
                string probe = Path.Combine(AppContext.BaseDirectory, ".diorama-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                _settingsPath = local;
            }
            catch
            {
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "IsaacDiorama");
                    Directory.CreateDirectory(dir);
                    _settingsPath = Path.Combine(dir, "diorama-settings.txt");
                }
                catch { _settingsPath = local; }   // nothing writable: try anyway, save logs the failure
            }
            Log.Info($"[Diorama] settings file: {_settingsPath}");
            return _settingsPath;
        }
    }

    static void Reg(string key, Func<float> get, Action<float> set)
        => _settings.Add(new Setting { Key = key, Get = get, Set = set, Default = get() });

    static void RegisterSettings()
    {
        if (_settings.Count > 0) return;
        // NOTE the passthrough setter does NOT recenter the room: at load time there is no
        // headset pose yet. _roomAnchored is still false, so the first frame anchors it.
        Reg("passthrough",  () => _passthrough ? 1f : 0f,
                            v => { _passthrough = v != 0f;
                                   Renderer.ClearColor = _passthrough ? PassthroughKey
                                                                      : new Color(0f, 0f, 0f); });
        Reg("showStats",    () => _showStats ? 1f : 0f, v => _showStats = v != 0f);
        Reg("showEid",      () => _showEid   ? 1f : 0f, v => _showEid   = v != 0f);
        Reg("rigScale",     () => _rigScale,            v => _rigScale = v);
        Reg("roomFloorY",   () => RoomFloorY,           v => RoomFloorY = v);
        Reg("roomZ",        () => RoomZ,                v => RoomZ = v);
        Reg("roomMPerTile", () => RoomMetersPerTile,    v => RoomMetersPerTile = v);
        Reg("menuX",        () => MenuX,                v => MenuX = v);
        Reg("menuY",        () => MenuY,                v => MenuY = v);
        Reg("statsX",       () => StatsX,               v => StatsX = v);
        Reg("statsY",       () => StatsY,               v => StatsY = v);
        Reg("statsZ",       () => StatsZ,               v => StatsZ = v);
        Reg("eidX",         () => EidX,                 v => EidX = v);
        Reg("eidY",         () => EidY,                 v => EidY = v);
        Reg("eidZ",         () => EidZ,                 v => EidZ = v);
    }

    static void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) { Log.Info("[Diorama] settings: none yet, using defaults"); return; }
            int n = 0;
            foreach (var raw in File.ReadAllLines(SettingsPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (!float.TryParse(v, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float f)) continue;
                foreach (var st in _settings)
                    if (st.Key == k) { st.Set(f); n++; break; }
            }
            Log.Info($"[Diorama] settings: loaded {n} value(s) from {SettingsPath}");
        }
        catch (Exception ex) { Log.Warn($"[Diorama] settings load failed: {ex.Message}"); }
    }

    static void SaveSettings()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("# IsaacDiorama settings. Delete this file to return to the built-in defaults.\n");
            foreach (var st in _settings)
                sb.Append(st.Key).Append('=')
                  .Append(st.Get().ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('\n');
            File.WriteAllText(SettingsPath, sb.ToString());
        }
        catch (Exception ex) { Log.Warn($"[Diorama] settings save failed: {ex.Message}"); }
    }

    /// <summary>Mark settings changed; the write is debounced so dragging a slider or scaling
    /// the board does not hit the disk every frame.</summary>
    static void TouchSettings() { _settingsDirty = true; _settingsSaveAt = Time.Totalf + 1.0f; }

    static void FlushSettings(bool force)
    {
        if (!_settingsDirty) return;
        if (!force && Time.Totalf < _settingsSaveAt) return;
        _settingsDirty = false;
        SaveSettings();
    }

    static void ResetSettings()
    {
        foreach (var st in _settings) st.Set(st.Default);
        try { if (File.Exists(SettingsPath)) File.Delete(SettingsPath); } catch { }
        _settingsDirty = false;
        Log.Info("[Diorama] settings reset to built-in defaults");
    }

    static string _resourceHow = "";
    static bool   _resourcesOk;
    // Hierarchy pushes made THIS frame. The frame guard below unwinds exactly these: a throw
    // between a Push and its Pop would otherwise leave StereoKit's transform stack unbalanced
    // for the rest of the run, which looks far worse than the original exception.
    static int  _hierDepth;
    static bool _frameErrLogged;

    static void FrameError(Exception ex)
    {
        while (_hierDepth > 0) { try { Hierarchy.Pop(); } catch { } _hierDepth--; }
        if (_frameErrLogged) return;      // once: a per-frame throw would flood the log
        _frameErrLogged = true;
        Log.Warn($"[Diorama] FRAME ERROR (further ones suppressed): {ex}");
    }

    /// <summary>
    /// Shown instead of a silently empty diorama when the game's extracted resources cannot
    /// be found. The failure a first-time user actually hits is almost never a rendering bug,
    /// and a blank board tells them nothing — so say what is missing and where we looked.
    /// </summary>
    static void DrawSetupHelp(SpriteRenderer sprites)
    {
        var L = new System.Collections.Generic.List<string>
        {
            "ISAAC DIORAMA " + Version + " -- SETUP NEEDED",
            "",
            "The game's extracted resources were not found,",
            "so no sprites can be drawn.",
            "",
            "1. Install REPENTOGON",
            "2. Run the Resource Extractor that ships with",
            "   Isaac (tools/ResourceExtractor)",
            "3. Enable the 'isaac-diorama-exporter' mod",
            "4. Launch Isaac with  --luadebug",
            "",
            "Resource path: " + (_resourceHow.Length > 0 ? _resourceHow : "(none)"),
            "Status: " + sprites.Status,
            "",
            "To set it by hand, put the Isaac folder path in",
            "resources_root.txt next to this app.",
        };
        DrawTextBlock(L, 0f, 0.10f, -0.90f, BtnTextScale * 0.55f);
    }

    // With OutputType=WinExe there is no console, so every Log.Info that used to scroll past
    // in a terminal would simply vanish — including the BUILD banner, the resource-path line
    // and the per-frame error guard, which are the first things any bug report needs. Mirror
    // the whole StereoKit log to a file next to the settings (so it lands somewhere writable
    // for the same reasons).
    static System.IO.StreamWriter? _logFile;
    static void StartFileLog()
    {
        try
        {
            string dir  = Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory;
            string path = Path.Combine(dir, "diorama-log.txt");
            // Truncate per run: a log that grows forever is one nobody will attach to a
            // report, and the interesting run is always the most recent one.
            _logFile = new System.IO.StreamWriter(path, false) { AutoFlush = true };
            _logFile.WriteLine($"Isaac Diorama v{Version}   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log.Subscribe((level, text) =>
            {
                try { _logFile?.WriteLine($"[{level}] {text.TrimEnd()}"); } catch { }
            });
            Log.Info($"[Diorama] log file: {path}");
        }
        catch (Exception ex) { Log.Warn($"[Diorama] could not open a log file: {ex.Message}"); }
    }

    static void RecenterBigRoom()
    {
        var h = Input.Head.position;
        // X/Z only. Y is read from RoomFloorY at DRAW time, so dragging the floor slider
        // moves the floor live instead of needing a re-anchor.
        _roomAnchor = new Vec3(h.x, 0f, h.z);
        _roomAnchored = true;
    }

    static void SetPassthrough(bool on)
    {
        _passthrough = on;
        Renderer.ClearColor = on ? PassthroughKey : new Color(0f, 0f, 0f);
        if (!on) RecenterBigRoom();
    }

    // ---------------- Options menu (hand-rolled, ray-driven) ----------------
    // StereoKit's own UI was tried first and its FAR interaction never engaged here: poking
    // the buttons worked, pointing at them did nothing, and forcing the global far-interact
    // switch changed nothing either. Rather than keep guessing at SK's internals from an
    // environment that cannot run it, the menu is now drawn and hit-tested by hand. Every
    // step — the ray, the plane intersection, the trigger edge — is ours, visible in the
    // log, and drawn on screen, so a failure says which step failed.
    static Material? _menuMatN, _menuMatH, _menuCursorMat;
    static Material MenuMat(ref Material? slot, Color c)
    {
        if (slot != null) return slot;
        var m = Default.MaterialUnlit.Copy();
        m.Transparency = Transparency.Blend;
        m.FaceCull     = Cull.None;
        m.DepthWrite   = false;
        m[MatParamName.ColorTint] = c;
        slot = m; return slot;
    }

    // 2/3 of the first pass, baked from VR.
    const float BtnW = 0.1733f, BtnH = 0.0500f, BtnGap = 0.0093f;
    const float BtnTextScale = 0.400f;   // doubled from 0.200 (baked from VR)
    static bool _trigPrev;
    static int  _menuHitLogged = -99;

    readonly struct MenuItem
    {
        public readonly string Label; public readonly int Id;
        public MenuItem(string label, int id) { Label = label; Id = id; }
    }

    /// <summary>Ray from a controller's AIM pose, or false when neither is tracked.</summary>
    static bool PointerRay(out Vec3 origin, out Vec3 dir, out bool trigger)
    {
        origin = Vec3.Zero; dir = Vec3.Forward; trigger = false;
        for (int i = 0; i < 2; i++)
        {
            var c = Input.Controller(i == 0 ? Handed.Right : Handed.Left);
            if (!c.IsTracked) continue;
            origin  = c.aim.position;
            dir     = c.aim.orientation * Vec3.Forward;   // poses look down their own -z
            trigger = c.trigger > 0.6f;
            return true;
        }
        return false;
    }

    static void DrawOptionsMenu()
    {
        // X1 (the A / X face button) on either controller toggles the Debug entry. BtnState's
        // IsJustActive() is the same edge helper the keyboard paths use, so this leans on an
        // API already proven in this codebase rather than a controller-specific helper.
        for (int i = 0; i < 2; i++)
        {
            var c = Input.Controller(i == 0 ? Handed.Right : Handed.Left);
            if (c.IsTracked && c.x1.IsJustActive())
            {
                _debugUnlocked = !_debugUnlocked;
                if (!_debugUnlocked && _menuPage == MenuPage.Debug) _menuPage = MenuPage.Options;
                Log.Info($"[Diorama] debug menu {(_debugUnlocked ? "UNLOCKED" : "hidden")}");
            }
        }

        // Menu frame: origin at the panel centre, facing the viewer.
        Vec3 local  = new Vec3(MenuX, MenuY, MenuZ);
        Vec3 centre = _rig.position + _rig.orientation * (_rigScale * local);
        Quat rot    = _rig.orientation * Quat.FromAngles(0, 180, 0);
        // Panel axes in world space. The 180 yaw means the panel's own +x runs to the
        // viewer's LEFT, so negate it and lay the buttons out left-to-right as read.
        Vec3 right  = -(rot * Vec3.Right);
        Vec3 up     =   rot * Vec3.Up;
        // A StereoKit pose looks down its OWN -z, so with the 180 yaw the panel's visible
        // face is rot*Vec3.Forward = +z world, i.e. toward the viewer. (rot*(0,0,1) is the
        // BACK of the panel; using that would have failed every front-face test below.)
        Vec3 normal =   rot * Vec3.Forward;        // out of the panel, toward the viewer

        var items = new System.Collections.Generic.List<MenuItem>();
        switch (_menuPage)
        {
            case MenuPage.Main:
                items.Add(new MenuItem("Options", 0));
                items.Add(new MenuItem("Exit", 5));
                break;
            case MenuPage.Options:
                items.Add(new MenuItem(_passthrough ? "Passthrough: ON" : "Passthrough: OFF", 1));
                if (_debugUnlocked) items.Add(new MenuItem("Debug", 15));
                items.Add(new MenuItem(_showStats ? "Stats: ON" : "Stats: OFF", 8));
                items.Add(new MenuItem(_showEid ? "EID: ON" : "EID: OFF", 9));
                items.Add(new MenuItem("Floor  -", 2));
                items.Add(new MenuItem("Floor  +", 3));
                items.Add(new MenuItem("Back", 4));
                break;
            case MenuPage.ConfirmExit:
                items.Add(new MenuItem("Yes", 6));
                items.Add(new MenuItem("No", 7));
                break;
            // The whole Tab tuner, driven by the ray. Keyboard and XInput both reach the GAME
            // rather than this app once the headset is on, so the VR controllers are the only
            // input that gets here — which makes every existing slider unreachable in VR
            // unless it has buttons of its own.
            case MenuPage.Debug:
                items.Add(new MenuItem("< prev", 10));
                items.Add(new MenuItem("next >", 11));
                items.Add(new MenuItem("   -   ", 12));
                items.Add(new MenuItem("   +   ", 13));
                items.Add(new MenuItem("Defaults", 16));
                items.Add(new MenuItem("Back", 14));
                break;
        }

        // ---- pointer ----
        int hit = -1;
        bool haveRay = PointerRay(out Vec3 ro, out Vec3 rd, out bool trig);
        float hx = 0, hy = 0, hitT = 0;
        if (haveRay)
        {
            // Intersect the ray with the panel plane. denom < 0 means we are pointing at the
            // FRONT face; a back-face hit is not a hit.
            float denom = Vec3.Dot(rd, normal);
            if (denom < -0.0001f)
            {
                float t = Vec3.Dot(centre - ro, normal) / denom;
                if (t > 0.02f && t < 12f)
                {
                    Vec3 p = ro + rd * t;
                    Vec3 d = p - centre;
                    hx = Vec3.Dot(d, right);
                    hy = Vec3.Dot(d, up);
                    hitT = t;
                    for (int i = 0; i < items.Count; i++)
                    {
                        float cy = -i * (BtnH + BtnGap);
                        if (MathF.Abs(hx) <= BtnW * 0.5f && MathF.Abs(hy - cy) <= BtnH * 0.5f)
                        { hit = i; break; }
                    }
                }
            }
            // Draw the ray so there is always visible feedback about where it is going.
            float len = hit >= 0 ? hitT : 1.5f;
            Lines.Add(ro, ro + rd * len,
                      hit >= 0 ? new Color32(120, 220, 255, 220) : new Color32(140, 140, 140, 110),
                      0.004f);
            if (hit >= 0)
                Mesh.Quad.Draw(MenuMat(ref _menuCursorMat, new Color(0.5f, 0.9f, 1f, 0.9f)),
                               Matrix.TRS(centre + right * hx + up * hy + normal * 0.002f,
                                          rot, new Vec3(0.012f, 0.012f, 1f)));
        }

        // ---- click (rising edge of the trigger) ----
        bool clicked = trig && !_trigPrev;
        _trigPrev = trig;
        if (hit != _menuHitLogged)
        {
            _menuHitLogged = hit;
            Log.Info($"[Diorama] MENU ray={haveRay} hit={hit} local=({hx:0.000},{hy:0.000}) trig={trig}");
        }
        if (clicked && hit >= 0)
        {
            int id = items[hit].Id;
            Log.Info($"[Diorama] MENU click id={id} '{items[hit].Label}'");
            switch (id)
            {
                case 0: _menuPage = MenuPage.Options; break;
                case 1: SetPassthrough(!_passthrough); TouchSettings(); break;
                case 2: RoomFloorY = Math.Clamp(RoomFloorY - 0.05f, -3f, 0.5f); TouchSettings(); break;
                case 3: RoomFloorY = Math.Clamp(RoomFloorY + 0.05f, -3f, 0.5f); TouchSettings(); break;
                case 4: _menuPage = MenuPage.Main; break;
                case 5: _menuPage = MenuPage.ConfirmExit; break;
                case 6:
                    Log.Info("[Diorama] EXIT confirmed — closing Isaac, then quitting");
                    FlushSettings(true);
                    GameLauncher.CloseIsaac();
                    SK.Quit();
                    break;
                case 7: _menuPage = MenuPage.Main; break;
                case 8: _showStats = !_showStats; TouchSettings(); break;
                case 9: _showEid   = !_showEid;   TouchSettings(); break;
                case 10: _tune = (Tune)(((int)_tune + TuneCount - 1) % TuneCount); PrintTune(); break;
                case 11: _tune = (Tune)(((int)_tune + 1) % TuneCount); PrintTune(); break;
                case 12: ApplyTune(-1); TouchSettings(); break;
                case 13: ApplyTune(+1); TouchSettings(); break;
                case 14: _menuPage = MenuPage.Options; break;
                case 15: _menuPage = MenuPage.Debug; PrintTune(); break;
                case 16: ResetSettings(); PrintTune(); break;
            }
        }

        // ---- draw ----
        for (int i = 0; i < items.Count; i++)
        {
            float cy = -i * (BtnH + BtnGap);
            Vec3 bc  = centre + up * cy;
            var mat  = i == hit
                     ? MenuMat(ref _menuMatH, new Color(0.22f, 0.45f, 0.62f, 0.95f))
                     : MenuMat(ref _menuMatN, new Color(0.10f, 0.11f, 0.14f, 0.85f));
            Mesh.Quad.Draw(mat, Matrix.TRS(bc, rot, new Vec3(BtnW, BtnH, 1f)));
            Text.Add(items[i].Label, Matrix.TRS(bc + normal * 0.001f, rot, BtnTextScale),
                     TextAlign.Center);
        }
        if (_menuPage == MenuPage.Options)
            Text.Add($"Floor {RoomFloorY:0.00} m",
                     Matrix.TRS(centre + up * (BtnH * 0.5f + 0.022f), rot, BtnTextScale * 0.85f),
                     TextAlign.Center);
        if (_menuPage == MenuPage.Debug)
            Text.Add($"v{Version}\n[{(int)_tune + 1}/{TuneCount}]\n{_tuneLine}",
                     Matrix.TRS(centre + up * (BtnH * 0.5f + 0.040f), rot, BtnTextScale * 0.70f),
                     TextAlign.Center);
        if (_menuPage == MenuPage.ConfirmExit)
        {
            // The message sits ABOVE the Yes/No pair, on its own backing panel so it stays
            // readable against a bright wall in the life-size room.
            Vec3 mc = centre + up * (BtnH * 0.5f + 0.060f);
            Mesh.Quad.Draw(MenuMat(ref _menuMatN, new Color(0.10f, 0.11f, 0.14f, 0.85f)),
                           Matrix.TRS(mc, rot, new Vec3(BtnW * 1.6f, 0.100f, 1f)));
            Text.Add("Quitting closes BOTH\nthe app and the game.\nAre you sure?",
                     Matrix.TRS(mc + normal * 0.001f, rot, BtnTextScale * 0.85f),
                     TextAlign.Center);
        }
    }

    /// <summary>
    /// Greedy word-wrap to a character budget. EID descriptions run to several sentences and
    /// StereoKit's Text.Add does not wrap, so long lines would simply run off into the room.
    /// Wrapping on characters rather than measured width is deliberate: the font is
    /// monospaced enough at this size, and a measured wrap would need a text-style round trip
    /// per candidate line for a panel that is read, not laid out.
    /// </summary>
    static System.Collections.Generic.List<string> Wrap(string text, int cols)
    {
        var outp = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(text)) return outp;
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            if (para.Length == 0) { outp.Add(""); continue; }
            var line = new System.Text.StringBuilder();
            foreach (var word in para.Split(' '))
            {
                if (word.Length == 0) continue;
                if (line.Length > 0 && line.Length + 1 + word.Length > cols)
                { outp.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) outp.Add(line.ToString());
        }
        return outp;
    }

    /// <summary>
    /// EID's text carries its own markup: `#` is a LINE BREAK, and `{{...}}` are icon tokens
    /// naming sprites we have no way to draw. Left alone, a description renders as one long
    /// run with `{{Collectible105}}` sitting in the middle of it.
    /// </summary>
    static string CleanEid(string src)
    {
        if (string.IsNullOrEmpty(src)) return "";
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (src[i] == '{' && i + 1 < src.Length && src[i + 1] == '{')
            {
                int end = src.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end >= 0) { i = end + 1; continue; }   // drop the whole token
            }
            sb.Append(src[i] == '#' ? '\n' : src[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Draw a block of lines as ONE multi-line Text.Add at a rig-relative position.
    ///
    /// One call, not one per line: hand-stepping a LineH gave double-spaced stats because the
    /// step had nothing to do with the font's real line height. StereoKit already knows its
    /// own leading, so "\n" is both simpler and correct by construction.
    ///
    /// Lines are LEFT-aligned inside a CENTRE-anchored block. An earlier version padded every
    /// line to the widest and centred them, on the theory that equal-length lines centre and
    /// left-align identically — that only holds for a MONOSPACED font, and StereoKit's default
    /// is proportional, so a row of narrow glyphs rendered narrower and drifted right. The
    /// block anchor stays centred so positions calibrated against the old behaviour still hold;
    /// only the per-line alignment changed.
    /// </summary>
    static void DrawTextBlock(System.Collections.Generic.List<string> lines,
                              float lx, float ly, float lz, float scale)
    {
        if (lines.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }
        Vec3 pos = _rig.position + _rig.orientation * (_rigScale * new Vec3(lx, ly, lz));
        Quat rot = _rig.orientation * Quat.FromAngles(0, 180, 0);
        Text.Add(sb.ToString(), Matrix.TRS(pos, rot, scale),
                 TextAlign.Center,      // block anchor: unchanged, so baked X/Y still hold
                 TextAlign.XLeft);      // lines inside the block: flush left
    }

    /// <summary>
    /// The stats readout and the EID readout. Two INDEPENDENT blocks with their own
    /// positions, no backing panel — they sit over the scene as plain text.
    /// </summary>
    static void DrawInfoPanel(UdpReceiver rx)
    {
        if (!_showStats && !_showEid) return;
        var st = rx.GetStats();
        if (!st.Valid) return;

        if (_showStats)
        {
            var L = new System.Collections.Generic.List<string>
            {
                "-- STATS --",
                $"Speed       {st.Speed:0.00}",
                $"Fire rate   {st.FireRate:0.00}/s",
                $"Damage      {st.Damage:0.00}",
                $"Range       {st.Range:0.00}",
                $"Shot speed  {st.ShotSpeed:0.00}",
                $"Luck        {st.Luck:0.00}",
                $"Planetarium {st.Planetarium * 100f:0.0}%",
                // NOT a probability: the game only exposes a modifier. Labelled so it can
                // never be read as "an N% chance of an angel room".
                $"Angel mod   {st.AngelMod:0.00}",
            };
            DrawTextBlock(L, StatsX, StatsY, StatsZ, BtnTextScale * 0.55f);
        }

        if (_showEid && st.HaveEid)
        {
            var L = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(st.EidName)) L.Add("-- " + st.EidName + " --");
            foreach (var l in Wrap(CleanEid(st.EidDesc), 34)) L.Add(l);
            DrawTextBlock(L, EidX, EidY, EidZ, BtnTextScale * 0.55f);
        }
    }

    /// <summary>
    /// Rebuilds the current room at life size around the player: walls and floor only, no
    /// entities — the diorama board in front of you is still where the game is played. Drawn
    /// OUTSIDE the grab rig so it stays put in the world while the board moves.
    /// </summary>
    static void DrawBigRoom(BackdropRenderer backdrop, UdpReceiver rx)
    {
        if (_passthrough) return;
        var bd = rx.GetBackdrop();
        if (!bd.Valid) return;
        if (!_roomAnchored) RecenterBigRoom();

        // Size from the room's own game-pixel bounds, NOT from the diorama's metre
        // dimensions: the diorama clamps large rooms to MaxFootprintM, which would silently
        // shrink the metres-per-tile here and make an L room feel smaller than a 1x1.
        float worldW = MathF.Max(1f, bd.MaxX - bd.MinX);
        float worldH = MathF.Max(1f, bd.MaxY - bd.MinY);
        float mPerPx = RoomMetersPerTile / TilePx;

        // The wall statics are calibrated for the ~0.6m diorama, so at room scale they have
        // to grow by the same ratio or the walls come out ankle-high. Set, draw, restore.
        float k  = mPerPx / RoomPxToM;
        float h0 = BackdropRenderer.WallHeightM;
        float r0 = BackdropRenderer.WallRaiseM;
        float o0 = BackdropRenderer.WallOutsetM;
        // HasLight/LightCenter/LightRadius are STATIC, i.e. shared with the diorama. Isaac's
        // light pool sits at the board, nowhere near the life-size room, so leaving it on
        // would just compute "fully outside the light" across the whole room. Off for this
        // pass = a uniform overlay, which is what a room you are standing in wants anyway.
        bool  l0 = BackdropRenderer.HasLight;
        backdrop.Water = rx.GetWater();
        try
        {
            BackdropRenderer.HasLight = false;
            BackdropRenderer.WallHeightM = h0 * k;
            BackdropRenderer.WallRaiseM  = r0 * k;
            BackdropRenderer.WallOutsetM = o0 * k;
            var centre = new Vec3(_roomAnchor.x, RoomFloorY, _roomAnchor.z + RoomZ);
            backdrop.Draw(bd.Type, bd.Shape, bd.RoomType, bd.Wall, bd.Floor, bd.Name,
                          centre, worldW * mPerPx, worldH * mPerPx, RoomFloorY);
        }
        finally
        {
            BackdropRenderer.WallHeightM = h0;
            BackdropRenderer.WallRaiseM  = r0;
            BackdropRenderer.WallOutsetM = o0;
            BackdropRenderer.HasLight    = l0;
        }
    }
    const float MaxFootprintM = 1.30f;     // safety clamp for the biggest rooms
    const float MaxFootprint  = 0.60f;   // room's longer axis -> this many metres
    static readonly Vec3 FloorCenter = new(0f, -0.32f, -0.58f);

    // Sprite pixels and Isaac world units are the SAME unit, so sprite size must
    // use the room's own scale. Two independent constants made sprites ~1.9x too
    // big for their spacing (grid rocks overlapped and z-fought).
    const float SpriteScaleFactor = 1.5f;   // calibrated by eye in the headset
    static float GridLiftM   = 0.010f;   // lift grid items (rocks/poop) so they sit on the floor (calibrated)
    static float ShadowBaseRadiusM = 0.220f;   // world radius per unit of (Isaac shadowSize * spriteScale); Tab->ShadowSize (calibrated)
    static float ShadowYNudgeM     = -0.0005f;  // shadow height above the floor; Tab->ShadowY (can go negative) (calibrated)
    static float ShadowMaxAlpha    = 0.55f;   // darkest a shadow's centre gets
    static float LaserWidthMul = 1.0f;    // laser beam width multiplier; Tab->LaserWidth
    static float LaserYNudgeM  = 0.037f;  // laser height above the floor; Tab->LaserY (calibrated)
    static float LaserGlowMul  = 3.0f;    // glow width as a multiple of beam width; Tab->LaserGlowW (baked)
    static float LaserGlowStr  = 0.05f;   // glow intensity (0 = off); Tab->LaserGlowStr (baked)

    // Per-variant beam sheet + body crop (from the .anm2) + tint. The sheet is
    // white/grey art tinted red for brimstone; technology's sheet is pre-coloured.
    // t* = tint applied to the SHEET ART. g* = the beam's APPARENT colour, used for the floor
    // glow. These differ whenever the art is already coloured: Technology's body crop measures
    // pure red (254,0,0) so its tint is WHITE — reusing that tint painted a WHITE glow under a
    // red beam. Keep g* as "what the beam looks like", independent of how the tint gets there.
    static (string sheet, int cx, int cy, int cw, int ch, float tr, float tg, float tb,
            float gr, float gg, float gb) LaserStyle(int variant) => variant switch
    {
        2 => ("Effects/Effect_018_TechnologyLaser.png", 0,   0, 32, 64, 1f, 1f, 1f, 1f, 0f, 0f), // THIN_RED / Technology / Tech X — art IS red, glow red
        3 => ("Effects/Effect_018_LaserEffects.png",    192, 0, 32, 64, 1f, 1f, 1f, 1f, 1f, 1f), // SHOOP / Trisagion — same crop as brimstone but WHITE (not red)
        5 => ("Effects/Effect_018_LaserEffects.png",    416, 0, 32, 64, 1f, 1f, 1f, 1f, 1f, 1f), // LIGHT_BEAM (Gabriel/Uriel) — warm-white crop at x=416
        9 => ("Effects/techstone.png",                  192, 0, 32, 64, 1f, 0f, 0f, 1f, 0f, 0f), // BRIM_TECH
        _ => ("Effects/Effect_018_LaserEffects.png",    192, 0, 32, 64, 1f, 0f, 0f, 1f, 0f, 0f), // THICK_RED brimstone + fallbacks
    };
    // Dogma's beams are shaded white TV-static by the game, and the telegraph line
    // between its Tech Dots is a THIN grey line -- not the thick red brimstone art.
    static float DogmaLaserWidthMul = 0.75f;   // Tab->DogmaLaserW (baked from VR) — THICK (7.1.0)
    // Dogma fires both: 7.1.0 THICK_RED and 7.2.0 THIN_RED (the one the Tech Dots telegraph).
    // The Dogma override swaps both onto the same white crop, so width is the only thing
    // left distinguishing them — without this they both came out thick.
    static float DogmaThinLaserWidthMul = 0.28f;   // Tab->DogmaThinW
    static float DogmaLaserGrey     = 0.80f;   // Tab->DogmaLaserGrey
    static float DogmaTvY           = 0.050f;  // lift for 950.1, Dogma's TV (baked from VR)
    // Mother phase 2 (912.10, `912.010_witness 2.anm2`) sits too low in the floor.
    static float MotherY            = 0.130f;  // Tab->MotherY (baked from VR)
    // Mother phase 1 is FOUR entities sharing one anm2 (912.0.0 body, .1 back, .2 left
    // arm, .3 right arm). The diorama's horizontal scale spreads them wider apart than
    // the game's flat view does, so the arms float away from the body. The app never
    // sees the subtype, so instead of a packet change we pull every Mother record toward
    // the room centre: the arms are off-centre and move inward, the centred body barely
    // moves at all. 0 = untouched, 1 = collapsed onto the centre.
    static float MotherArmPull      = 0.900f;  // Tab->MotherArmPull (baked from VR)
    // Mother phase 1 (912.0.0-.3, `912.000_witness.anm2`) — head, back and both arms all
    // ride this one lift. They are four entities sharing one anm2 and the app never sees
    // the subtype, so one slider moves the whole boss.
    static float MotherP1Y          = 0.190f;  // Tab->MotherP1Y (baked from VR)
    // Mega Satan's head (274.0, `274.000_MegaSatanHead.anm2`) — a wall-mounted set piece
    // whose pivot leaves it sunk into the floor, same shape as Dogma's TV. The HANDS
    // (274.1/274.2) share a different anm2 and are left alone.
    static float MegaSatanY         = 0.090f;  // Tab->MegaSatanY (baked from VR)
    // Dogma's arena wants a heavier looming shadow than the rest of Home.
    static float DogmaOverlayMul    = 2.0f;    // Tab->DogmaOvMul
    // Dogma's sprites are greyscale, so a hit reads as a brightness punch, not a colour.
    const  float DogmaHitFlash      = 2.0f;
    static bool  _dogmaOnScreen     = false;   // set while drawing, read for OverlayBoost

    // ---- Water reflections ---------------------------------------------------
    // In Downpour/Dross the game mirrors standing sprites in the water: the reflection is
    // drawn BELOW the entity on screen, faded, and wobbled horizontally
    // (resources/shaders/water_overlay.fs is that wobble).
    //
    // A true planar mirror would put the reflection UNDER the floor, where the opaque floor
    // would hide it. But the 2D game's "below on screen" IS the diorama's +Z, so the
    // reflection is laid FLAT on the water, anchored at the entity's feet and running
    // toward the front of the board. That is both what the game shows and the only version
    // that stays visible from a VR viewing angle.
    static readonly System.Collections.Generic.HashSet<string> _creepLogged = new();
    static readonly System.Collections.Generic.HashSet<string> _creepDrawLogged = new();
    static int   _reflCount, _reflCostumes;   // per-frame, for the debug overlay
    static bool  _reflPlayer;
    static float ReflectAlpha   = 0.26f;   // Tab->ReflectA (baked from VR)
    static float ReflectStretch = -1.00f;  // Tab->ReflectStretch: NEGATIVE mirrors it.
                                           // Sign decides which way it lies; magnitude
                                           // foreshortens it. Tune in VR.
    static float ReflectWobble  = 0.002f;  // Tab->ReflectWob: horizontal sway, metres (baked)
    // Clearance above the WATER plane, and it has to be real clearance. At the original
    // 0.0022 the reflection sat 0.4 mm over the water; both are transparent with no depth
    // write, so StereoKit sorts them by distance — and the water quad's sort centre is the
    // ROOM CENTRE, which is exactly where Isaac usually stands. His reflection tied with the
    // water and lost, while entities further out sorted clear of it and drew fine. That is
    // why the PLAYER specifically had no reflection.
    static float ReflectYNudge  = 0.0060f; // Tab->ReflectY
    const float GridZNudgeM = 0.012f;    // push grid items toward the viewer (+ = nearer)
    const float FloorDropM  = 0.010f;    // lower the floor plane slightly (gibs fell through)
    static float DoorInsetM  = 0.012f;   // pull doors inward off the wall plane (calibrated)
    static float SecretPassageStretchY = 1.30f;  // secret-room hole-in-wall passage renders short: stretch it taller
    static float SecretPassageRaiseM   = 0.010f;  // and nudge it up a little
    static float CostumeFwdM = -0.010f;  // costume depth nudge; + = toward viewer (calibrated)
    public static float HeldItemY = 0.060f;  // held-item lift above the head, in m at normal size (scaled by entScale)
    // Lift for an actively-HELD ENTITY (Mom's Bracelet rock/TNT, throwable bomb). Separate from
    // HeldItemY because those are real world objects of their own size, not a pickup-pose icon.
    // Distinguished on the wire by IsHeld WITHOUT IsCostume (the held ITEM sends costume+held).
    public static float HeldEntityY = 0.030f;   // Tab->HeldEntityY (baked from VR)
    public static int CostumeBehindOrder = 12; // orderBase for body-only costumes so they sit BEHIND the head (head overlay is higher)
    static float TearYNudgeM = -0.010f;  // vertical nudge for tears/projectiles (calibrated)
    static float GridSizeScale = 1.030f; // grid sprite size fudge (seamless tiling) (calibrated)
    static float FloorRaiseM = 0.006f;   // raise the floor + walls together (entities stay put) (calibrated)
    const int   StaleFrames = 40;        // effect frozen this many packets -> stop drawing it

    // Flat game screen (menus / pause / cutscenes): a floating panel showing the
    // real Isaac window, captured via WGC. Shown only when the mod reports a
    // non-gameplay state. All runtime-tunable, like the rest.
    static Vec3  ScreenOrigin = new(0f, -0.05f, -0.56f); // in front of + above the board (calibrated)
    static float ScreenScale  = 0.62f;   // panel WIDTH in metres (height follows aspect)
    static bool  ScreenFlipV  = false;   // calibrated (WGC came in upright here)
    static bool  ScreenFlipH  = true;    // mirror horizontally too (viewer-facing quad)
    // Controller button that HOLDS the full map. Default L3 (left stick click, 0x0040):
    // reliably present via XInput and unused by Isaac/the app. Quest/VD usually has no
    // Back button, which is why the old 0x0020 default did nothing. To rebind, read your
    // button's bit from the debug overlay (pad=0x....) and set it here.
    static ushort MapPad = 0x0040;
    // Hold the flat game screen on SELECT/Back. Rebind to your controller's real bit if
    // Virtual Desktop gives you no Back button — read it off the debug overlay (pad=0x....),
    // same workflow as MapPad. F3 does the same on the desktop.
    static ushort ScreenPad = 0x0020;
    static bool   _screenLatch;          // toggled, not held: you need both hands for a menu
    static bool   _prevScreenBtn;
    static bool   _mirrorDim;            // MSG_STATE bit 3: Downpour/Dross mirror dimension

    // ---- In-headset tuning ---------------------------------------------------
    // Every calibration pass so far has cost a headset-off round trip, because the Tab
    // slider needs a focused window and there ISN'T one in VR. Tuning mode moves the whole
    // slider onto the pad. Entering MUTES the gamepad stream, which freezes Isaac (our
    // injection is its only input while unfocused) so the D-pad drives the slider instead
    // of the player. Chord = L3+R3 together, which is unambiguous: L3 alone is map-hold and
    // R3 alone toggles the overlay, and both of those are suppressed while the chord is down.
    const ushort PadL3 = 0x0040, PadR3b = 0x0080;
    const ushort PadDUp = 0x0001, PadDDown = 0x0002, PadDLeft = 0x0004, PadDRight = 0x0008;
    const ushort PadA = 0x1000;
    static bool  _tuneMode;
    static bool  _prevChord;
    static ushort _prevTunePad;
    static float _repeatAt;              // next auto-repeat time while a direction is held
    const float  RepeatDelay = 0.35f, RepeatRate = 0.11f;

    /// <summary>Drive the Tab slider from the pad. Returns true while tuning mode owns the pad.</summary>
    static bool UpdateTuneMode(GamepadInput gamepad)
    {
        ushort pb = gamepad.Buttons;
        bool chord = (pb & PadL3) != 0 && (pb & PadR3b) != 0;
        if (chord && !_prevChord)
        {
            _tuneMode = !_tuneMode;
            _repeatAt = 0f;
            if (_tuneMode) { _showDebug = true; PrintTune(); }
            Log.Info($"[Diorama] >TUNE MODE {(_tuneMode ? "ON (Isaac frozen)" : "off")}");
        }
        _prevChord = chord;
        gamepad.Mute = _tuneMode;
        if (!_tuneMode) { _prevTunePad = pb; return chord; }

        bool Pressed(ushort b) => (pb & b) != 0 && (_prevTunePad & b) == 0;
        if (Pressed(PadDUp))   CycleTune(-1);
        if (Pressed(PadDDown)) CycleTune( 1);
        if (Pressed(PadA))     PrintTune();

        // Left/right adjust, with auto-repeat so a long drag doesn't need 40 taps.
        int dir = (pb & PadDRight) != 0 ? 1 : (pb & PadDLeft) != 0 ? -1 : 0;
        if (dir == 0) _repeatAt = 0f;
        else
        {
            float now = Time.Totalf;
            bool first = (_prevTunePad & (PadDLeft | PadDRight)) == 0;
            if (first) { ApplyTune(dir); _repeatAt = now + RepeatDelay; }
            else if (now >= _repeatAt) { ApplyTune(dir); _repeatAt = now + RepeatRate; }
        }
        _prevTunePad = pb;
        return true;
    }

    // ---- Quit when Isaac closes ----------------------------------------------
    // Only ever arms AFTER the game has been seen at least once, so launching the app
    // first (or waiting on the Steam auto-launch) can never trip it. The grace period
    // covers a restart or a momentary process-list miss.
    static bool  AutoQuitOnIsaacExit = true;
    const float  QuitGraceSec = 4f;
    static bool  _isaacEverSeen;
    static float _isaacGoneSince = -1f;
    static float _nextProcCheck;

    static void CheckIsaacAlive()
    {
        if (!AutoQuitOnIsaacExit) return;
        float now = Time.Totalf;
        if (now < _nextProcCheck) return;          // Process.GetProcesses() is not cheap
        _nextProcCheck = now + 1f;

        bool alive = false;
        try { alive = WindowFinder.IsaacProcessRunning(); } catch { return; }
        if (alive) { _isaacEverSeen = true; _isaacGoneSince = -1f; return; }
        if (!_isaacEverSeen) return;               // never started: nothing to mourn
        if (_isaacGoneSince < 0f) { _isaacGoneSince = now; return; }
        if (now - _isaacGoneSince >= QuitGraceSec)
        {
            Log.Info("[Diorama] Isaac closed — quitting.");
            SK.Quit();
        }
    }
    static Material? _screenMat;
    static Material ScreenMat()
    {
        if (_screenMat != null) return _screenMat;
        var m = Default.MaterialUnlit.Copy();
        m.FaceCull = Cull.None;            // double-sided so it's visible either way
        m.Transparency = Transparency.None;
        _screenMat = m; return m;
    }

    // Soft round floor shadow. Isaac has no shadow sprite file — the engine draws a
    // shadow layer — so we reproduce its look with a black radial texture whose alpha
    // fades to the edge, laid flat on the floor and sized by the entity's real
    // shadowSize (see LayerEntity.ShadowSize).
    static Material? _shadowMat;
    static Material ShadowMat()
    {
        if (_shadowMat != null) return _shadowMat;
        const int N = 64;
        var px = new Color32[N * N];
        float c = (N - 1) / 2f;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float ddx = (x - c) / c, ddy = (y - c) / c;
            float d = (float)Math.Sqrt(ddx * ddx + ddy * ddy);   // 0 centre .. 1 edge
            float a = Math.Clamp(1f - d, 0f, 1f);
            a *= a;                                               // soft falloff
            px[y * N + x] = new Color32(0, 0, 0, (byte)(a * ShadowMaxAlpha * 255f));
        }
        var tex = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
        tex.SetColors(N, N, px);
        var m = Default.MaterialUnlit.Copy();
        m[MatParamName.DiffuseTex] = tex;
        m.Transparency = Transparency.Blend;
        m.FaceCull     = Cull.None;
        m.DepthWrite   = false;           // sit on the floor without z-fighting the billboards
        _shadowMat = m; return m;
    }

    // ---- Curse of Darkness (LevelCurse bit 1) ----
    // The room goes dark and Isaac carries a pool of light. The floor + walls are dimmed
    // globally via BackdropRenderer.Dim and a bright additive pool is painted on the floor
    // around Isaac; entities are dimmed per-entity by their DISTANCE to him, so the ones
    // inside the light stay readable and distant ones fall away. All three are Tab-tunable.
    public static float CurseDark   = 0.97f;   // 0 = no darkening, 1 = pitch black
    public static float CurseLightR = 0.360f;  // light pool radius, metres
    public static float CurseLightB = 1.00f;   // how strongly the light lifts the dark (1 = full restore)
    // Veil footprint. The backdrop draws its floor at (roomW + 2*WallOutsetM), so the veil matches
    // that by default — sized to the bare room footprint it fell ~0.036 m short of the floor edge.
    // VeilScale multiplies on top of that; VeilY raises/lowers it off the floor plane.
    public static float VeilScale   = 1.00f;   // Tab->VeilScale
    public static float VeilY       = 0f;      // Tab->VeilY (metres, + = up)

    static bool  _darkOn;                       // curse active this frame
    static Vec3  _darkCenter;                   // Isaac's world position (light centre)

    // Brightness multiplier at a world position: full inside the pool, CurseDark-dimmed
    // far away, smoothly blended between.
    static float DarkLevelAt(Vec3 p)
    {
        if (!_darkOn) return 1f;
        float floorLo = Math.Clamp(1f - CurseDark, 0f, 1f);
        if (CurseLightR <= 0.0001f) return floorLo;
        float dx = p.x - _darkCenter.x, dz = p.z - _darkCenter.z;
        float d  = MathF.Sqrt(dx*dx + dz*dz) / CurseLightR;      // 0 at Isaac .. 1 at the edge
        float t  = Math.Clamp(1f - d, 0f, 1f);
        t = t * t * (3f - 2f * t);                                // smoothstep falloff
        return Math.Clamp(floorLo + (1f - floorLo) * t * CurseLightB, 0f, 1f);
    }

    // The darkness itself, as a per-vertex BLACK VEIL over the floor.
    //
    // Why not an additive white "lamp": adding white light washes the floor out to grey/white
    // and destroys its colour — the room stops reading as a dark Basement and just looks foggy.
    // Curse of Darkness is a MULTIPLY: the floor keeps its own colour, scaled toward black.
    // So the floor is drawn at FULL brightness and this veil multiplies it: a grid mesh lying on
    // the floor, black, with per-vertex ALPHA = how dark that spot should be (0 under Isaac's
    // light, up to CurseDark far away). The GPU interpolates the alpha across the triangles, so
    // the falloff is smooth with no texture and no custom shader, and it follows Isaac exactly.
    // Like the floor shadows it doesn't write depth, so sprites standing on the floor are nearer
    // and simply depth-test over it.
    const int DarkGridN = 24;                  // grid resolution (24x24 verts = ~1k tris, trivial)
    static Mesh?     _darkMesh;
    static Vertex[]? _darkVerts;
    static uint[]?   _darkInds;
    static Material? _darkMat;

    static Material DarkMat()
    {
        if (_darkMat != null) return _darkMat;
        var m = Default.MaterialUnlit.Copy();
        m.Transparency = Transparency.Blend;
        m.FaceCull     = Cull.None;
        m.DepthWrite   = false;                // sit on the floor like the shadows do
        _darkMat = m; return m;
    }

    // Veil over the floor rect. Built in LOCAL space (x,z in -0.5..0.5) and drawn with a TS
    // matrix — a hand-built mesh drawn at Matrix.Identity renders NOTHING here (bounds/culling),
    // a gotcha already paid for once with the laser ribbons.
    static void DrawDarknessVeil(Vec3 center, float w, float d, float topY, int shape)
    {
        // L-rooms have a cut-out corner with no floor under it — leave that fully transparent
        // or the veil renders as a black slab hanging in empty space.
        bool isL = shape >= 9 && shape <= 12;
        const int N = DarkGridN;
        _darkMesh  ??= new Mesh();
        _darkVerts ??= new Vertex[N * N];
        if (_darkInds == null)
        {
            var idx = new uint[(N - 1) * (N - 1) * 6];
            int k = 0;
            for (int y = 0; y < N - 1; y++)
            for (int x = 0; x < N - 1; x++)
            {
                uint a = (uint)(y * N + x), b = a + 1, c = (uint)((y + 1) * N + x), e = c + 1;
                idx[k++] = a; idx[k++] = c; idx[k++] = b;
                idx[k++] = b; idx[k++] = c; idx[k++] = e;
            }
            _darkInds = idx;
        }
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float fx = x / (float)(N - 1) - 0.5f;      // local -0.5 .. 0.5
            float fz = y / (float)(N - 1) - 0.5f;
            var world = new Vec3(center.x + fx * w, topY, center.z + fz * d);
            float a = Math.Clamp(1f - DarkLevelAt(world), 0f, 1f);   // 0 = lit, 1 = pitch black
            if (isL && BackdropRenderer.InMissingQuadrant(shape, fx, fz)) a = 0f;
            _darkVerts[y * N + x] = new Vertex(new Vec3(fx, 0f, fz), new Vec3(0, 1, 0),
                                               new Vec2(0, 0), new Color32(0, 0, 0, (byte)(a * 255f)));
        }
        _darkMesh.SetVerts(_darkVerts);
        _darkMesh.SetInds(_darkInds);
        _darkMesh.Draw(DarkMat(), Matrix.TS(new Vec3(center.x, topY, center.z), new Vec3(w, 1f, d)));
    }

    // Effects that belong FLAT on the floor (decals) rather than upright.
    static readonly string[] FloorDecalHints =
        { "creep", "charred", "ash", "scorch", "puddle", "splat", "blood pool", "gib",
          "spiderweb", "cobweb", "grid_pit", "trapdoor", "pressure", "grid_stairs" };

    // Sprites with genuinely soft (partial-alpha) pixels. Alpha-clip would make
    // these fully opaque, so they get real blending instead. Chosen by NAME
    // because category is unreliable here (a fireplace is an 'Enemy').
    // Only these are dropped when their animation freezes. Gibs/creep settle and
    // legitimately stop animating, so they must NOT be in this list.
    static readonly string[] FreezeDropHints = { "smoke", "poof", "puff" };
    static bool CanFreezeDrop(string anm2Lower)
    {
        foreach (var h in FreezeDropHints) if (anm2Lower.Contains(h)) return true;
        return false;
    }

    static readonly string[] SoftAlphaHints =
        { "fire", "flame", "smoke", "poof", "ember", "dust", "shockwave",
          "explosion", "blood", "glow", "light", "wisp", "steam", "mist" };

    static bool IsSoftAlpha(string anm2Lower)
    {
        foreach (var h in SoftAlphaHints) if (anm2Lower.Contains(h)) return true;
        return false;
    }
    static readonly Quat DecalFacing = Quat.FromAngles(-90f, 0f, 0f);   // lay flat on the floor

    // Entities the game draws ONLY in the water's reflection — invisible in the room
    // itself, and the reflection is how you track them. Our reflection pass makes this
    // reproducible: draw the mirrored copy, skip the upright sprite.
    //   807.0 Wraith — Downpour/Dross. xml shadowSize=14, so the engine still gives it a
    //   floor shadow; we keep ours for the same reason, and it keeps the fight fair.
    static readonly string[] ReflectionOnlyHints = { "wraith" };

    static bool IsReflectionOnly(string anm2Lower)
    {
        foreach (var h in ReflectionOnlyHints) if (anm2Lower.Contains(h)) return true;
        return false;
    }

    static bool IsFloorDecal(string anm2Lower)
    {
        foreach (var h in FloorDecalHints) if (anm2Lower.Contains(h)) return true;
        return false;
    }

    // Freeze detection: a finished effect the game has stopped drawing keeps
    // reporting the same frame at the same spot. Drop it once it stops advancing.
    readonly struct FreezeKey : IEquatable<FreezeKey>
    {
        public readonly int A, X, Y;
        public FreezeKey(int a, float x, float y) { A=a; X=(int)MathF.Round(x); Y=(int)MathF.Round(y); }
        public bool Equals(FreezeKey o) => A==o.A && X==o.X && Y==o.Y;
        public override int GetHashCode() => (A*397 ^ X)*397 ^ Y;
    }
    static readonly System.Collections.Generic.Dictionary<FreezeKey,(int frame,int stale,int seen)> _freeze = new();
    static int _seenTick = 0;
    const float HeightGain  = 1.0f;

    const float SpriteYawDeg = 0f, SpriteTiltDeg = 0f;
    // Identity-ish facing (no 180 yaw): sprite quads are double-sided, so they
    // face the camera without the horizontal texture mirror a 180 yaw causes.
    static readonly Quat SpriteFacing = Quat.FromAngles(SpriteTiltDeg, SpriteYawDeg, 0f);

    // Off by default now: with the headset on this overlay sits over the play area, and the
    // Debug menu page turns it on by itself. F1/R3 still force it for desktop work.
    static bool _showDebug = false;  // toggled by R3 / F1
    // ---- VR frame capture ----
    // `Renderer.Screenshot` can render from an ARBITRARY Pose, so passing `Input.Head` captures
    // exactly what the headset is seeing (mono) rather than the desktop window's own viewpoint.
    // StereoKit has no built-in "mirror the headset to the desktop" toggle, and RenderTo would
    // still need somewhere to blit it — a still frame is what's actually useful for debugging,
    // and it can be pulled off the machine because the exe lives inside the connected app folder.
    // Trigger: F2 on the keyboard ONLY. The right trigger used to fire this too, back when
    // nothing else wanted it — the options menu now clicks with that trigger, and a button
    // press that also saves a 1920x1080 PNG is not a button press anyone wants.
    static int  _shotN;
    static void CheckVrScreenshot()
    {
        if (!Input.Key(Key.F2).IsJustActive()) return;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, $"vrshot_{++_shotN:00}.png");
            Renderer.Screenshot(path, Input.Head, 1920, 1080, 90f);
            Log.Info($"[Diorama] VR SCREENSHOT -> {path}");
        }
        catch (Exception ex) { Log.Info("[Diorama] VR SCREENSHOT failed: " + ex.Message); }
    }

    static ushort _prevPad = 0;
    static int _dumpCount = 0;
    static int _frameTick  = 0;
    static readonly System.Collections.Generic.HashSet<string> _loggedFb = new();

    // ----- Grabbable rig -------------------------------------------------------
    // A single transform the whole scenario (diorama + HUD + game screen) is drawn
    // inside. Squeeze one controller's grip (the side button) to grab it, and it
    // rigidly follows that hand — position AND rotation — until you let go. Squeeze
    // BOTH grips at once and move your hands apart to grow the diorama / together to
    // shrink it (it scales in place around its centre). Press Y on the keyboard to
    // snap it back to the default pose and scale.
    static Pose  _rig       = new(Vec3.Zero, Quat.Identity);
    // Home scale of the whole scenario. The grab gesture moves _rigScale away from this;
    // the debug overlay prints the live value (DIORAMA=) so a size dialled in VR can be read
    // off and baked here. Both the startup value and the Y reset come from this one constant
    // so they cannot drift apart.
    public const  float RigScaleHome = 0.78f;   // baked from VR
    static float _rigScale  = RigScaleHome;    // uniform scale of the whole scenario
    static bool  _grabbing;          // one-hand grab (move + rotate)
    static bool  _scaling;           // two-hand grab (pinch/spread to scale)
    static Handed _grabHand;
    static Vec3  _cPos0, _rPos0;     // controller + rig positions at grab start
    static Quat  _cRot0, _rRot0;     // controller + rig orientations at grab start
    static float _handDist0, _scale0;// hand separation + scale at two-hand grab start
    const float  GripOn  = 0.6f;     // squeeze past this to start a grab
    const float  GripOff = 0.4f;     // release below this to let go
    const float  ScaleMin = 0.20f;   // clamp so the diorama can't vanish or swallow you
    const float  ScaleMax = 6.0f;

    static void UpdateRig()
    {
        var r = Input.Controller(Handed.Right);
        var l = Input.Controller(Handed.Left);
        bool rOn = r.IsTracked && r.grip > GripOn;
        bool lOn = l.IsTracked && l.grip > GripOn;

        // The Y key snaps the scenario back to its home pose and scale.
        if (Input.Key(Key.Y).IsJustActive())
        {
            _rig = new Pose(Vec3.Zero, Quat.Identity);
            _rigScale = RigScaleHome;
            _grabbing = false; _scaling = false;
            return;
        }

        // --- Two-hand grab: spread hands apart to grow, bring together to shrink. ---
        // While both grips are held, this takes priority over one-hand move.
        if (rOn && lOn)
        {
            _grabbing = false;   // two-hand scaling supersedes any one-hand grab
            float dist = Vec3.Distance(r.pose.position, l.pose.position);
            if (!_scaling)
            {
                _scaling   = true;
                _handDist0 = Math.Max(dist, 0.01f);
                _scale0    = _rigScale;
            }
            // Keep the diorama centre pinned in world space while it scales, so it
            // grows/shrinks in place instead of drifting.
            Vec3 anchorWorld = _rig.orientation * (_rigScale * FloorCenter) + _rig.position;
            float s = _scale0 * (dist / _handDist0);
            _rigScale = Math.Clamp(s, ScaleMin, ScaleMax);
            _rig.position = anchorWorld - _rig.orientation * (_rigScale * FloorCenter);
            return;
        }
        _scaling = false;

        if (!_grabbing)
        {
            // Grab with whichever hand squeezed (right wins ties).
            if (rOn)      { _grabbing = true; _grabHand = Handed.Right; }
            else if (lOn) { _grabbing = true; _grabHand = Handed.Left;  }
            if (_grabbing)
            {
                var c = Input.Controller(_grabHand);
                _cPos0 = c.pose.position; _cRot0 = c.pose.orientation;
                _rPos0 = _rig.position;   _rRot0 = _rig.orientation;
            }
            return;
        }

        // Currently grabbing: release when that hand's grip relaxes or drops track.
        var g = Input.Controller(_grabHand);
        if (!g.IsTracked || g.grip < GripOff) { _grabbing = false; return; }

        // Rigid follow: apply the hand's delta rotation to the rig, and move the rig
        // so the point it was grabbed at stays under the hand.
        Quat dRot = g.pose.orientation * _cRot0.Inverse;
        _rig.orientation = dRot * _rRot0;
        _rig.position    = g.pose.position + dRot * (_rPos0 - _cPos0);
    }

    static (float w, float h, Color col) Style(EntityCategory c) => c switch
    {
        EntityCategory.Player     => (0.055f, 0.085f, new Color(0.95f,0.25f,0.25f)),
        EntityCategory.Enemy      => (0.050f, 0.075f, new Color(0.95f,0.55f,0.15f)),
        EntityCategory.Pickup     => (0.030f, 0.035f, new Color(0.95f,0.85f,0.20f)),
        EntityCategory.Projectile => (0.018f, 0.018f, new Color(0.30f,0.85f,0.95f)),
        EntityCategory.Effect     => (0.035f, 0.035f, new Color(0.90f,0.90f,0.95f)),
        EntityCategory.Grid       => (0.045f, 0.040f, new Color(0.55f,0.45f,0.35f)),
        _                         => (0.035f, 0.045f, new Color(0.60f,0.60f,0.65f)),
    };

    static void Main(string[] args)
    {
        // Bring Isaac up via Steam alongside the app (no-op if already running).
        GameLauncher.EnsureRunning();

        var settings = new SKSettings { appName="IsaacDiorama-Phase2b", blendPreference=DisplayBlend.Opaque };
        if (!SK.Initialize(settings)) return;

        Renderer.ClearColor = _passthrough ? PassthroughKey : new Color(0f, 0f, 0f);
        Renderer.EnableSky  = false;
        StartFileLog();

        // Resolve resources: CLI arg, else resources_root.txt, else AUTO-DETECT the Steam
        // install. An explicit path always wins, so detection can never override a choice
        // someone made on purpose.
        string? resDir = args.Length>0 ? args[0] : null;
        string resHow  = resDir != null ? "command line" : "";
        if (resDir==null && File.Exists("resources_root.txt"))
        {
            var line = File.ReadLines("resources_root.txt").FirstOrDefaultSafe();
            if (!string.IsNullOrWhiteSpace(line)) { resDir = line!.Trim(); resHow = "resources_root.txt"; }
        }
        if (resDir==null) resDir = ResourceLocator.FindGameDir(out resHow);
        _resourceHow = resHow;
        Log.Info($"[Diorama] resources: {resHow}");
        var sprites = new SpriteRenderer(resDir);
        Log.Info($"[Diorama] BUILD 2026-09-27a v{Version} - WinExe (no console) + process-based window match");   // confirms the new binary is running
        PrintTune();   // populate _tuneLine so the always-on overlay row isn't blank at boot
        RegisterSettings();   // snapshots the compiled defaults, so "Defaults" can restore them
        LoadSettings();
        Log.Info($"[Diorama] sprite resources: {sprites.Status}");
        _resourcesOk = sprites.Configured && sprites.Status.StartsWith("gfx OK");

        using var rx = new UdpReceiver(47800);
        var scene = new ScenePacket();
        var lscene = new LayerScenePacket();
        var backdrop = new BackdropRenderer(sprites);
        // The life-size room draws through its OWN BackdropRenderer. BackdropRenderer keeps
        // SHARED MUTABLE MESHES that are rebuilt per draw from the geometry it is handed —
        // `_overlayQuad` (whose per-vertex alpha carries Isaac's light hole) and the water
        // grid slots. StereoKit's Mesh.Draw queues a REFERENCE and rasterises at frame end,
        // so a second Draw on the same instance silently rewrote the vertices the DIORAMA's
        // overlay was about to render with the big room's — which is exactly how the curse
        // light stopped opening a hole around Isaac. A separate instance isolates every one
        // of those caches by construction instead of needing each one found and fixed.
        var bigBackdrop = new BackdropRenderer(sprites);
        var hud = new HudRenderer(sprites);
        using var gamepad = new GamepadInput();
        using var capture = new WindowCapture();

        Material floorMat = Default.Material.Copy();
        floorMat[MatParamName.ColorTint] = new Color(0.10f,0.10f,0.14f);
        // A mirror flips triangle winding, so anything inside it must not cull back faces.
        // Every other material in the diorama is already Cull.None; this one wasn't.
        floorMat.FaceCull = Cull.None;
        Material Mat(Color c){ var m=Default.Material.Copy(); m[MatParamName.ColorTint]=c; m.FaceCull=Cull.None; return m; }
        var catMats = new System.Collections.Generic.Dictionary<EntityCategory,Material>();
        foreach (EntityCategory c in Enum.GetValues(typeof(EntityCategory))) catMats[c]=Mat(Style(c).col);

        Mesh quad = Mesh.Quad, cube = Mesh.Cube;

        Log.Info("[Diorama] TUNE: Tab cycles the slider (32 targets), Z/X adjust, Enter prints. IN VR: press L3+R3 together to enter TUNE MODE — that freezes Isaac and puts the whole slider on the pad (D-pad Up/Down = target, Left/Right = adjust with auto-repeat, A = print, L3+R3 again to exit); the current target and value are drawn on the debug overlay. SELECT/Back (or F3) toggles the flat game screen on and off. B=swap floor red/blue. V=live/tiled floor. C=live/tiled walls. G/H=flip game screen V/H. F1 or R3=toggle the debug overlay. F2=save a PNG of the headset view (the right TRIGGER now clicks the options menu). GRAB: squeeze a controller grip to move the scenario; both grips or Y resets it. The app quits itself a few seconds after Isaac closes.");
        SK.Run(() =>
        {
          // One bad packet or an unexpected null used to take the whole session down
          // mid-run. Log once, unwind the transform stack, and keep rendering.
          try
          {
            if (!_resourcesOk) { DrawSetupHelp(sprites); }
            gamepad.Poll();
            CheckIsaacAlive();
            // Tuning mode owns the pad while it's on (and while the L3+R3 chord is held),
            // so the map-hold and overlay toggle below must not also fire.
            bool tunePad = UpdateTuneMode(gamepad);
            // Toggle the debug overlay with R3 (right stick click) — Isaac ignores it,
            // so it won't leak into gameplay — or F1 on the keyboard.
            const ushort PadR3 = 0x0080;
            ushort pb = gamepad.Buttons;
            if ((!tunePad && (pb & PadR3) != 0 && (_prevPad & PadR3) == 0) || Input.Key(Key.F1).IsJustActive())
                _showDebug = !_showDebug;
            // Flat game screen on Select/Back (or F3): a TOGGLE, because reading a menu
            // needs both sticks free.
            bool screenBtn = !tunePad && (pb & ScreenPad) != 0;
            if ((screenBtn && !_prevScreenBtn) || Input.Key(Key.F3).IsJustActive())
            {
                _screenLatch = !_screenLatch;
                Log.Info($"[Diorama] >game screen latch = {(_screenLatch ? "ON" : "off")}");
            }
            _prevScreenBtn = screenBtn;
            _prevPad = pb;
            CheckVrScreenshot();
            HandleCropKeys();
            UpdateRig();
            byte[]? data = rx.TryGetLatestScene(out long count, out double ageMs, out bool layered);
            int drawn=0, spr=0, fb=0;
            float floorW=MaxFootprint, floorD=MaxFootprint*0.8f;

            // Everything below is drawn inside the grabbable rig so a controller grip
            // moves the whole scenario (diorama + HUD + screen) as one.
            Hierarchy.Push(Matrix.TRS(_rig.position, _rig.orientation, _rigScale)); _hierDepth++;

            // Downpour/Dross MIRROR DIMENSION: the game flips that stage horizontally at
            // RENDER time only, so every coordinate it reports — entities, grid, lasers,
            // and the streamed floor/wall images — is unflipped and must be mirrored here.
            // One reflection about the room centre does all of it at once, which is far
            // less code (and far less to get out of sync) than mirroring each data path.
            // It wraps the DIORAMA ONLY: the HUD, the boss bar and the flat game screen
            // are drawn after the Pop, because mirrored text is not a feature.
            // Reflect about a pivot = Scale(-1,1,1) then Translate(2*pivot.x) — StereoKit's
            // TS applies scale first, so this is exactly Matrix.TS(2*pivot.x, (-1,1,1)).
            bool mirror = _mirrorDim;
            if (mirror)
                { Hierarchy.Push(Matrix.TS(new Vec3(FloorCenter.x * 2f, 0f, 0f),
                                          new Vec3(-1f, 1f, 1f))); _hierDepth++; }

            // REPENTOGON layer-state scene takes priority; otherwise the anm2 path.
            bool haveLayered = layered && data != null && LayerScenePacket.TryParse(data, lscene);
            // Crawlspace / item-dungeon rooms (ROOM_DUNGEON = 16, debug-goto "ItemDungeon")
            // play like a 2D platformer (ladders, gravity) where a flat diorama adds nothing.
            // Skip the 3D scene entirely and show the captured game screen instead (forced on
            // below). MVP: the flat 2D view is simply the better representation here.
            bool isCrawlspace = rx.GetBackdrop().RoomType == 16;
            if (isCrawlspace) { /* diorama off; flat game screen shown below */ }
            else if (haveLayered)
            {
                drawn = DrawLayerScene(lscene, sprites, backdrop, rx, cube, floorMat,
                                       ref floorW, ref floorD, out spr);
            }
            else if (data!=null && !layered && ScenePacket.TryParse(data, scene))
            {
                float worldW=MathF.Max(1f,scene.RoomMaxX-scene.RoomMinX);
                float worldH=MathF.Max(1f,scene.RoomMaxY-scene.RoomMinY);
                float rscale=RoomPxToM;
                float longest=MathF.Max(worldW,worldH)*rscale;
                if (longest > MaxFootprintM) rscale *= MaxFootprintM/longest;   // clamp huge rooms
                float spritePxToM = rscale * SpriteScaleFactor;
                floorW=worldW*rscale; floorD=worldH*rscale;
                float cx=(scene.RoomMinX+scene.RoomMaxX)*0.5f, cy=(scene.RoomMinY+scene.RoomMaxY)*0.5f;

                var bd = rx.GetBackdrop();
                string rk0 = $"{bd.Type}_{bd.Shape}_{bd.MinX:0}_{bd.MinY:0}_{bd.MaxX:0}_{bd.MaxY:0}";
                var fimg0 = rx.GetFloorImage();
                if (fimg0.Valid) backdrop.SetLiveFloor(fimg0.Version, fimg0.W, fimg0.H, fimg0.Rgba, rk0);
                var wimg0 = rx.GetWallImage();
                if (wimg0.Valid) backdrop.SetLiveWall(wimg0.Version, wimg0.W, wimg0.H, wimg0.Rgba, rk0);
                string? bWall = bd.Wall, bFloor = bd.Floor, bName = bd.Name;
                backdrop._lastXmlCount = bd.XmlCount;
                backdrop.Water = rx.GetWater();
                bool drewBackdrop = bd.Valid &&
                    backdrop.Draw(bd.Type, bd.Shape, bd.RoomType, bWall, bFloor, bName, FloorCenter,
                                  floorW, floorD, FloorCenter.y + 0.003f - FloorDropM + FloorRaiseM);
                if (!drewBackdrop)
                {
                    cube.Draw(floorMat, Matrix.TS(FloorCenter, new Vec3(floorW,0.004f,floorD)));
                    DrawGrid(floorW,floorD);
                }

                _frameTick++;
                _seenTick++;
                if (_freeze.Count > 512)   // prune entries we haven't seen recently
                {
                    var dead = new System.Collections.Generic.List<FreezeKey>();
                    foreach (var kv in _freeze) if (_seenTick - kv.Value.seen > 120) dead.Add(kv.Key);
                    foreach (var k in dead) _freeze.Remove(k);
                }
                if (_dumpCount<6 && _frameTick%120==0 && rx.Strings.Count>0 && scene.Entities.Count>0)
                {
                    _dumpCount++;
                    Log.Info($"[Diorama] ===== dump {_dumpCount} (strings={rx.Strings.Count}) =====");
                    int lim = Math.Min(8, scene.Entities.Count);
                    for (int i=0;i<lim;i++)
                    {
                        var de = scene.Entities[i];
                        string? ap = rx.Strings.Get(de.Anm2Id);
                        string  an = rx.Strings.Get(de.AnimId) ?? "";
                        if (ap == null)      Log.Info($"[Diorama] ent{i} cat={de.Cat} anm2Id {de.Anm2Id} NOT REGISTERED");
                        else if (ap == "")   Log.Info($"[Diorama] ent{i} cat={de.Cat} EMPTY anm2 filename");
                        else                 Log.Info($"[Diorama] ent{i} cat={de.Cat} " + sprites.Diagnose(ap, an, de.AnimFrame));
                    }
                }

                int entIdx = 0;
                foreach (var e in scene.Entities)
                {
                    // sub-millimetre unique depth bias: kills z-fighting flicker
                    float zBias = (entIdx++) * 0.00002f;
                    float dx=(e.X-cx)*rscale, dz=(e.Y-cy)*rscale + zBias;
                    if (e.Cat==EntityCategory.Grid) dz += GridZNudgeM;
                    // Which wall a door sits on: 0 = far/near, +1 = east, -1 = west.
                    int doorSide = 0;
                    if (e.Cat == EntityCategory.Door)
                        doorSide = PlaceDoor(ref dx, ref dz, floorW, floorD);
                    float lift=e.HeightPx*spritePxToM*HeightGain;
                    float catLift = e.Cat==EntityCategory.Grid ? GridLiftM : 0f;
                    Vec3 pivotPos = new(FloorCenter.x+dx, FloorCenter.y + 0.004f + lift + catLift, FloorCenter.z+dz);

                    string? anm2 = rx.Strings.Get(e.Anm2Id);
                    string  anim = rx.Strings.Get(e.AnimId) ?? "";
                    var tint = new Color(e.R,e.G,e.B,e.A);
                    string anm2L = anm2?.ToLowerInvariant() ?? "";

                    // Frozen one-shot effects (smoke/poof the game already stopped
                    // drawing): same frame, same spot, packet after packet -> drop.
                    if (e.Cat == EntityCategory.Effect && CanFreezeDrop(anm2L))
                    {
                        var fk = new FreezeKey(e.Anm2Id, e.X, e.Y);
                        if (_freeze.TryGetValue(fk, out var st))
                        {
                            int stale = (st.frame == e.AnimFrame) ? st.stale + 1 : 0;
                            _freeze[fk] = (e.AnimFrame, stale, _seenTick);
                            if (stale > StaleFrames) continue;
                        }
                        else _freeze[fk] = (e.AnimFrame, 0, _seenTick);
                    }

                    // Flatness is decided per grid TYPE by the mod (bit1); effects
                    // fall back to name hints. No blanket grid override.
                    bool decal = e.IsFlat
                                 || (e.Cat == EntityCategory.Effect && IsFloorDecal(anm2L));

                    // Destroyed rocks linger in the grid until the room reloads.
                    if (e.IsDestroyed) continue;

                    // Skip near-invisible entities (faded-out effects).
                    if (e.A < 0.02f) continue;

                    // Skip full-room ambient light overlays — they translate to
                    // giant blobs as billboards. (Easy to extend this list.)
                    if (anm2 != null)
                    {
                        string ln = anm2.ToLowerInvariant();
                        if (ln.Contains("lightgradient") || ln.Contains("light_gradient")) continue;
                    }

                    // Side-wall doors rotate into their wall's plane; the yaw matches
                    // the wall quads (east faces -x, west faces +x).
                    Quat facing = decal ? DecalFacing
                                : doorSide ==  1 ? Quat.FromAngles(SpriteTiltDeg, -90f, 0f)
                                : doorSide == -1 ? Quat.FromAngles(SpriteTiltDeg,  90f, 0f)
                                : SpriteFacing;

                    // Decals lie flat on the floor; everything else stands upright.
                    // Mist gets the same 1mm lift as the layered path (see there) so the two
                    // code paths do not disagree about where a decal sits.
                    Vec3 drawPos = decal
                        ? new Vec3(pivotPos.x,
                                   FloorCenter.y + 0.005f
                                     + (anm2L.Contains("1000.138_mist") ? 0.0010f : 0f),
                                   pivotPos.z)
                        : pivotPos;

                    bool ok = false;
                    if (!string.IsNullOrEmpty(anm2))
                    {
                        ok = sprites.DrawEntity(anm2!, anim, e.AnimFrame, drawPos,
                                                facing,
                                                e.RotationDeg, spritePxToM, e.Scale, e.FlipX, tint,
                                                dropWhenFinished: e.Cat == EntityCategory.Effect && CanFreezeDrop(anm2L),
                                                softAlpha: IsSoftAlpha(anm2L) ||
                                                           e.Cat == EntityCategory.Effect ||
                                                           e.Cat == EntityCategory.Projectile);
                    }

                    if (ok) spr++;
                    else
                    {
                        if (!string.IsNullOrEmpty(anm2) && _loggedFb.Count<24 && _loggedFb.Add(anm2!))
                            Log.Info("[Diorama] FALLBACK " + sprites.Diagnose(anm2!, anim, e.AnimFrame));
                        var (w,h,_)=Style(e.Cat); w*=e.Scale; h*=e.Scale;
                        Vec3 c = new(pivotPos.x, FloorCenter.y + h*0.5f + lift + catLift, pivotPos.z);
                        quad.Draw(catMats[e.Cat], Matrix.TRS(c, facing, new Vec3(w,h,1f)));
                        fb++;
                    }
                    drawn++;
                }
            }
            else cube.Draw(floorMat, Matrix.TS(FloorCenter, new Vec3(floorW,0.004f,floorD)));

            if (mirror) { Hierarchy.Pop(); _hierDepth--; }   // end of the mirrored diorama

            if (!isCrawlspace)
            {
                hud.Draw(rx.GetHud());
                hud.DrawWorld(rx.GetHud(), FloorCenter, floorW, floorD);   // boss bar + pill popup, in the diorama
            }

            // Flat game screen for menus / pause / cutscenes / the main menu while a
            // run hasn't started yet. Show it whenever the mod reports a non-gameplay
            // state OR we aren't receiving live gameplay scene data (so you can see
            // and navigate the menus in VR to start a run). Hidden during gameplay.
            int gstate = rx.GetState();
            // Bit 3 (0x08) is the mirror dimension, NOT a "show the flat screen" state, so
            // the screen test below masks it out. Latched for the next frame because the
            // diorama is drawn above, before this read.
            _mirrorDim = (gstate & 0x08) != 0;
            int screenState = gstate & 0x07;
            // Bit 16: the game itself reports the MAP/SELECT button held. The app's own
            // XInput read is starved in VR — the Quest controller is bound straight to Isaac
            // — so this is the only source that works in the headset. (screenState still
            // masks 0x07, so neither this bit nor the mirror bit leaks into it.)
            bool mapHeldGame = (gstate & 0x10) != 0;
            // Grace window is generous so brief scene-packet gaps during room
            // transitions don't flash the game screen. A real menu/pause still shows
            // instantly because the mod reports it via gstate (!= 0).
            bool freshScene = data != null && ageMs < 1500;
            // Hold the map button (Back / Select, XInput 0x0020) to see the game's OWN
            // full map: we inject the map action to the mod, the game renders its map
            // overlay (with level name, run timer and score), and WGC captures it onto
            // the flat panel — a 1:1 copy, no rebuilt-minimap zoom needed.
            const ushort PadBack = 0x0020;
            // Map-hold trigger: keyboard Backspace (desktop) OR the Back pad bit. When
            // held we (a) inject BACK into the packet the mod sends Isaac, so the game
            // renders its map even if the controller has no physical Back button, and
            // (b) show the WGC capture of that map. Change MapPad to your controller's
            // real button once you've read its bit off the debug overlay (pad=0x....).
            bool mapHeld = !tunePad && (Input.Key(Key.Backspace).IsActive()
                                        || (gamepad.Buttons & MapPad) != 0
                                        || mapHeldGame);
            // Only INJECT Back when the hold came from us. If the game already reports the
            // button held it is drawing its map anyway, and feeding the action back in would
            // be us telling Isaac something it just told us.
            gamepad.ForceButtons = (mapHeld && !mapHeldGame) ? PadBack : (ushort)0;
            bool showScreen = screenState != 0 || !freshScene || mapHeld || isCrawlspace || _screenLatch;
            // Always try to latch the Isaac window (cheap once running) so the panel
            // is ready the instant it's needed, independent of state timing. Frames
            // are only copied to CPU when we actually want to show them.
            capture.EnsureStarted();
            capture.WantFrames = showScreen;
            if (showScreen)
            {
                capture.Upload();
                var stex = capture.Texture;
                if (stex != null)
                {
                    var m = ScreenMat();
                    m[MatParamName.DiffuseTex] = stex;
                    float aw = MathF.Max(1, stex.Width), ah = MathF.Max(1, stex.Height);
                    float w = ScreenScale, h = ScreenScale * (ah / aw);
                    quad.Draw(m, Matrix.TS(ScreenOrigin,
                        new Vec3(ScreenFlipH ? -w : w, ScreenFlipV ? -h : h, 1f)));
                }
            }

            // Close the grabbable rig. The debug overlay below stays OUTSIDE it so
            // it remains anchored in front of you even while you're moving the board.
            Hierarchy.Pop(); _hierDepth--;

            // Both of these live outside the rig: the life-size room is anchored to the
            // WORLD (it is the environment, not part of the board), and the menu computes
            // its own pose from the rig above.
            bigBackdrop.ShareLiveFrom(backdrop);   // same streamed floor/wall art, no re-upload
            DrawBigRoom(bigBackdrop, rx);
            DrawOptionsMenu();
            DrawInfoPanel(rx);
            FlushSettings(false);

            if (_showDebug || _menuPage == MenuPage.Debug)
            {
                string diag = $"{capture.Status}  state={gstate}  age={(double.IsNaN(ageMs)?-1:(int)ageMs)}ms{(_mirrorDim ? "  MIRROR" : "")}\n"
                            + $"pad=0x{gamepad.Buttons:X4}  map={(mapHeld ? (mapHeldGame ? "HELD(game)" : "HELD(pad)") : "-")}  screen={(showScreen?"on":"off")}{(_screenLatch?"(latched)":"")}  crawl={(isCrawlspace?"yes":"-")}"
                            + $"\nwater {BackdropRenderer.WaterDebug}  refl={_reflCount}(+{_reflCostumes} cos) player={(_reflPlayer ? "yes" : "NO")}"
                            + $"\nDIORAMA={_rigScale:0.000}  (home {RigScaleHome:0.000})  room {floorW:0.000}x{floorD:0.000} m"
                            // Always visible, not just in tune mode: with the headset on,
                            // this line is the only place the current slider can be read.
                            + $"\nv{Version}   TUNE [{(int)_tune + 1}/{TuneCount}] {_tuneLine}"
                            + (_tuneMode
                                ? "\n   == TUNE MODE: D-pad U/D = target, L/R = adjust, A = print, L3+R3 = exit (Isaac frozen)"
                                : "");
                DrawHud((data==null
                    ? "Waiting for Isaac...\n(--luadebug + exporter mod enabled)"
                    : $"{(haveLayered ? "RGON layers" : "anm2")}  frame {(haveLayered ? lscene.Frame : scene.Frame)}  ents {drawn}\n"+ $"sprites {spr}  fallback {fb}\nstrings {rx.Strings.Count}  {sprites.Status}")
                    + "\n" + diag);
            }
          }
          catch (Exception ex) { FrameError(ex); }
        });
    }

    // Tab cycles through the live-tunable scalars; Z (down) / X (up) adjust the selected
    // one, Enter prints the current value.
    // Slider holds only what's being actively tuned right now (planetarium). Everything
    // previously here is already baked into its default constant; re-add an entry when a
    // new calibration pass needs it.
    // ONLY what is actively being tuned. Everything retired from this list is baked
    // into its default constant and still lives as a field — re-add an entry here (plus
    // its ApplyTune and PrintTune cases) when a new calibration pass needs it. The cycle
    // had grown to 42 targets, which made reaching the one you wanted the slowest part
    // of a VR tuning session.
    enum Tune { WaterAlpha_, WaterRipple_, WaterScroll_, WaterLift_, WaterDarken_, WaterWash_, ReflectA_, ReflectStretch_, ReflectWob_, ReflectY_, MotherY_, MotherArmPull_, MotherP1Y_, RoomMPerTile_, MenuX_, MenuY_, RoomFloorY_, RoomZ_,
               StatsX_, StatsY_, StatsZ_, EidX_, EidY_, EidZ_, MegaSatanY_ }
    const int TuneCount = 25;
    static Tune _tune = Tune.WaterAlpha_;   // must name a LIVE entry — see the enum above

    static void CycleTune(int step)
    {
        _tune = (Tune)(((int)_tune + step + TuneCount) % TuneCount);
        PrintTune();
    }

    static void HandleCropKeys()
    {
        if (Input.Key(Key.Tab).IsJustActive()) CycleTune(1);

        // Scalar targets: Z / X.
        int dir = 0;
        if (Input.Key(Key.Z).IsJustActive()) dir = -1;
        if (Input.Key(Key.X).IsJustActive()) dir =  1;
        if (dir != 0) ApplyTune(dir);
        HandleToggleKeys();
    }

    static void ApplyTune(int dir)
    {
        {
            switch (_tune)
            {
                case Tune.WaterAlpha_:      BackdropRenderer.WaterAlphaMul  = Math.Clamp(BackdropRenderer.WaterAlphaMul + dir*0.05f, 0f, 2f); break;
                case Tune.WaterDarken_:     BackdropRenderer.WaterDarken    = Math.Clamp(BackdropRenderer.WaterDarken + dir*0.02f, 0f, 0.9f); break;
                case Tune.WaterWash_:       BackdropRenderer.WaterWash      = Math.Clamp(BackdropRenderer.WaterWash + dir*0.02f, 0f, 1f); break;
                case Tune.ReflectA_:        ReflectAlpha   = Math.Clamp(ReflectAlpha + dir*0.02f, 0f, 1f); break;
                case Tune.ReflectStretch_:  ReflectStretch = Math.Clamp(ReflectStretch + dir*0.05f, -2f, 2f); break;
                case Tune.ReflectWob_:      ReflectWobble  = Math.Clamp(ReflectWobble + dir*0.001f, 0f, 0.05f); break;
                case Tune.ReflectY_:        ReflectYNudge  = Math.Clamp(ReflectYNudge + dir*0.0004f, 0f, 0.03f); break;
                case Tune.MotherY_:         MotherY        = Math.Clamp(MotherY + dir*0.01f, -0.3f, 0.6f); break;
                case Tune.MotherArmPull_:   MotherArmPull  = Math.Clamp(MotherArmPull + dir*0.02f, 0f, 1f); break;
                case Tune.MotherP1Y_:       MotherP1Y      = Math.Clamp(MotherP1Y + dir*0.01f, -0.4f, 0.6f); break;
                case Tune.RoomMPerTile_:    RoomMetersPerTile = Math.Clamp(RoomMetersPerTile + dir*0.05f, 0.2f, 3f); break;
                case Tune.MenuX_:           MenuX          = Math.Clamp(MenuX + dir*0.02f, -1.2f, 1.2f); break;
                case Tune.MenuY_:           MenuY          = Math.Clamp(MenuY + dir*0.02f, -1.0f, 1.0f); break;
                case Tune.RoomFloorY_:      RoomFloorY     = Math.Clamp(RoomFloorY + dir*0.05f, -3f, 0.5f); break;
                case Tune.RoomZ_:           RoomZ          = Math.Clamp(RoomZ + dir*0.10f, -6f, 6f); break;
                case Tune.StatsX_:          StatsX         = Math.Clamp(StatsX + dir*0.02f, -1.5f, 1.5f); break;
                case Tune.StatsY_:          StatsY         = Math.Clamp(StatsY + dir*0.02f, -1.2f, 1.2f); break;
                case Tune.StatsZ_:          StatsZ         = Math.Clamp(StatsZ + dir*0.02f, -2.0f, 0.5f); break;
                case Tune.EidX_:            EidX           = Math.Clamp(EidX + dir*0.02f, -1.5f, 1.5f); break;
                case Tune.EidY_:            EidY           = Math.Clamp(EidY + dir*0.02f, -1.2f, 1.2f); break;
                case Tune.EidZ_:            EidZ           = Math.Clamp(EidZ + dir*0.02f, -2.0f, 0.5f); break;
                case Tune.MegaSatanY_:      MegaSatanY     = Math.Clamp(MegaSatanY + dir*0.01f, -0.4f, 0.8f); break;
                case Tune.WaterRipple_:     BackdropRenderer.WaterRippleMul = Math.Clamp(BackdropRenderer.WaterRippleMul + dir*0.05f, 0f, 3f); break;
                case Tune.WaterScroll_:     BackdropRenderer.WaterScrollMul = Math.Clamp(BackdropRenderer.WaterScrollMul + dir*0.1f, 0f, 5f); break;
                case Tune.WaterLift_:       BackdropRenderer.WaterLiftM     = Math.Clamp(BackdropRenderer.WaterLiftM + dir*0.0002f, 0f, 0.02f); break;
            }
            PrintTune();
        }
    }

    static void HandleToggleKeys()
    {
        // Live floor image: swap red/blue if blood renders blue (RGBA vs BGRA).
        if (Input.Key(Key.B).IsJustActive())
        {
            BackdropRenderer.ImageIsBGRA = !BackdropRenderer.ImageIsBGRA;
            Log.Info($"[Diorama] >floor image BGRA = {BackdropRenderer.ImageIsBGRA}");
        }
        // Compare the streamed floor image against the old tiled backdrop.
        if (Input.Key(Key.V).IsJustActive())
        {
            BackdropRenderer.ForceTiledFloor = !BackdropRenderer.ForceTiledFloor;
            Log.Info($"[Diorama] >ForceTiledFloor = {BackdropRenderer.ForceTiledFloor}");
        }
        // Compare the streamed wall split against the old tiled walls.
        if (Input.Key(Key.C).IsJustActive())
        {
            BackdropRenderer.ForceTiledWalls = !BackdropRenderer.ForceTiledWalls;
            Log.Info($"[Diorama] >ForceTiledWalls = {BackdropRenderer.ForceTiledWalls}");
        }

        // Flip the captured game screen vertically / horizontally.
        if (Input.Key(Key.G).IsJustActive())
        {
            ScreenFlipV = !ScreenFlipV;
            Log.Info($"[Diorama] >ScreenFlipV = {ScreenFlipV}");
        }
        if (Input.Key(Key.H).IsJustActive())
        {
            ScreenFlipH = !ScreenFlipH;
            Log.Info($"[Diorama] >ScreenFlipH = {ScreenFlipH}");
        }

        if (Input.Key(Key.Return).IsJustActive()) PrintTune();
    }

    // The Tab slider's readout. Log.Info alone is useless in a headset — there is no
    // window to read — so every tune print is ALSO latched into _tuneLine, which the
    // debug overlay draws.
    static string _tuneLine = "";
    static void TuneLog(string msg) { _tuneLine = msg; Log.Info("[Diorama] >" + msg); }

    static void PrintTune()
    {
        switch (_tune)
        {
            case Tune.WaterAlpha_:      TuneLog($"WaterAlphaMul = {BackdropRenderer.WaterAlphaMul:0.00}"); break;
            case Tune.WaterDarken_:     TuneLog($"WaterDarken = {BackdropRenderer.WaterDarken:0.00}"); break;
            case Tune.WaterWash_:       TuneLog($"WaterWash = {BackdropRenderer.WaterWash:0.00}"); break;
            case Tune.ReflectA_:        TuneLog($"ReflectAlpha = {ReflectAlpha:0.00}"); break;
            case Tune.ReflectStretch_:  TuneLog($"ReflectStretch = {ReflectStretch:0.00}  (negative = mirrored)"); break;
            case Tune.ReflectWob_:      TuneLog($"ReflectWobble = {ReflectWobble:0.000}"); break;
            case Tune.ReflectY_:        TuneLog($"ReflectYNudge = {ReflectYNudge:0.0000}"); break;
            case Tune.MotherY_:         TuneLog($"MotherY = {MotherY:0.000}"); break;
            case Tune.MotherArmPull_:   TuneLog($"MotherArmPull = {MotherArmPull:0.000}"); break;
            case Tune.MotherP1Y_:       TuneLog($"MotherP1Y = {MotherP1Y:0.000}"); break;
            case Tune.RoomMPerTile_:    TuneLog($"RoomMPerTile = {RoomMetersPerTile:0.000}"); break;
            case Tune.MenuX_:           TuneLog($"MenuX = {MenuX:0.000}"); break;
            case Tune.MenuY_:           TuneLog($"MenuY = {MenuY:0.000}"); break;
            case Tune.RoomFloorY_:      TuneLog($"RoomFloorY = {RoomFloorY:0.000}"); break;
            case Tune.RoomZ_:           TuneLog($"RoomZ = {RoomZ:0.000}"); break;
            case Tune.StatsX_:          TuneLog($"StatsX = {StatsX:0.000}"); break;
            case Tune.StatsY_:          TuneLog($"StatsY = {StatsY:0.000}"); break;
            case Tune.StatsZ_:          TuneLog($"StatsZ = {StatsZ:0.000}"); break;
            case Tune.EidX_:            TuneLog($"EidX = {EidX:0.000}"); break;
            case Tune.EidY_:            TuneLog($"EidY = {EidY:0.000}"); break;
            case Tune.EidZ_:            TuneLog($"EidZ = {EidZ:0.000}"); break;
            case Tune.MegaSatanY_:      TuneLog($"MegaSatanY = {MegaSatanY:0.000}"); break;
            case Tune.WaterRipple_:     TuneLog($"WaterRippleMul = {BackdropRenderer.WaterRippleMul:0.00}"); break;
            case Tune.WaterScroll_:     TuneLog($"WaterScrollMul = {BackdropRenderer.WaterScrollMul:0.0}"); break;
            case Tune.WaterLift_:       TuneLog($"WaterLiftM = {BackdropRenderer.WaterLiftM:0.0000}"); break;
        }
    }

    /// <summary>
    /// Render a REPENTOGON layer-state scene: every entity is drawn from the
    /// game's own resolved layers (runtime spritesheets included).
    /// </summary>
    static int DrawLayerScene(LayerScenePacket sc, SpriteRenderer sprites, BackdropRenderer backdrop,
                              UdpReceiver rx, Mesh cube, Material floorMat,
                              ref float floorW, ref float floorD, out int spr)
    {
        spr = 0;
        _statusTick++;
        float worldW = MathF.Max(1f, sc.RoomMaxX - sc.RoomMinX);
        float worldH = MathF.Max(1f, sc.RoomMaxY - sc.RoomMinY);
        float rscale = RoomPxToM;
        float longest = MathF.Max(worldW, worldH) * rscale;
        if (longest > MaxFootprintM) rscale *= MaxFootprintM / longest;
        float spritePxToM = rscale * SpriteScaleFactor;
        floorW = worldW * rscale; floorD = worldH * rscale;
        float cx = (sc.RoomMinX + sc.RoomMaxX) * 0.5f, cy = (sc.RoomMinY + sc.RoomMaxY) * 0.5f;

        // Curse of Darkness (bit 1): dim the whole board and light a pool around Isaac.
        // The mod already nulls the curse mask when Black Candle is held, so nothing to do here.
        var hudNow = rx.GetHud();
        _darkOn = hudNow != null && hudNow.Valid && (hudNow.Curses & 1) != 0;
        _darkCenter = FloorCenter;
        // Isaac's position is needed EVERY frame now, not just under the curse: the same light
        // that lifts the darkness also CULLS the level overlay around him, exactly as in game.
        bool foundIsaac = false;
        // Dogma's TELEGRAPH beams are drawn entirely by us (see DrawTechDotBeams): collect
        // the Tech Dots' game coords and Isaac's in the same pass that finds him anyway.
        _techDots.Clear();
        float isaacGX = cx, isaacGY = cy;
        foreach (var pe in sc.Entities)
        {
            if (!foundIsaac && pe.Cat == EntityCategory.Player && !pe.IsCostume && !pe.IsHeld)
            {
                _darkCenter = new Vec3(FloorCenter.x + (pe.X - cx) * rscale,
                                       FloorCenter.y,
                                       FloorCenter.z + (pe.Y - cy) * rscale);
                isaacGX = pe.X; isaacGY = pe.Y;
                foundIsaac = true;
            }
            if (pe.Cat == EntityCategory.Effect &&
                (rx.Strings.Get(pe.Anm2Id) ?? "").ToLowerInvariant().Contains("tech dot"))
                _techDots.Add(new Vec2(pe.X, pe.Y));
        }
        BackdropRenderer.Dim = _darkOn ? Math.Clamp(1f - CurseDark, 0f, 1f) : 1f;
        // Hand the same light to the overlay so it opens a hole around Isaac.
        BackdropRenderer.HasLight    = foundIsaac;
        BackdropRenderer.LightCenter = _darkCenter;
        BackdropRenderer.LightRadius = CurseLightR;

        var bd = rx.GetBackdrop();
        string rk = $"{bd.Type}_{bd.Shape}_{bd.MinX:0}_{bd.MinY:0}_{bd.MaxX:0}_{bd.MaxY:0}";
        var fimg = rx.GetFloorImage();
        if (fimg.Valid) backdrop.SetLiveFloor(fimg.Version, fimg.W, fimg.H, fimg.Rgba, rk);
        var wimg = rx.GetWallImage();
        if (wimg.Valid) backdrop.SetLiveWall(wimg.Version, wimg.W, wimg.H, wimg.Rgba, rk);
        backdrop._lastXmlCount = bd.XmlCount;
        // Read once per frame: GetWater() takes the receiver's lock, and the reflection
        // pass below would otherwise take it once per entity.
        var water = rx.GetWater();
        backdrop.Water = water;
        _reflCount = 0; _reflCostumes = 0; _reflPlayer = false;
        bool drew = bd.Valid && backdrop.Draw(bd.Type, bd.Shape, bd.RoomType, bd.Wall, bd.Floor, bd.Name,
                        FloorCenter, floorW, floorD, FloorCenter.y + 0.003f - FloorDropM + FloorRaiseM);
        if (!drew)
        {
            cube.Draw(floorMat, Matrix.TS(FloorCenter, new Vec3(floorW,0.004f,floorD)));
            DrawGrid(floorW, floorD);
        }

        // Curse of Darkness: multiply the floor down everywhere except Isaac's light.
        if (_darkOn)
            DrawDarknessVeil(FloorCenter,
                             (floorW + 2f*BackdropRenderer.WallOutsetM) * VeilScale,
                             (floorD + 2f*BackdropRenderer.WallOutsetM) * VeilScale,
                             FloorCenter.y + 0.003f - FloorDropM + FloorRaiseM + 0.0015f + VeilY,
                             bd.Shape);

        // Draw back-to-front by Isaac Y so overlapping billboards (e.g. Duke of Flies
        // and its orbiting flies) layer correctly: blend sprites don't write depth, so
        // paint order decides who's on top. Stable (index tiebreak) so a player's
        // costumes keep their order relative to the body.
        var order2 = new int[sc.Entities.Count];
        for (int i = 0; i < order2.Length; i++) order2[i] = i;
        Array.Sort(order2, (a, b) => { int c = sc.Entities[a].Y.CompareTo(sc.Entities[b].Y); return c != 0 ? c : a.CompareTo(b); });
        int drawn = 0;
        for (int oi = 0; oi < order2.Length; oi++)
        {
            var e = sc.Entities[order2[oi]];
            if (e.IsDestroyed) continue;
            // On a hit the game hides all cosmetics and flashes the body; skip the
            // costume sprites while hurt so only the flashing body shows.
            if (e.IsHurt && e.IsCostume) continue;

            float zBias = oi * 0.00002f;
            float dx = (e.X-cx)*rscale, dz = (e.Y-cy)*rscale + zBias;
            if (e.Cat == EntityCategory.Grid) dz += GridZNudgeM;

            int doorSide = 0;
            if (e.Cat == EntityCategory.Door)
                doorSide = PlaceDoor(ref dx, ref dz, floorW, floorD);

            float lift = e.HeightPx * spritePxToM * HeightGain;
            float catLift = e.Cat == EntityCategory.Grid ? GridLiftM : 0f;
            if (e.Cat == EntityCategory.Projectile) catLift += TearYNudgeM;   // tear eye-height nudge
            Vec3 pos = new(FloorCenter.x+dx, FloorCenter.y + 0.004f + lift + catLift, FloorCenter.z+dz);

            // Grid tiles need a hair more size to tile seamlessly.
            float entScale = e.Scale;
            if (e.Cat == EntityCategory.Grid) entScale *= GridSizeScale;

            string? anm2 = rx.Strings.Get(e.Anm2Id);
            string  anim = rx.Strings.Get(e.AnimId) ?? "";
            string  anm2L = anm2?.ToLowerInvariant() ?? "";

            // Flat = the mod's grid-flat bit OR an effect that reads as a floor
            // decal (creep, blood pools, poison puddles) — those lie on the floor.
            bool decal = e.IsFlat
                         || (e.Cat == EntityCategory.Effect && IsFloorDecal(anm2L));
            Quat facing = decal ? DecalFacing
                        : doorSide ==  1 ? Quat.FromAngles(SpriteTiltDeg, -90f, 0f)
                        : doorSide == -1 ? Quat.FromAngles(SpriteTiltDeg,  90f, 0f)
                        : SpriteFacing;
            if (decal)
            {
                // Mist (1000.138) is a wide ground fog that overlaps creep, blood and every
                // other decal at the SAME height, so they z-fight. One extra millimetre puts
                // it cleanly on top without lifting it off the floor. Fixed, not a slider —
                // the value only has to be "more than zero, less than a sprite".
                float mistLift = anm2L.Contains("1000.138_mist") ? 0.0010f : 0f;
                pos = new Vec3(pos.x, FloorCenter.y + 0.005f + mistLift, pos.z);
            }

            // Floor shadow: a soft dark ellipse the game draws as an engine layer
            // under most entities. Size is Isaac's own shadowSize (e.ShadowSize),
            // scaled by the sprite scale. The mod sends 0 for anything that should
            // not cast one (effects/grid/doors, costume sub-records); decals never do.
            if (!decal && !e.IsCostume && e.ShadowSize > 0f)
            {
                float shR = e.ShadowSize * entScale * ShadowBaseRadiusM;
                if (shR > 0.0015f)
                {
                    Vec3 shPos = new(FloorCenter.x + dx, FloorCenter.y + ShadowYNudgeM, FloorCenter.z + dz);
                    Mesh.Quad.Draw(ShadowMat(),
                        Matrix.TRS(shPos, DecalFacing, new Vec3(shR * 2f, shR * 2f, 1f)));
                }
            }

            // Costumes sit slightly in front of Isaac; nudge along the viewer axis
            // (+z = toward you). Live-tunable with , / . keys.
            // Costumes (Brimstone horns etc.) are kept in front of the body by their
            // orderBase-16 layer depth, which is baked in sprite-space and so scales with
            // Isaac's size. This nudge must scale the SAME way — a fixed nudge stays put
            // while the layer push shrinks with a small Isaac, flipping the costume behind
            // the body. Scaling by entScale keeps the whole relationship sign-stable.
            if (e.IsCostume) pos = new Vec3(pos.x, pos.y, pos.z + CostumeFwdM * entScale);
            // Held item (pickup pose): the anm2 keeps it near Isaac's feet, so lift it
            // above his head. The sprite scales around its feet pivot by entScale, so his
            // head-top height above the floor is proportional to entScale — scaling the
            // lift the same way tracks small↔big Isaac automatically. HeldItemY is the lift
            // in metres at normal size (set it so the item just clips his forehead); Tab-tunable.
            if (e.IsHeld)
                pos = new Vec3(pos.x, pos.y + (e.IsCostume ? HeldItemY : HeldEntityY) * entScale, pos.z);
            var tint = new Color(e.R, e.G, e.B, e.A);
            // Curse of Darkness: dim each entity by how far it sits from Isaac's light.
            if (_darkOn)
            {
                float lit = DarkLevelAt(pos);
                tint = new Color(tint.r * lit, tint.g * lit, tint.b * lit, tint.a);
            }
            // Secret-room "hole in wall" passage renders too short — stretch it taller.
            bool secretPassage = e.Cat == EntityCategory.Door && (anm2L.Contains("hole") || anm2L.Contains("secret"));
            float yStretch = secretPassage ? SecretPassageStretchY : 1f;
            if (secretPassage) pos = new Vec3(pos.x, pos.y + SecretPassageRaiseM, pos.z);
            // Dogma's TV (950.1) is a big set-piece whose sprite pivot leaves it sunk
            // into the floor in the diorama. Lift it by its own tunable.
            if (anm2L.Contains("dogma tv")) pos = new Vec3(pos.x, pos.y + DogmaTvY * entScale, pos.z);
            if (anm2L.Contains("witness 2")) pos = new Vec3(pos.x, pos.y + MotherY * entScale, pos.z);
            if (anm2L.Contains("megasatanhead"))
                pos = new Vec3(pos.x, pos.y + MegaSatanY * entScale, pos.z);
            // Mother phase 1 (912.0.0 head, .1 back, .2 left arm, .3 right arm). Matched on
            // the FULL stem so "912.010_witness 2" (phase 2, lifted above) and every other
            // "witness"/"gaper" sheet are left alone.
            //   X: the diorama's horizontal scale spreads the four entities wider apart than
            //      the game's flat view does, so the arms drift off the body. Pulling every
            //      record toward the centre moves the off-centre arms inward and barely moves
            //      the near-centred body — one scalar, both arms, opposite directions, with
            //      no left/right test.
            //   Y: one lift for all four, since the app never receives the subtype.
            if (anm2L.Contains("912.000_witness"))
            {
                float mx = MotherArmPull > 0.001f
                         ? FloorCenter.x + (pos.x - FloorCenter.x) * (1f - MotherArmPull)
                         : pos.x;
                pos = new Vec3(mx, pos.y + MotherP1Y * entScale, pos.z);
            }
            // The Tech Dot is the telegraph beam's ORIGIN, so it belongs on the beam's plane,
            // not on the floor. Derived from LaserYNudgeM rather than given its own slider:
            // "same height as the beam" is a relationship, and it should stay true if that
            // nudge is ever retuned.
            bool techDot = anm2L.Contains("tech dot");
            bool reflectionOnly = IsReflectionOnly(anm2L);
            // A COLORIZED shot (Mother's bullets) is drawn from BulletAtlas art that is
            // already red, and a tint can only multiply — so the colorize hue could never
            // land and they stayed red. Same shape as Dogma's Tech Dot: grey the TEXELS
            // first (max(r,g,b)), and the colorize below then paints the real colour on.
            bool colorizedShot = (e.Cat == EntityCategory.Projectile || e.Cat == EntityCategory.Enemy)
                                 && e.ColA > 0.004f;
            if (techDot) pos = new Vec3(pos.x, pos.y + LaserYNudgeM, pos.z);
            // Water reflection: a second, mirrored copy of this sprite lying on the water.
            // Drawn BEFORE the entity so the real sprite always wins the depth test, and
            // skipped for anything already flat (a decal has nothing to mirror) and for a
            // dry room.
            //
            // COSTUMES AND HELD ITEMS REFLECT TOO. Excluding them was wrong: a costume is
            // not a duplicate of the body, it is an extra LAYER that completes it — and
            // items like Brimstone REPLACE a base layer, with the mod hiding the base head
            // (pcBaseHide) because the costume supplies it. Skipping costume records meant
            // the reflection was drawn from a base sprite whose head had been deliberately
            // hidden, so picking up Brimstone emptied the reflection out.
            if (water.Valid && water.Amount > 0.05f && ReflectAlpha > 0.001f
                && !decal && !string.IsNullOrEmpty(anm2))
            {
                // water_overlay.fs sways the reflection horizontally with a sine. We can't
                // displace per-row without a shader, so the whole copy sways instead —
                // phase-shifted by its own position so neighbours don't move in lockstep.
                float wob = MathF.Sin(Time.Totalf * 2.1f + pos.z * 9f) * ReflectWobble;
                // Sub-records (costume layers, the held item) sit a hair above the body's
                // reflection so they always paint over it, the same way their orderBase
                // keeps them in front of the upright sprite. Tiny: they must still read as
                // one reflection, not as a stack of floating cut-outs.
                float subLift = (e.IsCostume || e.IsHeld) ? 0.0006f : 0f;
                // Built from the RAW room offsets, not from `pos`: by this point pos has
                // collected per-entity nudges (the costume forward-nudge, the held-item
                // lift, the Tech Dot and TV lifts) that exist to order UPRIGHT billboards.
                // Laid flat, the costume nudge alone would slide a costume's reflection a
                // centimetre off its own body's.
                var rPos = new Vec3(FloorCenter.x + dx + wob,
                                    FloorCenter.y + ReflectYNudge + subLift,
                                    FloorCenter.z + dz);
                float rAlpha = ReflectAlpha * Math.Clamp(water.Amount, 0f, 1f);
                // Tinted by the water, and never brighter than it: a reflection is part of
                // the surface, not an object sitting on it.
                var rTint = new Color(tint.r * 0.55f, tint.g * 0.70f, tint.b * 1.0f, rAlpha);
                _reflCount++;
                if (e.Cat == EntityCategory.Player && !e.IsCostume) _reflPlayer = true;
                if (e.IsCostume) _reflCostumes++;
                sprites.DrawEntity(anm2!, anim, e.AnimFrame, rPos, DecalFacing,
                        e.RotationDeg, spritePxToM, entScale, e.FlipX, rTint,
                        softAlpha: true,
                        overrides: e.Overrides, strings: rx.Strings,
                        overlayAnim: rx.Strings.Get(e.OverlayAnimId), overlayFrame: e.OverlayFrame,
                        colorOffset: new Vec3(e.OffR, e.OffG, e.OffB),
                        yStretch: ReflectStretch);
            }

            // Dogma's TV (950.1) is destroyed before Dogma itself can be damaged, so it needs
            // a visible hit reaction. The mod raises the hurt bit on any Dogma-shaded entity
            // whose HP just dropped; SpriteRenderer desaturates the tint for these, so a
            // brightness punch is exactly a white flash.
            // The dot art is pure red (254,0,0 body / 175,0,0 ring / white core); it should
            // read white, and a tint cannot do that to red, so the TEXELS are greyscaled.
            if (techDot) tint = new Color(1f, 1f, 1f, tint.a);
            if (e.IsDogmaShaded && e.IsHurt && !e.IsCostume)
                tint = new Color(tint.r * DogmaHitFlash, tint.g * DogmaHitFlash,
                                 tint.b * DogmaHitFlash, tint.a);
            // CREEP DIAGNOSTIC (app side). Every mod-side signal for 1000.23 is clean —
            // sent, visible, white tint, valid looping anim, a sheet that resolves — so the
            // remaining question is what the APP does with it. All the instrumentation so
            // far has been on the other side of the wire; this is the missing half.
            if (anm2L.Contains("creep") && _creepLogged.Add(anm2L))
                Log.Info($"[Diorama] CREEP DRAW anm2='{anm2}' anim='{anim}' frame={e.AnimFrame} " +
                         $"decal={decal} cat={e.Cat} flat={e.IsFlat} pos=({pos.x:0.000},{pos.y:0.000},{pos.z:0.000}) " +
                         $"scale={entScale:0.00} tint=({tint.r:0.00},{tint.g:0.00},{tint.b:0.00},{tint.a:0.00}) " +
                         $"ovCount={(e.Overrides?.Count ?? 0)} " + sprites.Diagnose(anm2!, anim, e.AnimFrame));

            bool any = false;
            // The Wraith and friends exist only as a reflection: the mirrored copy was
            // already drawn above, so the upright sprite is simply skipped. If the room has
            // no water there is nothing to see, which is exactly the game's behaviour — the
            // floor shadow above is the only tell either way.
            if (!string.IsNullOrEmpty(anm2) && !reflectionOnly)
                any = sprites.DrawEntity(anm2!, anim, e.AnimFrame, pos, facing,
                        e.RotationDeg, spritePxToM, entScale, e.FlipX, tint,
                        // A HELD object (Grid Projectile Helper) deliberately sits on a static
                        // frame, so the freeze-drop would bin it a second after pickup. Never
                        // drop what Isaac is holding.
                        dropWhenFinished: e.Cat == EntityCategory.Effect && !e.IsHeld && CanFreezeDrop(anm2L),
                        softAlpha: IsSoftAlpha(anm2L) || e.Cat == EntityCategory.Effect
                                                      || e.Cat == EntityCategory.Projectile,
                        overrides: e.Overrides, strings: rx.Strings,
                        overlayAnim: rx.Strings.Get(e.OverlayAnimId), overlayFrame: e.OverlayFrame,
                        orderBase: e.IsCostume ? (e.IsBodyCostume ? CostumeBehindOrder : 16) : 0,    // body costumes behind the head, others in front
                        colorOffset: new Vec3(e.OffR, e.OffG, e.OffB),
                        colorize: new Color(e.ColR, e.ColG, e.ColB, e.ColA),
                        yStretch: yStretch,
                        dogmaShader: e.IsDogmaShaded, forceGrey: techDot || colorizedShot);
            if (any) spr++;
            if (anm2L.Contains("creep") && _creepDrawLogged.Add(anm2L + "|" + any))
                Log.Info($"[Diorama] CREEP DRAW RESULT anm2='{anm2}' DrawEntity={any}");
            // Status-effect indicators float above afflicted enemies — but not above one
            // you are not supposed to be able to see.
            if (e.Cat == EntityCategory.Enemy && e.Status != 0 && !reflectionOnly)
                DrawStatusMarkers(e.Status, pos, e.HeightPx * spritePxToM, sprites, spritePxToM);
            drawn++;
            if (e.IsDogmaShaded) _dogmaOnScreen = true;
        }
        // The looming-shadow overlay is drawn earlier in the frame than this loop, so the
        // boost lands one frame late — invisible, and it means no extra scan of the scene.
        BackdropRenderer.OverlayBoost = _dogmaOnScreen ? DogmaOverlayMul : 1f;
        _dogmaOnScreen = false;

        // Dogma's telegraph beams. They exist only as a visual in the game — no entity,
        // no effect id, nothing the API exposes — so they are synthesised here for the
        // lifetime of each Tech Dot, which is exactly when the game shows them.
        if (_techDots.Count > 0 && foundIsaac)
            DrawTechDotBeams(sprites, sc, cx, cy, rscale, isaacGX, isaacGY);

        // Lasers (brimstone / technology / tech-x / etc): flat textured ribbons that
        // follow the beam's real sample polyline on the floor. Circle lasers use their
        // sample path too (so they animate) when it traces a ring; else a synth ring.
        var lasers = rx.GetLasers();
        if (lasers != null && lasers.Count > 0)
        {
            float ly = FloorCenter.y + LaserYNudgeM;
            int li = 0;
            Vec3 Map(float wx, float wy) =>
                new Vec3(FloorCenter.x + (wx - cx) * rscale, ly, FloorCenter.z + (wy - cy) * rscale);
            foreach (var L in lasers)
            {
                var st = LaserStyle(L.Variant);
                // Dogma's brimstone isn't red art at all: the game runs it through
                // coloroffset_dogma.fs and it reads as white TV static. Use the WHITE
                // beam crop (the same one Trisagion/Shoop uses) and jitter its brightness
                // per frame so it flickers like the static instead of sitting flat red.
                if (L.IsDogma)
                {
                    // WHITE crop (the Trisagion/Shoop one) instead of the red brimstone
                    // art, tinted grey and jittered per frame so it flickers like the
                    // static, and narrowed by DogmaLaserWidthMul -- the reported beam is
                    // a THIN grey line, not a brimstone column. Width is independent of
                    // the crop, so the sheet region stays 32x64.
                    float flick = DogmaLaserGrey * (0.74f + 0.26f * ((int)(Time.Totalf * 24f) % 5) / 4f);
                    st = ("Effects/Effect_018_LaserEffects.png", 192, 0, 32, 64,
                          flick, flick, flick, flick, flick, flick);
                }
                // Colour: normally the variant tint (red beam sheet etc) modulated by the
                // beam's tint. But a COLORIZE (homing/synergy, e.g. purple) recolours the
                // whole beam by hue and REPLACES the variant red — so when it's present,
                // use the colorize hue directly.
                Color tint = L.Ca > 0.004f
                    ? SpriteRenderer.SaturateColor(new Color(L.Cr, L.Cg, L.Cb, 1f))
                    : new Color(st.tr * L.R, st.tg * L.G, st.tb * L.B, 1f);
                // Glow follows the beam's APPARENT colour (st.g*), not the sheet tint — see
                // LaserStyle. A colorized beam (homing/synergy purple) already recolours the
                // whole beam, so there the glow correctly follows that tint instead.
                Color glowCol = L.Ca > 0.004f
                    ? tint
                    : new Color(st.gr * L.R, st.gg * L.G, st.gb * L.B, 1f);
                float wScale = MathF.Max(0.05f, L.Scale);
                float widthM = st.cw * spritePxToM * wScale * LaserWidthMul;
                if (L.IsDogma) widthM *= (L.Variant == 2 ? DogmaThinLaserWidthMul : DogmaLaserWidthMul);
                float tileM  = st.ch * spritePxToM * wScale;
                if (tileM < 0.001f) tileM = 0.01f;

                // Does the sample polyline actually describe a shape (spans real area)?
                bool samplesUsable = false;
                if (L.SampleCount >= 2)
                {
                    float minx=1e9f,miny=1e9f,maxx=-1e9f,maxy=-1e9f;
                    for (int k=0;k<L.SampleCount;k++)
                    { float x=L.Xs[k],y=L.Ys[k]; if(x<minx)minx=x; if(x>maxx)maxx=x; if(y<miny)miny=y; if(y>maxy)maxy=y; }
                    float span = MathF.Max(maxx-minx, maxy-miny);
                    samplesUsable = span > (L.IsCircle ? MathF.Max(8f, L.Radius*0.5f) : 1f);
                }

                Vec3[] pts; int npt;
                if (samplesUsable)
                {
                    // Real path. Close the loop for a circle so the ring is continuous.
                    bool close = L.IsCircle;
                    npt = L.SampleCount + (close ? 1 : 0);
                    pts = new Vec3[npt];
                    for (int k = 0; k < L.SampleCount; k++) pts[k] = Map(L.Xs[k], L.Ys[k]);
                    if (close) pts[L.SampleCount] = pts[0];
                }
                else if (L.IsCircle)
                {
                    // Synth ring from centre + radius (samples were degenerate).
                    const int RN = 48;
                    npt = RN + 1;
                    pts = new Vec3[npt];
                    float rM = L.Radius * rscale;
                    float ccx = FloorCenter.x + (L.CX - cx) * rscale;
                    float ccz = FloorCenter.z + (L.CY - cy) * rscale;
                    for (int k = 0; k <= RN; k++)
                    { float ang = (float)(k * 2.0 * Math.PI / RN); pts[k] = new Vec3(ccx + MathF.Cos(ang)*rM, ly, ccz + MathF.Sin(ang)*rM); }
                }
                else continue;   // straight/curvy laser with no usable samples this frame

                // Soft coloured light spilling onto the FLOOR (not at beam height), so it
                // reads as the beam lighting the ground below it.
                if (LaserGlowStr > 0f)
                    sprites.DrawLaserGlow(pts, npt, widthM * LaserGlowMul,
                        new Color(glowCol.r * LaserGlowStr, glowCol.g * LaserGlowStr, glowCol.b * LaserGlowStr, 1f),
                        new Vec3(0, 1, 0), FloorCenter.y + 0.0015f);
                sprites.DrawLaserRibbon(li++, pts, npt, widthM, tileM,
                                        st.sheet, st.cx, st.cy, st.cw, st.ch, tint, new Vec3(0, 1, 0),
                                        dogmaStatic: L.IsDogma);
            }
        }

        if (_dumpCount < 3 && (_frameTick++ % 120) == 0 && sc.Entities.Count > 0)
        {
            _dumpCount++;
            Log.Info($"[Diorama] ===== LAYER dump {_dumpCount} (entities={sc.Entities.Count}) =====");
            // Show every player-category record (base sprite + each costume).
            int shown = 0;
            for (int i=0;i<sc.Entities.Count && shown<8;i++)
            {
                var e = sc.Entities[i];
                if (e.Cat != EntityCategory.Player && shown >= 3) continue;
                shown++;
                Log.Info($"[Diorama] ent{i} cat={e.Cat} shadow={e.ShadowSize:0.###} anm2='{rx.Strings.Get(e.Anm2Id)}' " +
                         $"anim='{rx.Strings.Get(e.AnimId)}' f{e.AnimFrame} " +
                         $"overlay='{rx.Strings.Get(e.OverlayAnimId)}' of{e.OverlayFrame} overrides={e.Overrides.Count}");
                foreach (var kv in e.Overrides)
                    Log.Info($"[Diorama]    rt  layer{kv.Key} vis={kv.Value.Visible} " +
                             $"sheet='{rx.Strings.Get(kv.Value.SheetId)}'");
                // For the player, also show what the .anm2 itself says per layer —
                // that tells us whether costume layers lack keyframes or are hidden.
                string? ap = rx.Strings.Get(e.Anm2Id);
                if (e.Cat == EntityCategory.Player && !string.IsNullOrEmpty(ap))
                    foreach (var line in sprites.ExplainLayers(ap!, rx.Strings.Get(e.AnimId) ?? "", e.AnimFrame))
                        Log.Info($"[Diorama]    anm2 {line}");
            }
        }

        DrawPrices(rx.GetPrices(), cx, cy, rscale, sprites);
        return drawn;
    }

    // Shop / devil-deal prices, rendered from the REAL game sheet (Shop_001_Bitfont.png,
    // referenced by 005.150_shop item.anm2), laid FLAT on the floor just below the item.
    //   Digits:   16x16 cells, 6x2 grid — frame N = digit N (coin symbol = frame 10).
    //   Hearts:   32x32 cells, 4x2 grid starting at y=32 — heart-cost icons.
    //   Spikes:   a separate effect entity (1000.174) already drawn in the scene.
    public static float PriceBelowM      = 0.018f;    // how far below the item (toward viewer)
    public static float PriceGlyphPxToM  = 0.0011f;   // sheet px -> metres (icon size)
    public static float PriceDigitStepM  = 0.011f;    // spacing between digit centres
    public static int   PriceCoinFrame   = 10;        // bitfont cell for the coin symbol
    const string BitfontSheet = "items/shop/Shop_001_Bitfont.png";

    // ---- Dogma's telegraphed light beams -------------------------------------
    // The attack, as observed in game:
    //   1. Dogma shoots the dots out across the room — they TRAVEL to their spots.
    //   2. Each dot freezes, and throws a light beam at Isaac. The beam LOCKS at the
    //      direction it caught him in; it does not keep following him.
    //   3. The dot becomes a Tech Dot and fires a laser along that telegraphed line.
    // None of this is an entity: the beam has no id and nothing in the REPENTOGON API
    // reports it (user checked), so it is synthesised here, tied to the dot's lifetime.
    //
    // Step 2 is why the aim is LATCHED rather than recomputed: a beam that keeps tracking
    // sweeps around the room and stops telegraphing anything, which is what the first cut
    // did. And step 1 is why a beam only starts once its dot has held still for
    // SettleFrames — a travelling dot has not picked its target yet.
    //
    // Identity across frames comes from the dot's own position, quantised to AimKeyQ game
    // units: we have no per-entity id on the wire, but a dot that is FROZEN has a stable
    // position, and a dot still travelling changes key every frame and so never settles.
    // That one trick gives us both the identity and the "has it stopped?" test.
    static readonly System.Collections.Generic.List<Vec2> _techDots = new();
    static readonly System.Collections.Generic.Dictionary<(int, int), TechBeam> _techBeams = new();
    static int _techTick;
    struct TechBeam { public int Seen, LastTick; public float AimX, AimY; public bool Locked; }

    const float AimKeyQ      = 3f;   // game units per identity cell
    const int   SettleFrames = 3;    // frames held still before the dot picks its target

    static float TechBeamWidthMul = 0.18f;   // Tab->TechBeamW, relative to a laser's width
    static float TechBeamAlpha    = 0.40f;   // Tab->TechBeamA

    static void DrawTechDotBeams(SpriteRenderer sprites, LayerScenePacket sc,
                                 float cx, float cy, float rscale, float isaacGX, float isaacGY)
    {
        float ly = FloorCenter.y + LaserYNudgeM;
        float spritePxToM = rscale * SpriteScaleFactor;   // same derivation as the caller
        Vec3 Map(float wx, float wy) =>
            new Vec3(FloorCenter.x + (wx - cx) * rscale, ly, FloorCenter.z + (wy - cy) * rscale);

        // Crop x=416 is the LIGHT_BEAM art (Gabriel/Uriel's holy beam), not the brimstone
        // base at x=192. It is the right look for a telegraph: measured across the 32px
        // crop, brimstone is a hard slab (alpha jumps 40 -> 214 and stays flat for 24
        // columns), while the light beam feathers 14 -> 255 over four columns each side.
        // Straight, thin and soft-edged — which is what the telegraph should read as, since
        // it is a warning line and not the attack.
        const int CropX = 416, CropY = 0, CropW = 32, CropH = 64;
        float widthM = CropW * spritePxToM * LaserWidthMul * TechBeamWidthMul;
        var tint = new Color(1f, 1f, 1f, TechBeamAlpha);

        _techTick++;
        int slot = 900;   // ribbon pool slots well clear of the real lasers' 0..n
        foreach (var d in _techDots)
        {
            var key = ((int)MathF.Round(d.x / AimKeyQ), (int)MathF.Round(d.y / AimKeyQ));
            _techBeams.TryGetValue(key, out var b);
            // A gap in the key's history means either the dot moved or this is a NEW volley
            // reusing an old cell — both must re-latch, so clear Locked too. (Without this,
            // the next volley's dot would inherit the previous one's stale aim.)
            if (b.LastTick == _techTick - 1) b.Seen++;
            else { b.Seen = 1; b.Locked = false; }
            b.LastTick = _techTick;

            if (!b.Locked && b.Seen >= SettleFrames)
            {
                float ax = isaacGX - d.x, ay = isaacGY - d.y;
                float al = MathF.Sqrt(ax * ax + ay * ay);
                if (al >= 1f) { b.AimX = ax / al; b.AimY = ay / al; b.Locked = true; }
            }
            _techBeams[key] = b;
            if (!b.Locked) continue;                 // still travelling, or aimed at itself

            // Extend along the LATCHED direction to the room wall: the beam overshoots
            // Isaac, it does not stop at him.
            float t = 1e9f;
            if (b.AimX >  1e-6f) t = MathF.Min(t, (sc.RoomMaxX - d.x) / b.AimX);
            if (b.AimX < -1e-6f) t = MathF.Min(t, (sc.RoomMinX - d.x) / b.AimX);
            if (b.AimY >  1e-6f) t = MathF.Min(t, (sc.RoomMaxY - d.y) / b.AimY);
            if (b.AimY < -1e-6f) t = MathF.Min(t, (sc.RoomMinY - d.y) / b.AimY);
            if (t < 0f || t > 1e8f) t = 600f;        // fallback: well past the playfield

            var pts = new[] { Map(d.x, d.y), Map(d.x + b.AimX * t, d.y + b.AimY * t) };
            sprites.DrawLaserRibbon(slot++, pts, 2, widthM, CropH * spritePxToM,
                                    "Effects/Effect_018_LaserEffects.png",
                                    CropX, CropY, CropW, CropH, tint, new Vec3(0, 1, 0),
                                    whiten: true);
        }

        // Drop dots that are gone, so the next volley latches fresh aims.
        if (_techBeams.Count > 0)
        {
            var stale = new System.Collections.Generic.List<(int, int)>();
            foreach (var kv in _techBeams) if (kv.Value.LastTick != _techTick) stale.Add(kv.Key);
            foreach (var k in stale) _techBeams.Remove(k);
        }
    }

    static void DrawPrices(UdpReceiver.PriceItem[] prices, float cx, float cy, float rscale, SpriteRenderer sprites)
    {
        if (prices.Length == 0) return;
        foreach (var pr in prices)
        {
            float dx = (pr.X - cx) * rscale, dz = (pr.Y - cy) * rscale + PriceBelowM;
            Vec3 c = new(FloorCenter.x + dx, FloorCenter.y + 0.006f, FloorCenter.z + dz);
            if (pr.Price > 0)         DrawPriceNumber(sprites, c, pr.Price);
            else if (pr.Price == -5)  { /* spikes: the 1000.174 effect entity renders it */ }
            else                      DrawPriceHeartIcon(sprites, c, -pr.Price - 1);
        }
    }

    // One 16x16 glyph from the bitfont, flat on the floor. Rotation about X keeps
    // world X, so no flip is needed — the glyph reads correctly as-is.
    static void DrawGlyph16(SpriteRenderer sprites, int frame, Vec3 pos)
    {
        int col = frame % 6, row = frame / 6;   // 6x2 grid
        sprites.DrawLayer(BitfontSheet, col*16, row*16, 16, 16, -8, -8, 0f,
            pos, DecalFacing, PriceGlyphPxToM, 1f, false, false, Color.White, 0, false);
    }

    static void DrawPriceNumber(SpriteRenderer sprites, Vec3 center, int value)
    {
        string s = value.ToString();
        int n = s.Length + 1;                       // digits + trailing coin symbol
        // Left-to-right in world +x (= viewer left→right); no flip.
        float x = center.x - (n - 1) * PriceDigitStepM * 0.5f;
        foreach (char ch in s)
        {
            if (ch >= '0' && ch <= '9') DrawGlyph16(sprites, ch - '0', new Vec3(x, center.y, center.z));
            x += PriceDigitStepM;
        }
        DrawGlyph16(sprites, PriceCoinFrame, new Vec3(x, center.y, center.z));   // coin ¢
    }

    // Heart-cost icon (32x32) from the bitfont's Hearts region (starts at y=32).
    static void DrawPriceHeartIcon(SpriteRenderer sprites, Vec3 center, int frame)
    {
        if (frame < 0) frame = 0; if (frame > 7) frame = 7;
        int col = frame % 4, row = frame / 4;
        sprites.DrawLayer(BitfontSheet, col*32, 32 + row*32, 32, 32, -16, -16, 0f,
            center, DecalFacing, PriceGlyphPxToM, 1f, false, false, Color.White, 0, false);
    }

    /// <summary>
    /// Decide which wall a door belongs to by NEAREST EDGE (not distance from the
    /// room centre — that misplaces off-centre doors on a long wall), nudge it
    /// inward, and return -1 west / 0 far-near / +1 east.
    /// </summary>
    static int PlaceDoor(ref float dx, ref float dz, float w, float d)
    {
        float dEast = MathF.Abs(w/2 - dx), dWest = MathF.Abs(dx + w/2);
        float dFar  = MathF.Abs(dz + d/2), dNear = MathF.Abs(d/2 - dz);
        float best = MathF.Min(MathF.Min(dEast,dWest), MathF.Min(dFar,dNear));
        if (best == dEast) { dx -= DoorInsetM; return  1; }
        if (best == dWest) { dx += DoorInsetM; return -1; }
        if (best == dFar)  { dz += DoorInsetM; return  0; }
        dz -= DoorInsetM;  return 0;
    }

    // Status-effect indicators floating above an enemy, one per active status.
    // Rendered from the game's own gfx/ui/statuseffects.anm2 when we can resolve the
    // right animation; falls back to a coloured marker otherwise.
    static readonly string[] _statusAnm2Paths = { "ui/statuseffects.anm2", "statuseffects.anm2" };
    static string _statusAnm2 = "ui/statuseffects.anm2";   // resolved at first use
    static Mesh? _statusQuad;
    static Material[]? _statusMats;
    static readonly Color[] _statusColors = {
        new(0.30f,0.90f,0.20f),  // poison  green
        new(1.00f,0.50f,0.10f),  // burn    orange
        new(0.55f,0.75f,1.00f),  // slow    blue
        new(0.65f,0.95f,1.00f),  // freeze  cyan
        new(0.65f,0.30f,0.95f),  // fear    purple
        new(1.00f,0.90f,0.20f),  // confuse yellow
        new(1.00f,0.55f,0.85f),  // charm   pink
        new(0.85f,0.12f,0.12f),  // bleed   dark red
    };
    // Candidate animation names per status bit (first that exists in the anm2 wins).
    static readonly string[][] _statusAnimCandidates = {
        new[]{"Poison","poison","Sad"},
        new[]{"Burn","Burning","Fire","fire","Burnt"},
        new[]{"Slow","Slowing","slow","Slowed"},
        new[]{"Freeze","Frozen","Ice","freeze","Freezing"},
        new[]{"Fear","Fright","Feared","fear","Scared"},
        new[]{"Confusion","Confuse","Confused","confusion"},
        new[]{"Charm","Charmed","Love","charm"},
        new[]{"Bleed","Bleeding","bleed","BleedingOut"},
    };
    static string?[]? _statusAnimResolved;   // resolved anim name per bit (null = colour fallback)
    static bool _statusAnimsLogged;
    static int  _statusTick;

    static void DrawStatusMarkers(int status, Vec3 feet, float spriteH, SpriteRenderer sprites, float pxToM)
    {
        if (status == 0) return;
        _statusQuad ??= Mesh.Quad;
        if (_statusMats == null)
        {
            _statusMats = new Material[8];
            for (int i=0;i<8;i++)
            { var m = Default.MaterialUnlit.Copy(); m[MatParamName.ColorTint]=_statusColors[i]; m.FaceCull=Cull.None; _statusMats[i]=m; }
        }
        if (!_statusAnimsLogged)
        {
            _statusAnimsLogged = true;
            foreach (var p in _statusAnm2Paths)
            { var a = sprites.ListAnims(p); if (a.Count > 0 && !a[0].StartsWith("<")) { _statusAnm2 = p; break; } }
            Log.Info($"[Diorama] statuseffects anm2='{_statusAnm2}' anims: " + string.Join(", ", sprites.ListAnims(_statusAnm2)));
            _statusAnimResolved = new string?[8];
            for (int i=0;i<8;i++)
                foreach (var cand in _statusAnimCandidates[i])
                    if (sprites.HasAnim(_statusAnm2, cand)) { _statusAnimResolved[i] = cand; break; }
        }

        int n=0; for (int i=0;i<8;i++) if ((status&(1<<i))!=0) n++;
        if (n==0) return;
        const float step=0.026f;
        float y  = feet.y + spriteH + 0.09f;
        float x0 = feet.x - (n-1)*step*0.5f;
        int frame = (_statusTick/4);
        int k=0;
        for (int i=0;i<8;i++) if ((status&(1<<i))!=0)
        {
            var mp = new Vec3(x0 + k*step, y, feet.z);
            string? anim = _statusAnimResolved?[i];
            bool drew = false;
            if (anim != null)
                drew = sprites.DrawEntity(_statusAnm2, anim, frame, mp, SpriteFacing, 0f,
                                          pxToM*1.2f, 1f, false, Color.White);
            if (!drew)   // colour fallback
                _statusQuad.Draw(_statusMats[i], Matrix.TRS(mp, SpriteFacing, new Vec3(0.018f,0.018f,0.018f)));
            k++;
        }
    }

    static void DrawGrid(float w, float d)
    {
        Color col=new(0.25f,0.25f,0.30f); float y=FloorCenter.y+0.003f;
        Lines.Add(new Vec3(FloorCenter.x-w/2,y,FloorCenter.z), new Vec3(FloorCenter.x+w/2,y,FloorCenter.z), col, 0.002f);
        Lines.Add(new Vec3(FloorCenter.x,y,FloorCenter.z-d/2), new Vec3(FloorCenter.x,y,FloorCenter.z+d/2), col, 0.002f);
    }

    // Debug overlay, placed well above the HUD panel so it doesn't sit on top of it.
    static void DrawHud(string text) =>
        Text.Add(text, Matrix.TRS(new Vec3(0f, 0.20f, -0.66f), Quat.FromAngles(0,180,0), 0.40f), TextAlign.Center);
}

internal static class LinqSafe
{
    public static string? FirstOrDefaultSafe(this System.Collections.Generic.IEnumerable<string> src)
    { foreach (var s in src) return s; return null; }
}
