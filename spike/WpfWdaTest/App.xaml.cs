using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WpfWdaTest;

/// <summary>
/// 验证 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) 对 WPF 窗口是否生效，
/// 重点区分两种情况：
///   1. 普通 WPF 窗口（AllowsTransparency=false）
///   2. 分层窗口（AllowsTransparency=true）—— 产品悬浮控制条就是这种
/// 因为 WDA 对 WS_EX_LAYERED 窗口可能不生效，这直接决定悬浮条的修法。
/// </summary>
public partial class App : Application
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private readonly StringBuilder _log = new();
    private static readonly string OutPath =
        @"E:\开发练习项目\录屏软件\spike\WpfWdaTest\wpf_wda_result.txt";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Say("=== WPF 窗口 WDA 排除实测 ===");
            Say($"系统: {Environment.OSVersion.Version}");
            Say("");

            var r1 = await RunCase("普通窗口 AllowsTransparency=false", allowsTransparency: false);
            var r2 = await RunCase("分层窗口 AllowsTransparency=true（产品同款）", allowsTransparency: true);

            Say("");
            Say("--- 判定 ---");
            Say(r1.Excluded ? "普通窗口: 可排除 ✓" : "普通窗口: 无法排除 ✗");
            Say(r2.Excluded ? "分层窗口: 可排除 ✓ → 悬浮条直接加 P/Invoke 即可"
                            : "分层窗口: 无法排除 ✗ → 需去掉 AllowsTransparency 或改收边方案");
            Say("");
            Say(r2.Excluded
                ? "结论: 保持现有外观，增加 SetWindowDisplayAffinity 调用。"
                : "结论: 悬浮条改为非透明窗口（保留 WindowStyle=None + 深色实底），再调用 WDA。");

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

    private sealed record CaseResult(bool Excluded, bool WdaOk, double MagentaPct);

    private async Task<CaseResult> RunCase(string name, bool allowsTransparency)
    {
        Say($"[{name}]");

        var area = SystemParameters.WorkArea;
        const double barW = 400, barH = 300;
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = allowsTransparency,
            Background = allowsTransparency ? Brushes.Transparent : Brushes.Magenta,
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Math.Max(area.Left + 20, area.Right - barW - 80),
            Top = area.Top + 240,
            Width = barW,
            Height = barH
        };

        if (allowsTransparency)
        {
            // 模拟产品悬浮条：分层窗口 + 内部不透明深色面板
            w.Content = new Border { Background = Brushes.Magenta };
        }

        w.Show();
        await Task.Delay(800);

        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        GetWindowRect(hwnd, out RECT rect);
        Say($"  位置 ({rect.Left},{rect.Top}) 尺寸 {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");

        bool ok = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        Say($"  WDA(0x11) 返回 {(ok ? "TRUE" : $"FALSE err={err}")}");

        await Task.Delay(600);
        var (m, total) = SampleRegion(rect);
        double pct = total == 0 ? 0 : m * 100.0 / total;
        Say($"  抓取采样 {total} 像素 → 品红 {pct:F1}%");

        bool excluded = pct < 3;
        Say(excluded ? "  ✓ 该窗口已被排除出 BitBlt 捕获" : "  ✗ 仍被捕获");
        Say("");

        w.Close();
        await Task.Delay(400);
        return new CaseResult(excluded, ok, pct);
    }

    private (int magenta, int total) SampleRegion(RECT r)
    {
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return (0, 0);

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

        int magenta = 0, total = w * h;
        unsafe
        {
            byte* p = (byte*)bits;
            for (int i = 0; i < total; i++)
            {
                byte b = p[i * 4], g = p[i * 4 + 1], rr = p[i * 4 + 2];
                if (b > 200 && g < 60 && rr > 200) magenta++;
            }
        }

        SelectObject(memDC, old);
        DeleteObject(bmp);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);
        return (magenta, total);
    }

    private void Say(string s) => _log.AppendLine(s);

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

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
