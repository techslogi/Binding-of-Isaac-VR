using System;
using System.Collections.Generic;
using System.IO;
using StereoKit;

namespace IsaacDiorama;

/// <summary>
/// Turns (anm2, animation, frame) into a composited sprite billboard. Resolves
/// Isaac's resource folders, caches textures / materials / per-crop meshes, and
/// draws each visible layer back-to-front. Returns false when it can't draw so
/// the caller can fall back to a coloured quad.
/// </summary>
public sealed class SpriteRenderer
{
    private readonly List<string> _roots = new();
    public bool   Configured => _roots.Count > 0;
    public string Status { get; private set; } = "no resources configured";

    private readonly Dictionary<string,string?> _fileCache = new();
    private readonly Dictionary<string,Tex?>    _texCache  = new();
    private readonly Dictionary<Tex,Material>   _matClip  = new(); // alpha-clip: crisp solid sprites
    private readonly Dictionary<Tex,Material>   _matBlend = new(); // alpha-blend: fire/smoke (soft alpha)
    private readonly Dictionary<Tex,Material>   _matAdd   = new(); // additive: glow/light layers
    private readonly Dictionary<string,Mesh>    _meshCache = new();
    private readonly Dictionary<Tex,Tex>        _whiteCache = new(); // alpha-only copies for offset-recoloured sprites (creep)
    private bool _whitenStallLogged;                                 // one-shot: Whiten refused a readback
    private readonly Dictionary<Tex,Tex[]?>     _dogmaCache = new(); // baked TV-static variants for Dogma-shaded sheets (null = plain sheet)
    private readonly Dictionary<Tex,Tex>        _greyCache  = new(); // per-texel greyscale copies (Dogma's Tech Dot)

    public SpriteRenderer(string? gameOrResourcesDir)
    {
        if (!string.IsNullOrWhiteSpace(gameOrResourcesDir) && Directory.Exists(gameOrResourcesDir))
        {
            // Accept the game dir, the extracted_resources dir, or a resources
            // folder directly. The Resource Extractor writes loose gfx to
            // <game>/extracted_resources/resources/gfx/..., so search there first.
            string d = gameOrResourcesDir!;
            void Add(string p){ if (Directory.Exists(p) && !_roots.Contains(p)) _roots.Add(p); }
            Add(Path.Combine(d, "extracted_resources", "resources"));
            Add(Path.Combine(d, "extracted_resources", "resources-dlc3"));
            Add(Path.Combine(d, "extracted_resources"));
            Add(Path.Combine(d, "resources-dlc3"));
            Add(Path.Combine(d, "resources"));
            Add(d);

            // Report whether any root actually contains a gfx/ folder.
            string? withGfx = null;
            foreach (var r in _roots)
                if (Directory.Exists(Path.Combine(r, "gfx"))) { withGfx = r; break; }
            Status = withGfx != null
                ? $"gfx OK: ...{ShortTail(withGfx)}"
                : $"{_roots.Count} root(s) but NO gfx/ found (run Resource Extractor?)";
        }
    }

    private static string ShortTail(string p)
    {
        var parts = p.Replace('\\','/').TrimEnd('/').Split('/');
        int n = parts.Length;
        return n >= 2 ? parts[n-2] + "/" + parts[n-1] : p;
    }

    /// <summary>Locate a resource file by relative path (public, for backdrops.xml).</summary>
    public string? FindFile(string rel) => Find(rel);

    /// <summary>List filenames in a resource directory (diagnostic / asset discovery).</summary>
    public System.Collections.Generic.List<string> ListDir(string relDir)
    {
        var outp = new System.Collections.Generic.List<string>();
        relDir = relDir.Replace('\\','/').Trim('/');
        foreach (var root in _roots)
            foreach (var cand in new[]{ Path.Combine(root, relDir), Path.Combine(root, "gfx", relDir) })
                if (Directory.Exists(cand))
                {
                    try { foreach (var f in Directory.GetFiles(cand)) outp.Add(Path.GetFileName(f)); } catch { }
                    if (outp.Count > 0) return outp;
                }
        return outp;
    }

    private string? Find(string rel)
    {
        rel = rel.Replace('\\','/').TrimStart('/');
        if (_fileCache.TryGetValue(rel, out var hit)) return hit;
        string? found = null;
        foreach (var root in _roots)
        {
            foreach (var cand in new[]{ rel, "gfx/"+rel })
            {
                string p = Path.Combine(root, cand);
                if (File.Exists(p)) { found = p; break; }
            }
            if (found != null) break;
        }
        _fileCache[rel] = found;
        return found;
    }

    /// <summary>Load a texture by a gfx-relative path (for backdrop atlases).</summary>
    public Tex? LoadGfxTexture(string relFromGfx)
    {
        string? abs = Find("gfx/"+relFromGfx) ?? Find(relFromGfx);
        if (abs == null) return null;
        if (_texCache.TryGetValue(abs, out var t)) return t;
        Tex? tex = null;
        try {
            tex = Tex.FromFile(abs);
            if (tex != null) { tex.SampleMode = TexSample.Point; tex.AddressMode = TexAddress.Clamp; }
        } catch { tex = null; }
        _texCache[abs] = tex;
        return tex;
    }

    private Tex? LoadTex(string anm2Dir, string sheetRel)
    {
        // Isaac spritesheet paths are relative to the ANM2 FILE'S folder.
        string? abs = null;
        try {
            string cand = Path.GetFullPath(Path.Combine(anm2Dir, sheetRel));
            if (File.Exists(cand)) abs = cand;
        } catch { }
        // Fallbacks: relative to a gfx root, or bare.
        if (abs == null) abs = Find("gfx/"+sheetRel) ?? Find(sheetRel);
        if (abs == null) return null;

        if (_texCache.TryGetValue(abs, out var t)) return t;
        Tex? tex = null;
        try
        {
            tex = Tex.FromFile(abs);
            if (tex != null)
            {
                // Pixel art: nearest-neighbour sampling (no white-fringe bleed
                // from transparent-white texels) + clamp so atlas crops don't wrap.
                tex.SampleMode  = TexSample.Point;
                tex.AddressMode = TexAddress.Clamp;
            }
        }
        catch { tex = null; }
        _texCache[abs] = tex;
        return tex;
    }

    /// <summary>
    /// anm2 stem -> the NULL its OVERLAY animation is anchored to. Deliberately a small
    /// explicit list rather than a heuristic: most overlays (Isaac's own head) carry their
    /// real placement in their own frames and must NOT be moved, so guessing would break
    /// the common case to fix the rare one. Add an entry only for a sprite measured to
    /// author its overlay at (0,0) with the placement living in a null.
    /// </summary>
    private static string? AnchorNullFor(string anm2Rel)
    {
        string s = anm2Rel.ToLowerInvariant();
        // 880.0 Flesh Maiden: SwingLeft/SwingRight declare layer 1 (head) as an EMPTY
        // LayerAnimation and her head plays as the overlay `HeadLook` at (0,0); every bit
        // of its placement is in the body animation's `HeadPos` null.
        if (s.Contains("flesh maiden")) return "HeadPos";
        return null;
    }

