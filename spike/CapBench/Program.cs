using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CapBench;

/// <summary>
/// GDI 捕获路径 A/B 实测基准。
///
/// 目的：验证「用 CreateDIBSection 让 BitBlt 直接写入自有像素内存」
/// 是否能省掉 GetDIBits + Marshal.Copy 两次 8MB 全帧搬运。
///
/// 被测变体（每轮按固定顺序测一遍，避免屏幕内容变化带来的偏差）：
///   A = 产品现状：CreateCompatibleBitmap + BitBlt(CAPTUREBLT) + GetDIBits + Marshal.Copy
///   B = DIBSection：BitBlt(CAPTUREBLT) 直写 DIB 位
///   C = 仅 BitBlt 到 DDB（不读回）—— 用于拆分出 BitBlt 本身的耗时
///   D = 仅 GetDIBits（在位图已有内容时）—— 拆分出读回耗时
///   E = DIBSection + 不带 CAPTUREBLT 的 BitBlt —— 验证 CAPTUREBLT 的代价
/// </summary>
internal static class Program
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const uint DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private static readonly StringBuilder Report = new();

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("pipe", StringComparison.OrdinalIgnoreCase))
            return PipeBench.Run();

        int w = Native.GetSystemMetrics(SM_CXSCREEN);
        int h = Native.GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0)
        {
            Console.WriteLine("无法获取屏幕分辨率。");
            return 1;
        }

        Line($"=== GDI 捕获路径基准测试 ===");
        Line($"屏幕分辨率: {w} x {h}   单帧字节数: {w * h * 4 / 1024.0 / 1024.0:F1} MB");
        Line($"进程架构: {(Environment.Is64BitProcess ? "x64" : "x86")}   CPU: {Environment.ProcessorCount} 逻辑核");
        Line("");

        const int warmup = 20;
        const int rounds = 200;

        var a = new ProductCapturer(w, h);
        var b = new DibCapturer(w, h, captureBlt: true);
        var c = new DecomposeCapturer(w, h);
        var e = new DibCapturer(w, h, captureBlt: false);

        Line($"--- 预热 {warmup} 轮 / 正式 {rounds} 轮 ---");

        var listA = new List<double>(rounds);
        var listB = new List<double>(rounds);
        var listC = new List<double>(rounds);
        var listD = new List<double>(rounds);
        var listE = new List<double>(rounds);
        var sw = new Stopwatch();

        for (int i = 0; i < warmup + rounds; i++)
        {
            sw.Restart(); a.Capture(); sw.Stop();
            if (i >= warmup) listA.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart(); b.Capture(); sw.Stop();
            if (i >= warmup) listB.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart(); c.CaptureBitBltOnly(); sw.Stop();
            if (i >= warmup) listC.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart(); c.GetDIBitsOnly(); sw.Stop();
            if (i >= warmup) listD.Add(sw.Elapsed.TotalMilliseconds);

            sw.Restart(); e.Capture(); sw.Stop();
            if (i >= warmup) listE.Add(sw.Elapsed.TotalMilliseconds);
        }

        Line("");
        Line("变体                                    平均      最小      最大      P95     折算fps");
        Line("------------------------------------------------------------------------------------");
        Print("A 现状 DDB+GetDIBits+Marshal.Copy ", listA);
        Print("B DIBSection + BitBlt(CAPTUREBLT)", listB);
        Print("C 仅 BitBlt 到 DDB（拆分用）      ", listC);
        Print("D 仅 GetDIBits（拆分用）          ", listD);
        Print("E DIBSection + BitBlt(SRCCOPY)    ", listE);

        Line("");
        Line("--- 耗时归因（由 C / D 拆分 A 的内部构成）---");
        double avgC = listC.Average(), avgD = listD.Average(), avgA = listA.Average(), avgB = listB.Average();
        Line($"BitBlt 本身（DDB 目标）      ≈ {avgC,6:F2} ms");
        Line($"GetDIBits 读回              ≈ {avgD,6:F2} ms");
        Line($"A 总计实测                   ≈ {avgA,6:F2} ms   （差值 {avgA - avgC - avgD,6:F2} ms 即 Marshal.Copy 等开销）");
        Line($"B 总计实测（DIB 目标位块）   ≈ {avgB,6:F2} ms");
        Line($"B 相对 A 的节省              ≈ {avgA - avgB,6:F2} ms  （{(avgA - avgB) / avgA * 100:F1}%）");
        Line("");

        // ---- 正确性校验：A 与 B 抓到的画面必须一致 ----
        Line("--- 正确性校验（A 与 B 立即各抓一帧，逐像素比对）---");
        a.Capture();
        b.Capture();
        int diff = CompareBuffers(a.ManagedBuffer, b.PixelPointer, w * h * 4, out long maxDelta);
        double diffPct = diff * 100.0 / (w * h);
        Line($"差异像素数: {diff} / {w * h}  ({diffPct:F4}%)   最大通道差值: {maxDelta}");
        Line(diffPct < 1.0 ? "PASS: 两条路径画面一致（差异应仅来自时钟类动态区域）" : "WARN: 差异偏大，需人工确认是否抓取语义不同");

        Line("");
        Line("--- 结论 ---");
        if (avgB < avgA * 0.9)
            Line($"B 方案有效：每帧快 {avgA - avgB:F1}ms，折算上限从 {1000 / avgA:F0}fps 提升到 {1000 / avgB:F0}fps");
        else if (avgB < avgA)
            Line($"B 方案收益有限（仅快 {avgA - avgB:F1}ms），建议维持现状");
        else
            Line($"B 方案为负收益（慢 {avgB - avgA:F1}ms），此路不通，维持现状");

        a.Dispose(); b.Dispose(); c.Dispose(); e.Dispose();

        string outPath = Path.Combine(AppContext.BaseDirectory, "cap_bench.txt");
        File.WriteAllText(outPath, Report.ToString(), Encoding.UTF8);
        Console.WriteLine();
        Console.WriteLine($"报告已保存: {outPath}");

        // 结果同时落盘到源码目录，便于回看
        string srcPath = Path.Combine(@"E:\开发练习项目\录屏软件\spike\CapBench", "cap_bench.txt");
        try { File.WriteAllText(srcPath, Report.ToString(), Encoding.UTF8); } catch { }

        return 0;
    }

    private static void Line(string s)
    {
        Console.WriteLine(s);
        Report.AppendLine(s);
    }

    private static void Print(string name, List<double> data)
    {
        var sorted = data.OrderBy(x => x).ToList();
        double avg = data.Average();
        double p95 = sorted[(int)(sorted.Count * 0.95)];
        Line($"{name} {avg,8:F2}  {sorted[0],8:F2}  {sorted[^1],8:F2}  {p95,7:F2}  {1000 / avg,8:F1}");
    }

    private static unsafe int CompareBuffers(byte[] managed, IntPtr dibPtr, int length, out long maxDelta)
    {
        int diff = 0;
        maxDelta = 0;
        byte* p = (byte*)dibPtr;
        for (int i = 0; i < length; i += 4)
        {
            int d0 = Math.Abs(managed[i] - p[i]);
            int d1 = Math.Abs(managed[i + 1] - p[i + 1]);
            int d2 = Math.Abs(managed[i + 2] - p[i + 2]);
            int m = Math.Max(d0, Math.Max(d1, d2));
            if (m > 8)
            {
                diff++;
                if (m > maxDelta) maxDelta = m;
            }
        }
        return diff;
    }

    // ---------------- 变体 A：产品现状 ----------------

    private sealed class ProductCapturer : IDisposable
    {
        private readonly int _w, _h;
        private readonly IntPtr _screenDC, _memDC, _bitmap, _oldBmp, _pixelPtr;
        private BITS.BITMAPINFO _bi;

        public byte[] ManagedBuffer { get; }

        public ProductCapturer(int w, int h)
        {
            _w = w; _h = h;
            _screenDC = Native.GetDC(IntPtr.Zero);
            _memDC = Native.CreateCompatibleDC(_screenDC);
            _bitmap = Native.CreateCompatibleBitmap(_screenDC, w, h);
            _oldBmp = Native.SelectObject(_memDC, _bitmap);

            _bi = new BITS.BITMAPINFO
            {
                bmiHeader = new BITS.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITS.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };
            _pixelPtr = Marshal.AllocHGlobal(w * 4 * h);
            ManagedBuffer = new byte[w * 4 * h];
        }

        public void Capture()
        {
            if (!Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, SRCCOPY | CAPTUREBLT))
                Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, SRCCOPY);
            Native.GetDIBits(_memDC, _bitmap, 0, (uint)_h, _pixelPtr, ref _bi, DIB_RGB_COLORS);
            Marshal.Copy(_pixelPtr, ManagedBuffer, 0, ManagedBuffer.Length);
        }

        public void Dispose()
        {
            Native.SelectObject(_memDC, _oldBmp);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(_memDC);
            Native.ReleaseDC(IntPtr.Zero, _screenDC);
            Marshal.FreeHGlobal(_pixelPtr);
        }
    }

    // ---------------- 变体 B / E：DIBSection ----------------

    private sealed class DibCapturer : IDisposable
    {
        private readonly int _w, _h;
        private readonly bool _captureBlt;
        private readonly IntPtr _screenDC, _memDC, _bitmap, _oldBmp;

        public IntPtr PixelPointer { get; }

        public DibCapturer(int w, int h, bool captureBlt)
        {
            _w = w; _h = h; _captureBlt = captureBlt;

            _screenDC = Native.GetDC(IntPtr.Zero);
            _memDC = Native.CreateCompatibleDC(_screenDC);

            var bi = new BITS.BITMAPINFO
            {
                bmiHeader = new BITS.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITS.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // 自上而下，与产品缓冲布局一致
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            _bitmap = Native.CreateDIBSection(_screenDC, ref bi, DIB_RGB_COLORS,
                                             out IntPtr bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败。");

            PixelPointer = bits;
            _oldBmp = Native.SelectObject(_memDC, _bitmap);
        }

        public void Capture()
        {
            int flags = SRCCOPY | (_captureBlt ? CAPTUREBLT : 0);
            if (!Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, flags) && _captureBlt)
                Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, SRCCOPY);
        }

        public void Dispose()
        {
            Native.SelectObject(_memDC, _oldBmp);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(_memDC);
            Native.ReleaseDC(IntPtr.Zero, _screenDC);
        }
    }

    // ---------------- 变体 C / D：拆分测量 ----------------

    private sealed class DecomposeCapturer : IDisposable
    {
        private readonly int _w, _h;
        private readonly IntPtr _screenDC, _memDC, _bitmap, _oldBmp, _pixelPtr;
        private BITS.BITMAPINFO _bi;

        public DecomposeCapturer(int w, int h)
        {
            _w = w; _h = h;
            _screenDC = Native.GetDC(IntPtr.Zero);
            _memDC = Native.CreateCompatibleDC(_screenDC);
            _bitmap = Native.CreateCompatibleBitmap(_screenDC, w, h);
            _oldBmp = Native.SelectObject(_memDC, _bitmap);
            _bi = new BITS.BITMAPINFO
            {
                bmiHeader = new BITS.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITS.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };
            _pixelPtr = Marshal.AllocHGlobal(w * 4 * h);
        }

        public void CaptureBitBltOnly()
        {
            if (!Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, SRCCOPY | CAPTUREBLT))
                Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, SRCCOPY);
        }

        public void GetDIBitsOnly()
        {
            Native.GetDIBits(_memDC, _bitmap, 0, (uint)_h, _pixelPtr, ref _bi, DIB_RGB_COLORS);
        }

        public void Dispose()
        {
            Native.SelectObject(_memDC, _oldBmp);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(_memDC);
            Native.ReleaseDC(IntPtr.Zero, _screenDC);
            Marshal.FreeHGlobal(_pixelPtr);
        }
    }

    // ---------------- P/Invoke ----------------

    internal static class BITS
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
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
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                         IntPtr hdcSrc, int sx, int sy, int rop);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
                                           IntPtr lpvBits, ref BITS.BITMAPINFO lpbmi, uint usage);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITS.BITMAPINFO pbmi,
                                                     uint usage, out IntPtr ppvBits,
                                                     IntPtr hSection, uint offset);
    }
}
