using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Buffers.Binary;
using System.Text;

namespace IsaacDiorama;

/// <summary>
/// Background UDP listener. Sorts datagrams by message type: MSG_STRINGS (3)
/// are folded into the shared StringTable immediately; MSG_SCENE2 (4) are kept
/// as the latest raw scene datagram for the render loop to parse. This keeps
/// interleaved string/scene packets from clobbering each other.
/// </summary>
public sealed class UdpReceiver : IDisposable
{
    private readonly UdpClient _client;
    private readonly Thread    _thread;
    private volatile bool      _running = true;

    public StringTable Strings { get; } = new();

    public struct BackdropInfo
    { public bool Valid; public int Type, Shape, RoomType;
      public string Wall, Floor, Name;      // inline: no string-table dependency
      public int XmlCount;                  // entries parsed from backdrops.xml (0 = parse failed)
      public float MinX, MinY, MaxX, MaxY; }
    private BackdropInfo _backdrop;
    public BackdropInfo GetBackdrop() { lock (_gate) return _backdrop; }

    // A composited room image streamed from the game (MSG_IMG). Reassembled from
    // chunks on the rx thread; the completed buffer is published under _gate.
    public struct RoomImage { public bool Valid; public int Version, W, H; public byte[] Rgba; }
    private RoomImage _floorImg, _wallImg;
    public RoomImage GetFloorImage() { lock (_gate) return _floorImg; }
    public RoomImage GetWallImage()  { lock (_gate) return _wallImg;  }

    private HudData? _hud;
    public HudData? GetHud() { lock (_gate) return _hud; }

    // MSG_STATE (10): game-state byte. 0 = normal gameplay; non-zero = show the
    // flat game screen. bit0 paused, bit1 HUD hidden (cutscene/intro), bit2 menu.
    private int _state;
    public int GetState() { lock (_gate) return _state; }

    // MSG_WATER (14): the room's water plane. Amount 0 = dry, 1 = full (it changes live in
    // the rooms that flood). Current is the flow vector the game pushes entities along, and
    // which water_v2.fs turns into directional waves. Colours come from the room's FXParams
    // as FLOATS, because a multiplier is allowed to exceed 1.
    public struct WaterState
    {
        public bool  Valid, V2;
        public float Amount, CurX, CurY;
        public float R, G, B, A;        // WaterColor
        public float MR, MG, MB;        // WaterColorMultiplier
        public float ER, EG, EB, EA;    // WaterEffectColor
    }
    private WaterState _water;
    public WaterState GetWater() { lock (_gate) return _water; }

    // MSG_STATS (15): the stat block the 2D HUD never shows, plus EID's text for whatever
    // pickup the player is standing next to. AngelChance is the MODIFIER the game exposes,
    // not a probability — the panel labels it that way. There is deliberately no devil-room
    // chance: no API exposes one (see the mod's note).
    public struct StatsState
    {
        public bool   Valid, HaveEid;
        public float  Speed, FireRate, Damage, Range, ShotSpeed, Luck;
        public float  Planetarium, AngelMod;
        public string EidName, EidDesc;
    }
    private StatsState _stats;
    public StatsState GetStats() { lock (_gate) return _stats; }

    // MSG_PRICES (11): priced pickups in the room. price>0 = coins, <0 = PickupPrice.
    public struct PriceItem { public float X, Y; public int Price; }
    private PriceItem[] _prices = System.Array.Empty<PriceItem>();
    public PriceItem[] GetPrices() { lock (_gate) return _prices; }

    // MSG_ITEMINFO (12): pedestal collectibles / trinkets with a display name (+ effect).
    public struct ItemInfo { public float X, Y; public int Kind, Quality; public string Name, Desc; }
    private ItemInfo[] _items = System.Array.Empty<ItemInfo>();
    public ItemInfo[] GetItemInfos() { lock (_gate) return _items; }

    // MSG_LASERS (13): laser beams (brimstone / tech / tech-x). Parsed on the rx
    // thread into LaserData (sample polyline + variant + colour + radius).
    private System.Collections.Generic.List<LaserData> _lasers = new();
    public System.Collections.Generic.List<LaserData> GetLasers() { lock (_gate) return _lasers; }

