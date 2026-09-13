using System;
using System.Collections.Generic;
using System.IO;
using StereoKit;

namespace IsaacDiorama;

/// <summary>
/// Renders text with the game's OWN bitmap fonts (binary BMFont v3 `.fnt` + page PNG),
/// e.g. `luaminioutlined` (the pixelised HUD counter font), `upheaval` (item-name streak),
/// `pftempestasevencondensed` (item descriptions). StereoKit's Font loads TTF/OTF only, so
/// these bitmap fonts are drawn as textured glyph quads instead — the same trick the shop
/// price bitfont uses.
///
/// IMPORTANT — panel frame: every HUD/popup text plane faces the viewer via a 180° yaw, which
/// mirrors local x (that is why the HUD helpers negate x). So Draw() lays glyphs out in VIEWER
/// space (+x = right) and negates x per glyph, and each glyph quad has its U swapped, exactly
/// like `HudRenderer.CropQuad`. Call Draw() INSIDE a Hierarchy frame carrying that 180-yaw
/// (the HUD panel push, or a popup's `TRS(pos, yaw180)` push).
/// </summary>
public sealed class BmFont
{
    public struct Glyph { public int X, Y, W, H, XOff, YOff, XAdv; }

    public bool  Loaded { get; private set; }
    public int   LineHeightPx { get; private set; }
    public int   BasePx { get; private set; }
    private int  _pageW, _pageH;
    private readonly Dictionary<int, Glyph> _glyphs = new();
    private readonly Dictionary<int, Mesh>  _meshes = new();   // per-glyph unit quad, cached
    private Material? _mat;

    public enum Align { Left, Center }

    /// <summary>Parse `font/&lt;name&gt;.fnt` (binary BMFont v3) and load its page texture.</summary>
    public static BmFont Load(SpriteRenderer sprites, string fntRel)
    {
        var f = new BmFont();
        try { f.ParseAndLoad(sprites, fntRel); }
        catch { f.Loaded = false; }
        return f;
    }

    private void ParseAndLoad(SpriteRenderer sprites, string fntRel)
    {
        string? abs = sprites.FindFile(fntRel);
        if (abs == null) return;
        byte[] b = File.ReadAllBytes(abs);
        if (b.Length < 4 || b[0] != (byte)'B' || b[1] != (byte)'M' || b[2] != (byte)'F') return;

        string pageName = "";
        int o = 4;
        while (o + 5 <= b.Length)
        {
            int  type = b[o];
            int  size = BitConverter.ToInt32(b, o + 1);
            int  p    = o + 5;
            if (size < 0 || p + size > b.Length) break;
            switch (type)
            {
                case 2: // common
                    LineHeightPx = BitConverter.ToUInt16(b, p + 0);
                    BasePx       = BitConverter.ToUInt16(b, p + 2);
                    _pageW       = BitConverter.ToUInt16(b, p + 4);
                    _pageH       = BitConverter.ToUInt16(b, p + 6);
                    break;
                case 3: // pages — one or more null-terminated names; take the first
                {
                    int end = Array.IndexOf(b, (byte)0, p, size);
                    if (end < 0) end = p + size;
                    pageName = System.Text.Encoding.ASCII.GetString(b, p, end - p);
                    break;
                }
                case 4: // chars — 20 bytes each
                    for (int c = 0; c + 20 <= size; c += 20)
                    {
                        int q  = p + c;
                        int id = BitConverter.ToInt32(b, q + 0);
                        _glyphs[id] = new Glyph {
                            X    = BitConverter.ToUInt16(b, q + 4),
                            Y    = BitConverter.ToUInt16(b, q + 6),
                            W    = BitConverter.ToUInt16(b, q + 8),
                            H    = BitConverter.ToUInt16(b, q + 10),
                            XOff = BitConverter.ToInt16 (b, q + 12),
                            YOff = BitConverter.ToInt16 (b, q + 14),
                            XAdv = BitConverter.ToInt16 (b, q + 16),
                        };
                    }
                    break;
                // type 1 (info) and 5 (kerning) ignored — kerning is negligible here.
            }
            o = p + size;
        }
        if (LineHeightPx <= 0 || _pageW <= 0 || _glyphs.Count == 0 || pageName.Length == 0) return;

        // Page lives beside the .fnt (both under resources/font/). Windows FS is case-insensitive,
        // so the .fnt's embedded CamelCase page name resolves to the lowercase file on disk; try a
        // lowercase fallback anyway for safety.
        string dir = fntRel.Contains('/') ? fntRel[..(fntRel.LastIndexOf('/') + 1)] : "";
        Tex? page = sprites.LoadGfxTexture(dir + pageName)
                 ?? sprites.LoadGfxTexture(dir + pageName.ToLowerInvariant());
        if (page == null) return;

        _mat = Default.MaterialUnlit.Copy();
        _mat.Transparency = Transparency.Blend;
        _mat.DepthWrite   = false;
        _mat.FaceCull     = Cull.None;
        _mat[MatParamName.DiffuseTex] = page;
        Loaded = true;
    }

