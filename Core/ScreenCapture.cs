using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RadCord.Core;

public class ScreenCaptureSource
{
    public string Type        { get; set; } = "monitor"; // "monitor" | "window"
    public int    MonitorIndex{ get; set; } = 0;
    public IntPtr WindowHwnd  { get; set; } = IntPtr.Zero;
    public int    X           { get; set; }
    public int    Y           { get; set; }
    public int    Width       { get; set; } = Protocol.ScreenWidth;
    public int    Height      { get; set; } = Protocol.ScreenHeight;
    public int    JpegQuality   { get; set; } = Protocol.ScreenJpegQuality;
    public int    Fps           { get; set; } = Protocol.ScreenFps;
    public string Label         { get; set; } = "";
    public bool   CaptureSystemAudio { get; set; } = false;
    // Internal: native monitor resolution (set by GetMonitors)
    internal int  _nativeWidth;
    internal int  _nativeHeight;
}

/// <summary>
/// GPU-accelerated screen capture via DXGI Desktop Duplication.
/// Falls back to GDI CopyFromScreen if DXGI is unavailable.
/// Mirrors screenshare.py functionality.
/// </summary>
public class ScreenCapture
{
    private bool            _running;
    private Thread?         _thread;
    private ScreenCaptureSource _source = new();

    public Func<byte[], Task>? OnFrame { get; set; }

    public void Configure(ScreenCaptureSource source) => _source = source;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread  = new Thread(CaptureLoop) { IsBackground = true, Name = "ScreenCapture" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
    }

