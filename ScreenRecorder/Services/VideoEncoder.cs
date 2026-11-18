using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenRecorder.Models;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 视频编码器：将 BGRA 原始帧通过 stdin 管道送入 FFmpeg，编码为 H.264 + MP4。
    ///
    /// 编码器策略：
    /// - 自动（默认）：优先 NVIDIA NVENC 硬编码（GPU 承担编码，CPU 占用约为软编码的 1/19）；
    ///   探测失败则回退 libx264 软编码，保证无 NVIDIA 显卡的机器也能用。
    /// - 强制 NVENC / 强制软编码：由用户在界面指定。
    ///
    /// 注：本机 NVIDIA T1000 驱动 527.27 仅支持 NVENC API 12.0，
    /// 因此随程序分发的是 FFmpeg 6.0（基于 NVENC SDK 12.0）。
    /// 若换成要求 API 13.1 的新版 FFmpeg，硬编码会报 "Driver does not support the required nvenc API version"。
    /// </summary>
    internal sealed class VideoEncoder : IDisposable
    {
        private Process? _ffmpeg;
        private Stream? _stdin;
        private string? _outputPath;
        private bool _disposed;

        /// <summary>本次实际使用的编码器描述（NVENC 或 libx264）。</summary>
        public string ActualEncoder { get; private set; } = "未启动";

        public string? OutputPath => _outputPath;

        // NVENC 可用性探测缓存（按 ffmpeg 路径缓存，避免每次录制都探测）
        private static readonly Dictionary<string, bool> NvencProbeCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 探测指定 FFmpeg 是否能真正初始化 NVENC（不只是列出编码器，而是实际编码一小段）。
        /// </summary>
        public static bool ProbeNvenc(string ffmpegPath)
        {
            lock (NvencProbeCache)
            {
                if (NvencProbeCache.TryGetValue(ffmpegPath, out bool cached)) return cached;
            }

            bool ok = false;
            try
            {
                if (File.Exists(ffmpegPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        Arguments = "-hide_banner -loglevel error -f lavfi " +
                                    "-i color=c=black:s=256x256:d=0.2:r=30 -c:v h264_nvenc -f null -"
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        ok = p.WaitForExit(20000) && p.ExitCode == 0;
                        if (!p.HasExited)
                        {
                            try { p.Kill(entireProcessTree: true); } catch { }
                        }
                    }
                }
            }
            catch
            {
                ok = false;
            }

            lock (NvencProbeCache) { NvencProbeCache[ffmpegPath] = ok; }
            return ok;
        }

        /// <summary>
        /// 启动编码进程。
        /// </summary>
        /// <param name="settings">配置</param>
        /// <param name="width">帧宽</param>
        /// <param name="height">帧高</param>
        /// <param name="outputPath">输出文件完整路径</param>
        public void Start(RecorderSettings settings, int width, int height, string outputPath)
        {
            _outputPath = outputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            if (!File.Exists(settings.FfmpegPath))
                throw new FileNotFoundException("未找到 FFmpeg，请检查 tools 目录。", settings.FfmpegPath);

            // —— 决定编码器 ——
            bool useNvenc;
            switch (settings.Encoder)
            {
                case EncoderPreference.Nvenc:
                    useNvenc = true;
                    break;
                case EncoderPreference.Software:
                    useNvenc = false;
                    break;
                default: // Auto
                    useNvenc = ProbeNvenc(settings.FfmpegPath);
                    break;
            }

            string codecArgs;
            if (useNvenc)
            {
                ActualEncoder = "NVENC 硬编码 (GPU)";
                codecArgs = $"-c:v h264_nvenc -preset {settings.NvencPreset} " +
                            $"-b:v {settings.VideoBitrate} -pix_fmt yuv420p -movflags +faststart ";
            }
            else
            {
                ActualEncoder = "libx264 软编码 (CPU)";
                codecArgs = $"-c:v libx264 -preset {settings.EncoderPreset} " +
                            $"-b:v {settings.VideoBitrate} -pix_fmt yuv420p -movflags +faststart ";
            }

            var psi = new ProcessStartInfo
            {
                FileName = settings.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                Arguments =
                    "-hide_banner -loglevel warning -y " +
                    $"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {settings.FrameRate} -i - " +
                    "-an " + codecArgs +
                    $"\"{outputPath}\""
            };

            _ffmpeg = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _ffmpeg.Start();
            _stdin = _ffmpeg.StandardInput.BaseStream;
        }

        /// <summary>写入一帧 BGRA 数据。</summary>
        public void WriteFrame(byte[] frame)
        {
            if (_stdin == null) throw new InvalidOperationException("编码器尚未启动。");
            _stdin.Write(frame, 0, frame.Length);
        }

        /// <summary>写入多帧紧凑缓冲，减少系统调用次数。</summary>
        public void WriteFrames(byte[] buffer, int count)
        {
            if (_stdin == null) throw new InvalidOperationException("编码器尚未启动。");
            _stdin.Write(buffer, 0, count);
        }

        /// <summary>
        /// 收尾：关闭输入流并等待 FFmpeg 完成落盘。
        /// 返回 true 表示 FFmpeg 正常退出（exitCode=0）。
        /// </summary>
        public bool Finish(int timeoutMs = 30000)
        {
            if (_ffmpeg == null) return false;
            try
            {
                _stdin?.Flush();
                _stdin?.Close();
            }
            catch { /* 管道可能已断开，忽略 */ }

            bool exited = _ffmpeg.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { _ffmpeg.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return _ffmpeg.ExitCode == 0;
        }

        /// <summary>
        /// 将无声视频（临时 MP4）与音频 raw（s16le 48kHz 立体声）封装为带单音轨的最终 MP4。
        /// 视频流直接 copy（不重新编码，秒级完成），音频编码为 AAC。
        /// 若音频不可用或长度为 0，则退化为直接复制无声视频。
        /// 返回 true 表示成功。
        /// </summary>
        public static bool MuxAudio(string ffmpegPath, string silentVideoPath, string? audioRawPath,
            string outputPath, int timeoutMs = 60000)
        {
            if (!File.Exists(ffmpegPath) || !File.Exists(silentVideoPath)) return false;

            bool hasAudio = !string.IsNullOrEmpty(audioRawPath) && File.Exists(audioRawPath)
                            && new FileInfo(audioRawPath).Length > 0;

            string args;
            if (hasAudio)
            {
                args = "-hide_banner -loglevel error -y " +
                       $"-i \"{silentVideoPath}\" " +
                       $"-f s16le -ar 48000 -ac 2 -i \"{audioRawPath}\" " +
                       "-map 0:v:0 -map 1:a:0 " +
                       "-c:v copy -c:a aac -b:a 128k -shortest " +
                       "-movflags +faststart " +
                       $"\"{outputPath}\"";
            }
            else
            {
                args = "-hide_banner -loglevel error -y " +
                       $"-i \"{silentVideoPath}\" -c copy -movflags +faststart \"{outputPath}\"";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string err = p.StandardError.ReadToEnd();
                bool exited = p.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
                if (p.ExitCode != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"MuxAudio 失败: {err}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MuxAudio 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>读取 FFmpeg 的标准错误输出（诊断用）。</summary>
        public string ReadErrorTail(int maxChars = 2000)
        {
            try
            {
                string all = _ffmpeg?.StandardError.ReadToEnd() ?? string.Empty;
                return all.Length > maxChars ? all[^maxChars..] : all;
            }
            catch { return string.Empty; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { _stdin?.Close(); } catch { }
            if (_ffmpeg != null && !_ffmpeg.HasExited)
            {
                try { _ffmpeg.Kill(entireProcessTree: true); } catch { }
            }
            _ffmpeg?.Dispose();
            _disposed = true;
        }
    }
}