    // Width of a string in metres at the given line-height (metres).
    public float Measure(string s, float lineHeightM)
    {
        if (!Loaded || string.IsNullOrEmpty(s)) return 0f;
        float k = lineHeightM / LineHeightPx;
        float w = 0f;
        foreach (char ch in s) if (_glyphs.TryGetValue(ch, out var g)) w += g.XAdv * k;
        return w;
    }

    private Mesh GlyphMesh(int id, in Glyph g)
    {
        if (_meshes.TryGetValue(id, out var m)) return m;
        float u0 = (float)g.X / _pageW, v0 = (float)g.Y / _pageH;
        float u1 = (float)(g.X + g.W) / _pageW, v1 = (float)(g.Y + g.H) / _pageH;
        // U swapped (left verts get u1, right u0) to pre-cancel the 180-yaw mirror. V normal.
        Color32 w = new(255, 255, 255, 255);
        m = new Mesh();
        m.SetVerts(new Vertex[]{
            new(new Vec3(-0.5f,-0.5f,0), new Vec3(0,0,1), new Vec2(u1,v1), w),
            new(new Vec3( 0.5f,-0.5f,0), new Vec3(0,0,1), new Vec2(u0,v1), w),
            new(new Vec3( 0.5f, 0.5f,0), new Vec3(0,0,1), new Vec2(u0,v0), w),
            new(new Vec3(-0.5f, 0.5f,0), new Vec3(0,0,1), new Vec2(u1,v0), w),
        });
        m.SetInds(new uint[]{0,1,2,0,2,3});
        _meshes[id] = m;
        return m;
    }

    /// <summary>
    /// Draw <paramref name="s"/> with its vertical CENTRE at <paramref name="cy"/> and its
    /// horizontal anchor at <paramref name="cx"/> (viewer-space x, +x = right). Call inside a
    /// 180-yaw Hierarchy frame (x is negated here to un-mirror it). lineHeightM = the line
    /// height in metres. z &lt; 0 = toward the viewer.
    /// </summary>
    public void Draw(string s, float cx, float cy, float z, float lineHeightM, Color col, Align align = Align.Center)
    {
        if (!Loaded || string.IsNullOrEmpty(s) || _mat == null) return;
        Material mat = _mat;
        float k = lineHeightM / LineHeightPx;
        float penX = align == Align.Center ? cx - Measure(s, lineHeightM) * 0.5f : cx;
        float topY = cy + (LineHeightPx * k) * 0.5f;          // top of the line (y-up)
        foreach (char ch in s)
        {
            if (!_glyphs.TryGetValue(ch, out var g)) continue;
            if (g.W > 0 && g.H > 0)
            {
                float gw = g.W * k, gh = g.H * k;
                float glyphCx = penX + (g.XOff + g.W * 0.5f) * k;    // viewer-space centre x
                float glyphCy = topY - (g.YOff + g.H * 0.5f) * k;    // viewer-space centre y
                GlyphMesh(ch, g).Draw(mat, Matrix.TS(new Vec3(-glyphCx, glyphCy, z), new Vec3(gw, gh, 1f)), col);
            }
            penX += g.XAdv * k;
        }
    }
}
