using System.Diagnostics;
using System.IO;
using ScreenRecorder.Models;
using ScreenRecorder.Services;

// 端到端验收：直接驱动生产 RecorderEngine，真实录制 → 编码 → 落盘。
// 控制台无 UI 消息循环，鼠标点击钩子会安装失败（涟漪在 GUI 程序里才生效），
// 但捕获、效果渲染（光标/轨迹基于 GetCursorPos）、编码、落盘链路全部为真实代码。

string baseDir = AppContext.BaseDirectory;
string ffmpegPath = @"E:\开发练习项目\录屏软件\ScreenRecorder\bin\Debug\net8.0-windows\tools\ffmpeg.exe";
string outDir = @"E:\开发练习项目\录屏软件\spike\MvpE2E\output";
Directory.CreateDirectory(outDir);

var settings = new RecorderSettings
{
    FrameRate = 30,
    EncoderPreset = "veryfast",
    VideoBitrate = 4_000_000,
    FfmpegPath = ffmpegPath,
    OutputDirectory = outDir,
    ShowClickRipple = true,
    ShowCursorHighlight = true,
    ShowMouseTrail = true,
    DiskWarningMb = 2048
};

Console.WriteLine("=== MVP 端到端验收（真实引擎）===");
Console.WriteLine($"FFmpeg: {(File.Exists(ffmpegPath) ? "OK" : "MISSING")}");
Console.WriteLine($"输出目录: {outDir}");

using var engine = new RecorderEngine(settings);
engine.ErrorOccurred += m => Console.WriteLine("[警告] " + m);
engine.DiskWarning += free => Console.WriteLine($"[磁盘预警] 剩余 {free:0} MB");
engine.StateChanged += s => Console.WriteLine($"[状态] {s}");
engine.Progress += (frames, elapsed, fps) =>
    Console.WriteLine($"[进度] 帧={frames} 时长={elapsed.TotalSeconds:0.0}s FPS={fps:0.0}");

var sw = Stopwatch.StartNew();
try
{
    engine.Start();
    Console.WriteLine("开始录制，持续 6 秒…");
    Thread.Sleep(6000);
    string? path = engine.Stop();
    sw.Stop();

    Console.WriteLine("=== 结果 ===");
    Console.WriteLine($"编码器: {engine.EncoderDescription}");
    Console.WriteLine($"音源: {engine.AudioDescription}");
    Console.WriteLine($"总帧数: {engine.FrameCount}");
    Console.WriteLine($"耗时: {sw.Elapsed.TotalSeconds:0.00}s");

    double sec = sw.Elapsed.TotalSeconds;
    long attempts = engine.CaptureAttempts;
    Console.WriteLine($"--- 性能诊断 ---");
    Console.WriteLine($"捕获供给: {attempts} 次 / {sec:0.00}s → {attempts / sec:0.0} fps");
    Console.WriteLine($"单次捕获耗时(均值): {engine.CaptureOnlyAvgMs:0.00} ms");
    Console.WriteLine($"每轮处理耗时(均值): {engine.FrameWorkAvgMs:0.00} ms  (捕获+效果+写管道)");
    Console.WriteLine($"理论供给上限: {1000.0 / Math.Max(0.01, engine.FrameWorkAvgMs):0.0} fps");
    Console.WriteLine($"输出文件: {path}");
    if (path != null && File.Exists(path))
        Console.WriteLine($"文件大小: {new FileInfo(path).Length / 1024.0:0} KB");
    else
        Console.WriteLine("!! 未生成输出文件");
}
catch (Exception ex)
{
    Console.WriteLine("!! 异常: " + ex);
}
