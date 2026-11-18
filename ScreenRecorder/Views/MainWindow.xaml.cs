using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Forms = System.Windows.Forms;
using ScreenRecorder.Models;
using ScreenRecorder.Services;

namespace ScreenRecorder.Views
{
    /// <summary>
    /// 主窗口：配置参数、启动录制、查看与回放历史文件。
    /// </summary>
    public partial class MainWindow : Window
    {
        private string? _lastOutputPath;

        public MainWindow()
        {
            InitializeComponent();
            App.Engine.ErrorOccurred += Engine_ErrorOccurred; // 只订阅一次，避免重复注册
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtVersion.Text = $"v{LocalLogger.Version} · 关于";

            var s = App.Settings;
            TxtOutputDir.Text = s.OutputDirectory;
            TxtDiskWarn.Text = s.DiskWarningMb.ToString();
            CmbCountdown.SelectedIndex = s.CountdownSeconds switch { 0 => 0, 5 => 2, _ => 1 };
            CmbEncoder.SelectedIndex = s.Encoder switch
            {
                EncoderPreference.Nvenc => 1,
                EncoderPreference.Software => 2,
                _ => 0
            };

            // 音频设置
            ChkSystemAudio.IsChecked = s.CaptureSystemAudio;
            ChkMicrophone.IsChecked = s.CaptureMicrophone;
            SldSystemVol.Value = Math.Max(0, Math.Min(2, s.SystemVolume));
            SldMicVol.Value = Math.Max(0, Math.Min(2, s.MicVolume));
            TxtSystemVol.Text = $"{s.SystemVolume * 100:0}%";
            TxtMicVol.Text = $"{s.MicVolume * 100:0}%";

            var screen = Forms.Screen.PrimaryScreen;
            TxtScreenInfo.Text = screen != null
                ? $"{screen.Bounds.Width} × {screen.Bounds.Height}  ·  {s.FrameRate} fps  ·  H.264 / MP4"
                : $"{s.FrameRate} fps  ·  H.264 / MP4";

            // 热键被占用时给出明确提示，避免用户以为热键坏了
            if (!string.IsNullOrEmpty(App.HotkeyError))
            {
                TxtHotkeyHint.Text = "⚠ " + App.HotkeyError;
                TxtHotkeyHint.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(217, 119, 6));
            }

            CheckFfmpeg();
            CheckEncoders();
            CheckAudioDevices();
            RefreshFiles();
        }

