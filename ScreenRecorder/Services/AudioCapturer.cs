using System;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using ScreenRecorder.Models;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 音频采集器：同时采集系统声（WASAPI loopback）与麦克风，实时混音为单条 48kHz 立体声 PCM，
    /// 边录边写入 s16le raw 文件。停止后由 VideoEncoder 读取该文件封装为 AAC 音轨。
    ///
    /// 降级策略：任一音源不可用时自动降级为单音源；两路都不可用则降级为静音（视频仍正常录制）。
    /// 格式统一：所有输入重采样/声道转换为 48kHz 立体声 IEEE Float，混音后再转 s16le 写入。
    /// 暂停：暂停期间不写音频数据，与视频逻辑时钟保持一致。
    /// </summary>
    internal sealed class AudioCapturer : IDisposable
    {
        private readonly RecorderSettings _settings;
        private string? _rawPath;
        private FileStream? _rawStream;
        private WasapiLoopbackCapture? _systemCapture;
        private WasapiCapture? _micCapture;
        private Thread? _mixThread;
        private volatile bool _stopRequested;
        private volatile bool _paused;
        private readonly object _pauseLock = new();

        // 两路输入缓冲（48kHz 立体声 float，交错），由采集回调写入、混音线程读取
        private readonly CircularFloatBuffer _sysBuffer = new(48000 * 2 * 2); // 2 秒
        private readonly CircularFloatBuffer _micBuffer = new(48000 * 2 * 2);

        private bool _sysAvailable;
        private bool _micAvailable;
        private string _sysFormatDesc = "-";
        private string _micFormatDesc = "-";

        /// <summary>本次实际采集到的音源描述（供 UI 显示）。</summary>
        public string SourceDescription { get; private set; } = "未启动";

        /// <summary>音频 raw 文件路径（供编码器封装）。</summary>
        public string? RawPath => _rawPath;

        public AudioCapturer(RecorderSettings settings)
        {
            _settings = settings;
        }

        /// <summary>启动采集。返回是否有至少一路音源可用。</summary>
        public bool Start(string rawPath)
        {
            _rawPath = rawPath;
            _rawStream = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                bufferSize: 65536, options: FileOptions.SequentialScan);

            // —— 系统声 loopback ——
            if (_settings.CaptureSystemAudio)
            {
                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _systemCapture = new WasapiLoopbackCapture(dev);
                    var f = _systemCapture.WaveFormat;
                    _sysFormatDesc = $"{f.SampleRate}Hz/{f.BitsPerSample}bit/{f.Channels}ch";
                    _systemCapture.DataAvailable += OnSystemData;
                    _systemCapture.StartRecording();
                    _sysAvailable = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"系统声采集启动失败: {ex.Message}");
                    _sysAvailable = false;
                    _systemCapture?.Dispose();
                    _systemCapture = null;
                }
            }

            // —— 麦克风 ——
            if (_settings.CaptureMicrophone)
            {
                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    _micCapture = new WasapiCapture(dev);
                    var f = _micCapture.WaveFormat;
                    _micFormatDesc = $"{f.SampleRate}Hz/{f.BitsPerSample}bit/{f.Channels}ch";
                    _micCapture.DataAvailable += OnMicData;
                    _micCapture.StartRecording();
                    _micAvailable = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"麦克风采集启动失败: {ex.Message}");
                    _micAvailable = false;
                    _micCapture?.Dispose();
                    _micCapture = null;
                }
            }

            SourceDescription = BuildDescription();

            // 混音线程：无论是否有音源都运行（保证暂停/时间轴逻辑一致，无音源时写静音）
            _stopRequested = false;
            _paused = false;
            _mixThread = new Thread(MixLoop) { IsBackground = true, Name = "AudioMixThread" };
            _mixThread.Start();
            return _sysAvailable || _micAvailable;
        }

        private string BuildDescription()
        {
            if (_sysAvailable && _micAvailable)
                return $"系统声({_sysFormatDesc}) + 麦克风({_micFormatDesc})";
            if (_sysAvailable) return $"系统声({_sysFormatDesc})";
            if (_micAvailable) return $"麦克风({_micFormatDesc})";
            return "无可用音源（录制静音音轨）";
        }

        public void Pause()
        {
            lock (_pauseLock) { _paused = true; }
        }

        public void Resume()
        {
            lock (_pauseLock) { _paused = false; }
        }

        /// <summary>停止采集，冲刷缓冲并关闭 raw 文件。返回文件大小（字节）。</summary>
        public long Stop()
        {
            _stopRequested = true;
            _mixThread?.Join(5000);
            _mixThread = null;

            try { _systemCapture?.StopRecording(); } catch { }
            try { _micCapture?.StopRecording(); } catch { }

            // 冲刷残留缓冲：按当前混音再写一批（补齐尾部）
            if (!_paused) FlushRemainder();

            long length = 0;
            try
            {
                _rawStream?.Flush();
                length = _rawStream?.Length ?? 0;
                _rawStream?.Dispose();
            }
            catch { }
            _rawStream = null;
            return length;
        }

        private void OnSystemData(object? sender, WaveInEventArgs e)
        {
            if (_paused) return;
            if (_systemCapture == null) return;
            float[] samples = ConvertToFloat(e.Buffer, e.BytesRecorded, _systemCapture.WaveFormat);
            _sysBuffer.Write(samples, 0, samples.Length);
        }

        private void OnMicData(object? sender, WaveInEventArgs e)
        {
            if (_paused) return;
            if (_micCapture == null) return;
            float[] samples = ConvertToFloat(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat);
            _micBuffer.Write(samples, 0, samples.Length);
        }

        /// <summary>混音主循环：按 20ms 步长从两路缓冲取数、相加、限幅、写 s16le。</summary>
        private void MixLoop()
        {
            const int samplesPerBlock = 48000 / 50 * 2; // 20ms × 立体声 = 1920 float
            float[] mix = new float[samplesPerBlock];
            byte[] pcm = new byte[samplesPerBlock * 2];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long blockIndex = 0;

            while (!_stopRequested)
            {
                bool paused;
                lock (_pauseLock) { paused = _paused; }
                if (paused)
                {
                    // 暂停：丢弃两路缓冲积压，避免恢复瞬间涌入旧音频
                    _sysBuffer.DiscardAll();
                    _micBuffer.DiscardAll();
                    Thread.Sleep(20);
                    blockIndex = sw.ElapsedMilliseconds / 20 + 1; // 保持节奏基准
                    continue;
                }

                Array.Clear(mix, 0, mix.Length);
                if (_sysAvailable) _sysBuffer.ReadInto(mix, mix.Length, gain: _settings.SystemVolume);
                if (_micAvailable) _micBuffer.ReadInto(mix, mix.Length, gain: _settings.MicVolume);

                int idx = 0;
                for (int i = 0; i < mix.Length; i++)
                {
                    float v = mix[i];
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    short s = (short)(v * 32767);
                    pcm[idx++] = (byte)(s & 0xFF);
                    pcm[idx++] = (byte)(s >> 8);
                }

                try { _rawStream?.Write(pcm, 0, pcm.Length); }
                catch { break; }

                blockIndex++;
                long targetMs = blockIndex * 20;
                long wait = targetMs - sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -200) blockIndex = sw.ElapsedMilliseconds / 20; // 落后太多则重新同步
            }
        }

        private void FlushRemainder()
        {
            float[] mix = new float[48000 * 2];
            Array.Clear(mix, 0, mix.Length);
            int n1 = _sysAvailable ? _sysBuffer.ReadInto(mix, mix.Length, _settings.SystemVolume) : 0;
            int n2 = _micAvailable ? _micBuffer.ReadInto(mix, mix.Length, _settings.MicVolume) : 0;
            if (n1 == 0 && n2 == 0) return;
            byte[] pcm = new byte[mix.Length * 2];
            int idx = 0;
            for (int i = 0; i < mix.Length; i++)
            {
                float v = mix[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                short s = (short)(v * 32767);
                pcm[idx++] = (byte)(s & 0xFF);
                pcm[idx++] = (byte)(s >> 8);
            }
            try { _rawStream?.Write(pcm, 0, pcm.Length); } catch { }
        }

        /// <summary>
        /// 将任意 WASAPI 输入格式转换为 48kHz 立体声 float（交错 L,R,L,R…）。
        /// 支持 PCM 16bit / IEEE Float 32bit；重采样用线性插值，声道数 1→复制，2→直通，>2→取前两声道。
        /// </summary>
        private static float[] ConvertToFloat(byte[] buffer, int bytes, WaveFormat fmt)
        {
            int bps = fmt.BitsPerSample / 8;
            int ch = Math.Max(1, fmt.Channels);
            int frames = bytes / (bps * ch);
            if (frames <= 0) return Array.Empty<float>();

            // 1) 解码为交错 float
            float[] decoded = new float[frames * ch];
            bool isFloat = fmt.BitsPerSample == 32;
            for (int i = 0; i < frames; i++)
            {
                for (int c = 0; c < ch; c++)
                {
                    int off = (i * ch + c) * bps;
                    if (isFloat && off + 4 <= bytes)
                        decoded[i * ch + c] = BitConverter.ToSingle(buffer, off);
                    else if (bps == 2 && off + 2 <= bytes)
                        decoded[i * ch + c] = BitConverter.ToInt16(buffer, off) / 32768f;
                }
            }

            // 2) 重采样到 48000Hz + 转立体声
            int srcRate = fmt.SampleRate;
            if (srcRate <= 0) srcRate = 48000;
            int outFrames = (int)Math.Round(frames * 48000.0 / srcRate);
            if (outFrames <= 0) return Array.Empty<float>();
            float[] result = new float[outFrames * 2];

            for (int j = 0; j < outFrames; j++)
            {
                double pos = j * (double)srcRate / 48000.0;
                int i0 = (int)pos;
                int i1 = Math.Min(i0 + 1, frames - 1);
                float frac = (float)(pos - i0);

                int l0 = i0 * ch;
                int l1 = i1 * ch;
                int r0 = i0 * ch + (ch > 1 ? 1 : 0);
                int r1 = i1 * ch + (ch > 1 ? 1 : 0);

                float left = decoded[l0] * (1 - frac) + decoded[l1] * frac;
                float right = decoded[r0] * (1 - frac) + decoded[r1] * frac;
                result[j * 2] = left;
                result[j * 2 + 1] = right;
            }
            return result;
        }

        public void Dispose()
        {
            try { _stopRequested = true; } catch { }
            _mixThread?.Join(2000);
            try { _systemCapture?.Dispose(); } catch { }
            try { _micCapture?.Dispose(); } catch { }
            try { _rawStream?.Dispose(); } catch { }
        }

        /// <summary>简单的环形 float 缓冲，供采集回调与混音线程之间传递数据。</summary>
        private sealed class CircularFloatBuffer
        {
            private readonly float[] _data;
            private int _read;
            private int _write;
            private int _count;
            private readonly object _lock = new();

            public CircularFloatBuffer(int capacity) { _data = new float[capacity]; }

            public void Write(float[] src, int offset, int count)
            {
                lock (_lock)
                {
                    for (int i = 0; i < count; i++)
                    {
                        _data[_write] = src[offset + i];
                        _write = (_write + 1) % _data.Length;
                        if (_count < _data.Length) _count++;
                        else _read = (_read + 1) % _data.Length; // 满了覆盖最旧
                    }
                }
            }

            /// <summary>从缓冲读取到目标，不足部分保留目标原值（静音）。返回实际写入的 float 数。</summary>
            public int ReadInto(float[] dest, int count, float gain)
            {
                lock (_lock)
                {
                    int n = Math.Min(count, _count);
                    for (int i = 0; i < n; i++)
                    {
                        dest[i] += _data[_read] * gain;
                        _read = (_read + 1) % _data.Length;
                        _count--;
                    }
                    return n;
                }
            }

            public void DiscardAll()
            {
                lock (_lock) { _read = _write; _count = 0; }
            }
        }
    }
}
