using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenRecorder.Models;
using ScreenRecorder.Native;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 录制引擎：核心状态机与生产循环。
    /// 线程模型：捕获在独立后台线程运行；FFmpeg 在独立进程；UI 通过事件获取状态。
    /// 数据流：GDI 捕获 BGRA → （鼠标效果叠加）→ 转 BGR24 → FFmpeg stdin → MP4。
    /// </summary>
    public sealed class RecorderEngine : IDisposable
    {
        private readonly RecorderSettings _settings;
        private readonly MouseHook _mouseHook;
        private readonly object _stateLock = new();
        private Thread? _captureThread;
        private ScreenCapturer? _capturer;
        private VideoEncoder? _encoder;
        private AudioCapturer? _audio;
        private MouseEffectRenderer? _effects;
        private string? _tempSilentVideo;
        private string? _tempAudioRaw;
        private string? _finalOutputPath;
        private volatile bool _stopRequested;
        private volatile bool _paused;
        private bool _disposed;

        private int _width;
        private int _height;
        private long _frameCount;
        private DateTime _recordingStart;
        private TimeSpan _elapsedBeforePause;

        // ---- 事件（供 UI 订阅）----

        public event Action<RecorderState>? StateChanged;
        public event Action<string>? ErrorOccurred;
        /// <summary>进度事件：已录帧数、时长、当前帧率。</summary>
        public event Action<long, TimeSpan, double>? Progress;
        /// <summary>磁盘空间预警（仅提示）。</summary>
        public event Action<double>? DiskWarning;

        public RecorderState State { get; private set; } = RecorderState.Idle;
        public long FrameCount => _frameCount;

        // ---- 诊断计数器（供性能分析，不影响录制行为）----

        /// <summary>实际调用屏幕捕获的次数（不等于写入编码器的帧数）。</summary>
        public long CaptureAttempts => Interlocked.Read(ref _captureAttempts);

        /// <summary>单次屏幕捕获的平均耗时（毫秒）。</summary>
        public double CaptureOnlyAvgMs
        {
            get
            {
                long n = Interlocked.Read(ref _captureAttempts);
                return n == 0 ? 0 : Interlocked.Read(ref _captureTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n;
            }
        }

        /// <summary>每轮捕获体（捕获＋效果＋写管道）的平均耗时（毫秒）。</summary>
        public double FrameWorkAvgMs
        {
            get
            {
                long n = Interlocked.Read(ref _captureAttempts);
                return n == 0 ? 0 : Interlocked.Read(ref _frameWorkTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n;
            }
        }

        private long _captureAttempts;
        private long _captureTicks;
        private long _frameWorkTicks;

        /// <summary>本次录制实际使用的编码器（NVENC 硬编码 / libx264 软编码）。</summary>
        public string EncoderDescription => _encoder?.ActualEncoder ?? _lastEncoderDescription;

        private string _lastEncoderDescription = "-";

        /// <summary>本次录制实际使用的音源描述。</summary>
        public string AudioDescription => _lastAudioDescription;

        private string _lastAudioDescription = "-";
        public TimeSpan Elapsed
        {
            get
            {
                if (State == RecorderState.Recording)
                    return _elapsedBeforePause + (DateTime.Now - _recordingStart);
                return _elapsedBeforePause;
            }
        }

        public RecorderEngine(RecorderSettings settings)
        {
            _settings = settings;
            _mouseHook = new MouseHook();
        }

        /// <summary>开始录制。创建输出文件并启动捕获线程。</summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (State != RecorderState.Idle) return;

                _width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                _height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
                if (_width <= 0 || _height <= 0)
                    throw new InvalidOperationException("无法获取屏幕分辨率。");

                _frameCount = 0;
                _elapsedBeforePause = TimeSpan.Zero;
                _stopRequested = false;
                _paused = false;
                Interlocked.Exchange(ref _captureAttempts, 0);
                Interlocked.Exchange(ref _captureTicks, 0);
                Interlocked.Exchange(ref _frameWorkTicks, 0);

                string fileName = $"rec_{DateTime.Now:yyyyMMdd_HHmmss}{_settings.FileExtension}";
                _finalOutputPath = Path.Combine(_settings.OutputDirectory, fileName);
                Directory.CreateDirectory(_settings.OutputDirectory);

                // 临时文件：视频先编码为无声 MP4，音频写 raw，停止后再封装为最终文件
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                _tempSilentVideo = Path.Combine(Path.GetTempPath(), $"sr_silent_{stamp}.mp4");
                _tempAudioRaw = Path.Combine(Path.GetTempPath(), $"sr_audio_{stamp}.raw");

                // —— 启动音频采集 ——
                _lastAudioDescription = "未启动";
                _audio = new AudioCapturer(_settings);
                try
                {
                    bool audioOk = _audio.Start(_tempAudioRaw);
                    _lastAudioDescription = _audio.SourceDescription;
                    if (!audioOk)
                        ErrorOccurred?.Invoke("未检测到可用音频输入设备，本次将录制静音音轨。");
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke($"音频采集启动失败（将录制静音）：{ex.Message}");
                    _lastAudioDescription = "无可用音源（录制静音）";
                }

                // —— 启动视频编码（临时无声文件）——
                _encoder = new VideoEncoder();
                _encoder.Start(_settings, _width, _height, _tempSilentVideo);
                _lastEncoderDescription = _encoder.ActualEncoder;

                _capturer = new ScreenCapturer(_width, _height);
                _effects = new MouseEffectRenderer(_settings, _width, _height);
                _mouseHook.MouseClicked += OnMouseClicked;
                try { _mouseHook.Install(); }
                catch (Exception ex) { ErrorOccurred?.Invoke($"鼠标点击钩子安装失败（涟漪效果不可用）：{ex.Message}"); }

                _recordingStart = DateTime.Now;
                SetState(RecorderState.Recording);

                _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "CaptureThread" };
                _captureThread.Start();
            }
        }

        /// <summary>暂停录制（画面冻结，不写入帧，不计时）。</summary>
        public void Pause()
        {
            lock (_stateLock)
            {
                if (State != RecorderState.Recording) return;
                _elapsedBeforePause += DateTime.Now - _recordingStart;
                _paused = true;
                try { _audio?.Pause(); } catch { }
                SetState(RecorderState.Paused);
            }
        }

        /// <summary>从暂停恢复。</summary>
        public void Resume()
        {
            lock (_stateLock)
            {
                if (State != RecorderState.Paused) return;
                _recordingStart = DateTime.Now;
                _paused = false;
                try { _audio?.Resume(); } catch { }
                SetState(RecorderState.Recording);
            }
        }

        /// <summary>停止录制并落盘。返回输出文件路径。</summary>
        public string? Stop()
        {
            lock (_stateLock)
            {
                if (State != RecorderState.Recording && State != RecorderState.Paused) return null;
                if (State == RecorderState.Recording)
                    _elapsedBeforePause += DateTime.Now - _recordingStart;
                SetState(RecorderState.Saving);
                _stopRequested = true;
            }

            _captureThread?.Join(10000);
            _captureThread = null;

            // 停止音频采集（冲刷缓冲并关闭 raw 文件）
            long audioBytes = 0;
            try { audioBytes = _audio?.Stop() ?? 0; }
            catch (Exception ex) { ErrorOccurred?.Invoke($"音频收尾失败：{ex.Message}"); }

            // 视频编码收尾（临时无声文件）
            bool videoOk = false;
            try { videoOk = _encoder?.Finish() ?? false; }
            catch (Exception ex) { ErrorOccurred?.Invoke($"视频编码收尾失败：{ex.Message}"); }

            // 封装：无声视频 + 音频 raw → 最终带音轨 MP4
            bool muxOk = false;
            if (videoOk && _tempSilentVideo != null && _finalOutputPath != null)
            {
                try
                {
                    muxOk = VideoEncoder.MuxAudio(_settings.FfmpegPath, _tempSilentVideo,
                        audioBytes > 0 ? _tempAudioRaw : null, _finalOutputPath);
                }
                catch (Exception ex) { ErrorOccurred?.Invoke($"音视频封装失败：{ex.Message}"); }
            }

            string? outPath = muxOk ? _finalOutputPath : null;

            lock (_stateLock)
            {
                _encoder?.Dispose();
                _encoder = null;
                _audio?.Dispose();
                _audio = null;
                _capturer?.Dispose();
                _capturer = null;
                SetState(RecorderState.Idle);
            }

            // 清理临时文件
            TryDelete(_tempSilentVideo);
            TryDelete(_tempAudioRaw);
            _tempSilentVideo = null;
            _tempAudioRaw = null;

            if (!videoOk) ErrorOccurred?.Invoke("视频编码未正常完成，输出文件可能不完整。");
            else if (!muxOk) ErrorOccurred?.Invoke("音视频封装失败，未生成最终文件。");
            return outPath;
        }

        private void OnMouseClicked(int x, int y, bool rightButton)
        {
            _effects?.AddClick(x, y, rightButton);
        }

        /// <summary>
        /// 捕获主循环：按真实时间推进的恒定帧率(CFR)录制。
        /// 关键：输出帧数 = 真实录制时长 × 帧率。当 GDI 捕获跟不上目标帧率时，
        /// 重复上一帧补齐，保证视频时长与真实时间一致（否则视频会被加速播放）。
        /// 暂停时逻辑时钟停止，恢复后继续，暂停区间不写帧。
        /// </summary>
        private void CaptureLoop()
        {
            int fps = Math.Max(1, _settings.FrameRate);
            double frameIntervalMs = 1000.0 / fps;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            long lastWallMs = 0;
            double logicalMs = 0;          // 逻辑录制时长（暂停时不增长）
            long framesWritten = 0;        // 已写入编码器的帧数
            long lastProgressFrame = 0;
            double lastDiskCheck = 0;

            int stride = _width * 4;
            byte[] bgra = new byte[stride * _height];
            byte[] lastFrame = new byte[stride * _height]; // 上一帧备份，用于补帧
            bool hasLastFrame = false;

            try
            {
                while (!_stopRequested)
                {
                    long nowWall = wall.ElapsedMilliseconds;
                    long dt = nowWall - lastWallMs;
                    lastWallMs = nowWall;

                    if (_paused)
                    {
                        // 暂停：逻辑时钟不前进，重置基准避免恢复瞬间补大量帧
                        Thread.Sleep(20);
                        framesWritten = (long)(logicalMs / frameIntervalMs); // 与逻辑时钟对齐
                        continue;
                    }

                    if (dt > 0) logicalMs += dt;

                    // 目标：截至当前逻辑时刻应写入的帧数
                    long targetFrames = (long)(logicalMs / frameIntervalMs);
                    if (targetFrames < framesWritten) targetFrames = framesWritten;

                    // 超前判断：捕获速度快于目标帧率时，本次不捕获也不写帧。
                    // 若照写会把帧数撑到“时长×帧率”之外，导致视频比真实时间更长（慢放）。
                    if (hasLastFrame && framesWritten >= targetFrames)
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    var capturer = _capturer;
                    long tStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    long tCapStart = tStart;
                    if (capturer == null || !capturer.Capture())
                    {
                        ErrorOccurred?.Invoke("屏幕捕获失败一次，已跳过该帧。");
                        Thread.Sleep((int)frameIntervalMs);
                        continue;
                    }
                    long tCapEnd = System.Diagnostics.Stopwatch.GetTimestamp();

                    // 捕获新帧 → 叠加效果
                    Marshal.Copy(capturer.PixelBuffer, bgra, 0, bgra.Length);
                    _effects?.Apply(bgra, _width, _height, DateTime.Now);

                    // 补齐落后帧：用上一帧填充到目标帧数之前，保持时间轴连续
                    while (framesWritten < targetFrames - 1 && hasLastFrame)
                    {
                        _encoder?.WriteFrame(lastFrame);
                        framesWritten++;
                        _frameCount++;
                    }

                    // 写入当前帧并备份
                    _encoder?.WriteFrame(bgra);
                    Array.Copy(bgra, lastFrame, bgra.Length);
                    hasLastFrame = true;
                    framesWritten++;
                    _frameCount++;

                    // 诊断：记录本轮捕获与整体处理耗时
                    long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _captureAttempts);
                    Interlocked.Add(ref _captureTicks, tCapEnd - tCapStart);
                    Interlocked.Add(ref _frameWorkTicks, tEnd - tStart);

                    // 定期进度回调（每 500ms）
                    if (_frameCount - lastProgressFrame >= fps / 2)
                    {
                        lastProgressFrame = _frameCount;
                        Progress?.Invoke(_frameCount, Elapsed, CalculateFps());
                    }

                    // 定期磁盘检查（每 5 秒）
                    double nowSec = wall.Elapsed.TotalSeconds;
                    if (nowSec - lastDiskCheck >= 5)
                    {
                        lastDiskCheck = nowSec;
                        double freeMb = NativeMethods.GetFreeSpaceMb(_settings.OutputDirectory);
                        if (freeMb >= 0 && freeMb < _settings.DiskWarningMb)
                            DiskWarning?.Invoke(freeMb);
                    }

                    // 轻微节流：避免捕获过快时空转（按逻辑时钟与实际写入对齐）
                    long ahead = framesWritten - (long)(logicalMs / frameIntervalMs);
                    if (ahead > 0) Thread.Sleep(2);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"捕获线程异常：{ex.Message}");
            }
        }

        private DateTime _fpsWindowStart = DateTime.Now;
        private long _fpsWindowFrames;
        private double CalculateFps()
        {
            var now = DateTime.Now;
            double sec = (now - _fpsWindowStart).TotalSeconds;
            long frames = _frameCount - _fpsWindowFrames;
            if (sec <= 0) return 0;
            double fps = frames / sec;
            _fpsWindowStart = now;
            _fpsWindowFrames = _frameCount;
            return fps;
        }

        private void SetState(RecorderState newState)
        {
            State = newState;
            StateChanged?.Invoke(newState);
        }

        private static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (State == RecorderState.Recording || State == RecorderState.Paused) Stop();
            _mouseHook.MouseClicked -= OnMouseClicked;
            _mouseHook.Dispose();
            _disposed = true;
        }
    }
}
