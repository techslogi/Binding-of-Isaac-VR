using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace IsaacDiorama;

/// <summary>A resolved keyframe for one layer (source crop + destination transform).</summary>
public struct Anm2Frame
{
    public int   XCrop, YCrop, Width, Height;      // source rect in spritesheet px
    public float XPos, YPos, XPivot, YPivot;       // placement + pivot (px)
    public float XScale, YScale;                   // 1.0 = 100%
    public float Rotation;                         // degrees
    public bool  Visible;
    public float R, G, B, A;                       // tint 0..1 (multiply)
    public float RO, GO, BO;                        // colour offset -1..1 (added AFTER tint)
}

public sealed class Anm2Layer { public int Id; public string Name=""; public int SpritesheetId; }

public sealed class Anm2Anim
{
    public string Name = "";
    public int FrameNum;
    public bool Loop = true;
    // layerId -> ordered keyframes; each with its cumulative start tick.
    public readonly Dictionary<int,List<(int start,int delay,Anm2Frame f)>> Layers = new();
    // nullId -> ordered keyframes of an ANCHOR POINT (position only, no art). Isaac uses
    // these to park a separately-animated sprite on a moving body part — see Anm2.NullPos.
    public readonly Dictionary<int,List<(int start,int delay,float x,float y)>> Nulls = new();
    // Draw order for THIS animation = the order its <LayerAnimation> elements appear,
    // which the engine honors (it can differ from ascending LayerId — e.g. a pickup
    // draws its sparkle behind the item). Empty = fall back to ascending Id.
    public readonly List<int> DrawOrder = new();
}

/// <summary>Parses one .anm2 file and resolves (animation, frame) -> active layer frames.</summary>
public sealed class Anm2
{
    public readonly Dictionary<int,string> Spritesheets = new();  // id -> path (relative to gfx/)
    public readonly Dictionary<string,int> NullIdByName = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<Anm2Layer> Layers = new();               // ascending Id (fallback order)
    public readonly Dictionary<int,Anm2Layer> LayerById = new();  // id -> layer, for per-anim draw order
    public readonly Dictionary<string,Anm2Anim> Anims = new(StringComparer.OrdinalIgnoreCase);
    public string DefaultAnim = "";
    public string RootName = "";

    private static readonly Dictionary<string,Anm2?> _cache = new();

    public static Anm2? Load(string absPath)
    {
        if (_cache.TryGetValue(absPath, out var cached)) return cached;
        Anm2? result = null;
        try { result = Parse(absPath); }
        catch { result = null; }
        _cache[absPath] = result;
        return result;
    }

    private static float F(XElement e, string a, float def=0f)
    {
        var v = e.Attribute(a)?.Value;
        return v!=null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
    }
    private static int I(XElement e, string a, int def=0)
    {
        var v = e.Attribute(a)?.Value;
        return v!=null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;
    }
    private static bool B(XElement e, string a, bool def=true)
    {
        var v = e.Attribute(a)?.Value;
        if (v==null) return def;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v=="1";
    }
    // Colour offset, normalised to -1..1. anm2 files store it either as a float
    // (-1..1) or as a signed 0-255-scale integer depending on the tool/version;
    // if the magnitude is >1 assume the 255 scale.
    private static float Off(XElement e, string a)
    {
        float v = F(e, a, 0f);
        if (MathF.Abs(v) > 1.001f) v /= 255f;
        return v;
    }