    // A copy of a texture with RGB forced to white, alpha preserved. Used for creep:
    // the game recolours it via a colour OFFSET (out = texel*0 + offset), i.e. a flat
    // colour masked by the sprite's alpha. Whitening lets the multiply pipeline paint
    // that flat colour instead of the sheet's baked red. Falls back to the original
    // texture if the pixels can't be read back (creep then stays as-is, no crash).
    private Tex Whiten(Tex src)
    {
        if (_whiteCache.TryGetValue(src, out var cached)) return cached;
        try
        {
            // Tex.FromFile is ASYNC. An unloaded texture reports Width==Height==0 and
            // returns an EMPTY buffer, so the old "px.Length == Width*Height" guard read
            // 0 == 0 and passed -- we then built a 0x0 white texture and CACHED it, so
            // that sheet's creep was invisible for the rest of the run even though every
            // log line (DrawEntity=True, resolvedLayers=1) said it had drawn. Demand real
            // dimensions, a real buffer, and at least one opaque texel before caching.
            if (src.Width <= 0 || src.Height <= 0) { WhitenStall(src, null); return src; }
            var px = src.GetColorData<Color32>();   // GPU readback; only succeeds once loaded
            if (px != null && px.Length > 0 && px.Length == src.Width * src.Height)
            {
                bool anyOpaque = false;
                for (int i = 0; i < px.Length; i++)
                {
                    byte a = px[i].a;
                    if (a != 0) anyOpaque = true;
                    px[i] = new Color32(255, 255, 255, a);
                }
                if (!anyOpaque) { WhitenStall(src, px); return src; }   // not uploaded yet: don't cache
                var t = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
                t.SetColors(src.Width, src.Height, px);
                t.SampleMode = TexSample.Point; t.AddressMode = TexAddress.Clamp;
                _whiteCache[src] = t;   // cache only on success
                return t;
            }
        }
        catch { }
        return src;   // not ready / unreadable: use original this frame, retry next frame
    }

    // Whitening is what makes creep visible, so a PERMANENT stall here reads on screen as
    // "the entity simply isn't there" with nothing else in the log to say why (that cost
    // four debugging rounds on 1000.23 green creep). Say it once; a single line right
    // after startup is the texture still uploading and is expected.
    private void WhitenStall(Tex src, Color32[]? px)
    {
        if (_whitenStallLogged) return;
        _whitenStallLogged = true;
        Log.Info($"[Diorama] WHITEN STALL {src.Width}x{src.Height} " +
                 $"px={(px == null ? "null" : px.Length.ToString())} " +
                 "(texture not uploaded yet; retrying, nothing cached)");
    }

