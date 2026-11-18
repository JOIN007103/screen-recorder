using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 本地日志（阶段 3 · 崩溃上报降级方案）。
    ///
    /// 设计约束（来自阶段 3 方案）：
    /// - 只写程序目录 logs\ 下当天的文本文件，不外发、不写注册表、不写 AppData；
    /// - 任何失败都静默忽略，绝不影响录制，也绝不让"日志写不了"变成启动障碍；
    /// - 程序目录不可写（例如放在 Program Files）时自动禁用，保证"删除目录即卸载"。
    ///
    /// 记录内容：启动/退出、录制开始/停止、热键注册结果、编码器选择、异常堆栈。
    /// 不记录画面内容、录音内容、用户文件数据。
    /// </summary>
    public static class LocalLogger
    {
        private static readonly object Sync = new();
        private static string? _filePath;
        private static bool _enabled;

        /// <summary>当前日志文件路径；未启用时为 null。</summary>
        public static string? FilePath => _filePath;

        /// <summary>读取程序集版本，供界面"关于"与日志头使用。</summary>
        public static string Version
        {
            get
            {
                try
                {
                    var v = Assembly.GetExecutingAssembly().GetName().Version;
                    return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
                }
                catch { return "1.0.0"; }
            }
        }

        /// <summary>初始化：在程序目录下建 logs\，不可写则静默禁用。</summary>
        public static void Init()
        {
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);

                // 探测真实写权限（CreateDirectory 成功不代表目录可写）
                string probe = Path.Combine(dir, $".probe_{Guid.NewGuid():N}");
                File.WriteAllText(probe, "1", Encoding.UTF8);
                File.Delete(probe);

                _filePath = Path.Combine(dir, $"run_{DateTime.Now:yyyyMMdd}.log");
                _enabled = true;
                Write("INFO", $"=== 程序启动 v{Version} ===");
            }
            catch
            {
                _enabled = false;
                _filePath = null;
            }
        }

        /// <summary>写一条日志。level 建议 INFO / WARN / ERROR / FATAL。</summary>
        public static void Write(string level, string message)
        {
            if (!_enabled || _filePath == null) return;
            try
            {
                lock (Sync)
                {
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(_filePath, line, Encoding.UTF8);
                }
            }
            catch { /* 日志失败不影响程序 */ }
        }

        /// <summary>写异常（含类型、消息、堆栈）。</summary>
        public static void Exception(string where, Exception ex)
        {
            if (ex == null) return;
            Write("ERROR", $"{where}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }

        /// <summary>程序退出时收尾。</summary>
        public static void Shutdown()
        {
            Write("INFO", "=== 程序正常退出 ===");
        }
    }
}
