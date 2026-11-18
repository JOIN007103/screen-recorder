using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenRecorder.Models;
using ScreenRecorder.Native;
using ScreenRecorder.Services;

namespace ScreenRecorder.Views
{
    /// <summary>
    /// 悬浮控制条：录制期间的置顶操作面板，可拖动。
    ///
    /// 关键设计：窗口必须保持非透明实底——实测分层窗口（AllowsTransparency=true）
    /// 无法用 SetWindowDisplayAffinity 排除出屏幕捕获，会导致控制条被录进视频。
    /// </summary>
    public partial class FloatingControl : Window
    {
        private readonly RecorderEngine _engine;
        private string? _outputPath;

        /// <summary>控制条是否已成功排除出屏幕捕获（false 表示可能被录进画面）。</summary>
        public bool CaptureExcluded { get; private set; }

        /// <summary>排除失败时的原因描述（成功时为 null）。</summary>
        public string? ExclusionError { get; private set; }

        public FloatingControl(RecorderEngine engine)
        {
            InitializeComponent();
            _engine = engine;

            _engine.Progress += OnProgress;
            _engine.StateChanged += OnStateChanged;
            _engine.DiskWarning += OnDiskWarning;

            // 初始定位到屏幕右上角
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 20;
            Top = area.Top + 20;

            SourceInitialized += FloatingControl_SourceInitialized;
            Closed += FloatingControl_Closed;
        }

        /// <summary>
        /// 把本窗口排除出屏幕捕获。必须在窗口句柄创建后调用。
        /// </summary>
        private void FloatingControl_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                CaptureExcluded = NativeMethods.SetWindowDisplayAffinity(
                    hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
                if (!CaptureExcluded)
                    ExclusionError = $"控制条排除失败 (err={Marshal.GetLastWin32Error()})，它可能会被录进画面";
            }
            catch (Exception ex)
            {
                CaptureExcluded = false;
                ExclusionError = "控制条排除异常：" + ex.Message;
            }
        }

        /// <summary>
        /// 倒计时结束后再真正开始录制，避免录进找鼠标的开头废片。
        /// 倒计时期间引擎处于 Idle，各按钮禁用。
        /// </summary>
        public async void BeginRecordingWithCountdown(int seconds)
        {
            BtnPause.IsEnabled = false;
            BtnStop.IsEnabled = false;

            for (int i = seconds; i > 0; i--)
            {
                TxtTime.Text = i.ToString();
                TxtState.Text = "即将开始录制…";
                Led.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
                await Task.Delay(1000);
            }

            TxtTime.Text = "00:00";
            TxtState.Text = "● 正在录制";
            BtnPause.IsEnabled = true;
            BtnStop.IsEnabled = true;

            try
            {
                _engine.Start();
                // 引擎启动成功后再订阅进度显示（StateChanged 已在构造中订阅）
                if (!CaptureExcluded && ExclusionError != null)
                    TxtState.Text = "⚠ 控制条可能入镜";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"启动录制失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                _abortNotify = true; // 启动失败不当作“录制结束”回传，避免误导成未生成文件
                Close();
            }
        }

        private bool _abortNotify;

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            TogglePause();
        }

        /// <summary>暂停/继续切换（按钮与热键共用）。</summary>
        public void TogglePause()
        {
            if (_engine.State == RecorderState.Recording) _engine.Pause();
            else if (_engine.State == RecorderState.Paused) _engine.Resume();
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _ = StopRecordingAsync();
        }

        /// <summary>
        /// 停止录制并落盘（按钮与热键共用）。
        /// 落盘在工作线程执行，避免界面看起来卡死。
        /// </summary>
        public async Task<string?> StopRecordingAsync()
        {
            if (_engine.State != RecorderState.Recording && _engine.State != RecorderState.Paused)
                return null;

            BtnStop.IsEnabled = false;
            BtnPause.IsEnabled = false;
            TxtState.Text = "正在保存…";
            await Task.Yield();

            try
            {
                _outputPath = await Task.Run(() => _engine.Stop());
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Close();
            return _outputPath;
        }

        private void OnProgress(long frames, TimeSpan elapsed, double fps)
        {
            Dispatcher.Invoke(() =>
            {
                TxtTime.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
            });
        }

        private void OnStateChanged(RecorderState state)
        {
            Dispatcher.Invoke(() =>
            {
                switch (state)
                {
                    case RecorderState.Recording:
                        TxtState.Text = "● 正在录制";
                        Led.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                        BtnPause.Content = "暂停";
                        break;
                    case RecorderState.Paused:
                        TxtState.Text = "❚❚ 已暂停";
                        Led.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
                        BtnPause.Content = "继续";
                        break;
                    case RecorderState.Saving:
                        TxtState.Text = "正在保存…";
                        break;
                }
            });
        }

        private void OnDiskWarning(double freeMb)
        {
            Dispatcher.Invoke(() =>
            {
                TxtState.Text = $"⚠ 磁盘剩余 {freeMb:0} MB";
            });
        }

        private void FloatingControl_Closed(object? sender, EventArgs e)
        {
            _engine.Progress -= OnProgress;
            _engine.StateChanged -= OnStateChanged;
            _engine.DiskWarning -= OnDiskWarning;

            // 若关闭时仍在录制（例如用户强制关闭），确保停止引擎
            if (_engine.State == RecorderState.Recording || _engine.State == RecorderState.Paused)
                _outputPath = _engine.Stop();

            // 恢复主窗口并回传落点
            foreach (Window w in System.Windows.Application.Current.Windows)
            {
                if (w is MainWindow main)
                {
                    if (_abortNotify) { main.Show(); main.Activate(); }
                    else main.OnRecordingFinished(_outputPath);
                    break;
                }
            }
        }
    }
}
