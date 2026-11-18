using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace WPFCheck;

/// <summary>
/// S2-GC WPF 补验：验证 Windows Graphics Capture 在 WPF 宿主下能否创建帧池并接收帧。
/// 核心：Direct3D11CaptureFramePool.Create 接受 IDirect3DDevice（默认投影含此接口）。
/// 设备包装：原生 D3D11 设备指针 → WinRT IDirect3DDevice，绕过缺失的 Direct3D11Device 辅助类。
/// </summary>
public partial class MainWindow : Window
{
    private readonly StringBuilder _log = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunVerificationAsync();
    }

    private static class Native
    {
        // D3D_DRIVER_TYPE_HARDWARE = 1, sdkVersion = 7
        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        internal static extern int D3D11CreateDevice(
            IntPtr pAdapter, int driverType, IntPtr software, uint flags,
            IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDesktopWindow();

        // 取桌面所在显示器句柄（MONITOR_DEFAULTTOPRIMARY = 1）
        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory")]
        internal static extern int RoGetActivationFactory(
            IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        // HSTRING 操作（combase.dll）
        [DllImport("combase.dll", EntryPoint = "WindowsCreateString", CharSet = CharSet.Unicode)]
        internal static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

        [DllImport("combase.dll", EntryPoint = "WindowsDeleteString")]
        internal static extern int WindowsDeleteString(IntPtr hstring);

        // GraphicsCaptureItem 的 COM 创建接口（官方 GUID）
        // 返回原始 IInspectable 指针，交给 CsWinRT MarshalInspectable 包装，
        // 避免 out object 走 RCW 强转抛 InvalidCastException。
        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IGraphicsCaptureItemInterop
        {
            int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
            int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
        }

        // Direct3D11DeviceInterop 工厂（IDirect3DDevice 桥接）
        [ComImport, Guid("54FD2AC5-E925-401A-9C2A-F94F171072F8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDirect3DDeviceInterop
        {
            void CreateFromDirect3D11Device(IntPtr device, ref Guid riid,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
        }

        // 官方互操作函数：IDXGIDevice → WinRT IDirect3DDevice（d3d11.dll 导出）
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        internal static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);
    }

    /// <summary>
    /// 把原生 ID3D11Device 包装为 WinRT IDirect3DDevice：
    /// QI 出 IDXGIDevice → CreateDirect3D11DeviceFromDXGIDevice → CsWinRT 托管包装。
    /// </summary>
    private static IDirect3DDevice? CreateWinRTDeviceFromD3D11(IntPtr d3d11Device)
    {
        Guid dxgiIid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C"); // IDXGIDevice
        int hr = Marshal.QueryInterface(d3d11Device, ref dxgiIid, out IntPtr dxgiDevice);
        if (hr != 0) return null;
        try
        {
            hr = Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr graphicsDevice);
            if (hr != 0) return null;
            try
            {
                return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
            }
            finally
            {
                Marshal.Release(graphicsDevice);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevice);
        }
    }

    private static readonly string LogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "gc_verify.txt");

    private void Log(string line)
    {
        _log.AppendLine(line);
        try
        {
            System.IO.File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
        TxtLog.Text = _log.ToString();
        TxtLog.ScrollToEnd();
    }

    private async Task RunVerificationAsync()
    {
        try { System.IO.File.WriteAllText(LogPath, ""); } catch { }
        Log("=== Graphics Capture 可行性验证（WPF 宿主）===");
        try
        {
            // 1) 创建 D3D11 原生设备
            IntPtr device = IntPtr.Zero, ctx = IntPtr.Zero; int level = 0;
            int hr = Native.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0,
                IntPtr.Zero, 0, 7, out device, out level, out ctx);
            if (hr != 0) { Log($"FAIL: D3D11CreateDevice hr=0x{hr:X8}"); return; }
            Log($"D3D11 设备创建成功, feature level=0x{level:X}");

            // 2) 原生 D3D11 设备 → WinRT IDirect3DDevice
            //    官方互操作路径：QI 出 IDXGIDevice → CreateDirect3D11DeviceFromDXGIDevice
            var d3dDevice = CreateWinRTDeviceFromD3D11(device);
            if (d3dDevice == null) { Log("FAIL: IDirect3DDevice 包装失败"); return; }
            Log("WinRT IDirect3DDevice 包装成功");

            // 3) 从主显示器创建捕获项（全屏录制用 CreateForMonitor，窗口句柄不能直接捕桌面）
            var item = CreateCaptureItemForMonitor();
            if (item == null) { Log("FAIL: 捕获项创建失败"); return; }
            Log($"捕获项创建成功: {item.Size.Width}x{item.Size.Height}");

            // 4) 帧池 + 会话（WPF 主线程有 DispatcherQueue）
            var pool = Direct3D11CaptureFramePool.Create(
                d3dDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);

            long frameCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            pool.FrameArrived += (s, e) =>
            {
                using var frame = pool.TryGetNextFrame();
                if (frame != null) Interlocked.Increment(ref frameCount);
            };
            var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();
            Log("Graphics Capture 会话已启动");

            // 5) 采集 5 秒统计帧率
            await Task.Delay(5000);
            sw.Stop();
            double fps = frameCount / sw.Elapsed.TotalSeconds;
            Log($"[结果] 5 秒共收到 {frameCount} 帧，实测帧率 ≈ {fps:F1} fps");
            Log(frameCount > 0 ? "PASS: Graphics Capture 链路有效" : "WARN: 会话启动但无帧到达");

            // 6) 清理
            session.Dispose();
            pool.Dispose();
            Log("验证完成，会话已释放");
        }
        catch (Exception ex)
        {
            Log($"FAIL: {ex.GetType().Name}: {ex.Message}");
            Log(ex.StackTrace ?? "");
        }
        finally
        {
            // 自动化验证：写出结果后 1.5 秒关闭窗口
            Log("[END]");
            await Task.Delay(1500);
            Application.Current.Shutdown();
        }
    }

    private Native.IDirect3DDeviceInterop? GetDirect3DDeviceInteropFactory()
    {
        // 直接 COM: CLSID 已知（Windows.Graphics.DirectX.Direct3D11.Direct3D11Device）
        // 但常规做法是 RoGetActivationFactory("Windows.Graphics.DirectX.Direct3D11.Direct3D11Device",
        //   IID_IDirect3DDeviceInterop)，如果默认投影没有这个类，则反射 + 强转。
        try
        {
            var t = Type.GetType("Windows.Graphics.DirectX.Direct3D11.Direct3D11Device, Microsoft.Windows.SDK.NET");
            if (t != null)
            {
                var m = t.GetMethod("CreateFromDirect3D11Device",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m != null)
                {
                    // 这个方法存在时直接返回（但需传 device 指针）
                    return null; // 占位：走反射包装路径更复杂，先走 COM 激活工厂
                }
            }
        }
        catch { }

        // RoGetActivationFactory 路径（HSTRING 参数需要 WindowsCreateString 创建）
        var hstr = CreateHString("Windows.Graphics.DirectX.Direct3D11.Direct3D11Device");
        try
        {
            Guid interopIid = typeof(Native.IDirect3DDeviceInterop).GUID;
            int hr = Native.RoGetActivationFactory(hstr, ref interopIid, out IntPtr factory);
            if (hr == 0 && factory != IntPtr.Zero)
            {
                var obj = Marshal.GetObjectForIUnknown(factory);
                Marshal.Release(factory);
                return (Native.IDirect3DDeviceInterop)obj;
            }
            Log($"RoGetActivationFactory hr=0x{hr:X8}");
        }
        finally
        {
            DeleteHString(hstr);
        }
        return null;
    }

    /// <summary>
    /// 直接用 vtable 调用 IGraphicsCaptureItemInterop::CreateForMonitor。
    /// 说明：.NET 的 RCW 无法把 WinRT 激活工厂强转为 ComImport 接口（InvalidCastException），
    /// 因此按 IUnknown(3 槽) + CreateForWindow(1) + CreateForMonitor(1) 的布局直接调用，
    /// 见 windows.graphics.capture.interop.h。
    /// </summary>
    private unsafe GraphicsCaptureItem? CreateCaptureItemForMonitor()
    {
        var hstr = CreateHString("Windows.Graphics.Capture.GraphicsCaptureItem");
        try
        {
            Guid interopIid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            int hr = Native.RoGetActivationFactory(hstr, ref interopIid, out IntPtr factory);
            if (hr != 0)
            {
                Log($"RoGetActivationFactory(CaptureItem) hr=0x{hr:X8}");
                return null;
            }
            try
            {
                IntPtr hmon = Native.MonitorFromWindow(Native.GetDesktopWindow(), 1);
                if (hmon == IntPtr.Zero) { Log("FAIL: 取主显示器句柄失败"); return null; }
                Log($"主显示器句柄: 0x{hmon:X}");

                // GraphicsCaptureItem 运行时类 IID（官方值）；
                // 投影中 typeof(...).GUID 可能为空，导致 E_NOINTERFACE，故硬编码。
                Guid itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                Log($"投影 GUID={typeof(GraphicsCaptureItem).GUID} / 使用 IID={itemIid}");
                IntPtr itemPtr;

                // IInspectable 前 3 槽为 QI/AddRef/Release，第 4 槽 CreateForWindow，第 5 槽 CreateForMonitor
                void** vtbl = *(void***)factory;
                var fnCreateForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];
                int hr2 = fnCreateForMonitor(factory, hmon, &itemIid, &itemPtr);
                if (hr2 != 0) { Log($"CreateForMonitor hr=0x{hr2:X8}"); return null; }

                try
                {
                    return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
                }
                finally
                {
                    Marshal.Release(itemPtr);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            DeleteHString(hstr);
        }
    }

    // HSTRING 创建（RoGetActivationFactory 需要真正的 HSTRING 而非 HGlobal 指针）
    private static IntPtr CreateHString(string s)
    {
        IntPtr hstr;
        int hr = Native.WindowsCreateString(s, s.Length, out hstr);
        if (hr != 0) throw new COMException($"WindowsCreateString hr=0x{hr:X8}");
        return hstr;
    }

    private static void DeleteHString(IntPtr hstr) => Native.WindowsDeleteString(hstr);
}
