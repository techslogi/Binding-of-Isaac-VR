using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StereoKit;

namespace IsaacDiorama;

/// <summary>
/// Reads an XInput gamepad (VD forwards the Quest-paired controller to Windows as
/// one) and streams its raw state to the mod over UDP :47801. The mod injects it
/// into Isaac via MC_INPUT_ACTION, so it works even while Isaac is unfocused.
/// Packet: B magic(0x49) B ver(1) B type(9) B flags, H wButtons, B lTrig, B rTrig,
///         h LX, h LY, h RX, h RY  (16 bytes, little-endian).
/// </summary>
public sealed class GamepadInput : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Pad { public ushort wButtons; public byte lTrig, rTrig;
        public short lx, ly, rx, ry; }
    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint packet; public Pad pad; }

    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int idx, ref State state);

    private readonly UdpClient _tx = new UdpClient();
    private readonly byte[] _buf = new byte[16];
    private bool _warned;
    public bool Connected { get; private set; }
    // Latest XInput button bitmask, for app-side shortcuts (the debug toggle).
    // Isaac ignores stick-click bits, so R3 (0x0080) is safe to repurpose.
    public ushort Buttons { get; private set; }

    // Buttons the APP wants to inject into the packet sent to the mod, OR'd on top of
    // the real pad. Lets a keyboard key or a remapped control drive an Isaac action
    // (e.g. force BACK 0x0020 so the mod opens the map) without a physical Back button.
    public ushort ForceButtons { get; set; }

    /// <summary>
    /// Stop streaming to the mod while still reading the pad. Isaac is UNFOCUSED, so our
    /// injection is its only source of controller input — muting therefore freezes the
    /// game, which is exactly what the in-headset tuning mode needs: the sticks and D-pad
    /// drive the slider instead of Isaac. The mod's own PAD_STALE timeout means it simply
    /// stops injecting a few frames later; nothing to reset when we unmute.
    /// </summary>
    public bool Mute { get; set; }

    public GamepadInput(string host = "127.0.0.1", int port = 47801)
    {
        try { _tx.Connect(host, port); } catch { }
    }

    public void Poll()
    {
        var st = new State();
        int res;
        try { res = XInputGetState(0, ref st); }
        catch { Buttons = 0; if (!_warned) { Log.Info("[Diorama] XInput not available on this system"); _warned = true; } return; }

        if (res != 0)
        {
            // No pad. Still send a neutral packet carrying ForceButtons so a keyboard/
            // remapped trigger (e.g. the map) works without a controller; otherwise stop.
            Connected = false; Buttons = 0;
            if (ForceButtons == 0 || Mute) return;
            ushort fb = ForceButtons;
            _buf[0]=0x49; _buf[1]=1; _buf[2]=9; _buf[3]=0;
            _buf[4]=(byte)(fb & 0xFF); _buf[5]=(byte)(fb >> 8);
            _buf[6]=0; _buf[7]=0; WriteI16(8,0); WriteI16(10,0); WriteI16(12,0); WriteI16(14,0);
            try { _tx.Send(_buf, _buf.Length); } catch { }
            return;
        }
        Connected = true;
        var g = st.pad;
        Buttons = g.wButtons;
        if (Mute) return;                 // read, but don't drive Isaac
        ushort outBtn = (ushort)(g.wButtons | ForceButtons);
        _buf[0]=0x49; _buf[1]=1; _buf[2]=9; _buf[3]=0;
        _buf[4]=(byte)(outBtn & 0xFF); _buf[5]=(byte)(outBtn >> 8);
        _buf[6]=g.lTrig; _buf[7]=g.rTrig;
        WriteI16(8, g.lx); WriteI16(10, g.ly); WriteI16(12, g.rx); WriteI16(14, g.ry);
        try { _tx.Send(_buf, _buf.Length); } catch { }
    }

    private void WriteI16(int o, short v) { _buf[o]=(byte)(v & 0xFF); _buf[o+1]=(byte)((v>>8) & 0xFF); }

    public void Dispose() { try { _tx.Dispose(); } catch { } }
}