    private static Anm2 Parse(string path)
    {
        var doc  = XDocument.Load(path);
        var root = doc.Root!;
        var anm  = new Anm2();
        anm.RootName = root.Name.LocalName;

        // Match elements by LOCAL name anywhere in the tree — robust to XML
        // namespaces and to Info/Content wrapper differences.
        IEnumerable<XElement> Desc(string ln) => root.Descendants().Where(e => e.Name.LocalName == ln);

        foreach (var srt in Desc("Spritesheet"))
            anm.Spritesheets[I(srt,"Id")] = (srt.Attribute("Path")?.Value ?? "").Replace('\\','/');

        foreach (var l in Desc("Layer"))   // definition layers (LayerAnimation is a different name)
            anm.Layers.Add(new Anm2Layer{ Id=I(l,"Id"), Name=l.Attribute("Name")?.Value??"",
                                          SpritesheetId=I(l,"SpritesheetId") });
        anm.Layers.Sort((a,b)=>a.Id.CompareTo(b.Id));
        foreach (var ly in anm.Layers) anm.LayerById[ly.Id] = ly;

        foreach (var nl in Desc("Null"))
        {
            string nname = nl.Attribute("Name")?.Value ?? "";
            if (nname.Length > 0) anm.NullIdByName[nname] = I(nl,"Id");
        }

        var animsEl = Desc("Animations").FirstOrDefault();
        anm.DefaultAnim = animsEl?.Attribute("DefaultAnimation")?.Value ?? "";

        foreach (var a in Desc("Animation"))
        {
            var an = new Anm2Anim{ Name=a.Attribute("Name")?.Value??"",
                                   FrameNum=I(a,"FrameNum"), Loop=B(a,"Loop",true) };
            foreach (var lan in a.Descendants().Where(e=>e.Name.LocalName=="LayerAnimation"))
            {
                int layerId = I(lan,"LayerId");
                bool layerVis = B(lan,"Visible",true);
                if (!an.DrawOrder.Contains(layerId)) an.DrawOrder.Add(layerId);  // engine draw order
                var list = new List<(int,int,Anm2Frame)>();
                int tick = 0;
                foreach (var fe in lan.Elements().Where(e=>e.Name.LocalName=="Frame"))
                {
                    int delay = Math.Max(1, I(fe,"Delay",1));
                    var f = new Anm2Frame{
                        XCrop=I(fe,"XCrop"), YCrop=I(fe,"YCrop"),
                        Width=I(fe,"Width"), Height=I(fe,"Height"),
                        XPos=F(fe,"XPosition"), YPos=F(fe,"YPosition"),
                        XPivot=F(fe,"XPivot"), YPivot=F(fe,"YPivot"),
                        XScale=F(fe,"XScale",100f)/100f, YScale=F(fe,"YScale",100f)/100f,
                        Rotation=F(fe,"Rotation"),
                        Visible=layerVis && B(fe,"Visible",true),
                        R=I(fe,"RedTint",255)/255f,  G=I(fe,"GreenTint",255)/255f,
                        B=I(fe,"BlueTint",255)/255f, A=I(fe,"AlphaTint",255)/255f,
                        RO=Off(fe,"RedOffset"), GO=Off(fe,"GreenOffset"), BO=Off(fe,"BlueOffset"),
                    };
                    list.Add((tick, delay, f));
                    tick += delay;
                }
                if (list.Count>0) an.Layers[layerId] = list;
            }
            foreach (var nan in a.Descendants().Where(e=>e.Name.LocalName=="NullAnimation"))
            {
                int nullId = I(nan,"NullId");
                var nlist = new List<(int,int,float,float)>();
                int ntick = 0;
                foreach (var fe in nan.Elements().Where(e=>e.Name.LocalName=="Frame"))
                {
                    int delay = Math.Max(1, I(fe,"Delay",1));
                    nlist.Add((ntick, delay, F(fe,"XPosition"), F(fe,"YPosition")));
                    ntick += delay;
                }
                if (nlist.Count>0) an.Nulls[nullId] = nlist;
            }
            if (!string.IsNullOrEmpty(an.Name)) anm.Anims[an.Name] = an;
        }
        return anm;
    }

    /// <summary>
    /// Position of a named ANCHOR NULL at (animation, frame), in sprite pixels, or null if
    /// this anm2 has no such null or the animation does not animate it.
    ///
    /// Isaac uses nulls to park a separately-animated sprite onto a moving body part. The
    /// Flesh Maiden (880.0) is the clean example: her SwingLeft/SwingRight body animations
    /// declare layer 1 (head) as an EMPTY LayerAnimation, and her head is played as the
    /// OVERLAY animation `HeadLook`, whose own frames sit at (0,0). All of the head's
    /// placement lives in the body animation's `HeadPos` null — so an overlay drawn without
    /// it lands on the entity origin and the head sits inside the body.
    /// </summary>
    public bool NullPos(string animName, int frameIdx, string nullName, out float x, out float y)
    {
        x = 0; y = 0;
        if (!NullIdByName.TryGetValue(nullName, out int nid)) return false;
        if (!Anims.TryGetValue(animName, out var an)) return false;
        if (!an.Nulls.TryGetValue(nid, out var keys) || keys.Count == 0) return false;
        // Same frame walk as Resolve: wrap a looping animation, clamp a one-shot.
        int total = keys[^1].start + keys[^1].delay;
        int idx = frameIdx;
        if (an.Loop && total > 0) idx %= total;
        if (idx >= total) idx = total - 1;
        foreach (var (start, delay, kx, ky) in keys)
            if (idx >= start && idx < start + delay) { x = kx; y = ky; return true; }
        x = keys[^1].x; y = keys[^1].y;
        return true;
    }

    /// <summary>
    /// True when a NON-looping animation has already played past its last frame.
    /// One-shot effects (smoke, poofs) linger in the entity list after the game
    /// has stopped drawing them; this lets the renderer drop them instead of
    /// freezing on the final frame.
    /// </summary>
    public bool IsFinishedAt(string animName, int frameIdx)
    {
        if (!Anims.TryGetValue(animName, out var an))
            if (!Anims.TryGetValue(DefaultAnim, out an)) return false;
        if (an!.Loop) return false;
        int total = 0;
        foreach (var kv in an.Layers)
        { var keys = kv.Value; if (keys.Count==0) continue;
          int t = keys[^1].start + keys[^1].delay; if (t > total) total = t; }
        return total > 0 && frameIdx >= total;
    }

    public bool HasAnimation(string name) => Anims.ContainsKey(name);

