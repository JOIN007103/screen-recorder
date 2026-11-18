using System;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Native
{
    /// <summary>
    /// Windows 原生 API 封装：GDI 屏幕捕获、光标位置、磁盘空间查询。
    /// 全部为 P/Invoke，不依赖 Windows SDK（阶段0实测可行）。
    /// </summary>
    internal static class NativeMethods
    {
        // ---------- 结构与常量 ----------

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            // 32bpp 无调色板，保留 1 项占位即可
            public uint bmiColors;
        }

        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const int DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;

        // ---------- GDI 屏幕捕获 ----------

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int width, int height);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int GetDIBits(IntPtr hDC, IntPtr hBmp, uint uStartScan, uint cScanLines,
            IntPtr lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteDC(IntPtr hDC);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        internal const int SM_CXSCREEN = 0;
        internal const int SM_CYSCREEN = 1;

        // ---------- 光标位置 ----------

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        // ---------- 窗口捕获排除 ----------

        internal const uint WDA_NONE = 0x00000000;
        internal const uint WDA_MONITOR = 0x00000001;
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        /// <summary>
        /// 设置窗口的显示亲和性。WDA_EXCLUDEFROMCAPTURE 可让窗口从 BitBlt 等捕获 API 中消失。
        /// 实测（Win10 19045）：仅对非分层窗口生效；AllowsTransparency=true 的 WPF 窗口会返回
        /// FALSE(err=8) 且照样被抓到，因此悬浮条必须使用非透明实底窗口。
        /// 需要 Windows 10 2004(19041) 及以上。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        // ---------- 全局热键 ----------

        internal const int WM_HOTKEY = 0x0312;
        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ---------- 磁盘空间 ----------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        /// <summary>
        /// 捕获整个主屏为 32bpp BGRA 字节数组（自下而上 DIB 布局）。
        /// 返回值：宽、高、像素缓冲（调用方负责释放非托管内存后复制）。
        /// </summary>
        internal static bool CaptureScreen(
            int width, int height,
            out IntPtr pixelPtr, out int stride)
        {
            pixelPtr = IntPtr.Zero;
            stride = width * 4;

            IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero, oldBmp = IntPtr.Zero;
            try
            {
                screenDC = GetDC(IntPtr.Zero);
                if (screenDC == IntPtr.Zero) return false;

                memDC = CreateCompatibleDC(screenDC);
                bitmap = CreateCompatibleBitmap(screenDC, width, height);
                if (memDC == IntPtr.Zero || bitmap == IntPtr.Zero) return false;

                oldBmp = SelectObject(memDC, bitmap);

                // CAPTUREBLT 使某些硬件叠加层也能被捕获；失败时回退到纯 SRCCOPY
                if (!BitBlt(memDC, 0, 0, width, height, screenDC, 0, 0, SRCCOPY | CAPTUREBLT))
                {
                    if (!BitBlt(memDC, 0, 0, width, height, screenDC, 0, 0, SRCCOPY))
                        return false;
                }

                var bi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height, // 负数 = 自上而下布局，便于直接按行读取
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB
                    }
                };

                int bufferSize = stride * height;
                pixelPtr = Marshal.AllocHGlobal(bufferSize);
                int got = GetDIBits(memDC, bitmap, 0, (uint)height, pixelPtr, ref bi, DIB_RGB_COLORS);
                if (got <= 0)
                {
                    Marshal.FreeHGlobal(pixelPtr);
                    pixelPtr = IntPtr.Zero;
                    return false;
                }
                return true;
            }
            finally
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDC, oldBmp);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memDC != IntPtr.Zero) DeleteDC(memDC);
                if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        /// <summary>获取指定路径所在磁盘的剩余可用空间（MB）。</summary>
        public static double GetFreeSpaceMb(string path)
        {
            try
            {
                string root = System.IO.Path.GetPathRoot(path) ?? path;
                if (string.IsNullOrEmpty(root)) root = path;
                if (GetDiskFreeSpaceEx(root, out ulong available, out _, out _))
                    return available / (1024.0 * 1024.0);
            }
            catch { /* 查询失败时按未知处理，不触发预警 */ }
            return -1;
        }
    }
}
