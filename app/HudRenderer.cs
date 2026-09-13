using System;
using System.Collections.Generic;
using StereoKit;

namespace IsaacDiorama;

/// <summary>
/// Draws the HUD as a floating flat panel above the board, rebuilt from the game
/// data in <see cref="HudData"/>. First version: coloured-quad hearts, text
/// consumables, the real active-item icon + charge bar, and a rebuilt minimap.
/// Everything is laid out in a local frame (Hierarchy) that faces the viewer.
/// </summary>
public sealed class HudRenderer
{
    private readonly SpriteRenderer _sprites;
    public HudRenderer(SpriteRenderer s) { _sprites = s; }

    // Panel placement (runtime-tunable later if needed).
    public static Vec3  Origin = new(0f, -0.110f, -0.740f);   // calibrated
    public static float PanelScale = 1.0f;
    // Vertical offset for the lower-left group (coin/bomb/key + trinkets); Tab->HudLowerY.
    // Negative = down. Nudged down slightly from the first pass.
    public static float LowerGroupY = -0.020f;

    private readonly Mesh _quad = Mesh.Quad;
    private readonly Dictionary<int,Material>  _colMats = new();
    private readonly Dictionary<string,Material> _iconMats = new();
    private TextStyle _style, _styleSmall; private bool _styleReady;

    // The game's OWN fonts (see BmFont). _fontNum = luaminioutlined (pixelised HUD counters);
    // _fontName = upheaval (item-name streak); _fontDesc = pftempestasevencondensed (descriptions,
    // pocket-item names). Each falls back to the StereoKit default style if it fails to load.
    private BmFont? _fontNum, _fontName, _fontDesc; private bool _fontsTried;
    // Line heights (metres) for each font — starting values, easy to tune.
    private const float NumH = 0.028f, NameH = 0.034f, DescH = 0.022f;
    private void EnsureFonts()
    {
        if (_fontsTried) return;
        _fontsTried = true;
        _fontNum  = BmFont.Load(_sprites, "font/luaminioutlined.fnt");
        _fontName = BmFont.Load(_sprites, "font/upheaval.fnt");
        _fontDesc = BmFont.Load(_sprites, "font/pftempestasevencondensed.fnt");
    }

    // Panel-frame text (inside the 180-yaw push): a pixelised counter number, centred at (x,y).
    private void NumText(float x, float y, string s)
    {
        if (_fontNum != null && _fontNum.Loaded) _fontNum.Draw(s, x, y, -0.002f, NumH, Color.White);
        else LabelSmall(x, y, s);
    }
    // Panel-frame small readable text (pocket-item names), centred at (x,y).
    private void DescText(float x, float y, string s)
    {
        if (_fontDesc != null && _fontDesc.Loaded) _fontDesc.Draw(s, x, y, -0.002f, DescH, new Color(0.92f,0.94f,1f));
        else LabelSmall(x, y, s);
    }

    // The inky streak drawn BEHIND the popup text, in the CURRENT (pushed yaw) frame.
    private void DrawStreak()
    {
        if (_streakMat == null)
        {
            var tex = _sprites.LoadGfxTexture(StreakSheet);
            if (tex == null) return;                       // retry next frame (LoadGfxTexture caches)
            _streakMat = Default.MaterialUnlit.Copy();
            _streakMat.Transparency = Transparency.Blend;
            _streakMat.DepthWrite   = false;
            _streakMat.FaceCull     = Cull.None;
            // Render EARLIER than the text (which is at the default queue offset 0). With neither
            // writing depth, this painter's-order offset guarantees the text paints OVER the streak,
            // instead of the transparent distance-sort occasionally drawing the streak on top.
            _streakMat.QueueOffset  = -20;
            _streakMat[MatParamName.DiffuseTex] = tex;
        }
        _quad.Draw(_streakMat, Matrix.TS(StreakPos, StreakScale));
    }