    /// <summary>Per-layer anm2 state at a frame: does it have keyframes, is it visible, its size.</summary>
    public List<string> ExplainLayers(string animName, int frameIdx)
    {
        var outp = new List<string>();
        if (!Anims.TryGetValue(animName, out var an) && !Anims.TryGetValue(DefaultAnim, out an))
        { outp.Add("no animation"); return outp; }
        foreach (var layer in Layers)
        {
            if (!an!.Layers.TryGetValue(layer.Id, out var keys) || keys.Count==0)
            { outp.Add($"L{layer.Id} '{layer.Name}' NO-KEYFRAMES sheet{layer.SpritesheetId}"); continue; }
            int tot = keys[^1].start + keys[^1].delay;
            int idx = frameIdx; if (an.Loop && tot>0) idx %= tot; if (idx>=tot) idx = tot-1;
            var f = keys[^1].f;
            foreach (var (st,dl,fr) in keys) if (idx>=st && idx<st+dl) { f=fr; break; }
            outp.Add($"L{layer.Id} '{layer.Name}' vis={f.Visible} size={f.Width}x{f.Height} " +
                     $"crop=({f.XCrop},{f.YCrop}) sheet{layer.SpritesheetId}");
        }
        return outp;
    }

    /// <summary>Why did a resolve produce nothing? For diagnostics.</summary>
    public string Explain(string animName, int frameIdx)
    {
        bool found = Anims.TryGetValue(animName, out var an);
        if (!found && !Anims.TryGetValue(DefaultAnim, out an))
            return $"anim '{animName}' NOT FOUND and no default";
        string via = found ? "found" : $"MISSING -> fell back to '{DefaultAnim}'";
        int total = 0;
        foreach (var kv in an!.Layers)
        { var k = kv.Value; if (k.Count==0) continue; int t = k[^1].start + k[^1].delay; if (t>total) total=t; }
        int invisible=0, zeroSize=0, noKeys=0, ok=0;
        foreach (var layer in Layers)
        {
            if (!an.Layers.TryGetValue(layer.Id, out var keys) || keys.Count==0) { noKeys++; continue; }
            int idx = frameIdx; int tot = keys[^1].start + keys[^1].delay;
            if (an.Loop && tot>0) idx %= tot;
            if (idx >= tot) idx = tot-1;
            var chosen = keys[^1].f;
            foreach (var (st,dl,f) in keys) if (idx>=st && idx<st+dl) { chosen=f; break; }
            if (!chosen.Visible) invisible++;
            else if (chosen.Width<=0 || chosen.Height<=0) zeroSize++;
            else ok++;
        }
        return $"anim {via}, loop={an.Loop}, totalFrames={total}, req f{frameIdx} | " +
               $"layers ok={ok} invisible={invisible} zeroSize={zeroSize} noKeysForAnim={noKeys}";
    }

    /// <summary>Active frame per layer at the given animation + integer frame index.</summary>
    public List<(Anm2Layer layer, Anm2Frame frame)> Resolve(string animName, int frameIdx,
        System.Collections.Generic.Dictionary<int,LayerOverride>? overrides = null)
    {
        var outList = new List<(Anm2Layer,Anm2Frame)>();
        if (!Anims.TryGetValue(animName, out var an))
            if (!Anims.TryGetValue(DefaultAnim, out an)) return outList;

        // Draw in the animation's own layer order (engine semantics); fall back to
        // ascending Id when the animation didn't declare one.
        IEnumerable<Anm2Layer> drawLayers;
        if (an!.DrawOrder.Count > 0)
        {
            var tmp = new List<Anm2Layer>(an.DrawOrder.Count);
            foreach (var lid in an.DrawOrder)
                if (LayerById.TryGetValue(lid, out var ly)) tmp.Add(ly);
            drawLayers = tmp;
        }
        else drawLayers = Layers;

        foreach (var layer in drawLayers)   // back-to-front
        {
            if (!an!.Layers.TryGetValue(layer.Id, out var keys) || keys.Count==0) continue;
            int total = keys[^1].start + keys[^1].delay;
            int idx = frameIdx;
            if (an.Loop && total>0) idx %= total;
            if (idx >= total) idx = total-1;

            Anm2Frame chosen = keys[0].f; bool found=false;
            foreach (var (start,delay,f) in keys)
                if (idx >= start && idx < start+delay) { chosen=f; found=true; break; }
            if (!found) chosen = keys[^1].f;
            // BOTH must agree. The anm2 hides layers per-frame (e.g. a door's
            // 'closed' layer once it opens); the runtime hides unequipped costume
            // slots. Using runtime alone re-enabled layers the anm2 legitimately hid.
            bool visible = chosen.Visible;
            if (overrides != null && overrides.TryGetValue(layer.Id, out var ov))
                visible = visible && ov.Visible;
            if (visible && chosen.Width>0 && chosen.Height>0)
                outList.Add((layer, chosen));
        }
        return outList;
    }
}
