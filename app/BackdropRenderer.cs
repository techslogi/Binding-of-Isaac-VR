using System;
using System.Collections.Generic;
using StereoKit;

namespace IsaacDiorama;

/// <summary>
/// Draws the room floor (tiled) + walls (vertical, mirrored halves) using the
/// real backdrop PNGs the mod reads from backdrops.xml.
///
/// Shape-aware: L-shaped rooms mask out the missing quadrant's floor and get
/// inner notch walls, so the diorama matches the room's real footprint.
/// </summary>
public sealed class BackdropRenderer
{
    public struct Rect { public int X, Y, W, H; public Rect(int x,int y,int w,int h){X=x;Y=y;W=w;H=h;} }

    public static Rect FloorCrop = new(6, 78, 111, 91);    // calibrated floor tile
    public static Rect WallCrop  = new(24, 20, 210, 32);   // calibrated wall segment
    public static int  FloorRotate = 1;                    // 90-deg steps

    // ---- Tunables -------------------------------------------------------
    public static float FloorTileM  = 0.10f;   // world size of one floor tile
    public static float WallHeightM = 0.108f;  // wall height (runtime-tunable) (calibrated)
    public static float WallRaiseM  = -0.002f; // vertical offset for walls only (calibrated)
    public static float WallOutsetM = 0.018f;  // push walls OUTSIDE the play area
                                               // (tears collide at the play-area edge)

    // Room shapes (Isaac RoomShape enum)
    const int SHAPE_LTL = 9, SHAPE_LTR = 10, SHAPE_LBL = 11, SHAPE_LBR = 12;

    private readonly SpriteRenderer _sprites;
    private readonly Dictionary<Tex,Material> _mat = new();
    private readonly Dictionary<string,Mesh>  _meshCache = new();   // cached! (was per-frame alloc)
    private int _loggedType = int.MinValue;
    private int _loggedBands = int.MinValue;
    private const float MinWallBand = 0.004f;   // below this a wall strip is degenerate
    public int _lastXmlCount = -1;

    // ---- Live floor image (streamed from the game, blood/water/tint baked in) --
    // If red and blue look swapped, the source is BGRA: toggle this (the app binds
    // it to the 'B' key) and the texture rebuilds.
    public static bool ImageIsBGRA = false;
    private Tex?  _floorLiveTex;
    private int   _floorLiveVersion = -1;
    private bool  _floorLiveBgra;              // BGRA setting the current tex was built with
    private bool  _floorLiveUsable;           // false when the crop is degenerate -> use tiled
    private string _floorRoomKey = "";        // crop is frozen per room (see SetLiveFloor)
    private Mesh? _floorFullQuad;
    private Material? _floorLiveMat;           // one clip material, texture swapped per version
    public static bool ForceTiledFloor = false;   // 'V' key: ignore the live image, use tiled
    // Floor content bbox in the 780x468 image space (normalized). Frozen per room.
    private float _uv0x = 0f, _uv0y = 0f, _uv1x = 1f, _uv1y = 1f;
    // Crop is computed ONCE per room and cached here, so re-entering a room that has since
    // gained a bomb scorch / blood decal reuses the original (decal-free) crop instead of
    // recomputing from the now-dirty image and shifting the floor. Key = roomKey.
    private readonly System.Collections.Generic.Dictionary<string,(float x0,float y0,float x1,float y1,bool usable)> _floorCropCache = new();

    // ---- Live wall image (same camera space as the floor image, so their bboxes
    // are directly comparable). We split the wall ring into far/side strips and map
    // them onto the vertical walls. Sides are approximate (2D->3D); crop later.
    private Tex?  _wallLiveTex;
    private Material? _wallLiveMat;
    private int   _wallLiveVersion = -1;
    private bool  _wallLiveBgra, _wallLiveUsable;
    private string _wallRoomKey = "";
    private float _wx0 = 0f, _wy0 = 0f, _wx1 = 1f, _wy1 = 1f;   // wall outer bbox (normalized)
    private readonly System.Collections.Generic.Dictionary<string,(float x0,float y0,float x1,float y1,bool usable)> _wallCropCache = new();
    public static bool ForceTiledWalls = false;   // 'C' key: ignore the wall image, use tiled
    public static bool DrawLNotchWalls = false;    // L-room inner notch walls (off: unblocks the view)

    public BackdropRenderer(SpriteRenderer sprites) { _sprites = sprites; }

    /// <summary>
    /// Adopt another renderer's streamed floor/wall textures and their frozen crops.
    ///
    /// The life-size room draws through its OWN BackdropRenderer instance (the shared
    /// mutable meshes — `_overlayQuad`, the water grid — cannot serve two geometries in one
    /// frame). But a second instance starts with no live images and silently falls back to
    /// tiled xml art, which is why the big room stopped matching the game. Re-uploading the
    /// same RGBA into a second texture would double a per-frame streaming cost for pixels we
    /// already have on the GPU, so SHARE the Tex objects instead and copy the crop state
    /// that goes with them. Read-only use on both sides, so one texture is safe for both.
    /// </summary>
    public void ShareLiveFrom(BackdropRenderer src)
    {
        if (ReferenceEquals(src, this)) return;
        if (_floorLiveTex != src._floorLiveTex || _floorLiveVersion != src._floorLiveVersion)
        {
            _floorLiveTex     = src._floorLiveTex;
            _floorLiveVersion = src._floorLiveVersion;
            _floorLiveBgra    = src._floorLiveBgra;
            _floorLiveMat     = src._floorLiveMat;
            _floorFullQuad    = null;     // our own quad: its UVs must be rebuilt
        }
        _floorLiveUsable = src._floorLiveUsable;
        _uv0x = src._uv0x; _uv0y = src._uv0y; _uv1x = src._uv1x; _uv1y = src._uv1y;
        _roomKey = src._roomKey;

        _wallLiveTex     = src._wallLiveTex;
        _wallLiveMat     = src._wallLiveMat;
        _wallLiveVersion = src._wallLiveVersion;
        _wallLiveBgra    = src._wallLiveBgra;
        _wallLiveUsable  = src._wallLiveUsable;
        _wx0 = src._wx0; _wy0 = src._wy0; _wx1 = src._wx1; _wy1 = src._wy1;
    }

    /// <summary>
    /// Publish the latest streamed floor image. Rebuilds the texture only when the
    /// room's image version changes (or the BGRA toggle flips). Call on the MAIN
    /// thread (StereoKit textures must be created there).
    /// </summary>
    public void SetLiveFloor(int version, int w, int h, byte[] rgba, string roomKey)
    {
        _roomKey = roomKey;   // picks a stable per-room overlay variant
        if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w*h*4) return;
        // The crop is FROZEN per room: a bomb scorch / blood changes the image but must not
        // move the floor. Same ordering trap as SetLiveWall (see the long note there): the
        // unchanged-version early-return has to come AFTER the room check, or a room whose
        // image arrives a frame before its bounds never gets a crop of its own.
        bool sameImage = version == _floorLiveVersion && _floorLiveTex != null
                                                       && _floorLiveBgra == ImageIsBGRA;
        bool recomputeCrop = roomKey != _floorRoomKey;
        if (sameImage && !recomputeCrop) return;
        _floorRoomKey = roomKey;

        var colors = new Color32[w*h];
        if (ImageIsBGRA)
            for (int i=0;i<w*h;i++){ int j=i*4; colors[i]=new Color32(rgba[j+2],rgba[j+1],rgba[j],rgba[j+3]); }
        else
            for (int i=0;i<w*h;i++){ int j=i*4; colors[i]=new Color32(rgba[j],rgba[j+1],rgba[j+2],rgba[j+3]); }

        if (!sameImage)   // same pixels on a room change: keep the texture, redo the crop
        {
            var tex = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
            tex.SetColors(w, h, colors);
            tex.SampleMode  = TexSample.Point;
            tex.AddressMode = TexAddress.Clamp;
            _floorLiveTex     = tex;
            _floorLiveVersion = version;
            _floorLiveBgra    = ImageIsBGRA;
            // One clip material (transparent floor pixels discarded -> L-rooms self-mask);
            // swap its texture each version so we don't leak a material per blood update.
            if (_floorLiveMat == null)
            { _floorLiveMat = Default.MaterialUnlitClip.Copy(); _floorLiveMat.FaceCull = Cull.None; }
            _floorLiveMat[MatParamName.DiffuseTex] = tex;
        }

