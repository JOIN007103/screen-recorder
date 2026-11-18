using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ScreenRecorder.Models;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 配置持久化：把 RecorderSettings 存成程序目录下的 settings.json。
    ///
    /// 为什么需要：此前配置只活在内存里，每次启动都回默认值，用户要重新选保存目录、
    /// 重设鼠标效果与编码方式。
    ///
    /// 读写策略：任何异常都静默降级（读失败用默认值、写失败不影响录制），
    /// 绝不因为配置文件问题阻断启动。
    /// </summary>
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 中文不转义，便于人工查看
        };

        private static string? _path;

        /// <summary>配置文件实际使用的路径（首次访问时解析）。</summary>
        public static string FilePath => _path ??= ResolvePath();

        /// <summary>
        /// 优先用程序目录（绿色免安装版便于随程序携带），不可写时退到 %APPDATA%。
        /// </summary>
        private static string ResolvePath()
        {
            var candidates = new List<string>();

            try { candidates.Add(Path.Combine(AppContext.BaseDirectory, "settings.json")); } catch { }

            try
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ScreenRecorder", "settings.json");
                candidates.Add(appData);
            }
            catch { }

            foreach (string candidate in candidates)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(candidate);
                    if (string.IsNullOrEmpty(dir)) continue;
                    Directory.CreateDirectory(dir);

                    string probe = candidate + ".probe";
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return candidate;
                }
                catch { /* 该位置不可写，试下一个 */ }
            }

            return candidates.Count > 0 ? candidates[^1] : "settings.json";
        }

        /// <summary>读取配置。文件不存在或内容损坏时返回默认值。</summary>
        public static RecorderSettings Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return new RecorderSettings();

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new RecorderSettings();

                var loaded = JsonSerializer.Deserialize<RecorderSettings>(json, Options);
                if (loaded == null) return new RecorderSettings();

                // 防御：输出目录被删或为空时回落默认值
                if (string.IsNullOrWhiteSpace(loaded.OutputDirectory))
                    loaded.OutputDirectory = new RecorderSettings().OutputDirectory;
                return loaded;
            }
            catch
            {
                return new RecorderSettings();
            }
        }

        /// <summary>保存配置。失败静默忽略，不影响录制。</summary>
        public static bool Save(RecorderSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, Options);
                File.WriteAllText(FilePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
