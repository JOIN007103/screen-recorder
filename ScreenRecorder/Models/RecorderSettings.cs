using System.IO;

namespace ScreenRecorder.Models
{
    /// <summary>
    /// 录制配置模型。对应 11 项冻结决策中的 MVP 参数。
    /// </summary>
    public class RecorderSettings
    {
        /// <summary>帧率（冻结决策：30fps）。</summary>
        public int FrameRate { get; set; } = 30;

        /// <summary>开始录制前的倒计时秒数。0 表示立即开始。</summary>
        public int CountdownSeconds { get; set; } = 3;

        /// <summary>输出目录（默认：程序目录下的 Videos）。</summary>
        public string OutputDirectory { get; set; } =
            Path.Combine(System.AppContext.BaseDirectory, "Videos");

        /// <summary>导出格式固定 H.264 + MP4（冻结决策）。</summary>
        public string FileExtension { get; } = ".mp4";

        /// <summary>FFmpeg 可执行文件完整路径。默认随程序分发的 tools 目录。</summary>
        public string FfmpegPath { get; set; } =
            Path.Combine(System.AppContext.BaseDirectory, "tools", "ffmpeg.exe");

        // ---- 三项鼠标效果（冻结决策：全部纳入 MVP）----

        /// <summary>点击涟漪/光圈。</summary>
        public bool ShowClickRipple { get; set; } = true;

        /// <summary>光标高亮。</summary>
        public bool ShowCursorHighlight { get; set; } = true;

        /// <summary>鼠标轨迹（按用户意见默认关闭，避免影响观感；界面可随时打开）。</summary>
        public bool ShowMouseTrail { get; set; } = false;

        /// <summary>涟漪颜色（BGR，GDI 用）。默认青色高亮。</summary>
        public uint RippleColorBgr { get; set; } = 0x00FFFF;

        /// <summary>光标高亮颜色（BGR）。默认黄色。</summary>
        public uint HighlightColorBgr { get; set; } = 0x0022FF;

        // ---- 性能与存储（冻结决策：流畅优先 + 磁盘预警仅提示）----

        /// <summary>编码预设。veryfast 偏流畅（冻结：流畅优先）。</summary>
        public string EncoderPreset { get; set; } = "veryfast";

        /// <summary>
        /// 编码器偏好。默认自动：优先 NVIDIA NVENC 硬编码（CPU 占用约为软编码的 1/19），
        /// 检测不到可用 NVENC 时自动回退 libx264 软编码。
        /// </summary>
        public EncoderPreference Encoder { get; set; } = EncoderPreference.Auto;

        /// <summary>NVENC 预设（p1 最快 / p5 平衡 / p7 最高画质）。</summary>
        public string NvencPreset { get; set; } = "p5";

        /// <summary>码率（bps）。1080p/30fps 平衡画质与体积。</summary>
        public int VideoBitrate { get; set; } = 4_000_000;

        /// <summary>磁盘空间预警阈值（MB）。低于此值提示，不强制停止。</summary>
        public int DiskWarningMb { get; set; } = 2048;

        // ---- 阶段2：音频（系统声 + 麦克风，固定混音成单轨）----

        /// <summary>是否采集系统声（WASAPI loopback）。</summary>
        public bool CaptureSystemAudio { get; set; } = true;

        /// <summary>是否采集麦克风。</summary>
        public bool CaptureMicrophone { get; set; } = true;

        /// <summary>系统声音量增益（0.0~2.0，1.0 为原音量）。</summary>
        public float SystemVolume { get; set; } = 1.0f;

        /// <summary>麦克风云量增益（0.0~2.0，1.0 为原音量）。</summary>
        public float MicVolume { get; set; } = 1.0f;
    }
}
