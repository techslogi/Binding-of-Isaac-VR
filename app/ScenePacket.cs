using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace IsaacDiorama;

public enum EntityCategory : byte
{ Player=0, Enemy=1, Pickup=2, Projectile=3, Effect=4, Grid=5, Other=6, Door=7 }

public readonly struct SceneEntity
{
    public readonly float X, Y;
    public readonly EntityCategory Cat;
    public readonly bool  FlipX;
    public readonly bool  IsFlat;      // eflags bit1: lies flat on the floor
    public readonly bool  IsDestroyed; // eflags bit2: grid piece already broken
    public readonly float Scale;
    public readonly float HeightPx;
    public readonly int   Anm2Id, AnimId, AnimFrame;
    public readonly float R, G, B, A;      // 0..1
    public readonly float RotationDeg;

    public SceneEntity(float x, float y, EntityCategory cat, bool flip, float scale,
                       float height, int anm2, int anim, int frame,
                       float r, float g, float b, float a, float rot, bool flat=false, bool destroyed=false)
    { X=x; Y=y; Cat=cat; FlipX=flip; IsFlat=flat; IsDestroyed=destroyed; Scale=scale; HeightPx=height;
      Anm2Id=anm2; AnimId=anim; AnimFrame=frame; R=r; G=g; B=b; A=a; RotationDeg=rot; }
}

/// <summary>Thread-safe interned-string table, fed by MSG_STRINGS datagrams.</summary>
public sealed class StringTable
{
    private readonly ConcurrentDictionary<int,string> _map = new();
    public void Set(int id, string s) => _map[id] = s;
    public void Clear() => _map.Clear();
    public string? Get(int id) => _map.TryGetValue(id, out var s) ? s : null;
    public int Count => _map.Count;

    /// <summary>Parse a MSG_STRINGS (type 3) datagram into the table.</summary>
    public static void ParseInto(ReadOnlySpan<byte> b, StringTable t)
    {
        if (b.Length < 6 || b[0]!=0x49 || b[1]!=1 || b[2]!=3) return;
        // flags bit0 = full refresh: drop stale ids first. Without this, a game
        // restart reuses ids for different sprites and entities render as the
        // wrong thing (e.g. an enemy drawn with Isaac's spritesheet).
        if ((b[3] & 1) != 0) t.Clear();
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(4,2));
        int off = 6;
        for (int i=0;i<count;i++)
        {
            if (off+4 > b.Length) return;
            int id  = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off,2));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(off+2,2));
            off += 4;
            if (off+len > b.Length) return;
            string s = Encoding.UTF8.GetString(b.Slice(off,len));
            t.Set(id, s);
            off += len;
        }
    }
}

/// <summary>
/// MSG_SCENE2 (type 4). Entity layout (32 bytes):
///   f x, f y, B cat, B eflags(bit0 flip), f scale, f heightPx,
///   H anm2Id, H animId, H animFrame, B r,B g,B b,B a, f rotationDeg
/// </summary>
public sealed class ScenePacket
{
    public const byte Magic=0x49, Version=1, MsgScene2=4;
    private const int HeaderSize = 26;
    private const int EntSize    = 32;

    public uint  Frame { get; private set; }
    public float RoomMinX, RoomMinY, RoomMaxX, RoomMaxY;
    public readonly List<SceneEntity> Entities = new();

    public static bool TryParse(ReadOnlySpan<byte> b, ScenePacket into)
    {
        if (b.Length < HeaderSize) return false;
        if (b[0]!=Magic || b[1]!=Version || b[2]!=MsgScene2) return false;

        into.Frame    = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(4,4));
        into.RoomMinX = ReadF32(b,8);  into.RoomMinY = ReadF32(b,12);
        into.RoomMaxX = ReadF32(b,16); into.RoomMaxY = ReadF32(b,20);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(24,2));
        if (b.Length < HeaderSize + count*EntSize) return false;

        into.Entities.Clear();
        int o = HeaderSize;
        for (int i=0;i<count;i++)
        {
            float x = ReadF32(b,o);
            float y = ReadF32(b,o+4);
            var cat = (EntityCategory)b[o+8];
            bool fl   = (b[o+9] & 1) != 0;
            bool flat = (b[o+9] & 2) != 0;
            bool dead = (b[o+9] & 4) != 0;
            float sc= ReadF32(b,o+10);
            float hp= ReadF32(b,o+14);
            int a2  = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+18,2));
            int an  = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+20,2));
            int fr  = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+22,2));
            float r = b[o+24]/255f, g = b[o+25]/255f, bl = b[o+26]/255f, al = b[o+27]/255f;
            float rot = ReadF32(b,o+28);
            into.Entities.Add(new SceneEntity(x,y,cat,fl,sc,hp,a2,an,fr,r,g,bl,al,rot,flat,dead));
            o += EntSize;
        }
        return true;
    }

    public (float nx, float ny) Normalise(float wx, float wy)
    {
        float w=RoomMaxX-RoomMinX, h=RoomMaxY-RoomMinY;
        float nx = w>1e-3f ? (wx-RoomMinX)/w : 0.5f;
        float ny = h>1e-3f ? (wy-RoomMinY)/h : 0.5f;
        return (nx, ny);
    }

    private static float ReadF32(ReadOnlySpan<byte> b, int off) =>
        BinaryPrimitives.ReadSingleLittleEndian(b.Slice(off,4));
}
