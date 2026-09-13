using System;
using System.Runtime.InteropServices;
using StereoKit;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace IsaacDiorama;

/// <summary>
/// Captures the live Isaac window with Windows.Graphics.Capture (WGC) and exposes
/// it as a StereoKit <see cref="Tex"/>. WGC is the only reliable path because
/// Isaac renders in OpenGL — GDI / PrintWindow return black.
///
/// Flow: a private D3D11 device drives a WGC frame pool on the Isaac HWND. Each
/// arrived frame (a D3D texture) is CopyResource'd into a CPU-readable staging
/// texture, mapped, and packed tight into a BGRA byte buffer on the capture
/// thread. <see cref="Upload"/> (called on the main/render thread) pushes that
/// buffer into the StereoKit texture. The app shows it on a flat panel whenever
/// the mod reports a non-gameplay state (pause / menu / cutscene).
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private readonly ID3D11Device _d3d;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDirect3DDevice _rtDevice;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private int _poolW, _poolH;

    private readonly object _ctxLock = new();   // guards the immediate context + staging
    private readonly object _cpuLock = new();    // guards the published CPU buffer
    private byte[]? _cpu; private int _cpuW, _cpuH; private bool _dirty;

    private Tex? _tex;
    private IntPtr _hwnd;
    private bool _running;
    private bool _available = true;
    private int _findCooldown;                  // frames until we retry finding the window
    private bool _gotFrame;
    private string _lastError = "";

    public Tex? Texture => _tex;
    public bool Running => _running;

    // When false, arrived frames are drained but not copied to CPU (keeps the
    // session warm during gameplay without the per-frame cost). Set each frame.
    public volatile bool WantFrames = true;

    /// <summary>One-line status for the in-VR diagnostics overlay.</summary>
    public string Status
    {
        get
        {
            if (!_available) return "cap:unavailable" + (_lastError.Length > 0 ? " (" + _lastError + ")" : "");
            if (!_running)  return "cap:searching-for-window" + (_lastError.Length > 0 ? " (" + _lastError + ")" : "");
            if (!_gotFrame) return "cap:running,no-frame-yet";
            return $"cap:{_cpuW}x{_cpuH}";
        }
    }

    public WindowCapture()
    {
        try
        {
            // BgraSupport is required for the WGC / Direct3D interop surface.
            var res = D3D11.D3D11CreateDevice(
                (IDXGIAdapter)null!, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                (FeatureLevel[])null!, out ID3D11Device? dev, out ID3D11DeviceContext? ctx);
            if (res.Failure || dev == null || ctx == null)
            {
                // Retry with WARP so a headless / no-GPU box still runs.
                D3D11.D3D11CreateDevice(
                    (IDXGIAdapter)null!, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                    (FeatureLevel[])null!, out dev, out ctx);
            }
            _d3d = dev!; _ctx = ctx!;
            _rtDevice = Direct3D11Helper.CreateDirect3DDevice(_d3d);
        }
        catch (Exception ex)
        {
            _available = false; _lastError = ex.Message;
            Log.Warn($"[Diorama] WGC unavailable (D3D init failed): {ex.Message}");
            _d3d = null!; _ctx = null!; _rtDevice = null!;
        }
    }

    /// <summary>Find the Isaac window and start capturing, if not already running.</summary>
    public void EnsureStarted()
    {
        if (!_available || _running) return;
        if (_findCooldown > 0) { _findCooldown--; return; }
        _findCooldown = 60;                       // ~1s between search attempts

        IntPtr hwnd = WindowFinder.FindIsaac();
        if (hwnd == IntPtr.Zero) return;
        StartFor(hwnd);
    }

    private void StartFor(IntPtr hwnd)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            { _available = false; Log.Warn("[Diorama] WGC not supported on this system"); return; }

            _item = Direct3D11Helper.CreateItemForWindow(hwnd);
            if (_item == null) return;

            _poolW = Math.Max(1, _item.Size.Width);
            _poolH = Math.Max(1, _item.Size.Height);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                new SizeInt32 { Width = _poolW, Height = _poolH });
            _pool.FrameArrived += OnFrameArrived;

            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            _item.Closed += (_, _) => Stop();
            _session.StartCapture();

            _hwnd = hwnd; _running = true;
            Log.Info($"[Diorama] WGC capture started {_poolW}x{_poolH}");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log.Warn($"[Diorama] WGC start failed: {ex.Message}");
            Stop();
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            if (!WantFrames) return;          // draining to keep the session warm

            // Window resized: rebuild the pool to the new content size.
            var cs = frame.ContentSize;
            if (cs.Width > 0 && cs.Height > 0 && (cs.Width != _poolW || cs.Height != _poolH))
            {
                _poolW = cs.Width; _poolH = cs.Height;
                sender.Recreate(_rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                                new SizeInt32 { Width = _poolW, Height = _poolH });
            }

            using var src = Direct3D11Helper.GetTexture(frame.Surface);
            var d = src.Description;
            int w = (int)d.Width, h = (int)d.Height;
            if (w <= 0 || h <= 0) return;

            // Crop to the window's client area so the title bar (game name) + borders
            // are dropped. Falls back to the full frame if it can't be computed.
            int cx = 0, cy = 0, cw = w, ch = h;
            if (_hwnd != IntPtr.Zero) WindowFinder.GetClientCrop(_hwnd, w, h, ref cx, ref cy, ref cw, ref ch);

            byte[] buf;
            lock (_ctxLock)
            {
                EnsureStaging(w, h);
                _ctx.CopyResource(_staging!, src);
                var map = _ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                buf = new byte[cw * ch * 4];
                unsafe
                {
                    byte* p = (byte*)map.DataPointer;
                    int rp = (int)map.RowPitch;
                    int srcOff = cx * 4, dstRow = cw * 4;
                    for (int y = 0; y < ch; y++)
                        Marshal.Copy((IntPtr)(p + (long)(cy + y) * rp + srcOff), buf, y * dstRow, dstRow);
                }
                _ctx.Unmap(_staging!, 0);
            }

            lock (_cpuLock) { _cpu = buf; _cpuW = cw; _cpuH = ch; _dirty = true; }
            if (!_gotFrame) { _gotFrame = true; Log.Info($"[Diorama] WGC first frame {cw}x{ch}"); }
        }
        catch { /* transient capture hiccup: next frame heals it */ }
    }

    private void EnsureStaging(int w, int h)
    {
        if (_staging != null && (int)_staging.Description.Width == w && (int)_staging.Description.Height == h) return;
        _staging?.Dispose();
        _staging = _d3d.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private Color32[]? _px;   // reused RGBA scratch for the SK upload

    /// <summary>Push the newest captured frame into the StereoKit texture. Main thread only.</summary>
    public void Upload()
    {
        byte[]? buf; int w, h;
        lock (_cpuLock)
        {
            if (!_dirty || _cpu == null) return;
            buf = _cpu; w = _cpuW; h = _cpuH; _dirty = false;
        }
        if (buf == null || w <= 0 || h <= 0) return;

        // WGC gives BGRA8; swap to RGBA into a reused Color32[] (the SetColors
        // overload proven elsewhere in this app).
        int n = w * h;
        if (_px == null || _px.Length != n) _px = new Color32[n];
        for (int i = 0; i < n; i++)
        { int j = i * 4; _px[i] = new Color32(buf[j + 2], buf[j + 1], buf[j], buf[j + 3]); }

        if (_tex == null || _tex.Width != w || _tex.Height != h)
        {
            _tex = new Tex(TexType.ImageNomips, TexFormat.Rgba32);
            _tex.AddressMode = TexAddress.Clamp;
        }
        _tex.SetColors(w, h, _px);
    }

    /// <summary>Stop the session (e.g. back in gameplay) so we don't keep capturing.</summary>
    public void Stop()
    {
        _running = false;
        try { if (_pool != null) _pool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        _session = null; _pool = null; _item = null; _hwnd = IntPtr.Zero;
        _gotFrame = false;
    }

    public void Dispose()
    {
        Stop();
        try { _staging?.Dispose(); } catch { }
        try { _ctx?.Dispose(); } catch { }
        try { _d3d?.Dispose(); } catch { }
    }
}

/// <summary>
/// Launches Isaac via Steam so the game comes up alongside the VR app. Steam
/// applies the user's configured launch options (REPENTOGON + --luadebug), so
/// this is just "press play" on rungameid 250900 (Binding of Isaac: Rebirth,
/// the base app all DLC/Repentance+ launch through). No-op if it's already up.
/// </summary>
internal static class GameLauncher
{
    private const string SteamRunUrl = "steam://rungameid/250900";
    private static bool _tried;

    public static void EnsureRunning()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (WindowFinder.IsaacProcessRunning()) return;
            Log.Info("[Diorama] launching Isaac via Steam (rungameid 250900)...");
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"[Diorama] Isaac auto-launch failed: {ex.Message}"); }
    }

    /// <summary>
    /// Ask Isaac to shut down, for the options menu's Exit. Sends WM_CLOSE first so the game
    /// exits through its own path and SAVES — killing it outright can cost the run — and only
    /// falls back to Kill() if it is still up after a moment. The wait is a visible hitch, but
    /// it happens on the frame we are quitting anyway.
    /// </summary>
    public static void CloseIsaac()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                if (!WindowFinder.IsGameProcess(p)) continue;   // also excludes our own pid
                try
                {
                    Log.Info($"[Diorama] closing Isaac (pid {p.Id})");
                    if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                    if (!p.WaitForExit(2500)) { Log.Info("[Diorama] Isaac ignored WM_CLOSE; killing"); p.Kill(); }
                }
                catch (Exception ex) { Log.Warn($"[Diorama] closing Isaac failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"[Diorama] Isaac process scan failed: {ex.Message}"); }
    }
}