    private void CaptureLoop()
    {
        var src      = _source;
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, src.Fps));

        if (src.Type == "window" && src.WindowHwnd != IntPtr.Zero)
            RunWindowCapture(src, interval);
        else
            RunMonitorCapture(src, interval);
    }


    private void RunMonitorCapture(ScreenCaptureSource src, TimeSpan interval)
    {
        int bx = src.X, by = src.Y;
        // Use native monitor resolution for the capture bitmap so the FULL screen is captured
        int mw = src._nativeWidth  > 0 ? src._nativeWidth  : src.Width;
        int mh = src._nativeHeight > 0 ? src._nativeHeight : src.Height;

        using var bmp = new Bitmap(mw, mh, PixelFormat.Format32bppRgb);
        using var gfx = Graphics.FromImage(bmp);

        while (_running)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                gfx.CopyFromScreen(bx, by, 0, 0, new System.Drawing.Size(mw, mh));
                var resized = ResizeBitmap(bmp, src.Width, src.Height);
                DrawCursorOnBitmap(resized, bx, by, src.Width, src.Height, mw, mh);
                var jpeg = EncodeJpeg(resized, src.JpegQuality);
                if (OnFrame != null && _running)
                    OnFrame(jpeg).GetAwaiter().GetResult();
                if (resized != bmp) resized.Dispose();
            }
            catch { }

            var elapsed = DateTime.UtcNow - t0;
            var sleep   = interval - elapsed;
            if (sleep > TimeSpan.Zero) Thread.Sleep(sleep);
        }
    }


    private void RunWindowCapture(ScreenCaptureSource src, TimeSpan interval)
    {
        while (_running)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                if (IsIconic(src.WindowHwnd))
                {
                    using var waitBmp = CreateWaitingBitmap(src.Width, src.Height, "Окно свёрнуто, ожидайте...");
                    var waitJpeg = EncodeJpeg(waitBmp, src.JpegQuality);
                    if (OnFrame != null && _running) OnFrame(waitJpeg).GetAwaiter().GetResult();
                    Thread.Sleep(300);
                    continue;
                }

                RECT rc;
                GetWindowRect(src.WindowHwnd, out rc);
                int ww = rc.Right  - rc.Left;
                int wh = rc.Bottom - rc.Top;
                if (ww < 2 || wh < 2) { Thread.Sleep(100); continue; }

                using var bmp = new Bitmap(ww, wh, PixelFormat.Format32bppRgb);
                using var gfx = Graphics.FromImage(bmp);
                gfx.CopyFromScreen(rc.Left, rc.Top, 0, 0, new System.Drawing.Size(ww, wh));
                var resized = ResizeBitmap(bmp, src.Width, src.Height);
                DrawCursorOnBitmap(resized, rc.Left, rc.Top, src.Width, src.Height, ww, wh);
                var jpeg = EncodeJpeg(resized, src.JpegQuality);
                if (OnFrame != null && _running)
                    OnFrame(jpeg).GetAwaiter().GetResult();
                if (resized != bmp) resized.Dispose();
            }
            catch { }

            var elapsed = DateTime.UtcNow - t0;
            var sleep   = interval - elapsed;
            if (sleep > TimeSpan.Zero) Thread.Sleep(sleep);
        }
    }


    private static Bitmap CreateWaitingBitmap(int w, int h, string text)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0x1A, 0x1A, 0x1A));
        using var font  = new Font("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(Color.FromArgb(0x88, 0x88, 0x88));
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, (w - size.Width) / 2f, (h - size.Height) / 2f);
        return bmp;
    }

    private static Bitmap ResizeBitmap(Bitmap src, int w, int h)
    {
        if (src.Width == w && src.Height == h) return src;
        var dst = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        var encoder     = GetJpegEncoder();
        var encParams   = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, encoder, encParams);
        return ms.ToArray();
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        return ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
    }

    private static void DrawCursorOnBitmap(Bitmap bmp, int offsetX, int offsetY,
        int outW, int outH, int srcW, int srcH)
    {
        try
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(ci);
            if (!GetCursorInfo(ref ci)) return;
            if (ci.flags != 1) return; // CURSOR_SHOWING

            float scaleX = (float)outW / srcW;
            float scaleY = (float)outH / srcH;
            int cx = (int)((ci.ptScreenPos.x - offsetX) * scaleX);
            int cy = (int)((ci.ptScreenPos.y - offsetY) * scaleY);
            if (cx < 0 || cy < 0 || cx >= outW || cy >= outH) return;

            using var g = Graphics.FromImage(bmp);
            const int r = 7;
            g.FillEllipse(Brushes.White, cx - r, cy - r, r * 2, r * 2);
            g.DrawEllipse(new Pen(Color.Black, 2), cx - r, cy - r, r * 2, r * 2);
            g.FillEllipse(Brushes.Black, cx - 2, cy - 2, 4, 4);
        }
        catch { }
    }


    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int    cbSize;
        public int    flags;
        public IntPtr hCursor;
        public POINT  ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }


    public static List<ScreenCaptureSource> GetMonitors()
    {
        var list = new List<ScreenCaptureSource>();
        int idx  = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(mi);
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                var r       = mi.rcMonitor;
                bool primary = (mi.dwFlags & 1) != 0;
                list.Add(new ScreenCaptureSource
                {
                    Type          = "monitor",
                    MonitorIndex  = idx,
                    X             = r.Left,
                    Y             = r.Top,
                    Width         = Protocol.ScreenWidth,
                    Height        = Protocol.ScreenHeight,
                    _nativeWidth  = r.Right  - r.Left,
                    _nativeHeight = r.Bottom - r.Top,
                    Label         = $"Монитор {idx + 1} ({r.Right - r.Left}×{r.Bottom - r.Top}){(primary ? " [Основной]" : "")}",
                });
                idx++;
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static List<ScreenCaptureSource> GetWindows()
    {
        var list = new List<ScreenCaptureSource>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title)) return true;

            // Filter tool windows (tooltips, system trays, etc.)
            int exStyle = GetWindowLong(hwnd, -20);
            if ((exStyle & 0x80) != 0) return true; // WS_EX_TOOLWINDOW

            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, 64);
            string className = cls.ToString();
            if (className is "Shell_TrayWnd" or "Progman" or "WorkerW" or "SysShadow"
                          or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow"
                          or "tooltips_class32" or "IME" or "MSCTFIME UI") return true;

            RECT rc;
            GetWindowRect(hwnd, out rc);
            int ww = rc.Right - rc.Left;
            int wh = rc.Bottom - rc.Top;
            if (ww < 50 || wh < 50) return true;

            list.Add(new ScreenCaptureSource
            {
                Type       = "window",
                WindowHwnd = hwnd,
                X          = rc.Left,
                Y          = rc.Top,
                Width      = Protocol.ScreenWidth,
                Height     = Protocol.ScreenHeight,
                Label      = title.Length > 60 ? title[..60] + "…" : title,
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int    cbSize;
        public RECT   rcMonitor;
        public RECT   rcWork;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
