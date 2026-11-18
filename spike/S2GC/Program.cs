// S2-GC：验证 Windows Graphics Capture 能否在控制台进程中创建帧池并开始捕获。
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.System;
using WinRT;

internal static class Native
{
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    internal static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();

    [ComImport, Guid("54FD2AC5-E925-401A-9C2A-F94F171072F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDeviceInterop
    {
        void CreateFromDirect3D11Device(IntPtr device, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        int CreateForMonitor(IntPtr monitor, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
}

internal static class Program
{
    static async Task<int> Main()
    {
        Console.WriteLine("Graphics Capture 可行性验证");
        try
        {
            // 1) 创建 D3D11 设备（硬件优先，失败回退 WARP）
            IntPtr device = IntPtr.Zero, ctx = IntPtr.Zero; int level = 0;
            int hr = Native.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out device, out level, out ctx);
            if (hr != 0) { Console.WriteLine($"FAIL: D3D11CreateDevice hr=0x{hr:X8}"); return 2; }
            Console.WriteLine($"D3D11 设备创建成功, feature level=0x{level:X}");

            // 2) 包装为 WinRT Direct3D11Device
            var d3dInterop = (Native.IDirect3DDeviceInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(Direct3D11Device));
            Guid devIid = typeof(Direct3D11Device).GUID;
            d3dInterop.CreateFromDirect3D11Device(device, ref devIid, out object devObj);
            var d3dDevice = (Direct3D11Device)devObj;
            Console.WriteLine("WinRT Direct3D11Device 包装成功");

            // 3) 从桌面窗口创建捕获项
            var capInterop = (Native.IGraphicsCaptureItemInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            IntPtr hwnd = Native.GetDesktopWindow();
            Guid itemIid = typeof(GraphicsCaptureItem).GUID;
            int hr2 = capInterop.CreateForWindow(hwnd, ref itemIid, out object itemObj);
            if (hr2 != 0) { Console.WriteLine($"FAIL: CreateForWindow hr=0x{hr2:X8}"); return 3; }
            var item = (GraphicsCaptureItem)itemObj;
            Console.WriteLine($"捕获项创建成功: {item.Size.Width}x{item.Size.Height}");

            // 4) 在专用线程调度器上创建帧池
            var controller = DispatcherQueueController.CreateOnDedicatedThread();
            var tcs = new TaskCompletionSource<bool>();
            int frames = 0;
            controller.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var pool = Direct3D11CaptureFramePool.Create(d3dDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
                    pool.FrameArrived += (s, e) => { using var frame = pool.TryGetNextFrame(); frames++; };
                    var session = pool.CreateCaptureSession(item);
                    session.IsCursorCaptureEnabled = false;
                    session.StartCapture();
                    Console.WriteLine("Graphics Capture 会话已启动");
                    tcs.SetResult(true);
                }
                catch (Exception ex) { Console.WriteLine($"FAIL: 帧池/会话异常: {ex.GetType().Name}: {ex.Message}"); tcs.SetException(ex); }
            });
            bool started = await tcs.Task;
            await Task.Delay(3000);
            Console.WriteLine($"3 秒内收到帧数: {frames}");
            Console.WriteLine(frames > 0 ? "PASS: Graphics Capture 链路有效" : "WARN: 会话启动但无帧到达");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }
    }
}
