using System;
using System.Windows.Interop;
using ScreenRecorder.Native;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 全局热键管理：用一个消息专用窗口（HWND_MESSAGE）接收 WM_HOTKEY。
    ///
    /// 为什么需要：主窗口在录制开始时会被隐藏，只留悬浮控制条；
    /// 没有热键就只能去点悬浮条，而悬浮条盖住的区域用户看不到。
    ///
    /// 注册失败（被其他程序占用）时返回错误信息，由界面提示，不抛异常。
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        public const int IdToggle = 0xB001; // F9：开始 / 暂停 / 继续
        public const int IdStop = 0xB002;   // F10：停止

        private const uint VK_F9 = 0x78;
        private const uint VK_F10 = 0x79;
        private static readonly IntPtr HWND_MESSAGE = new(-3);

        private HwndSource? _source;
        private bool _disposed;

        /// <summary>热键按下事件，参数为 IdToggle / IdStop。</summary>
        public event Action<int>? Pressed;

        /// <summary>注册结果描述，成功时为 null。</summary>
        public string? ErrorMessage { get; private set; }

        /// <summary>热键说明，供界面展示。</summary>
        public static string Description => "F9 开始/暂停/继续 · F10 停止";

        public bool Register()
        {
            try
            {
                var p = new HwndSourceParameters("ScreenRecorderHotkeyWindow")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0,
                    ParentWindow = HWND_MESSAGE
                };

                _source = new HwndSource(p);
                _source.AddHook(WndProc);

                IntPtr hwnd = _source.Handle;
                bool okToggle = NativeMethods.RegisterHotKey(
                    hwnd, IdToggle, NativeMethods.MOD_NOREPEAT, VK_F9);
                bool okStop = NativeMethods.RegisterHotKey(
                    hwnd, IdStop, NativeMethods.MOD_NOREPEAT, VK_F10);

                if (!okToggle || !okStop)
                {
                    var missing = new System.Collections.Generic.List<string>();
                    if (!okToggle) missing.Add("F9");
                    if (!okStop) missing.Add("F10");
                    ErrorMessage = $"热键 {string.Join("、", missing)} 被其他程序占用，可在悬浮条上手动操作";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                ErrorMessage = "热键注册失败：" + ex.Message;
                return false;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                Pressed?.Invoke(wParam.ToInt32());
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_source != null)
                {
                    NativeMethods.UnregisterHotKey(_source.Handle, IdToggle);
                    NativeMethods.UnregisterHotKey(_source.Handle, IdStop);
                    _source.RemoveHook(WndProc);
                    _source.Dispose();
                    _source = null;
                }
            }
            catch { /* 退出阶段失败无需处理 */ }
        }
    }
}
