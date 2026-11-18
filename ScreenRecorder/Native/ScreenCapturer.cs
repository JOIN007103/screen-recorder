using System;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Native
{
    /// <summary>
    /// 可复用的 GDI 屏幕捕获器。
    /// 与一次性 CaptureScreen 不同：DC、兼容位图、像素缓冲在构造时创建一次并复用，
    /// 每帧只执行 BitBlt + GetDIBits，避免逐帧重建 GDI 资源造成的性能损耗。
    /// 性能定位实测：一次性方案 26.6ms/帧（37fps 上限），复用方案降到 BitBlt 本身耗时。
    /// </summary>
    internal sealed class ScreenCapturer : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly IntPtr _screenDC;
        private readonly IntPtr _memDC;
        private readonly IntPtr _bitmap;
        private readonly IntPtr _oldBmp;
        private readonly IntPtr _pixelPtr;
        private NativeMethods.BITMAPINFO _bi; // GetDIBits 会以 ref 修改，不可 readonly
        private bool _disposed;

        public int Stride => _width * 4;
        public IntPtr PixelBuffer => _pixelPtr;

        public ScreenCapturer(int width, int height)
        {
            _width = width;
            _height = height;

            _screenDC = NativeMethods.GetDC(IntPtr.Zero);
            if (_screenDC == IntPtr.Zero) throw new InvalidOperationException("无法获取屏幕设备上下文。");

            _memDC = NativeMethods.CreateCompatibleDC(_screenDC);
            _bitmap = NativeMethods.CreateCompatibleBitmap(_screenDC, width, height);
            if (_memDC == IntPtr.Zero || _bitmap == IntPtr.Zero)
            {
                Cleanup();
                throw new InvalidOperationException("无法创建兼容位图。");
            }
            _oldBmp = NativeMethods.SelectObject(_memDC, _bitmap);

            _bi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // 自上而下布局
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            _pixelPtr = Marshal.AllocHGlobal(Stride * height);
        }

        /// <summary>捕获一帧到复用缓冲。成功返回 true。</summary>
        public bool Capture()
        {
            if (_disposed) return false;
            const int SRCCOPY = 0x00CC0020;
            const int CAPTUREBLT = 0x40000000;
            const int DIB_RGB_COLORS = 0;

            if (!NativeMethods.BitBlt(_memDC, 0, 0, _width, _height, _screenDC, 0, 0, SRCCOPY | CAPTUREBLT))
            {
                if (!NativeMethods.BitBlt(_memDC, 0, 0, _width, _height, _screenDC, 0, 0, SRCCOPY))
                    return false;
            }

            int got = NativeMethods.GetDIBits(_memDC, _bitmap, 0, (uint)_height, _pixelPtr, ref _bi, DIB_RGB_COLORS);
            return got > 0;
        }

        private void Cleanup()
        {
            if (_oldBmp != IntPtr.Zero && _memDC != IntPtr.Zero) NativeMethods.SelectObject(_memDC, _oldBmp);
            if (_bitmap != IntPtr.Zero) NativeMethods.DeleteObject(_bitmap);
            if (_memDC != IntPtr.Zero) NativeMethods.DeleteDC(_memDC);
            if (_screenDC != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, _screenDC);
            if (_pixelPtr != IntPtr.Zero) Marshal.FreeHGlobal(_pixelPtr);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Cleanup();
            _disposed = true;
        }
    }
}