    // The dark HUD backing square — drawn behind the panel contents. Uses an early queue offset
    // (like the streak) so the transparent distance-sort can't draw it in front of the text.
    private void DrawBacking()
    {
        if (_panelMat == null)
        {
            _panelMat = Default.MaterialUnlit.Copy();
            _panelMat[MatParamName.ColorTint] = new Color(0f,0f,0f,0.72f);   // darker than the old 0.45
            _panelMat.Transparency = Transparency.Blend;
            _panelMat.DepthWrite   = false;
            _panelMat.FaceCull     = Cull.None;
            _panelMat.QueueOffset  = -20;
        }
        _quad.Draw(_panelMat, Matrix.TS(new Vec3(0f, 0f, 0.006f), new Vec3(0.70f, 0.22f, 1f)));
    }

    private Material Col(Color c)
    {
        int k = ((int)(c.r*255)<<24) ^ ((int)(c.g*255)<<16) ^ ((int)(c.b*255)<<8) ^ (int)(c.a*255);
        if (_colMats.TryGetValue(k, out var m)) return m;
        m = Default.MaterialUnlit.Copy();
        m[MatParamName.ColorTint] = c;
        m.FaceCull = Cull.None;
        if (c.a < 0.999f) { m.Transparency = Transparency.Blend; m.DepthWrite = false; }
        _colMats[k] = m;
        return m;
    }

    // ---- Real HUD sprite crops (hearts, pickup counters) from the game's UI sheets ----
    private readonly Dictionary<string,Mesh> _cropMesh = new();

    // A unit quad whose UVs select the (cx,cy,cw,ch) crop of a sheetW x sheetH sheet.
    private Mesh CropQuad(int sheetW, int sheetH, int cx, int cy, int cw, int ch)
    {
        string k = $"{sheetW}x{sheetH}:{cx},{cy},{cw},{ch}";
        if (_cropMesh.TryGetValue(k, out var m)) return m;
        float u0=(float)cx/sheetW, v0=(float)cy/sheetH, u1=(float)(cx+cw)/sheetW, v1=(float)(cy+ch)/sheetH;
        // U is swapped (left verts get u1, right get u0) to pre-cancel the panel's 180-yaw
        // horizontal flip, so sprites read the right way round (not mirrored).
        Color32 w = new(255,255,255,255);
        var q = new Mesh();
        q.SetVerts(new Vertex[]{
            new(new Vec3(-0.5f,-0.5f,0), new Vec3(0,0,1), new Vec2(u1,v1), w),
            new(new Vec3( 0.5f,-0.5f,0), new Vec3(0,0,1), new Vec2(u0,v1), w),
            new(new Vec3( 0.5f, 0.5f,0), new Vec3(0,0,1), new Vec2(u0,v0), w),
            new(new Vec3(-0.5f, 0.5f,0), new Vec3(0,0,1), new Vec2(u1,v0), w),
        });
        q.SetInds(new uint[]{0,1,2,0,2,3});
        _cropMesh[k] = q;
        return q;
    }

    // Draw one 16x16-ish crop of a gfx/ sheet at HUD centre (x,y), square size `size`.
    // (x is negated like Rect() to match the 180-yaw panel frame.) z<0 = toward viewer.
    private void DrawCrop(string sheetRel, int sheetW, int sheetH, int cx, int cy, int cw, int ch,
                          float x, float y, float size, float z = 0f)
    {
        var tex = _sprites.LoadGfxTexture(sheetRel);
        if (tex == null) return;
        if (!_iconMats.TryGetValue(sheetRel, out var mat))
        { mat = Default.MaterialUnlitClip.Copy(); mat.FaceCull = Cull.None; _iconMats[sheetRel] = mat; }
        mat[MatParamName.DiffuseTex] = tex;
        CropQuad(sheetW, sheetH, cx, cy, cw, ch)
            .Draw(mat, Matrix.TS(new Vec3(-x, y, z), new Vec3(size, size, 1f)));
    }

