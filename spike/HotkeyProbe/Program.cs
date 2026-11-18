using System.Runtime.InteropServices;

// 用途：探测 F9 / F10 是否已被其他进程注册为全局热键。
// 录屏软件运行时应当占用这两个键，因此本探针在此时应报告「已被占用」。

const uint MOD_NOREPEAT = 0x4000;
const uint VK_F9 = 0x78;
const uint VK_F10 = 0x79;

bool f9 = Native.RegisterHotKey(IntPtr.Zero, 1, MOD_NOREPEAT, VK_F9);
int f9Err = f9 ? 0 : Marshal.GetLastWin32Error();
bool f10 = Native.RegisterHotKey(IntPtr.Zero, 2, MOD_NOREPEAT, VK_F10);
int f10Err = f10 ? 0 : Marshal.GetLastWin32Error();

Console.WriteLine($"F9  注册成功={f9}  err={f9Err}  → {(f9 ? "当前空闲" : "已被占用")}");
Console.WriteLine($"F10 注册成功={f10} err={f10Err} → {(f10 ? "当前空闲" : "已被占用")}");

if (f9) Native.UnregisterHotKey(IntPtr.Zero, 1);
if (f10) Native.UnregisterHotKey(IntPtr.Zero, 2);

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
