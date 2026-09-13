using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace IsaacDiorama;

/// <summary>
/// MSG_LASERS (type 13): laser beams (brimstone / technology / tech-x / etc).
/// Each beam is its real SAMPLE POLYLINE in Isaac world coords (2 points = a
/// straight beam, many = curvy/homing), plus variant, colour, sprite-scale and
/// collision radius. Circle lasers (Tech X, tractor ring) carry no polyline; the
/// app draws a ring from centre (CX,CY) + Radius instead.
///
///   header : "&lt;BBBBH"  magic, ver, msg(13), 0, laserCount
///   laser  : "&lt;BBBBBBBBBBffffH" variant, FLAGS, r,g,b,a, colorizeR,G,B,A, scale, radius, cx, cy, sampleCount
///   FLAGS  : bit0 = circle laser, bit1 = fired by Dogma (render as TV static, not red)
///   sample : "&lt;ff" x, y   (repeated sampleCount times)
/// </summary>
public sealed class LaserData
{
    public int    Variant;
    public bool   IsCircle;
    public bool   IsDogma;      // spawner is Dogma: the game shades this beam as TV static
    public float  R, G, B, A;
    public float  Cr, Cg, Cb, Ca;   // colorize hue (normalised) + amount (0 = none)
    public float  Scale, Radius, CX, CY;
    public float[] Xs = Array.Empty<float>();
    public float[] Ys = Array.Empty<float>();
    public int SampleCount => Xs.Length;
}

public static class LaserScene
{
    public const byte Magic = 0x49, MsgLasers = 13;

    public static bool TryParse(ReadOnlySpan<byte> b, List<LaserData> into)
    {
        into.Clear();
        if (b.Length < 6 || b[0] != Magic || b[2] != MsgLasers) return false;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(4, 2));
        int o = 6;
        for (int i = 0; i < count; i++)
        {
            if (o + 28 > b.Length) return false;               // fixed per-laser header (10B + 4f + 1H)
            var L = new LaserData {
                Variant  = b[o],
                IsCircle = (b[o + 1] & 1) != 0,
                IsDogma  = (b[o + 1] & 2) != 0,
                R = b[o + 2] / 255f, G = b[o + 3] / 255f, B = b[o + 4] / 255f, A = b[o + 5] / 255f,
                Cr = b[o + 6] / 255f, Cg = b[o + 7] / 255f, Cb = b[o + 8] / 255f, Ca = b[o + 9] / 255f,
                Scale  = F(b, o + 10),
                Radius = F(b, o + 14),
                CX = F(b, o + 18),
                CY = F(b, o + 22),
            };
            int npt = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o + 26, 2));
            o += 28;
            if (o + npt * 8 > b.Length) return false;
            var xs = new float[npt];
            var ys = new float[npt];
            for (int k = 0; k < npt; k++)
            {
                xs[k] = F(b, o); ys[k] = F(b, o + 4);
                o += 8;
            }
            L.Xs = xs; L.Ys = ys;
            into.Add(L);
        }
        return true;
    }

    private static float F(ReadOnlySpan<byte> b, int off) =>
        BinaryPrimitives.ReadSingleLittleEndian(b.Slice(off, 4));
}
