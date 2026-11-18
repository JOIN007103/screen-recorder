using System.Runtime.InteropServices;

// 验证辅助工具：向系统发送一个真实的全局按键（F9 / F10），用于自动化验收全局热键。
// 仅用于 spike，不随绿色版分发。
if (args.Length != 1)
{
    Console.WriteLine("usage: KeySender F9|F10");
    return 1;
}

byte vk = args[0].ToUpperInvariant() switch
{
    "F9" => 0x78,
    "F10" => 0x79,
    _ => (byte)0
};
if (vk == 0)
{
    Console.WriteLine("only F9 / F10 supported");
    return 1;
}

keybd_event(vk, 0, 0, UIntPtr.Zero);
Thread.Sleep(60);
keybd_event(vk, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
Console.WriteLine($"sent {args[0]}");
return 0;

[DllImport("user32.dll")]
static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