    // NOTE: the panel faces the viewer via a 180 yaw, which flips local x. We lay
    // everything out in VIEWER space (x+ = right) and negate x here so it isn't
    // mirrored. Text glyphs still read correctly (the 180 yaw handles that).
    private void Rect(float x, float y, float w, float h, Color c, float z = 0f)
        => _quad.Draw(Col(c), Matrix.TS(new Vec3(-x, y, z), new Vec3(w, h, 1f)));

    private void Label(float x, float y, string s)
        => Text.Add(s, Matrix.TS(new Vec3(-x, y, -0.002f), 1f), _style,
                    TextAlign.Center, TextAlign.Center);

    private void LabelSmall(float x, float y, string s)
        => Text.Add(s, Matrix.TS(new Vec3(-x, y, -0.002f), 1f), _styleSmall,
                    TextAlign.Center, TextAlign.Center);

    public void Draw(HudData? hud)
    {
        if (hud == null || !hud.Valid) return;
        EnsureStyles();
        EnsureFonts();

        Hierarchy.Push(Matrix.TRS(Origin, Quat.FromAngles(0,180,0), PanelScale));
        try
        {
            DrawBacking();   // dark backing square, forced behind the contents (see DrawBacking)
            // Curse visuals: Curse of the Unknown (bit 8) hides the health bar, Curse of
            // the Lost (bit 4) hides the minimap — matching what the game itself hides.
            bool curseUnknown = (hud.Curses & 8) != 0;
            bool curseLost    = (hud.Curses & 4) != 0;
            // Left group, in order: Active item > vertical charge > Health.
            DrawActive(hud, -0.30f, 0.04f);        // icon at -0.30, charge just right
            if (!curseUnknown) DrawHearts(hud, -0.205f, 0.066f);   // hearts start right of the charge
            DrawTrinkets(hud, -0.30f, -0.08f + LowerGroupY);      // trinkets, bottom-left (extra -0.02 below the pickups)
            DrawConsumables(hud, -0.325f, 0.010f + LowerGroupY);  // coin/bomb/key, left column between active item & trinkets
            if (!curseLost) DrawMinimap(hud, 0.33f, 0.0f);        // far right
            DrawPockets(hud, 0.33f, -0.075f);      // held cards/runes/pills, by name
        }
        finally { Hierarchy.Pop(); }
    }

    // Boss HP bar + pill popup live IN the diorama (world space), near the board's
    // front/bottom-door edge and pushed toward the viewer — not on the HUD panel.
    // Billboards face +z (the viewer), so no mirror/negation like the panel needs.
    public static float BossBarY   = 0.02f;   // height above the floor plane
    public static float BossBarFwd = 0.06f;   // toward the viewer from the front edge
    public static float PopupY     = 0.16f;   // height above the floor plane
    public static float PopupFwd   = 0.02f;   // toward the viewer from the front edge
    // Popup depth nudge (Tab->PopupZ): added to the popup's z. Negative = away from the viewer,
    // toward the centre of the diorama. Baked from VR.
    public static float PopupZ     = -0.190f;
    // The inky "streak" sprite drawn BEHIND the popup text (replaces the old black bar).
    // effect_024_streak.png is 400x64, near-black with feathered ends (alpha-shaped). Scale =
    // world size of the quad; Pos = offset in the popup's local (viewer-facing) frame, +z = behind
    // the text. All six are Tab-tunable (StreakSX/SY/SZ, StreakPX/PY/PZ).
    private const string StreakSheet = "ui/effect_024_streak.png";
    public static Vec3  StreakScale = new(0.50f, 0.11f, 1f);
    public static Vec3  StreakPos   = new(0f, 0f, 0.004f);
    private Material? _streakMat;
    private Material? _panelMat;   // the HUD backing square (dark, drawn behind everything)

