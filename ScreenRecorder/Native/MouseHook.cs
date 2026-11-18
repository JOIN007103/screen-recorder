using System;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Native
{
    /// <summary>
    /// 全局低级鼠标钩子，捕获左键/右键点击事件（供点击涟漪效果使用）。
    /// WH_MOUSE_LL 在当前线程消息循环上回调，无需单独 DLL，兼容纯 .NET 工程。
    /// </summary>
    internal sealed class MouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public NativeMethods.POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly LowLevelMouseProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private bool _disposed;

        /// <summary>鼠标点击事件。参数：X、Y、是否为右键。</summary>
        public event Action<int, int, bool>? MouseClicked;

        public MouseHook()
        {
            _proc = HookCallback;
        }

        /// <summary>
        /// 安装钩子。必须在有消息循环的线程调用（WPF UI 线程即可）。
        /// </summary>
        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;
            using var cur = System.Diagnostics.Process.GetCurrentProcess();
            using var mod = cur.MainModule;
            IntPtr hMod = GetModuleHandle(mod?.ModuleName ?? string.Empty);
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            if (_hookId == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "安装鼠标钩子失败");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    MouseClicked?.Invoke(data.pt.X, data.pt.Y, msg == WM_RBUTTONDOWN);
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}
