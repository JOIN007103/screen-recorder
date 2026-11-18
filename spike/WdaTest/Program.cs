using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WdaTest;

/// <summary>
/// 验证 SetWindowDisplayAffinity 能否让一个置顶窗口从 GDI BitBlt 捕获中排除。
/// 这决定悬浮控制条「不被录进视频」该用哪种修法。
///
/// 方法：创建一个纯品红的无边框置顶窗口，分别用三种亲和性设置，
/// 再用 BitBlt 抓该窗口所在区域，统计品红像素占比。
///   - WDA_NONE(0x00)               → 对照组，应该抓得到（验证测试本身有效）
///   - WDA_EXCLUDEFROMCAPTURE(0x11) → 期望完全抓不到
///   - WDA_MONITOR(0x01)            → 期望抓到一块黑色
/// </summary>
internal static class Program
{
    private const uint WDA_NONE = 0x00;
    private const uint WDA_MONITOR = 0x01;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private static readonly StringBuilder Log = new();
    private static Form _form = null!;
    private static readonly int W = 400, H = 300;
    private static int _left, _top;

    [STAThread]
    private static void Main()
    {
        var area = Screen.PrimaryScreen!.Bounds;
        // 尽量避开控制台窗口：放到屏幕右侧偏下
        _left = Math.Min(area.Width - W - 40, 1100);
        _top = Math.Min(area.Height - H - 60, 380);

        Say("=== SetWindowDisplayAffinity vs GDI BitBlt 实测 ===");
        Say($"系统: {Environment.OSVersion.Version}  屏幕: {area.Width}x{area.Height}");
        Say($"测试窗口区域: ({_left},{_top}) {W}x{H}");
        Say("");

        _form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(_left, _top),
            Size = new Size(W, H),
            BackColor = Color.Magenta,
            TopMost = true,
            ShowInTaskbar = false,
            Text = "WDA_TEST"
        };
        _form.Show();
        Application.DoEvents();
        Thread.Sleep(900);
        Application.DoEvents();

        IntPtr hwnd = _form.Handle;
        Say($"窗口句柄: 0x{hwnd:X}");

        TestMode(hwnd, "对照 WDA_NONE(0x00)", WDA_NONE, expect: "抓得到（品红占比应 >90%）");
        TestMode(hwnd, "WDA_EXCLUDEFROMCAPTURE(0x11)", WDA_EXCLUDEFROMCAPTURE, expect: "抓不到（品红占比应 ≈0%）");
        TestMode(hwnd, "WDA_MONITOR(0x01)", WDA_MONITOR, expect: "抓成黑色（品红≈0%，黑色占比高）");

        // 复位，避免影响后续
        SetWindowDisplayAffinity(hwnd, WDA_NONE);

        Say("");
        Say("--- 判定 ---");
        Say(_excludeWorked
            ? "PASS: WDA_EXCLUDEFROMCAPTURE 对 BitBlt 生效 → 悬浮条加一条 P/Invoke 即可"
            : "FAIL: WDA_EXCLUDEFROMCAPTURE 对 BitBlt 无效 → 需改用悬浮条收边 + 全局热键方案");

        _form.Close();

        string path = Path.Combine(@"E:\开发练习项目\录屏软件\spike\WdaTest", "wda_result.txt");
        File.WriteAllText(path, Log.ToString(), Encoding.UTF8);
        Console.WriteLine($"报告已保存: {path}");
    }

    private static bool _excludeWorked;

    private static void TestMode(IntPtr hwnd, string name, uint affinity, string expect)
    {
        Say($"[{name}] 期望: {expect}");
        bool ok = SetWindowDisplayAffinity(hwnd, affinity);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        Say($"  SetWindowDisplayAffinity 返回 {(ok ? "TRUE" : $"FALSE (err={err})")}");

        Thread.Sleep(500);
        Application.DoEvents();

        var (magenta, black, total) = SampleRegion();
        double mPct = total == 0 ? 0 : magenta * 100.0 / total;
        double bPct = total == 0 ? 0 : black * 100.0 / total;
        Say($"  采样 {total} 像素 → 品红 {mPct:F1}%   近黑 {bPct:F1}%");

        if (affinity == WDA_NONE && mPct > 90) Say("  ✓ 对照组有效：抓得到窗口内容");
        if (affinity == WDA_EXCLUDEFROMCAPTURE && mPct < 5) { _excludeWorked = true; Say("  ✓ 已被排除出捕获"); }
        if (affinity == WDA_EXCLUDEFROMCAPTURE && mPct >= 5) Say("  ✗ 仍被抓到，该方案对 BitBlt 无效");
        if (affinity == WDA_MONITOR && mPct < 5 && bPct > 50) Say("  ✓ 渲染为黑色块");
        Say("");
    }

    /// <summary>抓取测试窗口所在区域，统计品红与近黑像素数。</summary>
    private static (int magenta, int black, int total) SampleRegion()
    {
        IntPtr screenDC = GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32, biCompression = BI_RGB
            }
        };
        IntPtr bmp = CreateDIBSection(screenDC, ref bi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDC, bmp);

        if (!BitBlt(memDC, 0, 0, W, H, screenDC, _left, _top, SRCCOPY | CAPTUREBLT))
            BitBlt(memDC, 0, 0, W, H, screenDC, _left, _top, SRCCOPY);

        int magenta = 0, black = 0;
        int total = W * H;
        unsafe
        {
            byte* p = (byte*)bits;
            for (int i = 0; i < total; i++)
            {
                byte b = p[i * 4], g = p[i * 4 + 1], r = p[i * 4 + 2];
                if (b > 200 && g < 60 && r > 200) magenta++;
                else if (b < 20 && g < 20 && r < 20) black++;
            }
        }

        SelectObject(memDC, old);
        DeleteObject(bmp);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);
        return (magenta, black, total);
    }

    private static void Say(string s)
    {
        Console.WriteLine(s);
        Log.AppendLine(s);
    }

    // ---------------- P/Invoke ----------------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

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
