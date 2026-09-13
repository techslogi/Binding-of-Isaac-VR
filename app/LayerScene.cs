using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace IsaacDiorama;

/// <summary>
/// Per-layer RUNTIME override from REPENTOGON. LayerState carries the resolved
/// spritesheet (costumes, pedestal art, recolours), visibility, colour and flip —
/// but NOT the frame crop, which still comes from the .anm2.
/// </summary>
public readonly struct LayerOverride
{
    public readonly int  LayerId, SheetId;
    public readonly bool Visible, FlipX, FlipY;
    public readonly float R, G, B, A;
    public LayerOverride(int id,int sheet,bool vis,bool fx,bool fy,float r,float g,float b,float a)
    { LayerId=id; SheetId=sheet; Visible=vis; FlipX=fx; FlipY=fy; R=r; G=g; B=b; A=a; }
}

public sealed class LayerEntity
{
    public float X, Y, Scale, HeightPx, RotationDeg;
    public EntityCategory Cat;
    public bool FlipX, IsFlat, IsDestroyed;
    public bool IsCostume;   // eflags bit3: a costume sprite layered over the player
    public bool IsHurt;      // eflags bit4: player just took damage (hide costumes + flash)
    public bool IsHeld;      // eflags bit5: held item in the pickup pose (lifted above the head)
    public bool IsBodyCostume; // eflags bit7: body-only costume (Cancer/Jupiter) → drawn behind the head
    public bool IsDogmaShaded; // eflags bit8 (128): draw through the Dogma TV-static shader
    public int Anm2Id, AnimId, AnimFrame;
    public int OverlayAnimId, OverlayFrame;   // Isaac's head is an overlay animation
    public float R, G, B, A;
    public float OffR, OffG, OffB;            // colour offset (-1..1): damage flash / status
    public float ColR, ColG, ColB, ColA;      // colorize (0..1): poison etc. (ColA=0 -> none)
    public int   Status;                      // status-effect bit field (poison/burn/slow/...)
    public float ShadowSize;                  // Isaac's own shadowSize (xml/100); 0 = no shadow
    public readonly Dictionary<int,LayerOverride> Overrides = new();
}

/// <summary>
/// MSG_SCENE3 (type 6): the anm2 scene PLUS per-layer runtime overrides.
///   entity: "&lt;ffBBffHHHBBBBfB" x,y,cat,eflags,scale,height,anm2,anim,frame,r,g,b,a,rot,ovCount
///   layer : "&lt;BHBBBBB"        layerId, sheetId, lflags(vis|flipX|flipY), r,g,b,a
/// </summary>
public sealed class LayerScenePacket
{
    public const byte Magic=0x49, Version=1, MsgScene3=6;
    private const int HeaderSize = 26;
    private const int EntSize    = 47;
    private const int OvSize     = 8;

    public uint  Frame { get; private set; }
    public float RoomMinX, RoomMinY, RoomMaxX, RoomMaxY;
    public readonly List<LayerEntity> Entities = new();

    public static bool TryParse(ReadOnlySpan<byte> b, LayerScenePacket into)
    {
        if (b.Length < HeaderSize) return false;
        if (b[0]!=Magic || b[1]!=Version || b[2]!=MsgScene3) return false;

        into.Frame    = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(4,4));
        into.RoomMinX = F(b,8);  into.RoomMinY = F(b,12);
        into.RoomMaxX = F(b,16); into.RoomMaxY = F(b,20);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(24,2));

        into.Entities.Clear();
        int o = HeaderSize;
        for (int i=0;i<count;i++)
        {
            if (o + EntSize > b.Length) return false;
            var e = new LayerEntity {
                X = F(b,o), Y = F(b,o+4),
                Cat = (EntityCategory)b[o+8],
                FlipX       = (b[o+9] & 1) != 0,
                IsFlat      = (b[o+9] & 2) != 0,
                IsDestroyed = (b[o+9] & 4) != 0,
                IsCostume   = (b[o+9] & 8) != 0,
                IsHurt      = (b[o+9] & 16) != 0,
                IsHeld      = (b[o+9] & 32) != 0,
                IsBodyCostume = (b[o+9] & 64) != 0,
                IsDogmaShaded = (b[o+9] & 128) != 0,
                Scale    = F(b,o+10),
                HeightPx = F(b,o+14),
                Anm2Id    = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+18,2)),
                AnimId    = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+20,2)),
                AnimFrame = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+22,2)),
                R = b[o+24]/255f, G = b[o+25]/255f, B = b[o+26]/255f, A = b[o+27]/255f,
                RotationDeg = F(b,o+28),
                OverlayAnimId = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+32,2)),
                OverlayFrame  = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+34,2)),
                OffR = (b[o+36]-128)/127f, OffG = (b[o+37]-128)/127f, OffB = (b[o+38]-128)/127f,
                ColR = b[o+39]/255f, ColG = b[o+40]/255f, ColB = b[o+41]/255f, ColA = b[o+42]/255f,
                Status = b[o+43],
                ShadowSize = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+44,2)) / 1000f,
            };
            int oc = b[o+46];
            o += EntSize;
            if (o + oc*OvSize > b.Length) return false;
            for (int l=0;l<oc;l++)
            {
                int id    = b[o];
                int sheet = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o+1,2));
                byte lf   = b[o+3];
                e.Overrides[id] = new LayerOverride(id, sheet,
                    (lf&1)!=0, (lf&2)!=0, (lf&4)!=0,
                    b[o+4]/255f, b[o+5]/255f, b[o+6]/255f, b[o+7]/255f);
                o += OvSize;
            }
            into.Entities.Add(e);
        }
        return true;
    }

    private static float F(ReadOnlySpan<byte> b, int off) =>
        BinaryPrimitives.ReadSingleLittleEndian(b.Slice(off,4));
}