        // Auto-crop, ROBUST + FROZEN. Only recompute on a room change; within a
        // room the crop is fixed so bombs/blood update the texture without moving
        // the floor. Count content per column/row and keep only lines with a real
        // amount of floor, so transient stray pixels are ignored.
        if (!recomputeCrop) return;   // texture updated (blood/scorch); crop stays frozen
        // Seen this room before? Reuse the ORIGINAL crop so a scorch/blood added since our
        // first visit can't re-drive the bbox and shift the floor on re-entry.
        if (_floorCropCache.TryGetValue(roomKey, out var fc))
        {
            _uv0x=fc.x0; _uv0y=fc.y0; _uv1x=fc.x1; _uv1y=fc.y1;
            _floorLiveUsable=fc.usable; _floorFullQuad=null;
            Log.Info($"[Diorama] live floor image v{version} {w}x{h} crop=CACHED uv=[{fc.x0:0.00},{fc.y0:0.00}..{fc.x1:0.00},{fc.y1:0.00}] usable={fc.usable}");
            return;
        }
        var colCount = new int[w];
        var rowCount = new int[h];
        for (int y=0; y<h; y++)
        {
            int row = y*w, rc = 0;
            for (int x=0; x<w; x++)
            {
                var c = colors[row+x];
                if (c.a > 16 && (c.r + c.g + c.b) > 8) { colCount[x]++; rc++; }
            }
            rowCount[y] = rc;
        }
        int colThresh = Math.Max(6, h/16);   // a floor column spans most of the room height
        int rowThresh = Math.Max(6, w/16);
        int minx=w, miny=h, maxx=-1, maxy=-1;
        for (int x=0; x<w; x++) if (colCount[x] >= colThresh) { if (x<minx) minx=x; if (x>maxx) maxx=x; }
        for (int y=0; y<h; y++) if (rowCount[y] >= rowThresh) { if (y<miny) miny=y; if (y>maxy) maxy=y; }

        float nx0, ny0, nx1, ny1;
        if (maxx < 0 || maxy < 0) { nx0=0; ny0=0; nx1=1; ny1=1; }
        else { nx0=minx/(float)w; ny0=miny/(float)h; nx1=(maxx+1)/(float)w; ny1=(maxy+1)/(float)h; }
        // Only trust the live floor if the crop covers a real area; otherwise fall
        // back to the tiled backdrop (a nearly-empty image would look wrong).
        _floorLiveUsable = (maxx >= 0 && maxy >= 0) && (nx1-nx0) > 0.12f && (ny1-ny0) > 0.12f;
        // First computation for this room: adopt it and cache it for every future visit.
        _uv0x=nx0; _uv0y=ny0; _uv1x=nx1; _uv1y=ny1; _floorFullQuad=null;
        _floorCropCache[roomKey] = (nx0, ny0, nx1, ny1, _floorLiveUsable);

