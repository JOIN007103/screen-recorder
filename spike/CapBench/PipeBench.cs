using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CapBench;

/// <summary>
/// 管道吞吐基准：测量「把 1 帧 1080p BGRA 写进 FFmpeg stdin」到底阻塞多久。
/// 这是产品每帧开销里除捕获之外的第二大块，必须单独量化。
/// </summary>
internal static class PipeBench
{
    private const string FfmpegPath =
        @"E:\开发练习项目\录屏软件\ScreenRecorder\tools\ffmpeg.exe";

    public static int Run(int frames = 600)
    {
        var sb = new StringBuilder();
        void W(string s) { Console.WriteLine(s); sb.AppendLine(s); }

        int w = 1920, h = 1080;
        int frameBytes = w * 4 * h;

        W("=== 送帧管道吞吐基准（1080p BGRA → FFmpeg stdin）===");
        W($"帧字节数: {frameBytes / 1024.0 / 1024.0:F1} MB   帧数: {frames}");
        W($"FFmpeg: {(File.Exists(FfmpegPath) ? "已找到" : "缺失!")}");
        W("");

        // 先抓一帧真实画面作为素材，避免纯黑帧让编码器走极快路径
        byte[] frame = new byte[frameBytes];
        try
        {
            using var cap = new BenchDib(w, h);
            cap.Capture();
            Marshal.Copy(cap.PixelPointer, frame, 0, frameBytes);
            W("素材帧: 已从真实屏幕抓取");
        }
        catch (Exception ex)
        {
            new Random(1).NextBytes(frame);
            W($"素材帧: 抓取失败({ex.Message})，改用随机噪声");
        }

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            Arguments =
                "-hide_banner -loglevel error -y " +
                $"-f rawvideo -pix_fmt bgra -s {w}x{h} -r 30 -i - " +
                "-an -c:v h264_nvenc -preset p5 -b:v 4000000 -pix_fmt yuv420p -f null -"
        };

        using var p = Process.Start(psi)!;
        var stdin = p.StandardInput.BaseStream;

        var times = new List<double>(frames);
        var sw = new Stopwatch();
        var total = Stopwatch.StartNew();

        for (int i = 0; i < frames; i++)
        {
            // 轻微改动，模拟真实画面变化（只动 64 个像素，成本可忽略）
            int idx = (i * 4096) % (frameBytes - 4);
            frame[idx] = (byte)(i & 0xFF);
            frame[idx + 1] = (byte)((i >> 8) & 0xFF);

            sw.Restart();
            stdin.Write(frame, 0, frameBytes);
            sw.Stop();
            if (i >= 30) times.Add(sw.Elapsed.TotalMilliseconds);
        }

        stdin.Flush();
        stdin.Close();
        bool exited = p.WaitForExit(60000);
        total.Stop();

        var sorted = times.OrderBy(x => x).ToList();
        double avg = times.Average();
        double wall = total.Elapsed.TotalSeconds;

        W("");
        W("写入 stdin 的阻塞耗时（不含 FFmpeg 收尾）：");
        W($"  平均 {avg:F2} ms   最小 {sorted[0]:F2}   最大 {sorted[^1]:F2}   P95 {sorted[(int)(sorted.Count * 0.95)]:F2}");
        W($"  折算吞吐上限 ≈ {1000 / avg:F1} fps");
        W("");
        W($"整轮墙钟: {wall:F2} s / {frames} 帧 → 端到端 {frames / wall:F1} fps");
        W($"FFmpeg 正常退出: {(exited ? $"是 (exit={p.ExitCode})" : "否，超时")}");
        W("");

        if (!exited) W("WARN: FFmpeg 未在 60s 内退出");
        else if (p.ExitCode != 0) W($"WARN: FFmpeg 退出码 {p.ExitCode}");
        else W("PASS: 管道 + NVENC 全链路正常");

        string outPath = Path.Combine(@"E:\开发练习项目\录屏软件\spike\CapBench", "pipe_bench.txt");
        try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { }
        Console.WriteLine($"报告已保存: {outPath}");
        return 0;
    }

    /// <summary>精简版 DIB 捕获器，仅用于取一帧素材。</summary>
    private sealed class BenchDib : IDisposable
    {
        private readonly IntPtr _screenDC, _memDC, _bitmap, _oldBmp;
        public IntPtr PixelPointer { get; }

        public BenchDib(int w, int h)
        {
            _screenDC = Program.Native.GetDC(IntPtr.Zero);
            _memDC = Program.Native.CreateCompatibleDC(_screenDC);
            var bi = new Program.BITS.BITMAPINFO
            {
                bmiHeader = new Program.BITS.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Program.BITS.BITMAPINFOHEADER>(),
                    biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
                    biCompression = 0
                }
            };
            _bitmap = Program.Native.CreateDIBSection(_screenDC, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            PixelPointer = bits;
            _oldBmp = Program.Native.SelectObject(_memDC, _bitmap);
            _w = w; _h = h;
        }

        private readonly int _w, _h;

        public void Capture()
        {
            if (!Program.Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, 0x00CC0020 | 0x40000000))
                Program.Native.BitBlt(_memDC, 0, 0, _w, _h, _screenDC, 0, 0, 0x00CC0020);
        }

        public void Dispose()
        {
            Program.Native.SelectObject(_memDC, _oldBmp);
            Program.Native.DeleteObject(_bitmap);
            Program.Native.DeleteDC(_memDC);
            Program.Native.ReleaseDC(IntPtr.Zero, _screenDC);
        }
    }
}
