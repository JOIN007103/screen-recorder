using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Spike A：验证本机 WASAPI 系统声（loopback）与麦克风两路音源是否真实可用。
// 方法：播放 440Hz 测试音 -> loopback 采集应能收到能量；麦克风采集环境声应能收到能量。
// 落盘 WAV + 文本报告，全部输出到程序目录。

string dir = AppContext.BaseDirectory;
var report = new List<string>();
void Log(string s) { report.Add(s); Console.WriteLine(s); }

Log("=== 阶段2 Spike A：WASAPI 音源采集验证 ===");
Log($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

var enumerator = new MMDeviceEnumerator();

// ---- 1. 枚举设备 ----
Log("\n[设备枚举]");
Log("渲染设备(播放):");
try
{
    var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
    foreach (var d in renders) Log($"  - {d.FriendlyName} (状态={d.State})");
    if (renders.Count == 0) Log("  (无活动渲染设备)");
}
catch (Exception ex) { Log($"  枚举失败: {ex.Message}"); }

Log("捕获设备(麦克风) - WASAPI:");
MMDevice? micDevice = null;
MMDevice? renderDevice = null;
try
{
    var caps = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
    foreach (var d in caps) Log($"  - {d.FriendlyName} (状态={d.State})");
    if (caps.Count == 0) Log("  (无活动捕获设备)");
}
catch (Exception ex) { Log($"  枚举失败: {ex.Message}"); }

Log("捕获设备(麦克风) - 传统 waveIn 交叉验证:");
try
{
    int waveInCount = WaveInEvent.DeviceCount;
    Log($"  waveIn 设备数: {waveInCount}");
    for (int i = 0; i < waveInCount; i++)
    {
        var caps = WaveInEvent.GetCapabilities(i);
        Log($"  [{i}] {caps.ProductName} ({caps.Channels}ch)");
    }
}
catch (Exception ex) { Log($"  waveIn 枚举失败: {ex.Message}"); }

try
{
    renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    Log($"默认渲染设备: {renderDevice.FriendlyName}");
}
catch (Exception ex) { Log($"默认渲染设备获取失败: {ex.Message}"); }

try
{
    micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
    Log($"默认捕获设备: {micDevice.FriendlyName}");
}
catch (Exception ex) { Log($"默认捕获设备获取失败: {ex.Message}"); }

// ---- 2. 采集函数：写 WAV 并统计 RMS ----
static (long bytes, double rms, string fmt, int sr, int ch) Capture(
    WaveFileWriter writer, IWaveIn waveIn, int durationMs)
{
    long totalBytes = 0;
    var fmt = waveIn.WaveFormat;
    string fmtDesc = $"{fmt.Encoding}/{fmt.SampleRate}Hz/{fmt.BitsPerSample}bit/{fmt.Channels}ch";
    waveIn.DataAvailable += (s, e) =>
    {
        if (e.BytesRecorded > 0) { writer.Write(e.Buffer, 0, e.BytesRecorded); totalBytes += e.BytesRecorded; }
    };
    var sw = Stopwatch.StartNew();
    waveIn.StartRecording();
    while (sw.ElapsedMilliseconds < durationMs) Thread.Sleep(20);
    waveIn.StopRecording();
    Thread.Sleep(150);
    return (totalBytes, 0, fmtDesc, fmt.SampleRate, fmt.Channels);
}

static double ComputeRms(string wavPath)
{
    try
    {
        byte[] bytes;
        using (var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[fs.Length];
            fs.Read(bytes, 0, bytes.Length);
        }
        // 从 fmt chunk 读位深；搜索 "data" 四字节定位数据起点
        int bits = BitConverter.ToUInt16(bytes, 34); // fmt 在 offset 20, bits 在 +14
        int dataPos = -1;
        for (int i = 4; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a')
            { dataPos = i; break; }
        }
        if (dataPos < 0) return -1;
        int dataStart = dataPos + 8; // 'data'(4)+size(4)
        int len = bytes.Length - dataStart;
        byte[] data = new byte[len];
        Array.Copy(bytes, dataStart, data, 0, len);

        double sum = 0; long n = 0;
        if (bits == 32)
        {
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                float v = BitConverter.ToSingle(data, i);
                sum += v * v; n++;
            }
        }
        else if (bits == 16)
        {
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                double v = BitConverter.ToInt16(data, i) / 32768.0;
                sum += v * v; n++;
            }
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
    catch (Exception ex) { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "rms_debug.txt"), $"RMS FAIL {wavPath}: {ex}\n"); return -1; }
}

int duration = 2500;

// ---- 3. 系统声 loopback（同时播放测试音，确保有内容可采）----
Log("\n[系统声 Loopback]");
bool loopOk = false;
try
{
    if (renderDevice == null) throw new InvalidOperationException("无默认渲染设备");

    // 播放 440Hz 正弦波作为"系统正在播放的声音"
    using var outDev = new WasapiOut(renderDevice, AudioClientShareMode.Shared, true, 50);
    var sig = new SignalGenerator(48000, 1)
    {
        Frequency = 440, Gain = 0.25, Type = SignalGeneratorType.Sin
    };
    outDev.Init(sig.ToWaveProvider16());

    string wav = Path.Combine(dir, "loopback.wav");
    using var cap = new WasapiLoopbackCapture(renderDevice);
    using var writer = new WaveFileWriter(wav, cap.WaveFormat);

    outDev.Play();
    Thread.Sleep(200); // 让播放先起振
    var r = Capture(writer, cap, duration);
    outDev.Stop();
    writer.Flush();

    double rms = ComputeRms(wav);
    loopOk = r.bytes > 0 && rms > 0.0005;
    Log($"  格式: {r.fmt}");
    Log($"  采集字节: {r.bytes}");
    Log($"  RMS: {rms:F6}");
    Log($"  判定: {(loopOk ? "PASS 有实际音频" : "FAIL 无有效信号")}");
}
catch (Exception ex)
{
    Log($"  采集异常: {ex.GetType().Name}: {ex.Message}");
}

// ---- 4. 麦克风 ----
Log("\n[麦克风]");
bool micOk = false;
try
{
    if (micDevice == null) throw new InvalidOperationException("无默认捕获设备");

    string wav = Path.Combine(dir, "mic.wav");
    using var cap = new WasapiCapture(micDevice);
    using var writer = new WaveFileWriter(wav, cap.WaveFormat);

    var r = Capture(writer, cap, duration);
    writer.Flush();

    double rms = ComputeRms(wav);
    micOk = r.bytes > 0 && rms > 0.00001; // 环境噪声阈值放宽
    Log($"  格式: {r.fmt}");
    Log($"  采集字节: {r.bytes}");
    Log($"  RMS: {rms:F6}");
    Log($"  判定: {(micOk ? "PASS 有实际音频" : "WARN 接近静音（可能无环境声或麦克风被静音）")}");
}
catch (Exception ex)
{
    Log($"  采集异常: {ex.GetType().Name}: {ex.Message}");
}

// ---- 5. 结论 ----
Log("\n=== 结论 ===");
Log($"系统声 Loopback: {(loopOk ? "可用" : "不可用")}");
Log($"麦克风:          {(micOk ? "可用" : "不可用")}");
Log($"推荐路线: {(loopOk && micOk ? "系统声+麦克风双音源" : (loopOk ? "仅系统声" : (micOk ? "仅麦克风" : "无可用音源")))}");

string resultPath = Path.Combine(dir, "a_result.txt");
File.WriteAllLines(resultPath, report);
Console.WriteLine($"\n报告已写入: {resultPath}");