    // Per-kind assembler scratch (rx thread only). kind 0 = floor, 1 = wall.
    private const int ImgChunk = 8000;   // MUST match the mod's IMG_CHUNK
    private sealed class ImgAsm { public int Ver=-1, W, H, Total, GotCount; public byte[]? Buf; public bool[]? Got; }
    private readonly ImgAsm[] _asm = { new ImgAsm(), new ImgAsm() };

    private readonly object _gate = new();
    private byte[]?  _latestScene;
    private bool     _sceneIsLayered;   // true when the newest scene is MSG_SCENE3
    private long     _sceneCount;
    private DateTime _lastScene = DateTime.MinValue;

    public UdpReceiver(int port = 47800)
    {
        _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        _client.Client.ReceiveTimeout = 500;
        // A full floor image arrives as a ~183-chunk burst; give the kernel room
        // to hold it until the rx thread drains, or chunks silently drop.
        try { _client.Client.ReceiveBufferSize = 8 * 1024 * 1024; } catch { }
        _thread = new Thread(Loop){ IsBackground=true, Name="IsaacUdpRx" };
        _thread.Start();
    }

    private void Loop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            byte[] data;
            try { data = _client.Receive(ref remote); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            if (data.Length < 3 || data[0] != 0x49) continue;

            switch (data[2])
            {
                case 3: StringTable.ParseInto(data, Strings); break;
                case 4:
                    lock (_gate) { _latestScene = data; _sceneIsLayered = false; _sceneCount++; _lastScene = DateTime.UtcNow; }
                    break;
                case 6:   // REPENTOGON layer-state scene
                    lock (_gate) { _latestScene = data; _sceneIsLayered = true;  _sceneCount++; _lastScene = DateTime.UtcNow; }
                    break;
                case 7: HandleImageChunk(data); break;
                case 8: { var h = new HudData(); if (HudData.TryParse(data, h)) lock (_gate) { _hud = h; } break; }
                case 10: if (data.Length >= 4) lock (_gate) { _state = data[3]; } break;
                case 14:
                    try
                    {
                        if (data.Length < 4 + 4*14) break;
                        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o,4));
                        var w = new WaterState {
                            Valid = true, V2 = data[3] != 0,
                            Amount = F(4),  CurX = F(8),  CurY = F(12),
                            R = F(16), G = F(20), B = F(24), A = F(28),
                            MR = F(32), MG = F(36), MB = F(40),
                            ER = F(44), EG = F(48), EB = F(52), EA = F(56) };
                        lock (_gate) { _water = w; }
                    }
                    catch { }
                    break;
                case 15:
                    try
                    {
                        if (data.Length < 4 + 4*8 + 1) break;
                        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o,4));
                        int p = 4 + 4*8;
                        bool haveEid = data[p] != 0; p += 1;
                        string ReadStr()
                        {
                            if (p + 2 > data.Length) return "";
                            int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2)); p += 2;
                            if (len < 0 || p + len > data.Length) return "";
                            string v = Encoding.UTF8.GetString(data, p, len); p += len; return v;
                        }
                        string nm = ReadStr(), ds = ReadStr();
                        var st = new StatsState {
                            Valid = true, HaveEid = haveEid,
                            Speed = F(4),  FireRate = F(8),  Damage = F(12),
                            Range = F(16), ShotSpeed = F(20), Luck = F(24),
                            Planetarium = F(28), AngelMod = F(32),
                            EidName = nm, EidDesc = ds };
                        lock (_gate) { _stats = st; }
                    }
                    catch { /* malformed stats packet: keep the previous one */ }
                    break;
                case 11:
                    try
                    {
                        int n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4,2));
                        var arr = new PriceItem[n];
                        int p = 6;
                        for (int i=0;i<n && p+10<=data.Length;i++)
                        {
                            arr[i] = new PriceItem {
                                X = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p,4)),
                                Y = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p+4,4)),
                                Price = (short)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p+8,2)) };
                            p += 10;
                        }
                        lock (_gate) { _prices = arr; }
                    }
                    catch { }
                    break;
                case 12:
                    try
                    {
                        int n = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4,2));
                        var arr = new ItemInfo[n];
                        int p = 6;
                        for (int i=0;i<n;i++)
                        {
                            float x = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p,4)); p += 4;
                            float y = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p,4)); p += 4;
                            int kind = data[p]; p += 1;
                            int qual = data[p]; p += 1;
                            int nl = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2)); p += 2;
                            string nm = Encoding.UTF8.GetString(data, p, nl); p += nl;
                            int dl = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2)); p += 2;
                            string ds = Encoding.UTF8.GetString(data, p, dl); p += dl;
                            arr[i] = new ItemInfo { X=x, Y=y, Kind=kind, Quality=qual, Name=nm, Desc=ds };
                        }
                        lock (_gate) { _items = arr; }
                    }
                    catch { }
                    break;
                case 13:
                    try
                    {
                        var list = new System.Collections.Generic.List<LaserData>();
                        if (LaserScene.TryParse(data, list)) lock (_gate) { _lasers = list; }
                    }
                    catch { }
                    break;
                case 5:
                    try
                    {
                        int roomType = data[3];   // byte 3: room type (24 = planetarium)
                        int p = 4;
                        int btype = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2)); p += 2;
                        int shape = data[p]; p += 1;
                        int xmlCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2)); p += 2;
                        string ReadStr()
                        {
                            int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2)); p += 2;
                            string v = Encoding.UTF8.GetString(data, p, len); p += len; return v;
                        }
                        string wall = ReadStr(), floor = ReadStr(), name = ReadStr();
                        var bd = new BackdropInfo {
                            Valid = true, Type = btype, Shape = shape, RoomType = roomType, XmlCount = xmlCount,
                            Wall = wall, Floor = floor, Name = name,
                            MinX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p,4)),
                            MinY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p+4,4)),
                            MaxX = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p+8,4)),
                            MaxY = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p+12,4)),
                        };
                        lock (_gate) { _backdrop = bd; }
                    }
                    catch { /* malformed backdrop packet: keep the previous one */ }
                    break;
            }
        }
    }

    // MSG_IMG (7): header (16 bytes, little-endian) then payload:
    //   B magic, B ver, B type(7), B kind(0=floor), H imgVersion, H w, H h,
    //   H totalChunks, H chunkIndex, H payloadLen, then payloadLen RGBA bytes.
    private void HandleImageChunk(byte[] data)
    {
        try
        {
            if (data.Length < 16) return;
            int kind  = data[3];
            int ver   = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4,2));
            int w     = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6,2));
            int h     = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8,2));
            int total = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10,2));
            int idx   = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12,2));
            int plen  = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14,2));
            if (kind < 0 || kind > 1) return;            // 0 = floor, 1 = wall
            if (w <= 0 || h <= 0 || total <= 0) return;
            if (16 + plen > data.Length) return;

            var a = _asm[kind];
            // Restart assembly whenever the image identity changes.
            if (ver != a.Ver || w != a.W || h != a.H || total != a.Total || a.Buf == null)
            {
                a.Ver=ver; a.W=w; a.H=h; a.Total=total;
                a.Buf = new byte[w*h*4];
                a.Got = new bool[total];
                a.GotCount = 0;
            }
            if (idx < 0 || idx >= total || a.Got![idx]) return;

            int off  = idx * ImgChunk;
            int copy = Math.Min(plen, a.Buf!.Length - off);
            if (copy > 0) Array.Copy(data, 16, a.Buf, off, copy);
            a.Got[idx] = true; a.GotCount++;

            if (a.GotCount == total)
            {
                var img = new RoomImage { Valid=true, Version=ver, W=w, H=h, Rgba=a.Buf };
                lock (_gate) { if (kind == 0) _floorImg = img; else _wallImg = img; }
                a.Buf = null;   // published; next version allocates fresh
            }
        }
        catch { /* malformed chunk: ignore, resend heals it */ }
    }

    public byte[]? TryGetLatestScene(out long count, out double ageMs, out bool layered)
    {
        lock (_gate)
        {
            layered = _sceneIsLayered;
            count = _sceneCount;
            ageMs = _latestScene!=null ? (DateTime.UtcNow-_lastScene).TotalMilliseconds : double.NaN;
            return _latestScene;
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _client.Dispose(); } catch { }
        try { _thread.Join(750); } catch { }
    }
}
