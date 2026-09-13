using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace IsaacDiorama;

public struct HudRoom { public int GridIndex, Type, Shape, Flags, Contents; public bool Clear, Visited; }
public struct PocketItem { public int Kind, Id; public string Name; }   // Kind: 1 card/rune, 2 pill

/// <summary>
/// MSG_HUD (type 8): player health / consumables / active item / minimap, read
/// from the game and rebuilt app-side. Layout after the 4-byte header:
///   H coins, H bombs, H keys, B flags1(bit0 goldenBomb),
///   B maxHearts, B redHearts, B soulHearts, B boneHearts, B rotten, B eternal, B golden,
///   H blackMask,
///   H activeId, B charge, B maxCharge, H iconLen, iconBytes,
///   B pocketCount, per pocket: B kind(1 card/rune, 2 pill), H id, H nameLen, nameBytes,
///   B hasBoss, H bossFillMilli(0..1000), H popupLen, popupBytes,
///   h currentRoomGridIndex, H roomCount,
///   per room: h gridIndex, B type, B shape, B displayFlags, B clear, B visited, B contents,
///   B curses (appended last)
/// </summary>
public sealed class HudData
{
    public bool Valid;
    public int  Coins, Bombs, Keys; public bool GoldenBomb;
    public int  MaxHearts, RedHearts, SoulHearts, BoneHearts, RottenHearts, EternalHearts, GoldenHearts;
    public int  BlackMask;
    public int  Curses;      // LevelCurse bits: 1 Dark, 2 Labyrinth, 4 Lost, 8 Unknown, 16 Cursed, 32 Maze, 64 Blind, 128 Giant
    public int  ActiveId, Charge, MaxCharge; public string ActiveIcon = "";
    public int  Trinket0, Trinket1; public string TrinketIcon0 = "", TrinketIcon1 = "";
    public int  Card0, Pill0;
    public int  CurrentRoom = -1;
    public readonly List<HudRoom> Rooms = new();
    public readonly List<PocketItem> Pockets = new();
    public bool  HasBoss; public float BossFill; public string Popup = "";

    public static bool TryParse(ReadOnlySpan<byte> b, HudData into)
    {
        if (b.Length < 26 || b[0] != 0x49 || b[2] != 8) return false;
        into.Coins = U16(b,4); into.Bombs = U16(b,6); into.Keys = U16(b,8);
        into.GoldenBomb = (b[10] & 1) != 0;
        into.MaxHearts=b[11]; into.RedHearts=b[12]; into.SoulHearts=b[13]; into.BoneHearts=b[14];
        into.RottenHearts=b[15]; into.EternalHearts=b[16]; into.GoldenHearts=b[17];
        into.BlackMask = U16(b,18);
        into.ActiveId = U16(b,20); into.Charge = b[22]; into.MaxCharge = b[23];
        int iconLen = U16(b,24); int o = 26;
        if (o + iconLen > b.Length) return false;
        into.ActiveIcon = Encoding.UTF8.GetString(b.Slice(o, iconLen)); o += iconLen;

        if (o + 4 > b.Length) return false;
        into.Trinket0 = U16(b,o); into.Trinket1 = U16(b,o+2); o += 4;
        int t0 = U16(b,o); o += 2; if (o + t0 > b.Length) return false;
        into.TrinketIcon0 = Encoding.UTF8.GetString(b.Slice(o, t0)); o += t0;
        int t1 = U16(b,o); o += 2; if (o + t1 > b.Length) return false;
        into.TrinketIcon1 = Encoding.UTF8.GetString(b.Slice(o, t1)); o += t1;
        if (o + 2 > b.Length) return false;
        into.Card0 = b[o]; into.Pill0 = b[o+1]; o += 2;

        // Pocket items (cards/runes/pills) with display names.
        into.Pockets.Clear();
        if (o + 1 > b.Length) return false;
        int pc = b[o]; o += 1;
        for (int i=0; i<pc; i++)
        {
            if (o + 5 > b.Length) return false;
            int kind = b[o]; int id = U16(b,o+1); int nlen = U16(b,o+3); o += 5;
            if (o + nlen > b.Length) return false;
            string nm = Encoding.UTF8.GetString(b.Slice(o, nlen)); o += nlen;
            into.Pockets.Add(new PocketItem { Kind=kind, Id=id, Name=nm });
        }

        // Boss HP bar + pill-effect popup.
        into.HasBoss = false; into.BossFill = 0f; into.Popup = "";
        if (o + 3 <= b.Length)
        {
            into.HasBoss = b[o] != 0; o += 1;
            into.BossFill = U16(b,o) / 1000f; o += 2;
            if (o + 2 > b.Length) return false;
            int pl = U16(b,o); o += 2;
            if (o + pl > b.Length) return false;
            into.Popup = Encoding.UTF8.GetString(b.Slice(o, pl)); o += pl;
        }

        if (o + 4 > b.Length) return false;
        into.CurrentRoom = (short)U16(b,o); o += 2;
        int rc = U16(b,o); o += 2;
        into.Rooms.Clear();
        int roomsRead = 0;
        for (int i=0; i<rc; i++)
        {
            if (o + 8 > b.Length) break;
            into.Rooms.Add(new HudRoom {
                GridIndex=(short)U16(b,o), Type=b[o+2], Shape=b[o+3], Flags=b[o+4],
                Clear=b[o+5]!=0, Visited=b[o+6]!=0, Contents=b[o+7] });
            o += 8; roomsRead++;
        }
        // Curse bitmask is APPENDED after the room list (so older packets just parse
        // as "no curses"). Only trust it when the whole room list was actually read.
        into.Curses = (roomsRead == rc && o < b.Length) ? b[o] : 0;
        into.Valid = true;
        return true;
    }

    private static int U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(o,2));
}