    public void DrawWorld(HudData? hud, Vec3 floorCenter, float floorW, float floorD)
    {
        if (hud == null || !hud.Valid) return;
        EnsureStyles();
        EnsureFonts();
        float frontZ = floorCenter.z + floorD * 0.5f;   // near edge (bottom doors)

        if (hud.HasBoss)
        {
            float w = Math.Min(0.34f, floorW * 0.9f), hgt = 0.018f;
            var c = new Vec3(floorCenter.x, floorCenter.y + BossBarY, frontZ + BossBarFwd);
            _quad.Draw(Col(new Color(0f,0f,0f,0.65f)),
                       Matrix.TS(c + new Vec3(0,0,-0.001f), new Vec3(w+0.006f, hgt+0.006f, 1f)));
            float f = Math.Clamp(hud.BossFill, 0f, 1f);
            if (f > 0f)
                _quad.Draw(Col(new Color(0.82f,0.12f,0.12f)),
                           Matrix.TS(c + new Vec3(-w*0.5f + w*f*0.5f, 0, 0), new Vec3(w*f, hgt, 1f)));
        }

        if (!string.IsNullOrEmpty(hud.Popup))
        {
            var yaw = Quat.FromAngles(0,180,0);   // face the viewer; glyphs read correctly
            // PopupZ nudges the whole element in depth (negative = toward the diorama centre).
            var c = new Vec3(floorCenter.x, floorCenter.y + PopupY, frontZ + PopupFwd + PopupZ);
            string popup = hud.Popup;
            // Use the game's fonts when both loaded (name = upheaval, desc = pftempesta).
            bool fontsOk = _fontName != null && _fontName.Loaded && _fontDesc != null && _fontDesc.Loaded;
            int nl = popup.IndexOf('\n');

            // Everything lives in one pushed yaw frame: the inky streak behind, then the text.
            Hierarchy.Push(Matrix.TRS(c, yaw, 1f));
            DrawStreak();
            if (nl < 0)
            {
                // Single line (pill effect).
                if (fontsOk) _fontName!.Draw(popup, 0f, 0f, -0.001f, NameH, Color.White);
                else Text.Add(popup, Matrix.TS(new Vec3(0,0,-0.001f), 1f), _style, TextAlign.Center, TextAlign.Center);
            }
            else
            {
                // Name on top, effect/description below.
                string name = popup.Substring(0, nl);
                string desc = popup.Substring(nl + 1);
                string[] descLinesArr = desc.Split('\n');
                const float nameH = 0.034f, descH = 0.022f, pad = 0.010f;
                float blockH = pad*2 + nameH + descLinesArr.Length*descH;
                float nameLocalY = blockH*0.5f - pad - nameH*0.5f;
                if (fontsOk)
                {
                    _fontName!.Draw(name, 0f, nameLocalY, -0.001f, NameH, Color.White);
                    float descCy = nameLocalY - nameH*0.5f - descH*0.5f;
                    var descCol = new Color(0.92f,0.94f,1f);
                    foreach (var line in descLinesArr)
                    { _fontDesc!.Draw(line, 0f, descCy, -0.001f, DescH, descCol); descCy -= descH; }
                }
                else
                {
                    Text.Add(name, Matrix.TS(new Vec3(0, nameLocalY, -0.001f), 1f),
                             _style, TextAlign.Center, TextAlign.Center);
                    float descTop = nameLocalY - nameH*0.5f - 0.002f;
                    Text.Add(desc, Matrix.TS(new Vec3(0, descTop, -0.001f), 1f),
                             _styleSmall, TextAlign.TopCenter, TextAlign.Center);
                }
            }
            Hierarchy.Pop();
        }
    }

    private void EnsureStyles()
    {
        if (_styleReady) return;
        _styleReady = true;
        _style = Text.MakeStyle(Default.Font, 0.028f, Color.White);
        _styleSmall = Text.MakeStyle(Default.Font, 0.018f, new Color(0.92f,0.94f,1f));
    }