        Log.Info($"[Diorama] live floor image v{version} {w}x{h} bgra={ImageIsBGRA} " +
                 $"content=[{minx},{miny}..{maxx},{maxy}] uv=[{nx0:0.00},{ny0:0.00}..{nx1:0.00},{ny1:0.00}] usable={_floorLiveUsable} (cached)");
    }

    public bool HasLiveFloor => _floorLiveTex != null;
    public bool HasLiveWall  => _wallLiveTex != null && _wallLiveUsable;

    /// <summary>Publish the streamed WALL image; compute its outer bbox (frozen per
    /// room), same robust method as the floor. Main thread only.</summary>
    public void SetLiveWall(int version, int w, int h, byte[] rgba, string roomKey)
    {
        if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w*h*4) return;
        // ORDER MATTERS. This used to early-return on an unchanged image version BEFORE it
        // looked at roomKey, which lost the crop for a whole room:
        //   frame N   a wall image arrives while the backdrop packet still carries the OLD
        //             room's bounds -> roomKey is stale -> recompute is false, and
        //             _wallRoomKey is left on the old room.
        //   frame N+1 the backdrop catches up and roomKey becomes the new room -- but the
        //             image version has not moved, so the old early-return fired and the
        //             crop was NEVER recomputed.
        // The previous room's UV bbox then stayed frozen in, which squashes the wall texture
        // and survives every later room change. A room change must force the crop pass even
        // when the pixels are identical, so the two conditions are now separate.
        bool sameImage = version == _wallLiveVersion && _wallLiveTex != null
                                                     && _wallLiveBgra == ImageIsBGRA;
        bool recompute = roomKey != _wallRoomKey;
        if (sameImage && !recompute) return;
        _wallRoomKey = roomKey;

        var colors = new Color32[w*h];
        if (ImageIsBGRA)
            for (int i=0;i<w*h;i++){ int j=i*4; colors[i]=new Color32(rgba[j+2],rgba[j+1],rgba[j],rgba[j+3]); }
        else
            for (int i=0;i<w*h;i++){ int j=i*4; colors[i]=new Color32(rgba[j],rgba[j+1],rgba[j+2],rgba[j+3]); }

        if (!sameImage)   // same pixels on a room change: keep the texture, redo the crop
        {
            var tex = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
            tex.SetColors(w, h, colors);
            tex.SampleMode = TexSample.Point; tex.AddressMode = TexAddress.Clamp;
            _wallLiveTex = tex; _wallLiveVersion = version; _wallLiveBgra = ImageIsBGRA;
            if (_wallLiveMat == null)
            { _wallLiveMat = Default.MaterialUnlitClip.Copy(); _wallLiveMat.FaceCull = Cull.None; }
            _wallLiveMat[MatParamName.DiffuseTex] = tex;
        }

        if (!recompute) return;
        // Reuse the original per-room crop on re-entry (see floor cache rationale).
        if (_wallCropCache.TryGetValue(roomKey, out var wc))
        {
            _wx0=wc.x0; _wy0=wc.y0; _wx1=wc.x1; _wy1=wc.y1; _wallLiveUsable=wc.usable;
            Log.Info($"[Diorama] WALLCROP room='{roomKey}' v{version} {w}x{h} crop=CACHED "
                   + $"uv=[{wc.x0:0.000},{wc.y0:0.000}..{wc.x1:0.000},{wc.y1:0.000}] usable={wc.usable}");
            return;
        }
        var colCount = new int[w]; var rowCount = new int[h];
        for (int y=0; y<h; y++){ int row=y*w, rc=0;
            for (int x=0; x<w; x++){ var c=colors[row+x]; if (c.a>16 && (c.r+c.g+c.b)>8){ colCount[x]++; rc++; } }
            rowCount[y]=rc; }
        int colT = Math.Max(6, h/16), rowT = Math.Max(6, w/16);
        int minx=w, miny=h, maxx=-1, maxy=-1;
        for (int x=0;x<w;x++) if (colCount[x]>=colT){ if(x<minx)minx=x; if(x>maxx)maxx=x; }
        for (int y=0;y<h;y++) if (rowCount[y]>=rowT){ if(y<miny)miny=y; if(y>maxy)maxy=y; }
        float nx0,ny0,nx1,ny1;
        if (maxx<0||maxy<0){ nx0=0;ny0=0;nx1=1;ny1=1; }
        else { nx0=minx/(float)w; ny0=miny/(float)h; nx1=(maxx+1)/(float)w; ny1=(maxy+1)/(float)h; }
        _wallLiveUsable = (maxx>=0&&maxy>=0) && (nx1-nx0)>0.12f && (ny1-ny0)>0.12f;
        _wx0=nx0; _wy0=ny0; _wx1=nx1; _wy1=ny1;
        _wallCropCache[roomKey] = (nx0, ny0, nx1, ny1, _wallLiveUsable);
        Log.Info($"[Diorama] WALLCROP room='{roomKey}' v{version} {w}x{h} sameImage={sameImage} "
               + $"content=[{minx},{miny}..{maxx},{maxy}] "
               + $"uv=[{nx0:0.000},{ny0:0.000}..{nx1:0.000},{ny1:0.000}] usable={_wallLiveUsable} (computed)");
    }

    // One vertical wall quad with explicit corner UVs (TL,TR,BR,BL). Cached by key.
    private Mesh WallStripQuad(string key, Vec2 uvTL, Vec2 uvTR, Vec2 uvBR, Vec2 uvBL)
    {
        if (_meshCache.TryGetValue(key, out var cached)) return cached;
        Color32 wc = new(255,255,255,255);
        var m = new Mesh();
        m.SetVerts(new Vertex[]{
            new(new Vec3(-0.5f,1,0), new Vec3(0,0,1), uvTL, wc),
            new(new Vec3( 0.5f,1,0), new Vec3(0,0,1), uvTR, wc),
            new(new Vec3( 0.5f,0,0), new Vec3(0,0,1), uvBR, wc),
            new(new Vec3(-0.5f,0,0), new Vec3(0,0,1), uvBL, wc),
        });
        m.SetInds(new uint[]{0,1,2,0,2,3});
        _meshCache[key] = m;
        return m;
    }

    // Draw the far + side walls from the streamed wall image, split by the gap
    // between the floor's inner bbox and the wall's outer bbox.
    private void DrawLiveWalls(Vec3 center, float w, float d, float topY)
    {
        topY += WallRaiseM;   // vertical wall position (independent of the floor)
        var mat = _wallLiveMat!;
        float fx0=_uv0x, fy0=_uv0y, fx1=_uv1x, fy1=_uv1y;   // floor inner bbox (image space)
        float wx0=_wx0, wy0=_wy0, wx1=_wx1, wy1=_wy1;        // wall outer bbox

        // Each wall strip is the BAND between the wall image's OUTER bbox and the floor's INNER
        // bbox. Those two crops come from DIFFERENT images (GetWallImage vs GetFloorImage), each
        // normalised to its OWN dimensions — so if the game hands back different sizes for the two
        // (non-1x1 rooms), a band can collapse to ~0 and that wall silently draws as a zero-area
        // sliver: the "long room didn't load a wall" bug. A wall ring is symmetric, so repair a
        // collapsed band from its widest healthy sibling, and log it once so the cause is visible.
        float bandL = fx0 - wx0, bandR = wx1 - fx1, bandT = fy0 - wy0;
        if (bandL < MinWallBand || bandR < MinWallBand || bandT < MinWallBand)
        {
            float good = MathF.Max(MathF.Max(bandL, bandR), bandT);
            if (good < MinWallBand) good = 0.06f;    // nothing healthy to copy: sane default ring
            if (bandL < MinWallBand) wx0 = fx0 - good;
            if (bandR < MinWallBand) wx1 = fx1 + good;
            if (bandT < MinWallBand) wy0 = fy0 - good;
            int key = (int)(bandL*1000) * 1000000 + (int)(bandR*1000) * 1000 + (int)(bandT*1000);
            if (key != _loggedBands)
            {
                _loggedBands = key;
                Log.Info($"[Diorama] WALL BAND COLLAPSE bands L={bandL:0.000} R={bandR:0.000} T={bandT:0.000}" +
                         $" -> repaired with {good:0.000};  floor uv=[{fx0:0.000},{fy0:0.000}..{fx1:0.000},{fy1:0.000}]" +
                         $" wall uv=[{_wx0:0.000},{_wy0:0.000}..{_wx1:0.000},{_wy1:0.000}]");
            }
        }
        float ow = w/2 + WallOutsetM, od = d/2 + WallOutsetM;
        var FAR  = Quat.FromAngles(0,  0,0);
        var EAST = Quat.FromAngles(0,-90,0);
        var WEST = Quat.FromAngles(0, 90,0);

        // FAR wall: top strip (outer-top .. floor-top), spans room width.
        var far = WallStripQuad($"WFAR:{fx0},{wy0},{fx1},{fy0}",
            new Vec2(fx0,wy0), new Vec2(fx1,wy0), new Vec2(fx1,fy0), new Vec2(fx0,fy0));
        far.Draw(mat, Matrix.TRS(new Vec3(center.x, topY, center.z - od), FAR,
                                 new Vec3(w + 2f*WallOutsetM, WallHeightM, 1)), DimCol);

        // EAST wall: right strip (floor-right .. outer-right), spans room depth.
        // UV rotated: wall height -> image X (inner..outer), wall length -> image Y.
        var east = WallStripQuad($"WEAST:{fx1},{wx1},{fy0},{fy1}",
            new Vec2(wx1,fy0), new Vec2(wx1,fy1), new Vec2(fx1,fy1), new Vec2(fx1,fy0));
        east.Draw(mat, Matrix.TRS(new Vec3(center.x + ow, topY, center.z), EAST,
                                  new Vec3(d + 2f*WallOutsetM, WallHeightM, 1)), DimCol);

        // WEST wall: left strip (outer-left .. floor-left), spans room depth.
        var west = WallStripQuad($"WWEST:{wx0},{fx0},{fy0},{fy1}",
            new Vec2(wx0,fy0), new Vec2(wx0,fy1), new Vec2(fx0,fy1), new Vec2(fx0,fy0));
        west.Draw(mat, Matrix.TRS(new Vec3(center.x - ow, topY, center.z), WEST,
                                  new Vec3(d + 2f*WallOutsetM, WallHeightM, 1)), DimCol);
    }

    // ---------------- Planetarium (custom starry-sphere backdrop) ----------------
    // The diorama sits inside an inner-facing sphere textured with the game's blue base;
    // rotating star shells drift behind it, and the glass floor sits on the floor plane.
    // All tunable so you can dial it in the headset later.
    // Brightness multiplier for the WALLS (1 = normal). Curse of Darkness drives this down.
    // The FLOOR is deliberately NOT dimmed here — Program multiplies it with a per-vertex
    // black veil instead, which keeps the floor's true colour inside Isaac's light.
    // ---------------- Level overlays ("looming shadows") ----------------
    // resources/gfx/overlays/<stage>/<shape>_overlay_<1..5>.png. MEASURED: these are FULLY
    // OPAQUE GREYSCALE masks (alpha=255 everywhere), NOT alpha-shaped art — basement is black
    // rafter beams on white, cathedral is a white window-light shape on black. Both read
    // correctly as a MULTIPLY BY LUMINANCE: white leaves the floor alone, black shadows it.
    // StereoKit has no multiply blend, so at load the mask becomes BLACK with alpha = 255-luma,
    // which is the identical operation for a black multiplicand. (Same "opaque black background"
    // trap as the planetarium starfields — never Blend these raw.)
    public static float OverlayStrength = 0.33f;    // 0 = off; Tab->OverlayStr (baked ~2/3 of 0.50)
    // Dogma's arena wants a heavier shadow than the rest of Home. Program sets this to
    // DogmaOverlayMul while any Dogma-shaded entity is on screen, and back to 1 after.
    public static float OverlayBoost = 1f;
    private static float OverlayAlpha => Math.Clamp(OverlayStrength * OverlayBoost, 0f, 1f);
    // Isaac's light, pushed in by Program each frame. In game the light around Isaac CULLS the
    // overlay in a radius, so the shadow never sits on top of him — reproduced here by fading the
    // overlay's per-vertex alpha to 0 inside that radius (same trick as the Curse of Darkness veil).
    public static Vec3  LightCenter;
    public static float LightRadius;
    public static bool  HasLight;
    private readonly System.Collections.Generic.Dictionary<string, Tex?> _overlayTex = new();
    private Material? _overlayMat;
    private Mesh? _overlayQuad;
    private string _roomKey = "";                   // set by SetLiveFloor; picks a stable variant

    // Only 13 overlay folders ship, so chapter variants share one (Cellar/Burning -> basement,
    // Catacombs/Flooded -> caves, ...). Derived from the backdrop gfx name, so NO packet change.
    private static string? OverlayFolder(string? wallGfx)
    {
        if (string.IsNullOrEmpty(wallGfx)) return null;
        string g = wallGfx!.ToLowerInvariant();
        if (g.Contains("basement") || g.Contains("cellar"))    return "basement";
        if (g.Contains("catacombs") || g.Contains("caves"))    return "caves";
        if (g.Contains("necropolis") || g.Contains("depths"))  return "depths";
        if (g.Contains("downpour") || g.Contains("dross"))     return "downpour";
        if (g.Contains("mines") || g.Contains("ashpit"))       return "mines";
        if (g.Contains("mausoleum"))                           return "mausoleum";
        if (g.Contains("gehenna"))                             return "gehenna";
        if (g.Contains("corpse"))                              return "corpse";
        if (g.Contains("sheol"))                               return "sheol";
        if (g.Contains("cathedral"))                           return "cathedral";
        if (g.Contains("chest"))                               return "chest";
        if (g.Contains("home"))                                return "home";
        if (g.Contains("utero") || g.Contains("womb"))         return "womb";
        return null;   // Dark Room / Void / shops ship no overlays
    }

    private static string ShapeBucket(int shape) => shape switch
    {
        4 or 5                   => "1x2",   // 1x2 / IIV (tall)
        6 or 7                   => "2x1",   // 2x1 / IIH (wide)
        8 or 9 or 10 or 11 or 12 => "2x2",   // 2x2 + the L shapes
        _                        => "1x1",   // 1x1 / IH / IV
    };

    // Stable per room (the game picks from a decoration seed we can't read; this at least never
    // flickers and differs room to room).
    private static int VariantFor(string roomKey)
    {
        int h = 17;
        unchecked { foreach (char c in roomKey) h = h * 31 + c; }
        return ((h & 0x7fffffff) % 5) + 1;
    }

    private string _ovWhy = "";
    private Tex? OverlayTexture(string rel)
    {
        if (_overlayTex.TryGetValue(rel, out var cached))
        { if (cached == null) _ovWhy = "cached MISSING (file not found)"; return cached; }
        var src = _sprites.LoadGfxTexture(rel);
        if (src == null) { _overlayTex[rel] = null; _ovWhy = "LoadGfxTexture returned null (FILE NOT FOUND)"; return null; }
        try
        {
            var px = src.GetColorData<Color32>();                     // GPU readback; only once loaded
            _ovWhy = px == null
                ? $"GetColorData returned NULL (src {src.Width}x{src.Height})"
                : $"GetColorData len={px.Length} expected={src.Width * src.Height}";
            if (px != null && px.Length == src.Width * src.Height)
            {
                // VALIDATE THE READBACK. A GPU readback taken before the texture has finished
                // uploading comes back UNIFORM (all zero) — it passes the null and length checks,
                // so the old code baked it into alpha=255 black and CACHED it: a solid black slab
                // over the whole floor with no shape, exactly what the first build did. A real
                // overlay mask is high-contrast black-and-white, so demand genuine light AND dark
                // pixels before trusting it. (This is the same "cache only on success" rule the
                // laser body-crop extractor already learned the hard way.)
                int lo = 255, hi = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    var q = px[i];
                    int l = (q.r * 77 + q.g * 150 + q.b * 29) >> 8;
                    if (l < lo) lo = l;
                    if (l > hi) hi = l;
                }
                if (hi < 40 || hi - lo < 24)
                {
                    _ovWhy = $"readback NOT READY (luma {lo}..{hi}, uniform) - retrying, not cached";
                    return null;
                }
                _ovWhy = $"ok (luma {lo}..{hi})";
                for (int i = 0; i < px.Length; i++)
                {
                    var q = px[i];
                    int luma = (q.r * 77 + q.g * 150 + q.b * 29) >> 8;
                    px[i] = new Color32(0, 0, 0, (byte)(255 - luma));
                }
                var t = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
                t.SetColors(src.Width, src.Height, px);
                t.SampleMode = TexSample.Linear; t.AddressMode = TexAddress.Clamp;
                _overlayTex[rel] = t;                                  // cache only on success
                return t;
            }
        }
        catch (Exception ex) { _ovWhy = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message; }
        return null;   // not uploaded yet: retry next frame, deliberately NOT cached
    }

    // XZ-plane GRID with plain 0..1 UVs (FloorFullQuad's UVs carry the live-floor crop, so it
    // cannot be reused). A grid rather than a single quad so per-vertex ALPHA can fade the overlay
    // out inside Isaac's light — the GPU interpolates it, so the cull is smooth with no texture and
    // no shader. Rebuilt each frame because Isaac moves; ~400 verts is nothing.
    private const int OverlayGridN = 20;
    private Vertex[]? _ovVerts;
    private uint[]?   _ovInds;
    private Mesh OverlayGrid(Vec3 center, float fw, float fd)
    {
        const int N = OverlayGridN;
        _overlayQuad ??= new Mesh();
        _ovVerts ??= new Vertex[N * N];
        if (_ovInds == null)
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
            _ovInds = idx;
        }
        bool cull = HasLight && LightRadius > 0.0001f;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float u = x / (N - 1f), v = y / (N - 1f);
            float lx = u - 0.5f, lz = v - 0.5f;
            float a = 1f;
            if (cull)
            {
                float wx = center.x + lx * fw, wz = center.z + lz * fd;
                float dx = wx - LightCenter.x, dz = wz - LightCenter.z;
                float t = Math.Clamp(1f - MathF.Sqrt(dx * dx + dz * dz) / LightRadius, 0f, 1f);
                t = t * t * (3f - 2f * t);          // smoothstep
                a = 1f - t;                          // 0 under the light, 1 outside it
            }
            _ovVerts[y * N + x] = new Vertex(new Vec3(lx, 0, lz), new Vec3(0, 1, 0),
                                             new Vec2(u, v), new Color32(255, 255, 255, (byte)(a * 255f)));
        }
        _overlayQuad.SetVerts(_ovVerts);
        _overlayQuad.SetInds(_ovInds);
        return _overlayQuad;
    }

    private string _loggedOverlay = "";
    private void OvLog(string msg)
    {
        if (msg == _loggedOverlay) return;
        _loggedOverlay = msg;
        Log.Info("[Diorama] OVERLAY " + msg);
    }

    // ======================= Water ============================================
    // The game draws water as a POST-PROCESS over the already-rendered floor:
    //   water.fs    (v1, Flooded Caves and friends) distorts the floor with a pair of
    //               sine oscillators, then desaturates it (rgb *= 0.75) and pushes it
    //               blue (b += 0.2, g += 0.1), blended by Amount.
    //   water_v2.fs (Downpour/Dross) builds a normal from four RADIAL wave sources, adds
    //               two LINEAR ones along CurrentDir when the room has a flow, refracts
    //               the floor through it and mixes toward WaterColor by a fresnel term,
    //               all scaled by WaterColorMultiplier.
    // We cannot post-process (no custom shader in this pipeline) and we cannot refract,
    // but the two things that actually READ as water from a metre away are the COLOUR WASH
    // and MOVING CRESTS. So: a translucent tinted plane on the floor, plus two scrolling
    // layers of a procedural ripple texture at different scales and speeds.
    //
    // Scrolling is done by rewriting the grid's UVs each frame rather than via a material
    // UV transform — the mesh is rebuilt anyway to cull the L-room notch, so it costs
    // nothing extra and avoids depending on a material param that may not be wired.
    public static float WaterAlphaMul  = 0.40f;   // Tab->WaterAlpha  (baked from VR)
    public static float WaterRippleMul = 0.90f;   // Tab->WaterRipple (baked from VR)
    public static float WaterLiftM     = 0.0016f; // above the floor plane (over the overlay)
    public static float WaterScrollMul = 2.50f;   // Tab->WaterScroll (baked from VR)
    public static float WaterTileA     = 3.0f;    // ripple tiles across the room, layer A
    public static float WaterTileB     = 1.7f;    // ...and layer B

    private Tex?      _waterTex;
    private Material? _waterTintMat, _waterRippleMat, _waterWashMat;
    // One mesh PER LAYER: Mesh.Draw only QUEUES the draw, so three layers sharing a single
    // mesh would all end up rendering with whichever UVs were written last.
    private readonly Mesh?[] _waterMesh = new Mesh?[4];
    private Vertex[]? _wVerts;
    private uint[]?   _wInds;

    /// <summary>
    /// A seamlessly TILEABLE ripple texture: a sum of sines at INTEGER frequencies (which is
    /// what makes it tile), shaped so only the wave crests are bright. That mirrors what
    /// water_v2 does with its wave sum, minus the normal/refraction step we can't do.
    /// White with the pattern in ALPHA, so one texture works additively or blended.
    /// </summary>
    private Tex WaterTex()
    {
        if (_waterTex != null) return _waterTex;
        const int N = 128;
        const float TAU = 6.2831853f;
        var h = new float[N * N];
        float lo = 1e9f, hi = -1e9f;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float u = x / (float)N, v = y / (float)N;
            // A DOMINANT wave along u whose phase is perturbed by slower cross waves. That
            // asymmetry is what makes crests: summing similar frequencies in both axes (the
            // first attempt) just gives round blobs, which read as spots, not water.
            // Crests therefore run along v, and DrawWater rotates the UVs so they always sit
            // ACROSS the current — which is how a flowing surface actually looks.
            float f = MathF.Sin(TAU * (3f * u) + 1.1f * MathF.Sin(TAU * v)
                                               + 0.5f * MathF.Sin(TAU * (2f * u + 2f * v)))
                    + 0.5f  * MathF.Sin(TAU * (5f * u) + 0.8f * MathF.Sin(TAU * (2f * v)))
                    + 0.25f * MathF.Sin(TAU * (2f * u + 1f * v));
            h[y * N + x] = f;
            if (f < lo) lo = f;
            if (f > hi) hi = f;
        }
        var px = new Color32[N * N];
        for (int i = 0; i < h.Length; i++)
        {
            float n = (h[i] - lo) / (hi - lo + 1e-9f);
            float a = Math.Clamp((n - 0.74f) / 0.26f, 0f, 1f);   // keep only the crests
            a = a * a * (3f - 2f * a);                           // smoothstep
            // ⚠ The crest lives in RGB, NOT alpha: the ripple layers render ADDITIVE, and
            // additive adds RGB while ignoring alpha. A white texture with the pattern in
            // alpha would add solid white across the whole floor. (Same lesson as the laser
            // floor glow and the level overlays — it has caught this project twice before.)
            byte q = (byte)(a * 255f);
            px[i] = new Color32(q, q, q, 255);
        }
        var t = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
        t.SetColors(N, N, px);
        t.SampleMode  = TexSample.Linear;      // water is the one thing here that is NOT pixel art
        t.AddressMode = TexAddress.Wrap;       // every frequency above is an integer, so it tiles
        _waterTex = t;
        return t;
    }

    /// <summary>
    /// Floor-sized grid whose UVs are ROTATED into the flow's frame and scrolled along it,
    /// so the crests always run across the current. L-room notch culled to alpha 0.
    /// </summary>
    private Mesh WaterGrid(int slot, int shape, float dirX, float dirZ,
                           float tile, float scroll)
    {
        const int N = 12;
        var mesh = _waterMesh[slot] ??= new Mesh();
        _wVerts ??= new Vertex[N * N];
        if (_wInds == null)
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
            _wInds = idx;
        }
        bool isL = shape >= SHAPE_LTL && shape <= SHAPE_LBR;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float u = x / (N - 1f), v = y / (N - 1f);
            float lx = u - 0.5f, lz = v - 0.5f;
            // Rotate into the flow frame. Any affine UV map still tiles, because the
            // texture wraps — so this costs nothing and keeps the crests oriented.
            float uu = ( u * dirX + v * dirZ) * tile + scroll;
            float vv = (-u * dirZ + v * dirX) * tile;
            // An L-room's missing quadrant has no floor, so it must have no water either.
            byte a = (byte)(isL && InMissingQuadrant(shape, lx, lz) ? 0 : 255);
            _wVerts[y * N + x] = new Vertex(new Vec3(lx, 0, lz), new Vec3(0, 1, 0),
                                            new Vec2(uu, vv), new Color32(255, 255, 255, a));
        }
        mesh.SetVerts(_wVerts);
        mesh.SetInds(_wInds);
        return mesh;
    }

    // water.fs does TWO things to the floor, and they are different operations:
    //     rgb *= 0.75           a MULTIPLY  -> darkens, keeps every bit of texture
    //     b += 0.2; g += 0.1    an ADD      -> puts the colour in
    // The first build did neither: it BLENDED the floor toward a flat colour, which
    // flattens contrast and is why the floor read as a solid sheet with no texture under
    // it. Reproduced properly here as two passes, because blending toward BLACK at alpha a
    // is exactly a multiply by (1-a) — it darkens without destroying detail — and the
    // colour then goes on additively over the top.
    public static float WaterDarken = 0.22f;   // = rgb *= 0.78. Tab->WaterAlpha scales it
    public static float WaterWash   = 0.10f;   // additive colour strength (baked from VR)
    public static float WaterCrestA = 0.07f;   // crest layer A, additive
    public static float WaterCrestB = 0.045f;  // crest layer B

    /// <summary>Last computed water state, for the debug overlay.</summary>
    public static string WaterDebug = "-";

    private void DrawWater(in UdpReceiver.WaterState w, int shape, Vec3 center,
                           float fw, float fd, float topY)
    {
        if (!w.Valid) { WaterDebug = "no packet"; return; }
        float amt = Math.Clamp(w.Amount, 0f, 1f);
        // A dry room must draw nothing at all. 0.05 rather than 0.01 so a room reporting a
        // trace amount can't put a film over the floor.
        if (amt <= 0.05f || WaterAlphaMul <= 0.001f)
        {
            WaterDebug = $"dry (amt={w.Amount:0.00})";
            return;
        }

        // --- Colour -----------------------------------------------------------
        // The mod prefers Room:GetWaterColor() (the live value the renderer uses) over the
        // FXParams template, which reported plain white in Dross. A room that still has
        // nothing to say reports white or black; both mean "no colour given" and fall back
        // to water.fs's own recipe, never to white — painting white over a floor is what
        // washed the first build out.
        float cr = w.R * w.MR, cg = w.G * w.MG, cb = w.B * w.MB;
        float mx = MathF.Max(cr, MathF.Max(cg, cb));
        float mn = MathF.Min(cr, MathF.Min(cg, cb));
        bool usable = mx > 0.05f && !(mn > 0.82f && mx - mn < 0.12f);
        if (!usable) { cr = 0.30f; cg = 0.55f; cb = 1.00f; }   // water.fs: b+0.2, g+0.1
        else if (mx > 1f) { cr /= mx; cg /= mx; cb /= mx; }    // a multiplier may exceed 1
        // water_v2 mixes toward the water colour by `Color0.a - fresnel`, so WaterColor's
        // ALPHA is how strongly the room wants to be tinted.
        float colA = (w.A > 0.02f && w.A <= 1.001f) ? w.A : 1f;

        WaterDebug = $"amt={amt:0.00} col=({cr:0.00},{cg:0.00},{cb:0.00}) a={colA:0.00} " +
                     $"{(usable ? "room" : "FALLBACK")} cur=({w.CurX:0.0},{w.CurY:0.0}) v2={w.V2}";

        float y = topY + WaterLiftM;

        if (_waterTintMat == null)
        {
            _waterTintMat = Default.MaterialUnlit.Copy();
            _waterTintMat.Transparency = Transparency.Blend;
            _waterTintMat.DepthWrite   = false;
            _waterTintMat.FaceCull     = Cull.None;
        }
        if (_waterRippleMat == null)
        {
            // Crests and the colour wash ADD light; blending them would darken the floor
            // between the crests instead of leaving it alone.
            _waterRippleMat = Default.MaterialUnlit.Copy();
            _waterRippleMat.Transparency = Transparency.Add;
            _waterRippleMat.DepthWrite   = false;
            _waterRippleMat.FaceCull     = Cull.None;
            _waterRippleMat[MatParamName.DiffuseTex] = WaterTex();
        }
        if (_waterWashMat == null)
        {
            _waterWashMat = Default.MaterialUnlit.Copy();     // untextured: a flat add
            _waterWashMat.Transparency = Transparency.Add;
            _waterWashMat.DepthWrite   = false;
            _waterWashMat.FaceCull     = Cull.None;
        }

        var xf = Matrix.TS(new Vec3(center.x, y, center.z), new Vec3(fw, 1f, fd));
        float k = amt * WaterAlphaMul;

        // 1) MULTIPLY: blend toward black. Darkens the floor and keeps its texture.
        //    NOT scaled by colA — in water_v2 the colour's alpha drives the mix toward the
        //    water COLOUR (`Color0.a - fresnel`), not the darkening, and folding it into
        //    both would attenuate the whole effect twice for a room with a faint tint.
        WaterGrid(0, shape, 1f, 0f, 1f, 0f).Draw(_waterTintMat, xf,
            new Color(0f, 0f, 0f, Math.Clamp(WaterDarken * k, 0f, 1f)));

        // 2) ADD the water colour. Additive keeps the floor's detail showing THROUGH the
        //    colour, which a blend cannot do.
        float wsh = WaterWash * k * colA;
        WaterGrid(3, shape, 1f, 0f, 1f, 0f).Draw(_waterWashMat, xf,
            new Color(cr * wsh, cg * wsh, cb * wsh, 1f));

        // 3) Two crest layers, scrolling along the current.
        float t = Time.Totalf * WaterScrollMul;
        float dx = w.CurX, dz = w.CurY;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        bool flowing = len > 0.001f;
        // Must be UNIT length either way: WaterGrid uses (dx,dz) as a rotation matrix, and
        // a non-unit vector would scale the UVs instead of just turning them.
        if (flowing) { dx /= len; dz /= len; } else { dx = 0.906f; dz = 0.423f; }
        float speed = flowing ? 0.16f : 0.035f;

        // Crests are a LIGHTENED water colour, never white: a white highlight on coloured
        // water reads as glare, a paler version of the water reads as a wave.
        float hr = cr + (1f - cr) * 0.45f, hg = cg + (1f - cg) * 0.45f, hb = cb + (1f - cb) * 0.45f;
        float aA = WaterCrestA * k * WaterRippleMul;
        float aB = WaterCrestB * k * WaterRippleMul;
        WaterGrid(1, shape, dx, dz, WaterTileA, t * speed)
            .Draw(_waterRippleMat, xf, new Color(hr * aA, hg * aA, hb * aA, 1f));
        // Layer B is rotated ~20 degrees off the flow and runs slower, so the two never
        // lock into one sliding pattern — which reads as a moving texture, not as water.
        const float C = 0.94f, S = 0.34f;                 // cos/sin 20 degrees
        WaterGrid(2, shape, dx * C - dz * S, dz * C + dx * S, WaterTileB, t * speed * 0.62f)
            .Draw(_waterRippleMat, xf, new Color(hr * aB, hg * aB, hb * aB, 1f));
    }

    private void DrawOverlay(string? wallGfx, int shape, Vec3 center, float fw, float fd, float topY)
    {
        if (OverlayAlpha <= 0.001f) { OvLog("off (OverlayStrength=0)"); return; }
        string? folder = OverlayFolder(wallGfx);
        if (folder == null) { OvLog($"no folder for wallGfx='{wallGfx}'"); return; }
        string rel = $"overlays/{folder}/{ShapeBucket(shape)}_overlay_{VariantFor(_roomKey)}.png";
        var tex = OverlayTexture(rel);
        if (tex == null) { OvLog($"rel='{rel}' -> {_ovWhy}"); return; }
        OvLog($"rel='{rel}' DRAWN {tex.Width}x{tex.Height} strength={OverlayAlpha:0.00} (base {OverlayStrength:0.00} x{OverlayBoost:0.0}) [{_ovWhy}]");
        if (_overlayMat == null)
        {
            _overlayMat = Default.MaterialUnlit.Copy();
            _overlayMat.Transparency = Transparency.Blend;
            _overlayMat.DepthWrite   = false;
            _overlayMat.FaceCull     = Cull.None;
            // NOTE: deliberately NO negative QueueOffset. A negative offset renders EARLIER, which
            // would put the overlay BEFORE the floor if the floor material is also in the
            // transparent queue — the floor would then paint straight over it. At the default
            // offset it is distance-sorted, and it sits 1.2mm above the floor, so it lands on top.
        }
        _overlayMat[MatParamName.DiffuseTex] = tex;
        OverlayGrid(center, fw, fd).Draw(_overlayMat,
            Matrix.TS(new Vec3(center.x, topY, center.z), new Vec3(fw, 1f, fd)),
            new Color(1f, 1f, 1f, OverlayAlpha));
    }

    public static float Dim = 1f;
    private static Color DimCol => new Color(Dim, Dim, Dim, 1f);

    public static float PlanetRadiusMul  = 1.45f;   // sphere radius = half the longest room side * this
    public static float PlanetStarSpin   = 1.0f;    // deg/sec base star-shell rotation
    public static float PlanetFloorAlpha = 0.9f;    // glass floor opacity
    public static float PlanetFloorLift  = 0.001f;  // glass sits just above the floor plane
    public static float PlanetBaseBright = 0.2f;    // brighten the (very dark navy) base to read as blue
    public static float PlanetSphereY    = 0.120f;  // vertical offset of the sphere centre (m)
    public static float PlanetFloorScale = 1.40f;   // glass floor size multiplier
    private Mesh? _planetSphere, _planetGlassQuad;
    private Tex?  _planetBaseTex; private readonly Tex?[] _planetStarTex = new Tex?[5];
    private Material? _planetBaseMat; private readonly Material?[] _planetStarMat = new Material?[5];
    private Material? _planetGlassMat;

    private Mesh PlanetSphere() => _planetSphere ??= Mesh.GenerateSphere(1f, 24);

    // Unit quad in the XZ plane, UV 0..1 (full texture), for the glass floor.
    private Mesh PlanetGlassQuad()
    {
        if (_planetGlassQuad != null) return _planetGlassQuad;
        Color32 wc = new(255,255,255,255);
        var m = new Mesh();
        m.SetVerts(new Vertex[]{
            new(new Vec3(-0.5f,0,-0.5f), new Vec3(0,1,0), new Vec2(0,0), wc),
            new(new Vec3( 0.5f,0,-0.5f), new Vec3(0,1,0), new Vec2(1,0), wc),
            new(new Vec3( 0.5f,0, 0.5f), new Vec3(0,1,0), new Vec2(1,1), wc),
            new(new Vec3(-0.5f,0, 0.5f), new Vec3(0,1,0), new Vec2(0,1), wc),
        });
        m.SetInds(new uint[]{0,1,2,0,2,3});
        _planetGlassQuad = m;
        return m;
    }

    private void DrawPlanetarium(int shape, Vec3 center, float w, float d, float topY)
    {
        float t = (float)Time.Totalf;
        // Load the game's own assets once (from the resource root, like every other sheet).
        _planetBaseTex ??= _sprites.LoadGfxTexture("backdrop/planetarium_blue_base.png");
        for (int i = 0; i < 5; i++)
            _planetStarTex[i] ??= _sprites.LoadGfxTexture($"backdrop/planetarium_starfield_{i+1}.png");
        // Glass floor: chosen by room shape (IH=2 thin horizontal, IV=3 thin vertical).
        string glassRel = shape == 2 ? "backdrop/planetarium_ih.png"
                        : shape == 3 ? "backdrop/planetarium_iv.png"
                        :              "backdrop/planetarium.png";
        Tex? glass = _sprites.LoadGfxTexture(glassRel);

        float radius = MathF.Max(w, d) * 0.5f * PlanetRadiusMul;
        Vec3 sc = new(center.x, topY + PlanetSphereY, center.z);

        // 1) Blue base sphere — an INSIDE-OUT backdrop dome. Cull.Front keeps only the FAR
        //    interior shell (the near hemisphere is culled, so it never draws in front of the
        //    diorama or the HUD). DepthWrite off + no depth occlusion → pure background.
        //    The base art is very dark navy (13,23,50); brighten it so it reads as blue.
        if (_planetBaseTex != null)
        {
            _planetBaseMat ??= new Material(Shader.Unlit){ FaceCull=Cull.Front, DepthWrite=false };
            _planetBaseMat[MatParamName.DiffuseTex] = _planetBaseTex;
            PlanetSphere().Draw(_planetBaseMat, Matrix.TS(sc, new Vec3(radius*2, radius*2, radius*2)),
                                new Color(PlanetBaseBright, PlanetBaseBright, PlanetBaseBright*1.12f, 1f));
        }

        // 2) Star shells — nested slightly inside the base, each rotating at its own speed for
        //    parallax. The star art has an OPAQUE BLACK background, so it must be ADDITIVE
        //    (black adds nothing, star pixels glow over the base). Cull.Front like the base so
        //    only the far shell shows. Separate material per texture (a shared one batches to one tex).
        for (int i = 0; i < 5; i++)
        {
            var st = _planetStarTex[i]; if (st == null) continue;
            var mat = _planetStarMat[i] ??= new Material(Shader.Unlit)
            { Transparency=Transparency.Add, FaceCull=Cull.Front, DepthWrite=false };
            mat[MatParamName.DiffuseTex] = st;
            float rr   = radius * (0.97f - i*0.015f);
            float spin = t * PlanetStarSpin * (1f + i*0.35f);
            PlanetSphere().Draw(mat, Matrix.TRS(sc, Quat.FromAngles(0, spin, i*23f),
                                                new Vec3(rr*2, rr*2, rr*2)));
        }

        // 3) Glass floor on the floor plane, slightly raised, semi-transparent so the stars
        //    read through it.
        if (glass != null)
        {
            _planetGlassMat ??= new Material(Shader.Unlit)
            { Transparency=Transparency.Blend, FaceCull=Cull.None, DepthWrite=false };
            _planetGlassMat[MatParamName.DiffuseTex] = glass;
            float fw = (w + 2f*WallOutsetM) * PlanetFloorScale, fd = (d + 2f*WallOutsetM) * PlanetFloorScale;
            PlanetGlassQuad().Draw(_planetGlassMat,
                Matrix.TS(new Vec3(center.x, topY + PlanetFloorLift, center.z), new Vec3(fw, 1f, fd)),
                new Color(1,1,1, PlanetFloorAlpha));
        }
    }

    // One flat quad, UV 0..1, top row (v=0) at far (-z), bottom (v=1) at near (+z).
    private Mesh FloorFullQuad()
    {
        if (_floorFullQuad != null) return _floorFullQuad;
        Color32 w = new(255,255,255,255);
        float u0=_uv0x, v0=_uv0y, u1=_uv1x, v1=_uv1y;   // cropped to room content
        var m = new Mesh();
        m.SetVerts(new Vertex[]{
            new(new Vec3(-0.5f,0,-0.5f), new Vec3(0,1,0), new Vec2(u0,v0), w),
            new(new Vec3( 0.5f,0,-0.5f), new Vec3(0,1,0), new Vec2(u1,v0), w),
            new(new Vec3( 0.5f,0, 0.5f), new Vec3(0,1,0), new Vec2(u1,v1), w),
            new(new Vec3(-0.5f,0, 0.5f), new Vec3(0,1,0), new Vec2(u0,v1), w),
        });
        m.SetInds(new uint[]{0,1,2,0,2,3});
        _floorFullQuad = m;
        return m;
    }

    // The APP parses backdrops.xml itself. The Lua side cannot read it reliably
    // (REPENTOGON changes what io.open sees — it reported xml=0), and the app
    // already knows the resource root, so this removes the dependency entirely.
    private sealed class BdDef { public int Id; public string Name="", Gfx="", NFloor=""; public int Walls=1; }
    private readonly HashSet<int> _bdLogged = new();   // one BACKDROP line per backdropType
    private List<BdDef>? _xml;

    private List<BdDef> Xml()
    {
        if (_xml != null) return _xml;
        _xml = new List<BdDef>();
        try
        {
            // Try a few locations: the extractor's layout varies, and REPENTOGON
            // runs the game from a Repentogon\ subfolder so relative paths shifted.
            string? abs = null;
            foreach (var cand in new[]{ "backdrops.xml", "content/backdrops.xml",
                                        "resources/backdrops.xml", "xml/backdrops.xml" })
            { abs = _sprites.FindFile(cand); if (abs != null) break; }
            if (abs == null)
            {
                Log.Info("[Diorama] backdrops.xml NOT FOUND under the resource roots");
                return _xml;
            }
            string text = System.IO.File.ReadAllText(abs);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         text, "<backdrop([^>]*)>",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string a = m.Groups[1].Value;
                string Attr(string k)
                {
                    var mm = System.Text.RegularExpressions.Regex.Match(
                        a, k + "\\s*=\\s*\"([^\"]*)\"",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    return mm.Success ? mm.Groups[1].Value : "";
                }
                string gfx = Attr("gfx");
                if (gfx.Length > 0)
                {
                    // `walls` is the game's own wall on/off count for this backdrop.
                    // 0 = the room has NO walls (Dark Room). Default 1 if absent.
                    string wa = Attr("walls");
                    int walls = 1;
                    if (wa.Length > 0 && int.TryParse(wa, out var wv)) walls = wv;
                    var def = new BdDef { Name = Attr("name"), Gfx = gfx,
                                          NFloor = Attr("nfloorgfx"), Walls = walls };
                    // BackdropType IS the xml `id` (1-based), so the list must be
                    // ID-INDEXED, not append-ordered. backdrops.xml contains EMPTY
                    // placeholder rows -- `<backdrop id="29" />`, and likewise 55/56/57 --
                    // which have no gfx and were therefore skipped. Appending shifted every
                    // id above 29 down by one, so Corpse I (34) resolved to id 35,
                    // `planetarium.anm2`, and rendered as the starry dome; Corpse II (43)
                    // picked up Corpse III's art, and Mom's Bedroom (51) got
                    // `house_closet.anm2` -- an anm2, which cannot load as a wall texture,
                    // so that room lost its walls. Place each row AT its id and pad the
                    // gaps. (The off-by-one hid for so long because the two rows ever
                    // spot-checked, Cellar=2 and Burning Basement=3, are both BELOW the
                    // first gap and so resolved correctly.)
                    string ia = Attr("id");
                    // Upper bound so a malformed id can't make us pad a huge list.
                    if (ia.Length > 0 && int.TryParse(ia, out var idv) && idv > 0 && idv <= 4096)
                    {
                        def.Id = idv;
                        while (_xml.Count < idv) _xml.Add(new BdDef());   // pad the holes
                        _xml[idv - 1] = def;
                    }
                    else _xml.Add(def);    // no id attribute: fall back to file order
                }
            }
            int real = 0; foreach (var d in _xml) if (d.Gfx.Length > 0) real++;
            Log.Info($"[Diorama] parsed {real} backdrops (id-indexed, {_xml.Count} slots) from {abs}");
        }
        catch (Exception ex) { Log.Info($"[Diorama] backdrops.xml parse failed: {ex.Message}"); }
        return _xml;
    }

    private Material MatFor(Tex tex)
    {
        if (_mat.TryGetValue(tex, out var m)) return m;
        m = new Material(Shader.Unlit) { FaceCull = Cull.None };
        m[MatParamName.DiffuseTex] = tex;
        _mat[tex] = m;
        return m;
    }

    private Mesh FloorQuad(Tex tex, Rect c)
    {
        string k = $"F{tex.Width}x{tex.Height}:{c.X},{c.Y},{c.W},{c.H}:{FloorRotate}";
        if (_meshCache.TryGetValue(k, out var cached)) return cached;
        float u0=(float)c.X/tex.Width,          v0=(float)c.Y/tex.Height;
        float u1=(float)(c.X+c.W)/tex.Width,    v1=(float)(c.Y+c.H)/tex.Height;
        Vec2[] uv = { new(u0,v0), new(u1,v0), new(u1,v1), new(u0,v1) };
        int r = ((FloorRotate % 4) + 4) % 4;
        Color32 w = new(255,255,255,255);
        var m = new Mesh();
        m.SetVerts(new Vertex[]{
            new(new Vec3(-0.5f,0,-0.5f), new Vec3(0,1,0), uv[(0+r)%4], w),
            new(new Vec3( 0.5f,0,-0.5f), new Vec3(0,1,0), uv[(1+r)%4], w),
            new(new Vec3( 0.5f,0, 0.5f), new Vec3(0,1,0), uv[(2+r)%4], w),
            new(new Vec3(-0.5f,0, 0.5f), new Vec3(0,1,0), uv[(3+r)%4], w),
        });
        m.SetInds(new uint[]{0,1,2,0,2,3});
        _meshCache[k] = m;
        return m;
    }

    private Mesh WallQuad(Tex tex, Rect c, bool mirror)
    {
        string k = $"W{tex.Width}x{tex.Height}:{c.X},{c.Y},{c.W},{c.H}:{mirror}";
        if (_meshCache.TryGetValue(k, out var cached)) return cached;
        float u0=(float)c.X/tex.Width,       v0=(float)c.Y/tex.Height;
        float u1=(float)(c.X+c.W)/tex.Width, v1=(float)(c.Y+c.H)/tex.Height;
        if (mirror) { var t=u0; u0=u1; u1=t; }
        Color32 w = new(255,255,255,255);
        var m = new Mesh();
        m.SetVerts(new Vertex[]{
            new(new Vec3(-0.5f,1,0), new Vec3(0,0,1), new Vec2(u0,v0), w),
            new(new Vec3( 0.5f,1,0), new Vec3(0,0,1), new Vec2(u1,v0), w),
            new(new Vec3( 0.5f,0,0), new Vec3(0,0,1), new Vec2(u1,v1), w),
            new(new Vec3(-0.5f,0,0), new Vec3(0,0,1), new Vec2(u0,v1), w),
        });
        m.SetInds(new uint[]{0,1,2,0,2,3});
        _meshCache[k] = m;
        return m;
    }

    /// <summary>One wall run: two mirrored halves, like the game builds them.</summary>
    private void DrawWallRun(Material mat, Tex tex, Vec3 pos, Quat facing, float span)
    {
        if (span <= 0.001f) return;
        var meshL = WallQuad(tex, WallCrop, false);
        var meshR = WallQuad(tex, WallCrop, true);
        Vec3 lp = pos + facing * new Vec3(-span/4f, 0, 0);
        Vec3 rp = pos + facing * new Vec3( span/4f, 0, 0);
        meshL.Draw(mat, Matrix.TRS(lp, facing, new Vec3(span/2f, WallHeightM, 1)), DimCol);
        meshR.Draw(mat, Matrix.TRS(rp, facing, new Vec3(span/2f, WallHeightM, 1)), DimCol);
    }

    /// <summary>Is this floor tile inside the L-shape's missing quadrant?</summary>
    /// <summary>True when (rx,rz) — floor-relative, -0.5..0.5 — falls in an L-room's cut-out
    /// corner. Public so the darkness veil can leave the notch fully transparent.</summary>
    public static bool InMissingQuadrant(int shape, float rx, float rz)
    {
        // rx/rz are -0.5..+0.5 relative to room centre.
        // Game "top" (small Y) maps to -z (far); "left" (small X) maps to -x.
        return shape switch
        {
            SHAPE_LTL => rx < 0 && rz < 0,   // far-left
            SHAPE_LTR => rx > 0 && rz < 0,   // far-right
            SHAPE_LBL => rx < 0 && rz > 0,   // near-left
            SHAPE_LBR => rx > 0 && rz > 0,   // near-right
            _ => false,
        };
    }

    /// <summary>The room's water, set by Program each frame before Draw.</summary>
    public UdpReceiver.WaterState Water;

    public bool Draw(int backdropType, int shape, int roomType, string? wallFromXml, string? floorFromXml,
                     string? name, Vec3 center, float w, float d, float topY)
    {
        // One line per backdrop type, naming the row the id-indexed lookup resolved to.
        // A wrong stage here is the single most informative symptom this renderer has: it
        // is what would have named the Corpse-as-planetarium shift in one glance.
        if (_bdLogged.Add(backdropType))
        {
            var dl = Xml(); int di = backdropType - 1;
            string dg = (di >= 0 && di < dl.Count) ? dl[di].Gfx : "(out of range)";
            int dw = (di >= 0 && di < dl.Count) ? dl[di].Walls : -1;
            Log.Info($"[Diorama] BACKDROP type={backdropType} -> gfx='{dg}' walls={dw} " +
                     $"roomType={roomType} wallFromMod='{wallFromXml}' name='{name}'");
        }

        // Planetarium: gets a fully custom render — a starry sphere around the diorama +
        // a glass floor, no walls. Detect it by ROOM TYPE (24 = ROOM_PLANETARIUM), which the
        // mod reports directly; fall back to the resolved def's gfx string for older packets.
        // That gfx fallback is what turned Corpse I into a starry dome while the xml list was
        // append-ordered; it is correct now the list is id-indexed, and is kept only so a
        // packet without the roomType byte still finds a real planetarium.
        {
            bool isPlanetarium = roomType == 24
                || (!string.IsNullOrEmpty(wallFromXml) && wallFromXml.ToLowerInvariant().Contains("planetarium"));
            if (!isPlanetarium)
            {
                var pl = Xml(); int pi = backdropType - 1;
                if (pi >= 0 && pi < pl.Count && pl[pi].Gfx.ToLowerInvariant().Contains("planetarium"))
                    isPlanetarium = true;
            }
            if (isPlanetarium)
            { DrawPlanetarium(shape, center, w, d, topY); return true; }
        }

        // Mod-supplied names win; otherwise fall back to our own parse of
        // backdrops.xml. BackdropType indexes the file 1-based (verified: type 2
        // -> 02_Cellar, type 3 -> 13_The Burning Basement).
        if (string.IsNullOrEmpty(wallFromXml))
        {
            var list = Xml();
            int idx = backdropType - 1;
            if (idx >= 0 && idx < list.Count)
            {
                wallFromXml  = list[idx].Gfx;
                floorFromXml = list[idx].NFloor;
                name         = list[idx].Name;
            }
        }

        // Some backdrops name an ANM2 as their gfx (house_closet, house_closet_b,
        // house_dogma, planetarium). `LoadGfxTexture` can only load an image, so those
        // resolved to null and the room silently lost its walls. Every one of them ships a
        // sibling PNG of the same name, so swap the extension.
        static string PngIfAnm2(string rel) =>
            rel.EndsWith(".anm2", StringComparison.OrdinalIgnoreCase) ? rel[..^5] + ".png" : rel;

        string? wallRel  = !string.IsNullOrEmpty(wallFromXml)  ? "backdrop/"+PngIfAnm2(wallFromXml) : null;
        string? floorRel = !string.IsNullOrEmpty(floorFromXml) ? "backdrop/"+PngIfAnm2(floorFromXml) : null;

        // If we still have no floor name, derive it from the wall name — Isaac's
        // convention is "<wall>_nfloor.png".
        if (floorRel == null && wallRel != null)
        {
            string bnx = wallRel.EndsWith(".png") ? wallRel[..^4] : wallRel;
            foreach (var cand in new[]{ bnx+"_nfloor.png", bnx+"_NFloor.png", bnx+"_floor.png" })
                if (_sprites.LoadGfxTexture(cand) != null) { floorRel = cand; break; }
        }

        Tex? wallTex  = wallRel  != null ? _sprites.LoadGfxTexture(wallRel)  : null;
        Tex? floorTex = floorRel != null ? _sprites.LoadGfxTexture(floorRel) : null;

        bool isL = shape >= SHAPE_LTL && shape <= SHAPE_LBR;

        // Some backdrops declare NO walls (Dark Room: walls="0"). Honour the game's own
        // flag: those rooms render floor-only. Secret/special rooms use a different
        // backdrop (walls>=1), so they keep their walls automatically. Default 1 if
        // the def can't be resolved, so nothing loses walls by accident.
        int backdropWalls = 1;
        {
            var wl = Xml(); int wi = backdropType - 1;
            if (wi >= 0 && wi < wl.Count) backdropWalls = wl[wi].Walls;
        }
        bool hasWalls = backdropWalls != 0;

        // Log once per (type, shape). Keyed on BOTH because walls are gated by the xml row
        // resolved at index (backdropType - 1) — if that index is off by one, `walls=` here
        // will disagree with the real stage and that is the bug. The old log keyed on type
        // alone, so moving between rooms of the SAME stage (the thin/long shapes) never logged.
        int logKey = backdropType * 100 + shape;
        if (logKey != _loggedType)
        {
            _loggedType = logKey;
            Log.Info($"[Diorama] backdrop type={backdropType} shape={shape} name='{name}' " +
                     $"walls={backdropWalls} hasWalls={hasWalls} " +
                     $"floor='{floorRel}' {(floorTex==null?"NOT FOUND":$"{floorTex.Width}x{floorTex.Height}")}  " +
                     $"wall='{wallRel}' {(wallTex==null?"NOT FOUND":$"{wallTex.Width}x{wallTex.Height}")} xml={_lastXmlCount}");
        }

        // The live floor image is the game's own composited floor (blood included).
        // Its clip material discards the transparent missing quadrant, so it now
        // works for L-shaped rooms too. Only bail entirely if we have nothing.
        bool useLiveFloor = HasLiveFloor && _floorLiveUsable && !ForceTiledFloor;
        bool useLiveWalls = HasLiveWall && !ForceTiledWalls && !isL && hasWalls;
        if (floorTex == null && wallTex == null && !useLiveFloor && !useLiveWalls) return false;

        // ---------- Floor ----------
        // Extend past the play area by the wall outset so the floor reaches under
        // the wall bases (otherwise you see a gap and the walls look like they float).
        float fw = w + 2f*WallOutsetM;
        float fd = d + 2f*WallOutsetM;
        if (useLiveFloor)
        {
            FloorFullQuad().Draw(_floorLiveMat!, Matrix.TS(new Vec3(center.x, topY, center.z),
                                                 new Vec3(fw, 1f, fd)));
        }
        else if (floorTex != null)
        {
            var fmat  = MatFor(floorTex);
            var fmesh = FloorQuad(floorTex, FloorCrop);
            int nx = Math.Max(1, (int)MathF.Ceiling(fw / FloorTileM));
            int nz = Math.Max(1, (int)MathF.Ceiling(fd / FloorTileM));
            float tw = fw/nx, td = fd/nz;
            float x0 = center.x - fw/2 + tw/2, z0 = center.z - fd/2 + td/2;
            for (int ix=0; ix<nx; ix++)
            for (int iz=0; iz<nz; iz++)
            {
                float px = x0 + ix*tw, pz = z0 + iz*td;
                if (isL && InMissingQuadrant(shape, (px-center.x)/fw, (pz-center.z)/fd)) continue;
                fmesh.Draw(fmat, Matrix.TS(new Vec3(px, topY, pz), new Vec3(tw, 1f, td)));
            }
        }

        // Level overlay ("looming shadows") laid over the floor, just above it so it also sits
        // over the floor's blood/decals — same plane trick as the Curse of Darkness veil.
        DrawOverlay(wallFromXml, shape, center, fw, fd, topY + 0.0012f);
        // Water sits ON the floor, above the looming-shadow overlay: in game it is a
        // post-process over everything the floor has already drawn, blood and shadow alike.
        DrawWater(Water, shape, center, fw, fd, topY + 0.0012f);

        // ---------- Walls ---------- (skipped entirely when the backdrop declares walls=0)
        if (useLiveWalls)
        {
            DrawLiveWalls(center, w, d, topY);
        }
        else if (wallTex != null && hasWalls)
        {
            var wmat = MatFor(wallTex);
            float ow = w/2 + WallOutsetM, od = d/2 + WallOutsetM;   // pushed outside play area
            var FAR = Quat.FromAngles(0,  0,0);   // faces +z (toward viewer)
            var EAST= Quat.FromAngles(0,-90,0);   // faces -x
            var WEST= Quat.FromAngles(0, 90,0);   // faces +x

            // Spans are extended by the same outset so the runs meet at the corners.
            float spanW = w + 2f*WallOutsetM;
            float spanD = d + 2f*WallOutsetM;
            if (!isL)
            {
                DrawWallRun(wmat, wallTex, new Vec3(center.x, topY, center.z - od), FAR,  spanW);
                DrawWallRun(wmat, wallTex, new Vec3(center.x + ow, topY, center.z), EAST, spanD);
                DrawWallRun(wmat, wallTex, new Vec3(center.x - ow, topY, center.z), WEST, spanD);
            }
            else
            {
                // Outer walls, skipping the halves that border the missing quadrant,
                // then the two inner walls that form the notch.
                bool missFar  = shape == SHAPE_LTL || shape == SHAPE_LTR;
                bool missLeft = shape == SHAPE_LTL || shape == SHAPE_LBL;

                // Far / near outer wall (only the far one is drawn; near stays open for the view)
                float halfW = w/4, halfD = d/4;
                if (missFar)
                {
                    // far wall exists only on the non-missing half
                    float cx = missLeft ? center.x + w/4 : center.x - w/4;
                    DrawWallRun(wmat, wallTex, new Vec3(cx, topY, center.z - od), FAR, spanW/2 + WallOutsetM);
                }
                else
                {
                    DrawWallRun(wmat, wallTex, new Vec3(center.x, topY, center.z - od), FAR, spanW);
                }

                // East wall
                if (!missLeft && missFar)      // missing far-right -> east wall only on near half
                    DrawWallRun(wmat, wallTex, new Vec3(center.x + ow, topY, center.z + d/4), EAST, spanD/2 + WallOutsetM);
                else if (!missLeft && !missFar) // missing near-right
                    DrawWallRun(wmat, wallTex, new Vec3(center.x + ow, topY, center.z - d/4), EAST, spanD/2 + WallOutsetM);
                else
                    DrawWallRun(wmat, wallTex, new Vec3(center.x + ow, topY, center.z), EAST, spanD);

                // West wall
                if (missLeft && missFar)        // missing far-left
                    DrawWallRun(wmat, wallTex, new Vec3(center.x - ow, topY, center.z + d/4), WEST, spanD/2 + WallOutsetM);
                else if (missLeft && !missFar)  // missing near-left
                    DrawWallRun(wmat, wallTex, new Vec3(center.x - ow, topY, center.z - d/4), WEST, spanD/2 + WallOutsetM);
                else
                    DrawWallRun(wmat, wallTex, new Vec3(center.x - ow, topY, center.z), WEST, spanD);

                // Inner notch walls (the two edges bordering the missing quadrant).
                // The horizontal one stands mid-room FACING the viewer and reads as a
                // wall across the bottom, blocking the view into the room — so it's off
                // by default. The vertical one runs front-to-back (seen edge-on) and is
                // kept for a little enclosure. Flip DrawLNotchWalls to restore both.
                if (DrawLNotchWalls)
                {
                    float notchX = center.x + (missLeft ? -w/4 : w/4);
                    float notchZ = center.z + (missFar  ? -d/4 : d/4);
                    // vertical edge of the notch (runs along z), faces into the room
                    DrawWallRun(wmat, wallTex, new Vec3(center.x, topY, notchZ),
                                missLeft ? EAST : WEST, spanD/2 + WallOutsetM);
                    // horizontal edge of the notch (runs along x), faces into the room
                    DrawWallRun(wmat, wallTex, new Vec3(notchX, topY, center.z), FAR, spanW/2);
                }
            }
        }
        return true;
    }
}
