// S2 全屏捕获基准：纯 GDI BitBlt，测量帧率/CPU，并采样像素检测黑屏（无桌面访问）。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Gdi
{
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hDC, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr hDC, int x, int y);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hDC);
}

internal static class Program
{
    const int SRCCOPY = 0x00CC0020;
    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    static int Main(string[] args)
    {
        int durationSec = args.Length > 0 && int.TryParse(args[0], out var d) ? d : 5;
        int targetFps = args.Length > 1 && int.TryParse(args[1], out var f) ? f : 30;

        int w = Gdi.GetSystemMetrics(SM_CXSCREEN);
        int h = Gdi.GetSystemMetrics(SM_CYSCREEN);
        Console.WriteLine($"屏幕分辨率: {w}x{h}");

        IntPtr screenDC = Gdi.GetDC(IntPtr.Zero);
        if (screenDC == IntPtr.Zero) { Console.WriteLine("FAIL: 无法获取屏幕 DC（无桌面访问）"); return 2; }
        IntPtr memDC = Gdi.CreateCompatibleDC(screenDC);
        IntPtr bmp = Gdi.CreateCompatibleBitmap(screenDC, w, h);
        IntPtr old = Gdi.SelectObject(memDC, bmp);

        // 黑屏检测：取 5 个采样点
        int[] xs = { w / 4, w / 2, 3 * w / 4, w / 6, 5 * w / 6 };
        int[] ys = { h / 4, h / 2, 3 * h / 4, h / 3, 2 * h / 3 };
        int nonBlack = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            uint c = Gdi.GetPixel(screenDC, xs[i], ys[i]);
            if ((c & 0xFFFFFF) != 0) nonBlack++;
        }
        Console.WriteLine($"桌面像素采样: {nonBlack}/{xs.Length} 非黑点");

        var proc = Process.GetCurrentProcess();
        TimeSpan cpuStart = proc.TotalProcessorTime;
        long memStart = proc.WorkingSet64;
        var sw = Stopwatch.StartNew();
        long frames = 0;
        long intervalTicks = Stopwatch.Frequency / targetFps;
        long nextTick = 0;

        while (sw.Elapsed.TotalSeconds < durationSec)
        {
            bool ok = Gdi.BitBlt(memDC, 0, 0, w, h, screenDC, 0, 0, SRCCOPY);
            frames++;
            long elapsedTicks = (long)(sw.Elapsed.TotalSeconds * Stopwatch.Frequency);
            long due = nextTick;
            while (sw.Elapsed.TotalSeconds * Stopwatch.Frequency < due)
            {
                double ms = (due - sw.Elapsed.TotalSeconds * Stopwatch.Frequency) / (double)Stopwatch.Frequency * 1000;
                if (ms > 1) System.Threading.Thread.Sleep((int)ms);
                else break;
            }
            nextTick += intervalTicks;
        }
        sw.Stop();
        TimeSpan cpuEnd = proc.TotalProcessorTime;
        long memEnd = proc.WorkingSet64;

        double actualFps = frames / sw.Elapsed.TotalSeconds;
        double cpuPct = (cpuEnd - cpuStart).TotalMilliseconds / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
        double memDeltaMB = (memEnd - memStart) / 1024.0 / 1024.0;

        Console.WriteLine($"时长: {sw.Elapsed.TotalSeconds:F2}s | 帧数: {frames} | 实际帧率: {actualFps:F1} fps (目标 {targetFps})");
        Console.WriteLine($"CPU: {cpuPct:F1}% (按 {Environment.ProcessorCount} 核归一) | 内存增量: {memDeltaMB:F1} MB");
        Console.WriteLine(nonBlack > 0 ? "PASS: 交互桌面可达，捕获链路有效" : "WARN: 采样全黑，可能无桌面访问");

        Gdi.SelectObject(memDC, old);
        Gdi.DeleteObject(bmp);
        Gdi.DeleteDC(memDC);
        Gdi.ReleaseDC(IntPtr.Zero, screenDC);
        return 0;
    }
}
