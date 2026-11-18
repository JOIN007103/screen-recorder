using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorder.Views;
using ProdApp = ScreenRecorder.App;

namespace BarCaptureTest;

/// <summary>
/// 集成级验证：直接实例化产品真实的 FloatingControl 窗口，
/// 用 BitBlt 抓它所在区域，确认它已被 SetWindowDisplayAffinity 排除出捕获。
///
/// 判定依据：悬浮条底色 #1F2937 = RGB(31,41,55)，统计该颜色的像素数。
///   - 对照组（同色普通窗口、不设 WDA）→ 应有大量该色像素（证明检测有效）
///   - 真实悬浮条 → 应为 0 或极少（证明已排除）
/// </summary>
public partial class App : Application
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    // 悬浮条底色 #1F2937
    private const int TARGET_R = 31, TARGET_G = 41, TARGET_B = 55;

    private readonly StringBuilder _log = new();
    private static readonly string OutPath =
        @"E:\开发练习项目\录屏软件\spike\BarCaptureTest\bar_result.txt";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Say("=== 悬浮控制条入镜集成验证 ===");
            Say($"系统: {Environment.OSVersion.Version}");
            Say("");

            // ---- 对照组：不设 WDA 的同色窗口 ----
            var ctrl = new Window
            {
                WindowStyle = WindowStyle.None,
                Background = new SolidColorBrush(Color.FromRgb(TARGET_R, TARGET_G, TARGET_B)),
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 880, Top = 800, Width = 452, Height = 96,
                Topmost = true
            };
            ctrl.Show();
            await Task.Delay(900);
            var ctrlRect = RectOf(ctrl);
            int ctrlCount = CountBarColor(ctrlRect);
            Say($"[对照] 普通同色窗口（未设 WDA）区域 {Describe(ctrlRect)}");
            Say($"  命中底色像素: {ctrlCount}");
            Say(ctrlCount > 20000 ? "  ✓ 检测有效（窗口如实被抓到）" : "  ✗ 检测异常，对照失败");
            Say("");
            ctrl.Close();
            await Task.Delay(400);

            // ---- 被测：产品真实悬浮条 ----
            var bar = new FloatingControl(ProdApp.Engine)
            {
                Left = 200,
                Top = 800
            };
            bar.Show();
            await Task.Delay(1200);

            var barRect = RectOf(bar);
            int barCount = CountBarColor(barRect);

            Say($"[被测] 产品真实悬浮条 区域 {Describe(barRect)}");
            Say($"  CaptureExcluded = {bar.CaptureExcluded}");
            if (!string.IsNullOrEmpty(bar.ExclusionError)) Say($"  ExclusionError = {bar.ExclusionError}");
            Say($"  命中底色像素: {barCount}");

            Say("");
            Say("--- 判定 ---");
            bool pass = barCount < 500;
            Say(pass
                ? "PASS: 悬浮条已被排除出 BitBlt 捕获，不会出现在成片中"
                : "FAIL: 悬浮条仍被捕获，会出现在成片中");

            bar.Close();
            await Task.Delay(300);

            File.WriteAllText(OutPath, _log.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Say("异常: " + ex);
            try { File.WriteAllText(OutPath, _log.ToString(), Encoding.UTF8); } catch { }
        }
        finally
        {
            Shutdown();
        }
    }

    private static string Describe(RECT r) => $"({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}";

    private static RECT RectOf(Window w)
    {
        GetWindowRect(new WindowInteropHelper(w).Handle, out RECT r);
        return r;
    }

    private int CountBarColor(RECT r)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return -1;

        IntPtr screenDC = GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = BI_RGB
            }
        };
        IntPtr bmp = CreateDIBSection(screenDC, ref bi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDC, bmp);

        if (!BitBlt(memDC, 0, 0, w, h, screenDC, r.Left, r.Top, SRCCOPY | CAPTUREBLT))
            BitBlt(memDC, 0, 0, w, h, screenDC, r.Left, r.Top, SRCCOPY);

        int hit = 0, total = w * h;
        unsafe
        {
            byte* p = (byte*)bits;
            for (int i = 0; i < total; i++)
            {
                int b = p[i * 4], g = p[i * 4 + 1], rr = p[i * 4 + 2];
                if (Math.Abs(b - TARGET_B) <= 6 && Math.Abs(g - TARGET_G) <= 6 && Math.Abs(rr - TARGET_R) <= 6)
                    hit++;
            }
        }

        SelectObject(memDC, old);
        DeleteObject(bmp);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);
        return hit;
    }

    private void Say(string s) => _log.AppendLine(s);

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                      IntPtr hdcSrc, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
                                                  out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