    // Real heart sprites from ui_hearts.png (16x16 tiles). Containers grow rightward
    // from x0, wrapping to a new row. MaxHearts/RedHearts/SoulHearts are HALF-heart
    // counts; BoneHearts is a container count; BlackMask bit i = soul pair i is black.
    // Crops verified against the game's own ui_hearts.anm2 (112x64 sheet, 16px tiles):
    //   RedFull(0,0) RedHalf(16,0) Empty(32,0) WhiteHalf(48,0) WhiteOverlay(64,0)
    //   BlueFull(0,16) BlueHalf(16,16) BlackFull(32,16) BlackHalf(48,16) GoldOverlay(64,16)
    //   BoneFull(64,32) BoneHalf(80,32) BoneEmpty(96,32).
    // z<0 = toward the viewer (in front). Layered cells (bg + fill + overlay) MUST use
    // distinct z or the clip material's depth write makes the later draws z-fight and
    // vanish — that was the "red hearts stay empty" bug (soul hearts, a single draw, were fine).
    private const string HeartsSheet = "ui/ui_hearts.png";
    private readonly List<Vec2> _heartCenters = new();   // reused each frame (no per-frame alloc)
    private void DrawHearts(HudData h, float x0, float yTop)
    {
        const int SW = 112, SH = 64;
        const float s = 0.026f, gap = 0.001f, rowH = 0.030f, maxX = 0.03f;
        const float zBg = 0f, zFill = -0.002f, zOverlay = -0.004f;
        float x = x0, y = yTop;
        _heartCenters.Clear();
        int lastRedIdx = -1;                                 // index (into _heartCenters) of the last red container
        void Cell(int cx, int cy, float z) => DrawCrop(HeartsSheet, SW, SH, cx, cy, 16, 16, x + s*0.5f, y, s, z);
        void Overlay(int idx, int cx, int cy) => DrawCrop(HeartsSheet, SW, SH, cx, cy, 16, 16, _heartCenters[idx].x, _heartCenters[idx].y, s, zOverlay);
        void Mark(bool red) { _heartCenters.Add(new Vec2(x + s*0.5f, y)); if (red) lastRedIdx = _heartCenters.Count - 1; }
        void Adv() { x += s + gap; if (x > maxX) { x = x0; y -= rowH; } }

        int redC = (h.MaxHearts + 1) / 2;                       // red containers (MaxHearts is halves)
        // GetHearts() includes red HP stored INSIDE bone hearts, and GetMaxHearts() does not
        // count bone capacity — so any red HP beyond the red containers is what fills the bones.
        // (Model: red containers fill left-to-right first, then bone hearts.)
        int boneRedHalves = Math.Max(0, h.RedHearts - h.MaxHearts);

        for (int i = 0; i < redC; i++)
        {
            int fh = Math.Clamp(h.RedHearts - 2*i, 0, 2);
            Cell(32,0, zBg);                                     // EmptyHeart container background
            if (fh == 2) Cell(0,0, zFill); else if (fh == 1) Cell(16,0, zFill);  // RedFull / RedHalf
            Mark(true); Adv();
        }
        // Bone hearts: the cage sprite already encodes fullness (full/half/empty).
        for (int i = 0; i < h.BoneHearts; i++)
        {
            int bfh = Math.Clamp(boneRedHalves - 2*i, 0, 2);
            int cx = bfh == 2 ? 64 : (bfh == 1 ? 80 : 96);       // BoneFull / BoneHalf / BoneEmpty
            Cell(cx,32, zBg);
            Mark(false); Adv();
        }
        int soulPairs = (h.SoulHearts + 1) / 2;
        for (int i = 0; i < soulPairs; i++)
        {
            int fh = Math.Clamp(h.SoulHearts - 2*i, 0, 2);
            bool black = (h.BlackMask & (1 << i)) != 0;
            int fullX = black ? 32 : 0, halfX = black ? 48 : 16;  // Black/Blue Full/Half on row y=16
            if (fh == 2) Cell(fullX,16, zFill); else if (fh == 1) Cell(halfX,16, zFill);
            Mark(false); Adv();
        }

        // Eternal heart (white half): overlaid on the LAST red container. With no red
        // containers it stands alone as an empty container carrying the white half.
        if (h.EternalHearts > 0)
        {
            if (lastRedIdx >= 0) Overlay(lastRedIdx, 48, 0);     // WhiteHeartHalf over last red
            else { Cell(32,0, zBg); Cell(48,0, zFill); Mark(false); Adv(); }
        }
        // Golden hearts: a gold shell over the RIGHTMOST hearts of the bar (any type),
        // not new containers — one gold heart shells the last heart, two shell the last two, ...
        int gold = Math.Min(h.GoldenHearts, _heartCenters.Count);
        for (int i = 0; i < gold; i++) Overlay(_heartCenters.Count - 1 - i, 64, 16);   // GoldHeartOverlay
    }

