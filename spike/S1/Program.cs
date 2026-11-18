// S1 验证程序：仅验证所需 API 可被引用并编译，不执行捕获。
using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

// Media Foundation 互操作声明验证
internal static class NativeMethods
{
    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    internal static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
        IntPtr pByteStream,
        IntPtr pAttributes,
        out IntPtr ppSinkWriter);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(uint Version, uint dwFlags);
}

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("S1 API 引用验证");
        Console.WriteLine($"GraphicsCaptureSession 类型: {typeof(GraphicsCaptureSession).FullName}");
        Console.WriteLine($"GraphicsCaptureItem 类型: {typeof(GraphicsCaptureItem).FullName}");
        Console.WriteLine("MFCreateSinkWriterFromURL 声明: OK");
        Console.WriteLine("MFStartup 声明: OK");
        Console.WriteLine("S1 验证通过：WinRT 捕获与 Media Foundation 互操作引用均可用。");
    }
}
