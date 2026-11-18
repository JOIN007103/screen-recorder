// S4 端到端：GDI 捕获真实屏幕 -> 管道送入 FFmpeg(libx264) -> MP4 落盘，测量帧率/CPU/内存基线。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal static class Gdi
{
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int n);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] internal static extern bool GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, IntPtr bits, ref BITMAPINFO bi, uint usage);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }
}

internal static class Program
{
    const int SRCCOPY = 0x00CC0020;
    static int Main(string[] args)
    {
        int durationSec = args.Length > 0 && int.TryParse(args[0], out var d) ? d : 10;
        int fps = 30;
        string ffmpeg = @"E:\ffmpeg\bin\ffmpeg.exe";
        string outFile = Path.Combine(AppContext.BaseDirectory, "s4_e2e.mp4");
        if (File.Exists(outFile)) File.Delete(outFile);

        int W = Gdi.GetSystemMetrics(0), H = Gdi.GetSystemMetrics(1);
        // 编码尺寸固定 1080p（若屏幕非16:9则按比例缩放到1920x1080，由ffmpeg scale处理）
        Console.WriteLine($"屏幕: {W}x{H} -> 编码 1920x1080@{fps}fps");

        IntPtr screenDC = Gdi.GetDC(IntPtr.Zero);
        IntPtr memDC = Gdi.CreateCompatibleDC(screenDC);
        IntPtr bmp = Gdi.CreateCompatibleBitmap(screenDC, W, H);
        IntPtr old = Gdi.SelectObject(memDC, bmp);
        int stride = W * 4;
        IntPtr pixels = Marshal.AllocHGlobal(stride * H);

        var bi = new Gdi.BITMAPINFO();
        bi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Gdi.BITMAPINFOHEADER));
        bi.bmiHeader.biWidth = W; bi.bmiHeader.biHeight = -H; // 负号=自上而下
        bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = 0;

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = $"-hide_banner -loglevel error -f rawvideo -pix_fmt bgr24 -s {W}x{H} -r {fps} -i - " +
                        $"-vf scale=1920:1080 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -y \"{outFile}\""
        };
        var proc = Process.Start(psi);
        Stream stdin = proc.StandardInput.BaseStream;

        var procSelf = Process.GetCurrentProcess();
        TimeSpan cpuStart = procSelf.TotalProcessorTime;
        long memStart = procSelf.WorkingSet64;
        var sw = Stopwatch.StartNew();
        long frames = 0;
        long intervalTicks = Stopwatch.Frequency / fps;
        long nextTick = 0;
        byte[] rowBuf = new byte[stride];

        try
        {
            while (sw.Elapsed.TotalSeconds < durationSec)
            {
                Gdi.BitBlt(memDC, 0, 0, W, H, screenDC, 0, 0, SRCCOPY);
                Gdi.GetDIBits(memDC, bmp, 0, (uint)H, pixels, ref bi, 0);
                // BGRA -> RGB24 转换（FFmpeg 输入用 bgr24 更省带宽）
                int outStride = W * 3;
                byte[] rgb = new byte[outStride * H];
                for (int y = 0; y < H; y++)
                {
                    IntPtr src = IntPtr.Add(pixels, y * stride);
                    Marshal.Copy(src, rowBuf, 0, stride);
                    int dst = y * outStride;
                    for (int x = 0; x < W; x++)
                    {
                        int s = x * 4, di = dst + x * 3;
                        rgb[di] = rowBuf[s + 2]; rgb[di + 1] = rowBuf[s + 1]; rgb[di + 2] = rowBuf[s];
                    }
                }
                stdin.Write(rgb, 0, rgb.Length);
                frames++;

                double due = nextTick;
                while (sw.Elapsed.TotalSeconds * Stopwatch.Frequency < due)
                {
                    double ms = (due - sw.Elapsed.TotalSeconds * Stopwatch.Frequency) / (double)Stopwatch.Frequency * 1000;
                    if (ms > 1) System.Threading.Thread.Sleep((int)ms); else break;
                }
                nextTick += intervalTicks;
            }
        }
        finally
        {
            stdin.Flush(); stdin.Close();
        }
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);
        sw.Stop();
        TimeSpan cpuEnd = procSelf.TotalProcessorTime;
        long memEnd = procSelf.WorkingSet64;

        double actualFps = frames / sw.Elapsed.TotalSeconds;
        double cpuPct = (cpuEnd - cpuStart).TotalMilliseconds / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
        double memDeltaMB = (memEnd - memStart) / 1024.0 / 1024.0;
        long fileSize = File.Exists(outFile) ? new FileInfo(outFile).Length : 0;

        Console.WriteLine($"时长 {sw.Elapsed.TotalSeconds:F2}s | 帧 {frames} | 帧率 {actualFps:F1}fps | FFmpeg退出码 {proc.ExitCode}");
        Console.WriteLine($"CPU(采集进程) {cpuPct:F1}% | 内存增量 {memDeltaMB:F1}MB | 输出 {fileSize/1024.0/1024.0:F2}MB");
        if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine("FFmpeg stderr: " + stderr.Substring(0, Math.Min(300, stderr.Length)));
        Console.WriteLine(proc.ExitCode == 0 && fileSize > 10240 ? "PASS: 端到端捕获->编码->落盘链路有效" : "FAIL: 端到端链路失败");

        Marshal.FreeHGlobal(pixels);
        Gdi.SelectObject(memDC, old); Gdi.DeleteObject(bmp); Gdi.DeleteDC(memDC); Gdi.ReleaseDC(IntPtr.Zero, screenDC);
        return 0;
    }
}