    // Vertical stack coin/bomb/key, left-aligned in the left column, sitting between the
    // active item (above) and the trinkets (below). Icon then amount.
    // Crops verified against hudpickups.png (128x128): coin(0,0), key(16,0), bomb(0,16).
    private const string PickupsSheet = "ui/hudpickups.png";
    private void DrawConsumables(HudData h, float x0, float yTop)
    {
        const int SW = 128, SH = 128; const float ic = 0.020f, rowH = 0.026f;
        void Row(float y, int cx, int cy, int n)
        {
            DrawCrop(PickupsSheet, SW, SH, cx, cy, 16, 16, x0 + ic*0.5f, y, ic);  // icon, left-aligned
            NumText(x0 + ic + 0.014f, y, n.ToString());                          // count (pixel font) to its right
        }
        Row(yTop,          0, 0,  h.Coins);   // coin (penny)
        Row(yTop - rowH,   0,16,  h.Bombs);   // bomb
        Row(yTop - 2*rowH,16, 0,  h.Keys);    // key
    }

    // Active item icon, with a VERTICAL charge bar just to its right.
    private void DrawActive(HudData h, float xIcon, float y)
    {
        if (h.ActiveId <= 0) return;
        const float icon = 0.060f;
        var tex = string.IsNullOrEmpty(h.ActiveIcon) ? null : _sprites.LoadGfxTexture(h.ActiveIcon);
        if (tex != null)
        {
            if (!_iconMats.TryGetValue(h.ActiveIcon, out var mat))
            { mat = Default.MaterialUnlitClip.Copy(); mat.FaceCull = Cull.None; _iconMats[h.ActiveIcon] = mat; }
            mat[MatParamName.DiffuseTex] = tex;
            _quad.Draw(mat, Matrix.TS(new Vec3(-xIcon, y, 0f), new Vec3(icon, icon, 1f)));
        }
        else Rect(xIcon, y, icon, icon, new Color(0.3f,0.3f,0.35f));

        // Vertical charge bar to the right of the icon, filling bottom-up.
        if (h.MaxCharge > 0)
        {
            float bx = xIcon + icon*0.5f + 0.014f, bh = icon, bw = 0.014f;
            float frac = Math.Clamp(h.Charge / (float)h.MaxCharge, 0f, 1f);
            Rect(bx, y, bw, bh, new Color(0.1f,0.1f,0.1f,0.85f));
            float fillH = bh * frac;
            Rect(bx, y - bh*0.5f + fillH*0.5f, bw*0.7f, fillH, new Color(0.35f,0.85f,1.0f));
        }
    }

    private void DrawIcon(string path, float x, float y, float size)
    {
        if (string.IsNullOrEmpty(path)) return;
        var tex = _sprites.LoadGfxTexture(path);
        if (tex == null) return;
        if (!_iconMats.TryGetValue(path, out var mat))
        { mat = Default.MaterialUnlitClip.Copy(); mat.FaceCull = Cull.None; _iconMats[path] = mat; }
        mat[MatParamName.DiffuseTex] = tex;
        _quad.Draw(mat, Matrix.TS(new Vec3(-x, y, 0f), new Vec3(size, size, 1f)));
    }

    // Trinkets (2 slots, real icons).
    private void DrawTrinkets(HudData h, float x0, float y)
    {
        const float size = 0.040f, step = 0.046f;
        DrawIcon(h.TrinketIcon0, x0,        y, size);
        DrawIcon(h.TrinketIcon1, x0 + step, y, size);
    }