        /// <summary>检测系统声与麦克风设备，给出中文提示（后台线程，避免卡顿）。</summary>
        private async void CheckAudioDevices()
        {
            TxtAudioHint.Text = "正在检测音频设备…";
            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                bool sys = false, mic = false;
                try
                {
                    var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    sys = e.GetDefaultAudioEndpoint(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.Role.Multimedia) != null;
                }
                catch { }
                try
                {
                    var e = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    mic = e.GetDefaultAudioEndpoint(
                        NAudio.CoreAudioApi.DataFlow.Capture,
                        NAudio.CoreAudioApi.Role.Multimedia) != null;
                }
                catch { }
                return (sys, mic);
            });

            if (result.sys && result.mic)
                TxtAudioHint.Text = "音频设备已就绪 ✓（系统声 + 麦克风）";
            else if (result.sys)
                TxtAudioHint.Text = "仅检测到系统声 ✓；未检测到麦克风，麦克风选项将被忽略";
            else if (result.mic)
                TxtAudioHint.Text = "仅检测到麦克风 ✓；未检测到播放设备，系统声将被忽略";
            else
                TxtAudioHint.Text = "未检测到音频设备，将录制静音音轨（画面不受影响）";
        }

        /// <summary>
        /// 探测 NVENC 硬编码可用性（在后台线程实际跑一次极小编码，避免界面卡顿）。
        /// </summary>
        private async void CheckEncoders()
        {
            string path = App.Settings.FfmpegPath;
            if (!File.Exists(path)) { TxtEncoderHint.Text = ""; return; }

            TxtEncoderHint.Text = "检测显卡硬编码…";
            bool nvenc;
            try
            {
                nvenc = await System.Threading.Tasks.Task.Run(
                    () => Services.VideoEncoder.ProbeNvenc(path));
            }
            catch { nvenc = false; }

            TxtEncoderHint.Text = nvenc
                ? "本机支持 NVENC 硬编码 ✓（CPU 占用约为软编码 1/19）"
                : "本机不支持 NVENC，将使用 libx264 软编码";
            TxtFfmpegStatus.Text = nvenc
                ? "编码组件：已就绪 ✓（NVENC 硬编码可用）"
                : "编码组件：已就绪 ✓（软编码）";
        }

        /// <summary>检测 FFmpeg 是否可用，给出中文状态提示。</summary>
        private void CheckFfmpeg()
        {
            string path = App.Settings.FfmpegPath;
            if (File.Exists(path))
            {
                TxtFfmpegStatus.Text = "编码组件：已就绪 ✓";
                TxtFfmpegStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(5, 150, 105));
                BtnStart.IsEnabled = true;
            }
            else
            {
                TxtFfmpegStatus.Text = $"编码组件缺失：请把 ffmpeg.exe 放到 {Path.GetDirectoryName(path)}";
                TxtFfmpegStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(220, 38, 38));
                BtnStart.IsEnabled = false;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new Forms.FolderBrowserDialog
            {
                Description = "选择录制视频的保存位置",
                SelectedPath = TxtOutputDir.Text
            };
            if (dlg.ShowDialog() == Forms.DialogResult.OK)
                TxtOutputDir.Text = dlg.SelectedPath;
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e) => StartRecording();

        /// <summary>点击版本号显示"关于"——阶段 3 要求的轻量版本展示，不做在线检查。</summary>
        private void TxtVersion_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string logHint = LocalLogger.FilePath == null
                ? "本地日志：当前目录不可写，已禁用"
                : $"本地日志：{LocalLogger.FilePath}";
            System.Windows.MessageBox.Show(
                $"屏幕录制工具 v{LocalLogger.Version}\n\n" +
                "全屏录制 · 1080p / 30fps · H.264 / MP4\n" +
                "编码：NVENC 显卡硬编码优先，自动回退软编码\n" +
                "音频：系统声 + 麦克风，固定混音为单音轨\n\n" +
                logHint + "\n日志仅保存在本目录，不会外发。",
                "关于", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 开始录制。按钮与 F9 热键共用同一入口。
        /// 流程：写回配置 → 建悬浮条 → 隐藏主窗口 → 倒计时 → 引擎启动。
        /// </summary>
        public void StartRecording()
        {
            // 写回配置
            var s = App.Settings;
            s.OutputDirectory = string.IsNullOrWhiteSpace(TxtOutputDir.Text)
                ? s.OutputDirectory : TxtOutputDir.Text.Trim();
            if (int.TryParse(TxtDiskWarn.Text, out int warn)) s.DiskWarningMb = warn;
            s.ShowClickRipple = ChkRipple.IsChecked == true;
            s.ShowCursorHighlight = ChkHighlight.IsChecked == true;
            s.ShowMouseTrail = ChkTrail.IsChecked == true;

            s.Encoder = CmbEncoder.SelectedIndex switch
            {
                1 => EncoderPreference.Nvenc,
                2 => EncoderPreference.Software,
                _ => EncoderPreference.Auto
            };
            s.CountdownSeconds = CmbCountdown.SelectedIndex switch { 0 => 0, 2 => 5, _ => 3 };

            // 音频设置
            s.CaptureSystemAudio = ChkSystemAudio.IsChecked == true;
            s.CaptureMicrophone = ChkMicrophone.IsChecked == true;
            s.SystemVolume = (float)Math.Max(0, Math.Min(2, SldSystemVol.Value));
            s.MicVolume = (float)Math.Max(0, Math.Min(2, SldMicVol.Value));

            // 持久化，下次启动直接复用
            SettingsStore.Save(s);

            LocalLogger.Write("INFO",
                $"请求开始录制：目录={s.OutputDirectory} 倒计时={s.CountdownSeconds}s " +
                $"编码={s.Encoder} 系统声={s.CaptureSystemAudio} 麦克风={s.CaptureMicrophone}");

            try
            {
                Directory.CreateDirectory(s.OutputDirectory);
                CardResult.Visibility = Visibility.Collapsed;

                var bar = new FloatingControl(App.Engine);
                bar.Show();
                Hide();

                bar.BeginRecordingWithCountdown(s.CountdownSeconds);
            }
            catch (Exception ex)
            {
                LocalLogger.Exception("启动录制失败", ex);
                System.Windows.MessageBox.Show($"启动录制失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Show();
            }
        }

        private void AudioSource_Changed(object sender, RoutedEventArgs e)
        {
            // XAML 初始化期间 IsChecked 赋值也会触发此事件，此时滑块可能尚未创建
            if (SldSystemVol == null || SldMicVol == null) return;
            var s = App.Settings;
            s.CaptureSystemAudio = ChkSystemAudio.IsChecked == true;
            s.CaptureMicrophone = ChkMicrophone.IsChecked == true;
            SldSystemVol.IsEnabled = s.CaptureSystemAudio;
            SldMicVol.IsEnabled = s.CaptureMicrophone;
            TxtSystemVol.Opacity = s.CaptureSystemAudio ? 1.0 : 0.4;
            TxtMicVol.Opacity = s.CaptureMicrophone ? 1.0 : 0.4;
        }

        private void SldSystemVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtSystemVol != null)
                TxtSystemVol.Text = $"{SldSystemVol.Value * 100:0}%";
        }

        private void SldMicVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtMicVol != null)
                TxtMicVol.Text = $"{SldMicVol.Value * 100:0}%";
        }

        private void Engine_ErrorOccurred(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = "注意：" + message;
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(217, 119, 6));
            });
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshFiles();

        private void RefreshFiles()
        {
            try
            {
                string dir = App.Settings.OutputDirectory;
                if (!Directory.Exists(dir)) { LstFiles.ItemsSource = null; return; }
                var files = Directory.GetFiles(dir, "*.mp4")
                    .OrderByDescending(File.GetCreationTime)
                    .Select(Path.GetFileName)
                    .ToList();
                LstFiles.ItemsSource = files;
            }
            catch { LstFiles.ItemsSource = null; }
        }

        /// <summary>录制窗口关闭后回调，恢复主窗口、显示落点并提供后续动作。</summary>
        public void OnRecordingFinished(string? outputPath)
        {
            LocalLogger.Write("INFO", $"录制结束：{outputPath ?? "(未生成文件)"}");
            _lastOutputPath = outputPath;
            Show();
            Activate();

            RefreshFiles();

            if (!string.IsNullOrEmpty(outputPath) && File.Exists(outputPath))
            {
                var fi = new FileInfo(outputPath);
                TxtStatus.Text = "录制已保存";
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(5, 150, 105));
                TxtResultTitle.Text = $"录制已保存（{fi.Length / 1024.0 / 1024.0:0.0} MB）";
                TxtResultPath.Text = outputPath;
                CardResult.Visibility = Visibility.Visible;
            }
            else
            {
                TxtStatus.Text = "录制结束，但未生成文件";
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(217, 119, 6));
                CardResult.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutputPath}\"")
                    { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(App.Settings.OutputDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"打开文件夹失败：{ex.Message}", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnPlayLast_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastOutputPath) && File.Exists(_lastOutputPath))
                new PlaybackWindow(_lastOutputPath).Show();
        }

        private void LstFiles_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PlaySelected();
        }

        private void PlaySelected()
        {
            if (LstFiles.SelectedItem is not string name) return;
            string full = Path.Combine(App.Settings.OutputDirectory, name);
            if (!File.Exists(full)) return;
            var win = new PlaybackWindow(full);
            win.Show();
        }
    }
}