    /// <summary>
    /// A copy of a texture with each texel collapsed to its own brightness, alpha kept.
    /// Unlike <see cref="Whiten"/> (flat white, used as an alpha mask for creep) this KEEPS
    /// the sprite's internal shading: Dogma's Tech Dot is authored pure red — (254,0,0) body,
    /// (175,0,0) ring, white core — so a white TINT cannot lift it (multiplying red by white
    /// is still red) and flat-whitening it would erase the ring. max(r,g,b) turns the body
    /// white, leaves the ring a mid grey, and keeps the core white.
    /// Falls back to the original if the pixels can't be read back yet.
    /// </summary>
    private Tex Greyscale(Tex src)
    {
        if (_greyCache.TryGetValue(src, out var cached)) return cached;
        try
        {
            if (src.Width <= 0 || src.Height <= 0) return src;
            var px = src.GetColorData<Color32>();
            if (px != null && px.Length > 0 && px.Length == src.Width * src.Height)
            {
                bool anyOpaque = false;
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    if (c.a != 0) anyOpaque = true;
                    byte q = Math.Max(c.r, Math.Max(c.g, c.b));
                    px[i] = new Color32(q, q, q, c.a);
                }
                if (!anyOpaque) return src;            // not uploaded yet: don't cache
                var t = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
                t.SetColors(src.Width, src.Height, px);
                t.SampleMode = TexSample.Point; t.AddressMode = TexAddress.Clamp;
                _greyCache[src] = t;                   // cache only on success
                return t;
            }
        }
        catch { }
        return src;
    }

    // ---- Dogma "TV static" ---------------------------------------------------
    // Repentance renders Dogma and everything it spawns through a dedicated fragment
    // shader (resources/shaders/coloroffset_dogma.fs). Its spritesheets are therefore
    // DATA, not final art: they are authored almost entirely in blue and green, and
    // the shader replaces those texels with animated greyscale noise. Drawing the
    // sheet raw is exactly why the boss, its creep, its shots and its FX all came
    // through BLUE in the app.
    //
    // The shader's two rules, verbatim:
    //   r == g && b > r  (BLUE)  -> a = mix((noise + 0.5) * b, b, r / b)
    //   r == b && g > r  (GREEN) -> a = mix(step(0, noise_t) * g, g, r / g)
    //   ...then rgb = (a, a, a); alpha and every other texel are untouched.
    // So `b` (or `g`) is the brightness and `r` is how much of it is SOLID rather than
    // noisy -- pure blue is full static, a blue with a high red is nearly steady grey.
    // Black outlines match neither rule and survive, which is what makes the result
    // read as white static inside black linework.
    //
    // CHANNEL ORDER: these are stated in RGBA, and `GetColorData<Color32>()` does
    // return RGBA here -- proven by the laser work, where Technology's body crop
    // measured (254,0,0); a BGRA readback would have reported (0,0,254).
    // DO NOT "make this order-agnostic" by also accepting the mirrored pattern
    // (g == b && r > g): that pattern just means REDDISH, which ordinary art is full
    // of -- measured, ui_hearts.png hits it on 20.7% of its texels and tech_dot.png on
    // 82.5%, so both would be wrongly shaded into grey static. The blue pattern
    // (r == g && b > r) is the discriminating one: 39-62% on real Dogma sheets, 0.00%
    // on every ordinary sheet tested.
    //
    // StereoKit's stock pipeline can't run a custom fragment shader, so we bake
    // DogmaFrames noise variants per sheet and cycle them at DogmaStaticFps. The
    // game's noise is screen-space; texture-space static reads the same in VR and
    // has the advantage of staying put when the sprite moves.
    private const int   DogmaFrames       = 8;      // baked noise variants per sheet (VRAM: sheet x8)
    private const float DogmaStaticFps    = 24f;    // how fast the static churns
    // The sheet heuristic is now only a SAFETY NET -- the mod flags Dogma's entities
    // explicitly (eflags bit 128), because the game binds the shader PER ENTITY and no
    // whole-sheet test can work for a shared atlas: BulletAtlas.png measures just 14.3%
    // coded, since only DOGMA's bullets are drawn that way and the rest of the sheet is
    // ordinary projectiles. Threshold raised to 35% so it still catches the dedicated
    // Dogma sheets (dogma_fetus 54.7%, dogma_angel 62.4%, static_blood 100%) and cannot
    // reach the atlas.
    private const float DogmaCodedMinFrac = 0.35f;
    // Noise block size in TEXELS. The shader quantises gl_FragCoord to 2 SCREEN pixels,
    // but a diorama sprite is magnified hugely in VR, so 2 texels reads as coarse grain.
    // 1 = per-texel, the finest the sprite resolution allows. Tab->DogmaGrain.
    public static int DogmaNoiseBlock = 1;
    private int _bakedNoiseBlock = -1;
    // Classification needs a GPU readback, so spend at most ONE per frame: a sheet
    // draws raw for a single frame the first time it appears, instead of stalling.
    private float _classifyFrameT = -1f;
    private bool  _classifiedThisFrame;

    // Dogma Angel (950.2) arrives mid-fight on its own 864x960 sheet, and `Tex.FromFile`
    // is async — so the first frames it is on screen there is nothing to read back yet and
    // it renders raw (blue) until the upload lands and the one-per-frame classifier gets
    // to it. Kick the uploads off as soon as ANY Dogma entity is seen, and let the normal
    // budget classify them while phase 1 is still being fought.
    private static readonly string[] DogmaPrewarmSheets = {
        "bosses/repentance/dogma_fetus.png",
        "bosses/repentance/dogma_angel.png",
        "bosses/repentance/dogma_angel_baby.png",
        "bosses/repentance/dogma_fx.png",
        "effects/static_blood.png",
    };
    private bool _dogmaPrewarmed;
    private readonly List<(Tex tex, string rel)> _dogmaPrewarmQueue = new();

    private void PrewarmDogma()
    {
        if (_dogmaPrewarmed) return;
        _dogmaPrewarmed = true;
        foreach (var rel in DogmaPrewarmSheets)
        {
            var t = LoadGfxTexture(rel);                  // starts the async upload
            if (t != null) _dogmaPrewarmQueue.Add((t, rel));
        }
        Log.Info($"[Diorama] DOGMA PREWARM queued {_dogmaPrewarmQueue.Count} sheets");
    }

    /// <summary>One queued prewarm sheet per call; drops each once it has been classified.</summary>
    private void PumpDogmaPrewarm()
    {
        for (int i = _dogmaPrewarmQueue.Count - 1; i >= 0; i--)
        {
            var (tex, rel) = _dogmaPrewarmQueue[i];
            if (_dogmaCache.ContainsKey(tex)) { _dogmaPrewarmQueue.RemoveAt(i); continue; }
            DogmaVariants(tex, rel, force: true);         // respects the one-per-frame budget
            return;                                       // at most one attempt per call
        }
    }

    private static float Hash01(int x, int y, int v)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + v * 1442695041 + 1013904223);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / (float)0xFFFFFF;
        }
    }

    /// <summary>
    /// The DogmaFrames static variants of <paramref name="src"/>, or null when the sheet
    /// isn't Dogma-coded (the overwhelmingly common case, negatively cached) or when the
    /// pixels can't be read back yet (NOT cached — retried on a later frame).
    /// </summary>
    private Tex[]? DogmaVariants(Tex src, string sheetPath, bool force)
    {
        // Changing the grain re-bakes everything.
        if (_bakedNoiseBlock != DogmaNoiseBlock)
        {
            _dogmaCache.Clear();
            _laserBodyCache.Clear();      // beam static is baked with the same grain
            _dogmaPrewarmed = false;      // re-queue the prewarm sheets for the new grain
            _bakedNoiseBlock = DogmaNoiseBlock;
        }
        if (_dogmaCache.TryGetValue(src, out var cached) && !(force && cached == null)) return cached;

        float t = Time.Totalf;
        if (t != _classifyFrameT) { _classifyFrameT = t; _classifiedThisFrame = false; }
        if (_classifiedThisFrame) return null;          // budget spent this frame

        int w = src.Width, h = src.Height;
        // ⚠ A texture that has not finished uploading reports 0x0 and hands back an
        // EMPTY buffer — and `px.Length == w*h` is then `0 == 0`, i.e. TRUE. Checking
        // null + length is NOT enough (the same trap that ate the laser body crop and
        // the level overlays). Demand real dimensions and real pixels, and never cache
        // a verdict reached from an empty read.
        if (w <= 0 || h <= 0) return null;

        Color32[]? px;
        try { px = src.GetColorData<Color32>(); } catch { return null; }
        if (px == null || px.Length == 0 || px.Length != w * h) return null;
        _classifiedThisFrame = true;

        int opaque = 0, blue = 0, green = 0;
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            if (c.a == 0) continue;
            opaque++;
            if      (c.r == c.g && c.b > c.r) blue++;
            else if (c.r == c.b && c.g > c.r) green++;
        }
        if (opaque == 0) return null;          // fully transparent read: not ready, don't cache
        int coded = blue + green;
        // `force` = the mod says this entity IS Dogma-shaded, so bake regardless of how
        // much of the sheet is coded; the per-texel rules decide what actually changes.
        // A sheet with NO coded texels (e.g. Effect_BrimstoneImpact.png at 0.0%) would
        // bake 8 identical copies for nothing -- its colour comes from the tint, which
        // the dogmaShader desaturation handles instead.
        bool isDogma = coded > 0 && (force || coded >= opaque * DogmaCodedMinFrac);

        // Report every Dogma-NAMED sheet, classified or not: "still blue" has two very
        // different causes — never reached here, or reached and rejected — and only a
        // log distinguishes them. Absence of this line for dogma_fetus.png means the
        // sheet never got as far as DrawEntity.
        if (isDogma || sheetPath.ToLowerInvariant().Contains("dogma"))
            Log.Info($"[Diorama] DOGMA SHEET '{sheetPath}' {w}x{h} " +
                     $"{(isDogma ? "SHADED" : "REJECTED")} opaque={opaque} " +
                     $"coded={coded * 100f / opaque:0.0}% blue={blue} green={green}");

        if (!isDogma) { _dogmaCache[src] = null; return null; }

        var variants = new Tex[DogmaFrames];
        for (int v = 0; v < DogmaFrames; v++)
        {
            // The green rule's noise argument depends only on time, so its step() is a
            // single on/off for the WHOLE sheet in a given frame -- a solid flicker.
            float flicker = Hash01(0, 0, v * 7919) > 0.5f ? 1f : 0f;
            var dst = new Color32[px.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var c = px[i];
                int bright, solid;
                float srcVal;
                if (c.a != 0 && c.r == c.g && c.b > c.r)
                {
                    bright = c.b; solid = c.r;
                    // Noise is quantised to 2x2 texel blocks, as the shader quantises
                    // gl_FragCoord to 2px blocks. Hash01 is [0,1]; the shader's simplex
                    // noise is [-1,1], and the rule adds 0.5 to it.
                    int nb = DogmaNoiseBlock < 1 ? 1 : DogmaNoiseBlock;
                    srcVal = Hash01(x / nb, y / nb, v) * 2f - 1f + 0.5f;
                }
                else if (c.a != 0 && c.r == c.b && c.g > c.r)
                {
                    bright = c.g; solid = c.r;
                    srcVal = flicker;
                }
                else { dst[i] = c; continue; }          // not coded: passes through

                float bf = bright / 255f, k = solid / (float)bright;   // k = solid fraction
                float a  = srcVal * bf * (1f - k) + bf * k;
                byte q = (byte)Math.Clamp(a * 255f, 0f, 255f);
                dst[i] = new Color32(q, q, q, c.a);
            }
            var tv = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
            tv.SetColors(w, h, dst);
            tv.SampleMode = TexSample.Point; tv.AddressMode = TexAddress.Clamp;
            variants[v] = tv;
        }
        _dogmaCache[src] = variants;
        return variants;
    }

    private readonly HashSet<string> _sheetMissLogged = new();
    /// <summary>One line per unresolvable spritesheet path — silence here means an entity
    /// that never draws and never explains itself.</summary>
    private void SheetMiss(string anm2Rel, int layerId, string path, string? fallback)
    {
        string key = anm2Rel + "|" + layerId + "|" + path + "|" + (fallback ?? "-");
        if (!_sheetMissLogged.Add(key)) return;
        Log.Info($"[Diorama] SHEET MISS anm2='{anm2Rel}' layer={layerId} path='{path}'" +
                 (fallback != null ? $" -> falling back to '{fallback}'" : " -> LAYER DROPPED"));
    }

    public enum BlendMode { Clip, Blend, Add }

    // Depth between successive layers, in sprite-pixels. Only needs to be large
    // enough to order coplanar layers without z-fighting; 2px made costumes
    // (orderBase 16) float ~6.5cm off Isaac.
    public static float LayerZStepPx = 0.4f;
    // How far (in sprite pixels) additive glow/fire layers sit behind the body.
    public static float AdditiveBehindPx = 1.5f;

    private Material MatFor(Tex tex, BlendMode mode)
    {
        var cache = mode switch { BlendMode.Add => _matAdd, BlendMode.Blend => _matBlend, _ => _matClip };
        if (cache.TryGetValue(tex, out var m)) return m;
        switch (mode)
        {
            case BlendMode.Add:
                // Black = transparent; glow adds light.
                m = new Material(Shader.Unlit)
                { Transparency = Transparency.Add, DepthWrite = false, FaceCull = Cull.None };
                break;
            case BlendMode.Blend:
                // Real partial alpha for fire/smoke. Depth write OFF so soft edges
                // don't punch holes in what's behind them.
                m = new Material(Shader.Unlit)
                { Transparency = Transparency.Blend, DepthWrite = false, FaceCull = Cull.None };
                break;
            default:
                // Alpha-clip: transparent texels discarded, so crisp pixel art that
                // still writes depth and occludes correctly.
                m = Default.MaterialUnlitClip.Copy();
                m.FaceCull = Cull.None;
                break;
        }
        m[MatParamName.DiffuseTex] = tex;
        cache[tex] = m;
        return m;
    }

    private Mesh CropMesh(int texW, int texH, Anm2Frame f, float drawZpx)
    {
        string key = $"{texW}x{texH}|{f.XCrop},{f.YCrop},{f.Width},{f.Height}|" +
                     $"{f.XPos},{f.YPos},{f.XPivot},{f.YPivot}|{f.XScale},{f.YScale},{f.Rotation}|{drawZpx}";
        if (_meshCache.TryGetValue(key, out var cached)) return cached;

        float left = f.XPos - f.XPivot * f.XScale;
        float top  = f.YPos - f.YPivot * f.YScale;
        float right= left + f.Width  * f.XScale;
        float bot  = top  + f.Height * f.YScale;

        // rotate corners about (XPos,YPos) in image space (y-down)
        float rad = f.Rotation * (MathF.PI/180f);
        float ca = MathF.Cos(rad), sa = MathF.Sin(rad);
        (float x,float y) Rot(float px,float py){
            float dx=px-f.XPos, dy=py-f.YPos;
            return (f.XPos + dx*ca - dy*sa, f.YPos + dx*sa + dy*ca);
        }
        var TL=Rot(left,top);  var TR=Rot(right,top);
        var BR=Rot(right,bot); var BL=Rot(left,bot);

        // local space: x right, y up (negate image y), z = draw order depth
        Vec3 P(( float x,float y) c) => new Vec3(c.x, -c.y, drawZpx);
        float u0=(float)f.XCrop/texW, v0=(float)f.YCrop/texH;
        float u1=(float)(f.XCrop+f.Width)/texW, v1=(float)(f.YCrop+f.Height)/texH;
        Color32 w = new Color32(255,255,255,255);
        var verts = new Vertex[]{
            new Vertex(P(TL), new Vec3(0,0,1), new Vec2(u0,v0), w),
            new Vertex(P(TR), new Vec3(0,0,1), new Vec2(u1,v0), w),
            new Vertex(P(BR), new Vec3(0,0,1), new Vec2(u1,v1), w),
            new Vertex(P(BL), new Vec3(0,0,1), new Vec2(u0,v1), w),
        };
        var inds = new uint[]{ 0,1,2, 0,2,3 };
        var mesh = new Mesh(); mesh.SetVerts(verts); mesh.SetInds(inds);
        _meshCache[key] = mesh;
        return mesh;
    }

    private readonly Dictionary<string,Mesh> _layerMesh = new();

    /// <summary>
    /// Draw one RESOLVED layer straight from the game's render state (REPENTOGON).
    /// No .anm2 involved: sheetRel is the runtime spritesheet, so costumes,
    /// pedestal item art and recoloured props come through correctly.
    /// </summary>
    public bool DrawLayer(string sheetRel, int cropX, int cropY, int w, int h,
                          int posX, int posY, float layerRotDeg,
                          Vec3 worldPos, Quat facing, float pxToM, float entScale,
                          bool flipX, bool flipY, Color tint, int order, bool softAlpha)
    {
        if (!Configured || w <= 0 || h <= 0) return false;
        var tex = LoadGfxTexture(sheetRel);
        if (tex == null) return false;

        string key = $"L{tex.Width}x{tex.Height}:{cropX},{cropY},{w},{h}:{posX},{posY}:{layerRotDeg}:{flipX}{flipY}:{order}";
        if (!_layerMesh.TryGetValue(key, out var mesh))
        {
            float u0 = (float)cropX / tex.Width,       v0 = (float)cropY / tex.Height;
            float u1 = (float)(cropX+w) / tex.Width,   v1 = (float)(cropY+h) / tex.Height;
            if (flipX) { (u0,u1) = (u1,u0); }
            if (flipY) { (v0,v1) = (v1,v0); }

            // Destination rect in entity-local pixels (y-down in game space).
            float left = posX, top = posY, right = posX + w, bot = posY + h;
            float rad = layerRotDeg * (MathF.PI/180f);
            float ca = MathF.Cos(rad), sa = MathF.Sin(rad);
            float ax = posX + w*0.5f, ay = posY + h*0.5f;      // rotate about the layer centre
            (float x,float y) Rot(float px,float py)
            { float dx=px-ax, dy=py-ay; return (ax + dx*ca - dy*sa, ay + dx*sa + dy*ca); }

            var TL=Rot(left,top); var TR=Rot(right,top);
            var BR=Rot(right,bot); var BL=Rot(left,bot);
            float z = order * 2f;                              // stable layer ordering
            Vec3 P((float x,float y) c) => new Vec3(c.x, -c.y, z);   // flip y: game is y-down
            Color32 wht = new(255,255,255,255);
            mesh = new Mesh();
            mesh.SetVerts(new Vertex[]{
                new(P(TL), new Vec3(0,0,1), new Vec2(u0,v0), wht),
                new(P(TR), new Vec3(0,0,1), new Vec2(u1,v0), wht),
                new(P(BR), new Vec3(0,0,1), new Vec2(u1,v1), wht),
                new(P(BL), new Vec3(0,0,1), new Vec2(u0,v1), wht),
            });
            mesh.SetInds(new uint[]{0,1,2,0,2,3});
            _layerMesh[key] = mesh;
        }

        float sc = pxToM * entScale;
        var xform = Matrix.TRS(worldPos, facing, new Vec3(sc, sc, sc));
        mesh.Draw(MatFor(tex, softAlpha ? BlendMode.Blend : BlendMode.Clip), xform, tint);
        return true;
    }

    // Colorize saturation. Isaac's colorize is a luminance recolour whose RGB can
    // exceed 1; we ship the hue normalised to max=1, which reads pale. Pushing the
    // lower channels away from the max deepens it. Live-tunable via Tab -> ColorSat.
    public static float ColorizeSat = 1.6f;
    public static Color SaturateColor(Color c)
    {
        float m = MathF.Max(c.r, MathF.Max(c.g, c.b));
        float k = ColorizeSat;
        return new Color(
            Math.Clamp(m - (m - c.r) * k, 0f, 1f),
            Math.Clamp(m - (m - c.g) * k, 0f, 1f),
            Math.Clamp(m - (m - c.b) * k, 0f, 1f), c.a);
    }

    // ----- Laser ribbons -------------------------------------------------------
    // A laser is drawn as a flat ribbon that follows its sample polyline on the
    // floor, tiling the beam-body crop along its length. The crop is lifted out of
    // its atlas into a standalone WRAP texture so V can repeat freely down the beam.
    // Laser material built the SAME proven way as the shadow material
    // (Default.MaterialUnlit.Copy()), NOT `new Material(Shader.Unlit)` which renders
    // the textured beam invisible.
    private readonly Dictionary<Tex,Material> _laserMat = new();
    private Material LaserMat(Tex tex)
    {
        if (_laserMat.TryGetValue(tex, out var m)) return m;
        m = Default.MaterialUnlit.Copy();
        m[MatParamName.DiffuseTex] = tex;
        m.Transparency = Transparency.Blend;
        m.FaceCull     = Cull.None;
        m.DepthWrite   = false;
        _laserMat[tex] = m;
        return m;
    }

    // Additive glow material: a strip whose BRIGHTNESS falls off across the WIDTH
    // (bright centre line, black edges) and is uniform along the length. Additive
    // blending ADDS the texture's RGB (black adds nothing), so the falloff must live in
    // the colour value, NOT in alpha — an alpha gradient would just add flat white.
    // Tinted by the laser colour and drawn wider than the beam, it reads as coloured
    // light spilling onto the floor; Add means it only ever brightens.
    private Material? _laserGlowMat;
    private Material LaserGlowMat()
    {
        if (_laserGlowMat != null) return _laserGlowMat;
        const int W = 64, H = 4;
        var px = new Color32[W * H];
        float c = (W - 1) / 2f;
        for (int x = 0; x < W; x++)
        {
            float dx = (x - c) / c;                 // -1 edge .. 0 centre .. 1 edge
            float a = Math.Clamp(1f - Math.Abs(dx), 0f, 1f);
            a *= a;                                  // soft falloff
            byte v = (byte)(a * 255f);              // brightness gradient (black at edges)
            for (int y = 0; y < H; y++) px[y * W + x] = new Color32(v, v, v, 255);
        }
        var tex = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
        tex.SetColors(W, H, px);
        tex.AddressMode = TexAddress.Clamp;
        var m = new Material(Shader.Unlit)
        { Transparency = Transparency.Add, DepthWrite = false, FaceCull = Cull.None };
        m[MatParamName.DiffuseTex] = tex;
        _laserGlowMat = m;
        return m;
    }

    /// <summary>
    /// Soft additive glow following the same polyline as a laser beam. Same per-segment
    /// flat-quad path as <see cref="DrawLaserRibbon"/>, but a wider quad with the glow
    /// material and the beam's colour, so it looks like the beam is casting light on the
    /// floor. Draw this BEFORE the beam so the crisp body sits on top.
    /// </summary>
    public void DrawLaserGlow(Vec3[] pts, int nPts, float widthM, Color tint, Vec3 up, float floorY)
    {
        if (nPts < 2 || widthM <= 0f) return;
        var mat = LaserGlowMat();
        bool closed = nPts >= 3 && Vec3.Distance(pts[0], pts[nPts - 1]) < 1e-4f;
        float overlap = widthM * 0.5f;
        for (int i = 0; i < nPts - 1; i++)
        {
            Vec3 a = pts[i], b = pts[i + 1];
            Vec3 seg = b - a; seg.y = 0f;
            float len = seg.Magnitude;
            if (len < 1e-4f) continue;
            Vec3 dir = seg.Normalized;
            float startExt = (closed || i > 0) ? overlap : 0f;
            float endExt   = (closed || i < nPts - 2) ? overlap : 0f;
            Vec3 a2 = a - dir * startExt, b2 = b + dir * endExt;
            float len2 = len + startExt + endExt;
            Vec3 center = (a2 + b2) * 0.5f;
            center.y = floorY;                       // sit on the floor, not at beam height
            Quat rot = Quat.LookAt(Vec3.Zero, up, dir);
            Matrix xf = Matrix.TRS(center, rot, new Vec3(widthM, len2, 1f));
            Mesh.Quad.Draw(mat, xf, tint);
        }
    }

    private readonly Dictionary<string,Tex> _laserBodyCache = new();
    /// <summary>
    /// The beam-body crop, optionally re-shaded as TV static for Dogma.
    /// <paramref name="staticVariant"/> &lt; 0 = the plain crop; 0..DogmaFrames-1 picks a
    /// baked noise variant. The laser art itself is NOT blue-coded (measured 0.0% on
    /// Effect_018_LaserEffects.png — it is ordinary pale-white beam art), so the sprite
    /// path's rules have nothing to act on and the beam has to be staticked here instead:
    /// keep each texel's ALPHA (that is what gives the beam its soft edge and taper) and
    /// replace its colour with noise scaled by the texel's own brightness, using the same
    /// solid-fraction blend the shader uses so the core does not strobe away.
    /// </summary>
    private Tex? LaserBodyTex(string sheetRel, int cx, int cy, int cw, int ch,
                              int staticVariant = -1, bool whiten = false)
    {
        string key = $"{sheetRel}|{cx},{cy},{cw},{ch}|s{staticVariant}|w{whiten}";
        if (_laserBodyCache.TryGetValue(key, out var cached)) return cached;
        var sheet = LoadGfxTexture(sheetRel);
        if (sheet == null) return null;
        try
        {
            var px = sheet.GetColorData<Color32>();          // GPU readback; only once loaded
            if (px == null || px.Length != sheet.Width * sheet.Height) return null;  // not ready: retry
            if (cx < 0 || cy < 0 || cx + cw > sheet.Width || cy + ch > sheet.Height) return null;
            int W = sheet.Width;
            var sub = new Color32[cw * ch];
            int maxA = 0;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    var c = px[(cy + y) * W + (cx + x)];
                    sub[y * cw + x] = c;
                    if (c.a > maxA) maxA = c.a;
                }
            // The GPU readback can return an all-transparent buffer before the sheet
            // has finished uploading. Don't CACHE that (it would stick forever and the
            // beam stays invisible) — return null and retry next frame, like Whiten.
            if (maxA == 0) return null;
            // The LIGHT_BEAM art is warm white (~255,255,205). A tint can only multiply, so
            // it can never add back the missing blue — collapse each texel to its own
            // brightness instead, which whitens it while keeping the feathered alpha profile
            // that makes this crop read as a soft beam rather than a slab.
            if (whiten)
                for (int i = 0; i < sub.Length; i++)
                {
                    var c = sub[i];
                    byte q = Math.Max(c.r, Math.Max(c.g, c.b));
                    sub[i] = new Color32(q, q, q, c.a);
                }
            if (staticVariant >= 0)
            {
                const float Solid = 0.30f;                 // keep this much of the beam steady
                int nb = DogmaNoiseBlock < 1 ? 1 : DogmaNoiseBlock;
                for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    var c = sub[y * cw + x];
                    if (c.a == 0) continue;
                    float lum = MathF.Max(c.r, MathF.Max(c.g, c.b)) / 255f;
                    float n   = Hash01(x / nb, y / nb, staticVariant) * 2f - 1f + 0.5f;
                    float a   = n * lum * (1f - Solid) + lum * Solid;
                    byte q = (byte)Math.Clamp(a * 255f, 0f, 255f);
                    sub[y * cw + x] = new Color32(q, q, q, c.a);
                }
            }
            var t = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
            t.SetColors(cw, ch, sub);
            t.SampleMode  = TexSample.Point;
            t.AddressMode = TexAddress.Clamp;                // (was Wrap; matching the shadow tex that renders)
            _laserBodyCache[key] = t;                        // cache only a GOOD extraction
            return t;
        }
        catch { return null; }
    }

    /// <summary>Diagnostic: report the actual pixel content of the laser body crop
    /// (so we can tell transparent vs black-on-additive vs normal-alpha art).</summary>
    public string LaserDiag(string sheetRel, int cx, int cy, int cw, int ch)
    {
        if (!Configured) return "NOT CONFIGURED (no resource roots)";
        var sheet = LoadGfxTexture(sheetRel);
        if (sheet == null) return $"sheet '{sheetRel}' NOT FOUND";
        int w = sheet.Width, h = sheet.Height;
        Color32[]? px;
        try { px = sheet.GetColorData<Color32>(); }
        catch (Exception ex) { return $"readback THREW {ex.GetType().Name}"; }
        if (px == null || px.Length != w * h) return $"readback bad (px={(px==null?"null":px.Length.ToString())} need {w*h})";
        if (cx < 0 || cy < 0 || cx + cw > w || cy + ch > h) return $"crop out of range on {w}x{h}";
        int maxA = 0, opaque = 0, n = 0; long sumA = 0, sr = 0, sg = 0, sb = 0;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                var c = px[(cy + y) * w + (cx + x)];
                if (c.a > maxA) maxA = c.a;
                sumA += c.a; sr += c.r; sg += c.g; sb += c.b; n++;
                if (c.a > 40) opaque++;
            }
        var mid = px[(cy + ch / 2) * w + (cx + cw / 2)];
        return $"sheet {w}x{h} crop=({cx},{cy},{cw},{ch}) maxA={maxA} avgA={(n>0?sumA/n:0)} " +
               $"avgRGB=({(n>0?sr/n:0)},{(n>0?sg/n:0)},{(n>0?sb/n:0)}) opaque={opaque}/{n} " +
               $"mid=({mid.r},{mid.g},{mid.b},a{mid.a})";
    }

    /// <summary>DEBUG: draw the laser body crop on one Mesh.Quad with a caller-given
    /// facing, so we can compare flat-on-floor vs camera-facing visibility.</summary>
    public bool DebugLaserFlatQuad(Vec3 center, float size, Quat facing,
                                   string sheetRel, int cx, int cy, int cw, int ch, Color tint)
    {
        var tex = LaserBodyTex(sheetRel, cx, cy, cw, ch);
        if (tex == null) return false;
        Mesh.Quad.Draw(LaserMat(tex), Matrix.TRS(center, facing, new Vec3(size, size, 1f)), tint);
        return true;
    }

    /// <summary>
    /// Draw a laser as a flat ribbon following <paramref name="pts"/> (already in
    /// world space on the floor plane). One flat `Mesh.Quad` per segment — the proven
    /// render path — oriented by an explicit basis matrix: local X → across (width),
    /// local Y → along the segment (length), local Z (normal) → up. The body crop is
    /// stretched over each segment (no tiling yet). Returns false if the sheet isn't
    /// GPU-ready. <paramref name="poolIndex"/>/<paramref name="tileLenM"/> are unused
    /// now (kept for signature stability).
    /// </summary>
    public bool DrawLaserRibbon(int poolIndex, Vec3[] pts, int nPts, float widthM,
                                float tileLenM, string sheetRel, int cx, int cy, int cw, int ch,
                                Color tint, Vec3 up, bool dogmaStatic = false, bool whiten = false)
    {
        if (nPts < 2 || widthM <= 0f) return false;
        var tex = LaserBodyTex(sheetRel, cx, cy, cw, ch,
                               dogmaStatic ? (int)(Time.Totalf * DogmaStaticFps) % DogmaFrames : -1,
                               whiten);
        if (tex == null) return false;
        var mat = LaserMat(tex);

        // Closed loop (circle laser)? Then every joint is interior and should overlap.
        bool closed = nPts >= 3 && Vec3.Distance(pts[0], pts[nPts - 1]) < 1e-4f;
        float overlap = widthM * 0.5f;   // extend joints so bends don't leave gaps

        bool any = false;
        for (int i = 0; i < nPts - 1; i++)
        {
            Vec3 a = pts[i], b = pts[i + 1];
            Vec3 seg = b - a;
            seg.y = 0f;
            float len = seg.Magnitude;
            if (len < 1e-4f) continue;
            Vec3 dir = seg.Normalized;

            // Extend each end into its neighbour so consecutive segments overlap and the
            // corner gaps at bends close (open beams keep their true start/end).
            float startExt = (closed || i > 0) ? overlap : 0f;
            float endExt   = (closed || i < nPts - 2) ? overlap : 0f;
            Vec3 a2 = a - dir * startExt;
            Vec3 b2 = b + dir * endExt;
            float len2 = len + startExt + endExt;
            Vec3 center = (a2 + b2) * 0.5f;

            // Lay the quad flat via the SAME proven path as shadows (Matrix.TRS + a
            // real Quat). Quad normal (-Z / Vec3.Forward) -> up; local +Y -> dir (length);
            // local +X -> perpendicular (width). LookAt's up-hint aligns local +Y to dir.
            Quat rot = Quat.LookAt(Vec3.Zero, up, dir);
            Matrix xf = Matrix.TRS(center, rot, new Vec3(widthM, len2, 1f));
            Mesh.Quad.Draw(mat, xf, tint);
            any = true;
        }
        return any;
    }

    /// <summary>Does this anm2 have the given animation?</summary>
    public bool HasAnim(string anm2Rel, string anim)
    {
        var abs = Find(anm2Rel); if (abs == null) return false;
        var a = Anm2.Load(abs); return a != null && a.HasAnimation(anim);
    }

    /// <summary>List an anm2's animation names (diagnostic / discovery).</summary>
    public System.Collections.Generic.List<string> ListAnims(string anm2Rel)
    {
        var abs = Find(anm2Rel); if (abs == null) return new(){ "<file not found>" };
        var a = Anm2.Load(abs); if (a == null) return new(){ "<parse failed>" };
        return new System.Collections.Generic.List<string>(a.Anims.Keys);
    }

    /// <summary>Per-layer anm2 breakdown (diagnostics).</summary>
    public System.Collections.Generic.List<string> ExplainLayers(string anm2Rel, string anim, int frame)
    {
        var abs = Find(anm2Rel);
        if (abs == null) return new(){"FILE NOT FOUND"};
        var a = Anm2.Load(abs);
        if (a == null) return new(){"PARSE FAILED"};
        return a.ExplainLayers(anim, frame);
    }

    /// <summary>One-line diagnostic for a given entity's sprite resolution.</summary>
    public string Diagnose(string anm2Rel, string anim, int frame)
    {
        if (!Configured) return $"[{anm2Rel}] no resources";
        string? abs = Find(anm2Rel);
        if (abs == null) return $"[{anm2Rel}] FILE NOT FOUND";
        var a = Anm2.Load(abs);
        if (a == null) return $"[{anm2Rel}] PARSE FAILED";
        int resolved = a.Resolve(anim, frame).Count;
        string why = resolved == 0 ? "  WHY: " + a.Explain(anim, frame) : "";
        string sheet0 = a.Spritesheets.Count>0 ? a.Spritesheets[System.Linq.Enumerable.First(a.Spritesheets.Keys)] : "-";
        return $"[{System.IO.Path.GetFileName(anm2Rel)}] root='{a.RootName}' anim='{anim}' f{frame} -> sheets={a.Spritesheets.Count} layers={a.Layers.Count} anims={a.Anims.Count} default='{a.DefaultAnim}' resolvedLayers={resolved} sheet0='{sheet0}'{why}";
    }

    /// <summary>
    /// Draw an entity's sprite. worldPos = where the sprite pivot (feet) sits.
    /// Returns false if nothing could be drawn (caller draws fallback).
    /// </summary>
    public bool DrawEntity(string anm2Rel, string anim, int frame,
                           Vec3 worldPos, Quat facing, float entityRotDeg,
                           float pxToM, float entScale, bool flipX,
                           Color tint, bool dropWhenFinished = false, bool softAlpha = false,
                           System.Collections.Generic.Dictionary<int,LayerOverride>? overrides = null,
                           StringTable? strings = null,
                           string? overlayAnim = null, int overlayFrame = 0,
                           int orderBase = 0,
                           Vec3 colorOffset = default, Color colorize = default,
                           float yStretch = 1f, bool dogmaShader = false, bool forceGrey = false)
    {
        if (!Configured) return false;
        string? abs = Find(anm2Rel);
        if (abs == null) return false;
        var anm2 = Anm2.Load(abs);
        if (anm2 == null) return false;

        // Finished one-shot (smoke/poof): draw nothing, but report handled so the
        // caller doesn't fall back to a coloured quad. ONLY for effects — doors and
        // corpses legitimately rest on the last frame of a non-looping animation.
        if (dropWhenFinished && anm2.IsFinishedAt(anim, frame)) return true;

        bool haveOv = overrides != null && overrides.Count > 0 && strings != null;
        var layers = anm2.Resolve(anim, frame, haveOv ? overrides : null);

        // The overlay animation is an independent second animation on the same
        // sprite — this is how Isaac's head animates separately from his body.
        if (!string.IsNullOrEmpty(overlayAnim) && anm2.HasAnimation(overlayAnim!))
        {
            var ol = anm2.Resolve(overlayAnim!, overlayFrame, haveOv ? overrides : null);
            if (ol.Count > 0)
            {
                // ANCHOR NULL. Some sprites put ALL of the overlay's placement in a null on
                // the MAIN animation and author the overlay's own frames at (0,0) — the
                // Flesh Maiden's head is the clean case (see Anm2.NullPos). Drawn without
                // it, such an overlay collapses onto the entity origin and sits inside the
                // body. Offset it by the null, sampled on the BODY's animation and frame
                // because that is what the null moves with.
                string? anchor = AnchorNullFor(anm2Rel);
                if (anchor != null && anm2.NullPos(anim, frame, anchor, out float ax, out float ay))
                {
                    for (int i = 0; i < ol.Count; i++)
                    {
                        var (oly, ofr) = ol[i];
                        ofr.XPos += ax; ofr.YPos += ay;
                        ol[i] = (oly, ofr);
                    }
                }
                if (layers.Count == 0) layers = ol;
                else layers.AddRange(ol);      // overlay draws on top
            }
        }
        if (layers.Count == 0)
            // If the animation genuinely exists, "nothing visible this frame" is a
            // legitimate state (a finished pickup, a hidden door) - draw nothing
            // rather than falling back to a coloured box.
            return anm2.HasAnimation(anim);
        string anm2Dir = System.IO.Path.GetDirectoryName(abs) ?? "";

        Quat rot = facing * Quat.FromAngles(0,0,-entityRotDeg);
        float sx = pxToM * entScale * (flipX ? -1f : 1f);
        float sy = pxToM * entScale * yStretch;   // yStretch>1 = taller (secret-room passage)
        Matrix xform = Matrix.TRS(worldPos, rot, new Vec3(sx, sy, pxToM*entScale));

        // Damage flash / status offset (white hit-flash, green poison...). The game
        // does out = texel*tint + offset per pixel. We can't add a true per-pixel
        // offset without a custom shader, and an additive pass draws an ugly bright
        // ghost around the sprite. Instead fold the offset into the tint as a
        // brighten factor (1+offset) so the sprite recolours IN PLACE, no ghost.
        bool offOn = MathF.Abs(colorOffset.x) > 0.02f || MathF.Abs(colorOffset.y) > 0.02f
                                                       || MathF.Abs(colorOffset.z) > 0.02f;
        const float OffGain = 1.45f;   // push the recolour a bit harder (more visible)
        float ofx = MathF.Max(0f, 1f + colorOffset.x * OffGain);
        float ofy = MathF.Max(0f, 1f + colorOffset.y * OffGain);
        float ofz = MathF.Max(0f, 1f + colorOffset.z * OffGain);
        // Colorize (e.g. poison green): blend the tint toward the colorize colour.
        bool colorizeOn = colorize.a > 0.004f;

        // Creep is coloured by a colour OFFSET with a near-BLACK tint (out = texel*0 +
        // offset), where the offset can come from the ENTITY colour AND/OR the anm2
        // FRAME (green creep bakes it in the frame). Our multiply pipeline turns that
        // black, so creep is drawn specially: whitened sheet (alpha mask) painted the
        // combined flat offset colour.
        bool isCreep = anm2Rel.ToLowerInvariant().Contains("creep");

        int drawn = 0, order = orderBase;
        foreach (var (layer, f) in layers)
        {
            // Glow/light/fire layers render ADDITIVE (soft glow, black = transparent);
            // everything else alpha-clipped. Additive layers also sit slightly BEHIND
            // the body so the glow reads as coming from behind the sprite.
            string lname = (layer.Name ?? "").ToLowerInvariant();
            bool additive = lname.Contains("glow") || lname.Contains("light")
                         || lname.Contains("fire") || lname.Contains("flame")
                         || lname.Contains("burn") || lname.Contains("ember")
                         || lname.Contains("flare") || lname.Contains("halo")
                         || lname.Contains("aura");

            // Runtime spritesheet wins: this is what makes costumes, pedestal item
            // art and recoloured props (rainbow poop) render correctly.
            string? sheetPath = null;
            Color layerTint = Color.White;
            bool ovFlipX = false;
            if (haveOv && overrides!.TryGetValue(layer.Id, out var ov))
            {
                // Respect the game's runtime layer visibility: when an item's cosmetic
                // is superseded (e.g. 9-Volt's eye over Eden's Blessing) the game hides
                // that layer. Skipping it stops the stale, non-updating sprite lingering.
                if (!ov.Visible) { order++; continue; }
                var rp = strings!.Get(ov.SheetId);
                if (!string.IsNullOrEmpty(rp)) sheetPath = rp;
                layerTint = new Color(ov.R, ov.G, ov.B, ov.A);
                ovFlipX = ov.FlipX;
            }
            anm2.Spritesheets.TryGetValue(layer.SpritesheetId, out string? bakedPath);
            if (sheetPath == null) sheetPath = bakedPath;
            if (sheetPath == null) { order++; continue; }
            var tex = LoadTex(anm2Dir, sheetPath);
            // A RUNTIME sheet path that doesn't resolve used to drop the layer silently,
            // which makes an entity simply not appear with nothing in the log to say why.
            // Fall back to the anm2's own baked sheet — stale art beats an invisible entity —
            // and say so once per path.
            if (tex == null && bakedPath != null && bakedPath != sheetPath)
            {
                SheetMiss(anm2Rel, layer.Id, sheetPath, bakedPath);
                tex = LoadTex(anm2Dir, bakedPath);
            }
            if (tex == null) { SheetMiss(anm2Rel, layer.Id, sheetPath, null); order++; continue; }
            // Dogma-family sheets are shader DATA, not art (see DogmaVariants): swap in
            // the baked TV-static variant for this instant.
            if (dogmaShader) { PrewarmDogma(); PumpDogmaPrewarm(); }
            var dogmaVariants = DogmaVariants(tex, sheetPath, dogmaShader);
            // Additive glow sits behind the body (negative z = away from viewer);
            // opaque layers keep their front-to-back order.
            float lz = additive ? -AdditiveBehindPx : order * LayerZStepPx;
            var mesh = CropMesh(tex.Width, tex.Height, f, lz);
            var drawTex = tex;
            if (dogmaVariants != null)
                drawTex = dogmaVariants[(int)(Time.Totalf * DogmaStaticFps) % DogmaFrames];
            else if (forceGrey)
                drawTex = Greyscale(tex);
            // Total tint (entity * frame * layer) and total colour offset (entity + frame).
            Color c = new Color(tint.r*f.R*layerTint.r, tint.g*f.G*layerTint.g,
                                tint.b*f.B*layerTint.b, tint.a*f.A*layerTint.a);
            float tox = colorOffset.x + f.RO, toy = colorOffset.y + f.GO, toz = colorOffset.z + f.BO;
            bool totalOffOn = MathF.Abs(tox) > 0.02f || MathF.Abs(toy) > 0.02f || MathF.Abs(toz) > 0.02f;
            // Creep = flat offset colour masked by the sheet's alpha. The tint (e.g.
            // (0,1,1) for green) drives the red-pool texel toward black, so the colour
            // is the OFFSET; we don't gate on the tint being fully black. The animated
            // offset gives the dark<->bright pulse for free.
            // Creep is the red BloodPool art recoloured at runtime. The game does it two
            // different ways and only one was handled:
            //   * a colour OFFSET (out = texel*0 + offset) — the original case, and
            //   * a plain saturated TINT, which is how "Creep (Green)" (1000.23) is done.
            // The second multiplied a RED sheet by a GREEN tint, i.e. by ~zero, so that
            // creep rendered black-on-black and looked like it was simply missing.
            // Detect a tint that is saturated rather than merely dim — a white or
            // curse-dimmed tint has max≈min, a hue does not — so Curse of Darkness can't
            // trip it. Either way the sheet is whitened and painted with the flat colour.
            float tmax = MathF.Max(c.r, MathF.Max(c.g, c.b));
            float tmin = MathF.Min(c.r, MathF.Min(c.g, c.b));
            bool tintIsHue = (tmax - tmin) > 0.25f && tmax > 0.2f;
            bool creepFlat = isCreep && (totalOffOn || tintIsHue) && dogmaVariants == null;
            if (creepFlat)
            {
                // Whiten the sheet so the multiply paints a flat colour through its alpha.
                // The colour is the OFFSET when there is one, otherwise the tint itself.
                drawTex = Whiten(tex);
                if (totalOffOn)
                    c = new Color(MathF.Max(0f,tox), MathF.Max(0f,toy), MathF.Max(0f,toz), c.a);
                // else: keep c — the saturated tint IS the creep's colour.
            }
            else
            {
                if (colorizeOn)
                {
                    var cz = SaturateColor(colorize);   // our max-normalised hue reads pale; deepen it
                    c = new Color(c.r + (cz.r - c.r)*colorize.a,
                                  c.g + (cz.g - c.g)*colorize.a,
                                  c.b + (cz.b - c.b)*colorize.a, c.a);
                }
                if (offOn)   // recolour in place (flash/status); values >1 clamp to white at render
                    c = new Color(c.r*ofx, c.g*ofy, c.b*ofz, c.a);
            }
            // Dogma's sprites are greyscale by construction, so any colour left on the
            // tint is wrong -- that is what made its Brimstone Impact (1000.50.1) render
            // RED while the static around it was already correct. Collapse the tint to
            // its own brightness, keeping intensity and alpha.
            if (dogmaShader)
            {
                float lum = MathF.Max(c.r, MathF.Max(c.g, c.b));
                c = new Color(lum, lum, lum, c.a);
            }
            // Effects are just transparent: use plain alpha Blend (NOT alpha-clip, which
            // hardens soft edges into a square; NOT additive). Solid sprites stay clipped
            // so they're crisp and occlude correctly. Glow/fire layers keep their
            // behind-the-body depth (lz above), just rendered with normal transparency.
            var mode = (additive || softAlpha || creepFlat || c.a < 0.95f) ? BlendMode.Blend : BlendMode.Clip;
            mesh.Draw(MatFor(drawTex, mode), xform, c);
            drawn++; order++;
        }
        return drawn > 0;
    }
}
