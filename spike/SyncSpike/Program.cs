using System.Diagnostics;
using System.Text;

// Spike B（方案2）：验证 FFmpeg 双输入（视频 stdin + 音频 s16le 文件）→ 单音轨 MP4，以及音画同步精度。
// 命名管道方案在 Windows 上让 FFmpeg 连接失败（WaitForConnection 死锁），改用临时文件承载音频。
// 视频 320x240 / 30fps / 3秒 = 90 帧，第 30~39 帧涂白（对应 1.0~1.3 秒）。
// 音频 s16le 48kHz 立体声 / 3秒，1.0~1.3 秒放 880Hz 强音（幅值 0.5），其余 220Hz 弱音（0.05）。

string dir = AppContext.BaseDirectory;
string ffmpeg = @"E:\开发练习项目\录屏软件\spike\ffmpeg6\ffmpeg-6.0-full_build\bin\ffmpeg.exe";
string outPath = Path.Combine(dir, "sync_out.mp4");
string audioRaw = Path.Combine(dir, "audio.raw");
string reportPath = Path.Combine(dir, "b_result.txt");
if (File.Exists(reportPath)) File.Delete(reportPath);
var report = new List<string>();
void Log(string s) { report.Add(s); Console.WriteLine(s); try { File.AppendAllText(reportPath, s + "\n"); } catch { } }

Log("=== 阶段2 Spike B（方案2：视频管道+音频文件）：双输入混音与同步验证 ===");
Log($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

const int W = 320, H = 240, FPS = 30, SECONDS = 3;
const int SR = 48000, CH = 2;

// —— 先生成音频 raw 文件（模拟"边录边写"，此处一次性写完）——
Log("生成音频 PCM…");
using (var fs = new FileStream(audioRaw, FileMode.Create, FileAccess.Write))
{
    int blockSamples = SR / 100; // 10ms
    int totalBlocks = SECONDS * 100;
    byte[] block = new byte[blockSamples * CH * 2];
    for (int b = 0; b < totalBlocks; b++)
    {
        double tStart = b / 100.0;
        bool loud = tStart >= 1.0 && tStart < 1.3;
        double amp = loud ? 0.5 : 0.05;
        double freq = loud ? 880 : 220;
        int idx = 0;
        for (int s = 0; s < blockSamples; s++)
        {
            double t = tStart + s / (double)SR;
            short val = (short)(Math.Sin(2 * Math.PI * freq * t) * amp * 32767);
            block[idx++] = (byte)(val & 0xFF);
            block[idx++] = (byte)(val >> 8);
            block[idx++] = (byte)(val & 0xFF);
            block[idx++] = (byte)(val >> 8);
        }
        fs.Write(block, 0, block.Length);
    }
}
Log($"音频文件: {new FileInfo(audioRaw).Length} bytes");

// —— 启动 FFmpeg：视频走 stdin，音频走文件 ——
var psi = new ProcessStartInfo
{
    FileName = ffmpeg,
    Arguments = "-hide_banner -loglevel error -y " +
                $"-f rawvideo -pix_fmt bgra -s {W}x{H} -r {FPS} -i pipe:0 " +
                $"-f s16le -ar {SR} -ac {CH} -i \"{audioRaw}\" " +
                "-map 0:v:0 -map 1:a:0 " +
                "-c:v libx264 -preset ultrafast -pix_fmt yuv420p -b:v 500k " +
                "-c:a aac -b:a 128k " +
                $"\"{outPath}\"",
    RedirectStandardInput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
    StandardErrorEncoding = Encoding.UTF8
};

var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
proc.Start();
Log($"FFmpeg 已启动 PID={proc.Id}");

byte[] frame = new byte[W * H * 4];
var vsw = Stopwatch.StartNew();
for (int f = 0; f < FPS * SECONDS; f++)
{
    bool white = f >= 30 && f < 39;
    byte v = white ? (byte)255 : (byte)0;
    int idx = 0;
    for (int p = 0; p < W * H; p++)
    {
        frame[idx++] = v; frame[idx++] = v; frame[idx++] = v; frame[idx++] = 255;
    }
    proc.StandardInput.BaseStream.Write(frame, 0, frame.Length);
    long targetMs = (long)((f + 1) * 1000.0 / FPS);
    long wait = targetMs - vsw.ElapsedMilliseconds;
    if (wait > 0) Thread.Sleep((int)wait);
}
Log("视频帧写入完成");

proc.StandardInput.BaseStream.Close();
string err = proc.StandardError.ReadToEnd();
bool exited = proc.WaitForExit(30000);
Log($"FFmpeg 退出: exited={exited} ExitCode={proc.ExitCode}");
if (!string.IsNullOrWhiteSpace(err)) Log($"FFmpeg stderr: {err.Trim()}");

Log($"\n输出文件: {outPath}");
Log($"文件大小: {(File.Exists(outPath) ? new FileInfo(outPath).Length : 0)} bytes");
Log("\n报告已写入 b_result.txt");