    // Held pocket items (cards / runes / pills): names only, right-anchored under
    // the minimap, stacked upward. (Icons are parked for a later pass.)
    private void DrawPockets(HudData h, float xRight, float yBottom)
    {
        if (h.Pockets.Count == 0) return;
        float y = yBottom;
        foreach (var p in h.Pockets)
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            DescText(xRight - 0.06f, y, p.Name);
            y += 0.028f;
        }
    }

    // The grid cells a room occupies, relative to its top-left anchor GridIndex,
    // keyed by Isaac RoomShape. Large + L rooms span several cells; the L shapes
    // are a 2x2 block with one corner missing.
    private static (int dx, int dy)[] ShapeCells(int shape) => shape switch
    {
        4 or 5  => new[] { (0,0), (0,1) },                  // 1x2 / IIV  (tall)
        6 or 7  => new[] { (0,0), (1,0) },                  // 2x1 / IIH  (wide)
        8       => new[] { (0,0), (1,0), (0,1), (1,1) },    // 2x2
        9       => new[] { (1,0), (0,1), (1,1) },           // LTL (missing top-left)
        10      => new[] { (0,0), (0,1), (1,1) },           // LTR (missing top-right)
        11      => new[] { (0,0), (1,0), (1,1) },           // LBL (missing bottom-left)
        12      => new[] { (0,0), (1,0), (0,1) },           // LBR (missing bottom-right)
        _       => new[] { (0,0) },                         // 1x1 / IH / IV
    };

    private void DrawMinimap(HudData h, float xRight, float yc)
    {
        if (h.Rooms.Count == 0) return;
        int minx=999, miny=999, maxx=-999, maxy=-999;
        foreach (var r in h.Rooms)
        {
            int gx=r.GridIndex%13, gy=r.GridIndex/13;
            foreach (var (dx,dy) in ShapeCells(r.Shape))
            {
                int x=gx+dx, y=gy+dy;
                if (x<minx)minx=x; if (x>maxx)maxx=x; if (y<miny)miny=y; if (y>maxy)maxy=y;
            }
        }
        if (maxx < minx) return;
        const float cell = 0.011f, gap = 0.0015f, step = cell + gap;
        float wSpan = (maxx-minx+1)*step, hSpan = (maxy-miny+1)*step;
        float ox = xRight - wSpan;                 // right-anchored
        float oy = yc + hSpan*0.5f;                 // top

        foreach (var r in h.Rooms)
        {
            if (r.Flags == 0) continue;             // undiscovered / no icon
            int gx=r.GridIndex%13, gy=r.GridIndex/13;
            var cells = ShapeCells(r.Shape);
            // CurrentRoom is the sub-cell you entered from, not the anchor — so a big
            // room counts as current if ANY of its cells is the current grid index.
            bool cur = false;
            foreach (var (dx,dy) in cells)
                if ((gy+dy)*13 + (gx+dx) == h.CurrentRoom) { cur = true; break; }
            Color c = RoomColor(r, cur, r.Visited);
            foreach (var (dx,dy) in cells)
            {
                float cx = ox + (gx+dx-minx)*step;
                float cy = oy - (gy+dy-miny)*step;
                Rect(cx, cy, cell, cell, c, -0.001f);
                if (cur)                            // outline every cell of the current room
                    Rect(cx, cy, cell+0.004f, cell+0.004f, new Color(1f,1f,1f,0.9f), 0.0005f);
            }
            // Leftover-pickup pips on the first OCCUPIED cell (the anchor may be the
            // cut-out corner of an L room), only for rooms we've been in.
            if (r.Visited && r.Contents != 0)
            {
                var (pdx,pdy) = cells[0];
                DrawContentPips(ox + (gx+pdx-minx)*step, oy - (gy+pdy-miny)*step, cell, r.Contents);
            }
        }
    }

    // Small coloured pips showing what's left in a room (keys/bombs/hearts/...),
    // mirroring the game's minimap. Content bits: 1 heart, 2 coin, 4 key, 8 bomb,
    // 16 chest, 32 collectible, 64 other.
    private static readonly (int bit, Color col)[] _pips = {
        (32, new Color(0.96f,0.96f,1.00f)),   // collectible - white
        ( 1, new Color(0.92f,0.16f,0.20f)),   // heart       - red
        ( 4, new Color(0.96f,0.86f,0.32f)),   // key         - gold
        ( 8, new Color(0.16f,0.16f,0.20f)),   // bomb        - near-black
        (16, new Color(0.76f,0.55f,0.30f)),   // chest       - brown
        ( 2, new Color(0.98f,0.83f,0.20f)),   // coin        - yellow
        (64, new Color(0.72f,0.42f,0.92f)),   // other       - purple
    };
    private void DrawContentPips(float ccx, float ccy, float cell, int contents)
    {
        float ps = cell * 0.42f, gap = ps * 0.25f, step = ps + gap;
        // Collect up to 4 present pips (priority order in _pips).
        Span<Color> show = stackalloc Color[4];
        int n = 0;
        foreach (var (bit,col) in _pips) { if (n<4 && (contents & bit)!=0) show[n++] = col; }
        if (n == 0) return;
        // Lay them out in a 2-column grid (1 row for 1-2 pips, 2x2 for 3-4) so they
        // stay within the room cell instead of overflowing a single long row.
        int cols = Math.Min(n, 2), rows = (n + 1) / 2;
        float gw = cols*ps + (cols-1)*gap, gh = rows*ps + (rows-1)*gap;
        float x0 = ccx - gw*0.5f + ps*0.5f;
        float y0 = ccy + gh*0.5f - ps*0.5f;                // top row first (world y up)
        for (int i=0;i<n;i++)
            Rect(x0 + (i%2)*step, y0 - (i/2)*step, ps, ps, show[i], -0.0015f);   // slightly in front
    }

    private static Color RoomColor(HudRoom r, bool current, bool visited)
    {
        // Special rooms keep their hue; normal rooms are grey shaded by explored
        // state. Current = bright, visited = mid, revealed-but-unvisited = dark.
        switch (r.Type)
        {
            case 5:  return Shade(new Color(0.85f,0.15f,0.15f), current, visited); // boss
            case 4:  return Shade(new Color(0.90f,0.80f,0.20f), current, visited); // treasure
            case 2:  return Shade(new Color(0.20f,0.75f,0.35f), current, visited); // shop
            case 7: case 8: return Shade(new Color(0.55f,0.30f,0.85f), current, visited); // secret / super
            case 14: return Shade(new Color(0.78f,0.14f,0.14f), current, visited); // devil
            case 15: return Shade(new Color(0.85f,0.90f,1.00f), current, visited); // angel
            case 6:  return Shade(new Color(0.90f,0.50f,0.15f), current, visited); // miniboss
            case 9:  return Shade(new Color(0.60f,0.80f,0.20f), current, visited); // arcade
            case 10: return Shade(new Color(0.50f,0.10f,0.14f), current, visited); // curse
            case 11: return Shade(new Color(0.80f,0.35f,0.10f), current, visited); // challenge
            case 12: return Shade(new Color(0.65f,0.45f,0.25f), current, visited); // library
            case 13: return Shade(new Color(0.75f,0.20f,0.22f), current, visited); // sacrifice
            case 24: return Shade(new Color(0.28f,0.45f,0.95f), current, visited); // planetarium (navy/blue)
            default:
                float g = current ? 0.90f : (visited ? 0.52f : 0.28f);
                return new Color(g, g, g);
        }
    }

    // Brighten the current room, keep visited at full accent, dim unvisited.
    private static Color Shade(Color c, bool current, bool visited)
    {
        if (current) return new Color(MathF.Min(1f, c.r*0.7f+0.3f),
                                      MathF.Min(1f, c.g*0.7f+0.3f),
                                      MathF.Min(1f, c.b*0.7f+0.3f));
        float k = visited ? 1.0f : 0.55f;
        return new Color(c.r*k, c.g*k, c.b*k);
    }
}