/// <summary>
/// Locates the running Isaac GAME window — the actual game, never this app.
/// Our own window ("IsaacDiorama-...") and process both contain "isaac", so we
/// match the game specifically: window title "Binding of Isaac", or the
/// isaac-ng process, and we always skip our own process id.
/// </summary>
internal static class WindowFinder
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);

    private static readonly int OwnPid = System.Diagnostics.Process.GetCurrentProcess().Id;

    /// <summary>
    /// Compute the capture crop rect (in FRAME pixels) that isolates the window's
    /// client area — i.e. drops the title bar (the game name) and borders. Derives
    /// the DPI scale from the captured frame vs the logical window size, so it works
    /// whether the app is DPI-aware or not. Leaves the crop untouched on any failure.
    /// </summary>
    public static void GetClientCrop(IntPtr hwnd, int frameW, int frameH,
                                     ref int cx, ref int cy, ref int cw, ref int ch)
    {
        try
        {
            if (!GetWindowRect(hwnd, out RECT wr) || !GetClientRect(hwnd, out RECT cr)) return;
            int winW = wr.right - wr.left, winH = wr.bottom - wr.top;
            if (winW <= 0 || winH <= 0) return;

            var tl = new POINT { x = 0, y = 0 };
            if (!ClientToScreen(hwnd, ref tl)) return;

            double sx = (double)frameW / winW, sy = (double)frameH / winH;
            int rx = (int)Math.Round((tl.x - wr.left) * sx);
            int ry = (int)Math.Round((tl.y - wr.top)  * sy);
            int rw = (int)Math.Round((cr.right - cr.left) * sx);
            int rh = (int)Math.Round((cr.bottom - cr.top) * sy);

            if (rx < 0) rx = 0; if (ry < 0) ry = 0;
            if (rx + rw > frameW) rw = frameW - rx;
            if (ry + rh > frameH) rh = frameH - ry;
            if (rw <= 0 || rh <= 0) return;                 // bogus -> keep full frame

            cx = rx; cy = ry; cw = rw; ch = rh;
        }
        catch { }
    }

    // The game is "isaac-ng.exe"; our app is "IsaacDiorama". Match the game only.
    private static bool IsGameProcessName(string name)
        => name.IndexOf("isaac-ng", StringComparison.OrdinalIgnoreCase) >= 0
        || name.Equals("isaac", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the GAME's process — never this app's. Used by GameLauncher.CloseIsaac.</summary>
    public static bool IsGameProcess(System.Diagnostics.Process p)
    {
        try { return p.Id != OwnPid && IsGameProcessName(p.ProcessName); }
        catch { return false; }
    }

    public static IntPtr FindIsaac()
    {
        IntPtr found = IntPtr.Zero;
        try
        {
            // THE OWNING PROCESS DECIDES, NOT THE TITLE.
            // This used to match any visible window whose title contained "binding of
            // isaac" — and a console window's title is its command line, so running the
            // app from a folder named "Binding of Isaac VR Mod" made it latch onto the
            // TERMINAL and mirror that onto the flat screen instead of the game. A title is
            // user-controlled text; the process name is not.
            IntPtr titled = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out int pid);
                if (pid == OwnPid) return true;                 // never our own window
                string pname;
                try { pname = System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
                catch { return true; }                          // gone or not ours to query
                if (!IsGameProcessName(pname)) return true;

                // Among the GAME's own windows, prefer the one actually titled "Binding of
                // Isaac" (it can own helper windows); otherwise the first one will do.
                var sb = new System.Text.StringBuilder(256);
                if (GetWindowText(h, sb, sb.Capacity) > 0 &&
                    sb.ToString().IndexOf("binding of isaac", StringComparison.OrdinalIgnoreCase) >= 0)
                { found = h; return false; }
                if (titled == IntPtr.Zero) titled = h;
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) found = titled;
        }
        catch { }
        if (found != IntPtr.Zero) return found;

        // Fallback: match the isaac-ng process and take its main window.
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                if (p.Id != OwnPid && IsGameProcessName(p.ProcessName)
                    && p.MainWindowHandle != IntPtr.Zero)
                    return p.MainWindowHandle;
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    public static bool IsaacProcessRunning()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
                if (p.Id != OwnPid && IsGameProcessName(p.ProcessName))
                    return true;
        }
        catch { }
        return false;
    }
}

/// <summary>
/// The small pile of COM / WinRT interop WGC needs: wrapping our D3D11 device as
/// an <see cref="IDirect3DDevice"/>, creating a capture item from an HWND, and
/// pulling the <see cref="ID3D11Texture2D"/> out of a captured frame surface.
/// </summary>
internal static class Direct3D11Helper
{
    private static readonly Guid ID3D11Texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgi = d3dDevice.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr ptr);
        var device = MarshalInspectable<IDirect3DDevice>.FromAbi(ptr);
        Marshal.Release(ptr);
        return device;
    }

    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var guid = GraphicsCaptureItemGuid;
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref guid);
        if (itemPtr == IntPtr.Zero) return null;
        var item = GraphicsCaptureItem.FromAbi(itemPtr);
        Marshal.Release(itemPtr);
        return item;
    }

    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var guid = ID3D11Texture2DGuid;
        IntPtr p = access.GetInterface(ref guid);
        return new ID3D11Texture2D(p);
    }
}
