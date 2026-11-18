using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ScreenRecorder.Models;
using ScreenRecorder.Services;
using ScreenRecorder.Views;
using Application = System.Windows.Application;
using ExitEventArgs = System.Windows.ExitEventArgs;

namespace ScreenRecorder;

/// <summary>
/// 应用入口。持有全局共享的配置、录制引擎与热键管理器。
/// 配置在启动时从 settings.json 读回，退出时写回，避免每次重设。
/// </summary>
public partial class App : Application
{
    private static RecorderSettings? _settings;
    private static RecorderEngine? _engine;
    private static HotkeyManager? _hotkeys;

    public static RecorderSettings Settings => _settings ??= SettingsStore.Load();

    public static RecorderEngine Engine => _engine ??= CreateEngine();

    private static RecorderEngine CreateEngine()
    {
        var engine = new RecorderEngine(Settings);
        engine.ErrorOccurred += message => LocalLogger.Write("ERROR", $"录制引擎: {message}");
        return engine;
    }

    /// <summary>热键注册失败的原因（成功时为 null），供界面提示。</summary>
    public static string? HotkeyError => _hotkeys?.ErrorMessage;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 阶段 3：本地日志先初始化，后续所有异常都能被记录
        LocalLogger.Init();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LocalLogger.Exception("AppDomain 未处理异常", ex);
            else
                LocalLogger.Write("FATAL", $"AppDomain 未处理异常: {args.ExceptionObject}");
        };
        DispatcherUnhandledException += (_, args) =>
            LocalLogger.Exception("UI 线程未处理异常", args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LocalLogger.Exception("后台任务未观察异常", args.Exception);

        // 提前触发配置加载，让窗口构造时能拿到已持久化的值
        _ = Settings;

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += OnHotkeyPressed;
        bool registered = _hotkeys.Register(); // 失败仅记录原因，不影响使用
        LocalLogger.Write("INFO", registered
            ? "全局热键注册成功 (F9/F10)"
            : $"全局热键注册失败: {_hotkeys.ErrorMessage}");
    }

    /// <summary>
    /// 全局热键：F9 开始/暂停/继续，F10 停止。
    /// 开始录制需要有主窗口实例，因此通过窗口查找而不是直接调用。
    /// </summary>
    private void OnHotkeyPressed(int id)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (id == HotkeyManager.IdToggle)
                {
                    switch (Engine.State)
                    {
                        case RecorderState.Idle:
                            // 悬浮条已存在但引擎还没启动 = 正在倒计时，忽略重复触发
                            if (FindBar() != null) return;
                            FindMain()?.StartRecording();
                            break;
                        case RecorderState.Recording:
                            Engine.Pause();
                            break;
                        case RecorderState.Paused:
                            Engine.Resume();
                            break;
                    }
                }
                else if (id == HotkeyManager.IdStop)
                {
                    _ = FindBar()?.StopRecordingAsync();
                }
            }
            catch { /* 热键处理失败不应打断录制 */ }
        });
    }

    private static MainWindow? FindMain()
        => Current.Windows.OfType<MainWindow>().FirstOrDefault();

    private static FloatingControl? FindBar()
        => Current.Windows.OfType<FloatingControl>().FirstOrDefault();

    protected override void OnExit(ExitEventArgs e)
    {
        // 保存配置（含本次在界面上做过的修改）
        try { if (_settings != null) SettingsStore.Save(_settings); } catch { }

        _hotkeys?.Dispose();
        _hotkeys = null;
        _engine?.Dispose();
        LocalLogger.Shutdown();
        base.OnExit(e);
    }
}
