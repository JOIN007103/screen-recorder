using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorder.Native;

int W = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
int H = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
Console.WriteLine($"屏幕 {W}x{H}  缓冲 {W*H*4/1024/1024}MB/帧");
const int N = 120;

// 一次性方案
var sw = Stopwatch.StartNew();
for (int i = 0; i < N; i++)
{
    if (NativeMethods.CaptureScreen(W, H, out IntPtr p, out _)) Marshal.FreeHGlobal(p);
}
sw.Stop();
double once = sw.Elapsed.TotalMilliseconds / N;
Console.WriteLine($"一次性CaptureScreen: {once:0.00} ms/帧 -> {1000/once:0.0} fps上限");

// 复用方案
using (var cap = new ScreenCapturer(W, H))
{
    for (int i = 0; i < 10; i++) cap.Capture(); // 预热
    sw.Restart();
    for (int i = 0; i < N; i++) cap.Capture();
    sw.Stop();
    double reuse = sw.Elapsed.TotalMilliseconds / N;
    Console.WriteLine($"复用ScreenCapturer:  {reuse:0.00} ms/帧 -> {1000/reuse:0.0} fps上限");
}

// 仅BitBlt（不GetDIBits）：测纯屏幕拷贝成本
IntPtr sdc = NativeMethods.GetDC(IntPtr.Zero);
IntPtr mdc = NativeMethods.CreateCompatibleDC(sdc);
IntPtr bmp = NativeMethods.CreateCompatibleBitmap(sdc, W, H);
NativeMethods.SelectObject(mdc, bmp);
sw.Restart();
for (int i = 0; i < N; i++) NativeMethods.BitBlt(mdc, 0, 0, W, H, sdc, 0, 0, 0x00CC0020);
sw.Stop();
double blt = sw.Elapsed.TotalMilliseconds / N;
Console.WriteLine($"仅BitBlt(无GetDIBits): {blt:0.00} ms/帧 -> {1000/blt:0.0} fps上限");
NativeMethods.DeleteObject(bmp); NativeMethods.DeleteDC(mdc); NativeMethods.ReleaseDC(IntPtr.Zero, sdc);
