// S3：Media Foundation Sink Writer 将帧编码为 H.264 并写入 MP4。
// 采用纯 vtable 直接调用，绕开 CLR COM 转换层，不依赖 Windows SDK。
using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class Mf
{
    internal const uint MFStartupFull = 0x00020070;

    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102d");
    internal static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid MF_MT_MPEG2_PROFILE = new("226cdb88-9d4f-4dea-8ba4-e28e920e0a88");
    internal static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a41a-6b60-42cb-b0fc-21048a5bf5be");
    internal static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b6f00d-c02e-455e-8d5c-6e5542c0e8c1");
    internal static long Pack2(uint a, uint b) => ((long)a << 32) | b;

    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IntPtr pp);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateSample(out IntPtr pp);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMemoryBuffer(uint cb, out IntPtr pp);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] internal static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr stream, IntPtr attrs, out IntPtr pp);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateAttributes(out IntPtr pp, uint cInitial);
}

// 通过 vtable 槽位直接调用 COM 方法
internal static class Vt
{
    static IntPtr Slot(IntPtr obj, int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size);
    static T Del<T>(IntPtr obj, int index) => (T)(object)Marshal.GetDelegateForFunctionPointer(Slot(obj, index), typeof(T));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetU32(IntPtr self, ref Guid key, uint val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetU64(IntPtr self, ref Guid key, ulong val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetGuid(IntPtr self, ref Guid key, ref Guid val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AddStream(IntPtr self, IntPtr type, out uint idx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetInputType(IntPtr self, uint idx, IntPtr type, IntPtr parms);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int BeginWriting(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int WriteSample(IntPtr self, uint idx, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Finalize(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetTime(IntPtr self, long t);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetDuration(IntPtr self, long d);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AddBuffer(IntPtr self, IntPtr buf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int LockBuf(IntPtr self, out IntPtr data, out uint maxLen, out uint curLen);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetCurLen(IntPtr self, uint len);

    // IMFAttributes: GetItem(3) GetItemType(4) CompareItem(5) Compare(6) GetU32(7) GetU64(8) GetDouble(9)
    //   GetGUID(10) GetStringLen(11) GetString(12) GetAllocStr(13) GetBlobSize(14) GetBlob(15)
    //   GetAllocBlob(16) GetUnknown(17) SetItem(18) SetU32(19) SetU64(20) SetDouble(21) SetGUID(22)
    internal static int AttrU32(IntPtr o, Guid g, uint v) { var k = g; return Del<SetU32>(o, 19)(o, ref k, v); }
    internal static int AttrU64(IntPtr o, Guid g, ulong v) { var k = g; return Del<SetU64>(o, 20)(o, ref k, v); }
    internal static int AttrGuid(IntPtr o, Guid g, Guid v) { var k = g; return Del<SetGuid>(o, 22)(o, ref k, ref v); }

    // IMFSinkWriter: AddStream(3) SetInputMediaType(4) BeginWriting(5) WriteSample(6) ... Finalize(11)
    internal static int SW_AddStream(IntPtr o, IntPtr t, out uint i) => Del<AddStream>(o, 3)(o, t, out i);
    internal static int SW_SetInput(IntPtr o, uint i, IntPtr t) => Del<SetInputType>(o, 4)(o, i, t, IntPtr.Zero);
    internal static int SW_Begin(IntPtr o) => Del<BeginWriting>(o, 5)(o);
    internal static int SW_Write(IntPtr o, uint i, IntPtr s) => Del<WriteSample>(o, 6)(o, i, s);
    internal static int SW_Finalize(IntPtr o) => Del<Finalize>(o, 11)(o);

    // IMFSample: GetFlags(3) SetFlags(4) GetTime(5) SetTime(6) GetDuration(7) SetDuration(8) ... AddBuffer(11)
    internal static int S_SetTime(IntPtr o, long t) => Del<SetTime>(o, 6)(o, t);
    internal static int S_SetDur(IntPtr o, long d) => Del<SetDuration>(o, 8)(o, d);
    internal static int S_AddBuf(IntPtr o, IntPtr b) => Del<AddBuffer>(o, 11)(o, b);

    // IMFMediaBuffer: GetMaxLen(3) GetCurLen(4) Lock(5) Unlock(6) SetCurLen(7) SetMaxLen(8)
    internal static int B_Lock(IntPtr o, out IntPtr d, out uint mx, out uint cur) => Del<LockBuf>(o, 5)(o, out d, out mx, out cur);
    internal static int B_SetCurLen(IntPtr o, uint len) => Del<SetCurLen>(o, 7)(o, len);
}

internal static class Program
{
    static int Chk(string name, int hr) { Console.WriteLine($"{name}: 0x{hr:X8}"); return hr; }

    static int Main()
    {
        Console.WriteLine("S3 Media Foundation H.264/MP4 编码验证");
        int hr = Chk("MFStartup", Mf.MFStartup(Mf.MFStartupFull, 0));
        if (hr != 0) return 2;

        string outFile = Path.Combine(AppContext.BaseDirectory, "s3_output.mp4");
        if (File.Exists(outFile)) File.Delete(outFile);

        // 创建 SinkWriter 属性：启用硬件变换（调用 NVIDIA NVENC）
        Mf.MFCreateAttributes(out IntPtr swAttrs, 2);
        Chk("  attrHWTransform", Vt.AttrU32(swAttrs, Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1));
        Chk("  attrNoThrottle", Vt.AttrU32(swAttrs, Mf.MF_SINK_WRITER_DISABLE_THROTTLING, 1));
        hr = Chk("MFCreateSinkWriterFromURL", Mf.MFCreateSinkWriterFromURL(outFile, IntPtr.Zero, swAttrs, out IntPtr sw));
        if (hr != 0) { Mf.MFShutdown(); return 3; }

        // 输出类型 H.264（模式可通过命令行参数切换：0=完整 1=加Profile 2=NV12输入 3=小尺寸）
        int mode = Environment.GetCommandLineArgs().Length > 1 && int.TryParse(Environment.GetCommandLineArgs()[1], out var m) ? m : 1;
        int W = mode == 3 ? 640 : 1920, H = mode == 3 ? 360 : 1080;

        Mf.MFCreateMediaType(out IntPtr outType);
        Chk("  setMajor", Vt.AttrGuid(outType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video));
        Chk("  setSubtype", Vt.AttrGuid(outType, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264));
        Chk("  setBitrate", Vt.AttrU32(outType, Mf.MF_MT_AVG_BITRATE, 4000000));
        Chk("  setInterlace", Vt.AttrU32(outType, Mf.MF_MT_INTERLACE_MODE, 2));
        Vt.AttrU64(outType, Mf.MF_MT_FRAME_SIZE, (ulong)Mf.Pack2((uint)W, (uint)H));
        Vt.AttrU64(outType, Mf.MF_MT_FRAME_RATE, (ulong)Mf.Pack2(30, 1));
        Vt.AttrU64(outType, Mf.MF_MT_PIXEL_ASPECT_RATIO, (ulong)Mf.Pack2(1, 1));
        if (mode >= 1) Chk("  setProfile", Vt.AttrU32(outType, Mf.MF_MT_MPEG2_PROFILE, 77)); // Main profile
        hr = Chk($"AddStream(H.264,{W}x{H},mode={mode})", Vt.SW_AddStream(sw, outType, out uint stream));
        if (hr != 0) { Mf.MFShutdown(); return 4; }

        // 输入类型：默认 RGB32，mode>=2 用 NV12
        Mf.MFCreateMediaType(out IntPtr inType);
        Vt.AttrGuid(inType, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
        Vt.AttrGuid(inType, Mf.MF_MT_SUBTYPE, mode >= 2 ? Mf.MFVideoFormat_NV12 : Mf.MFVideoFormat_RGB32);
        Vt.AttrU64(inType, Mf.MF_MT_FRAME_SIZE, (ulong)Mf.Pack2((uint)W, (uint)H));
        Vt.AttrU64(inType, Mf.MF_MT_FRAME_RATE, (ulong)Mf.Pack2(30, 1));
        hr = Chk($"SetInputMediaType({(mode >= 2 ? "NV12" : "RGB32")})", Vt.SW_SetInput(sw, stream, inType));
        if (hr != 0) { Mf.MFShutdown(); return 5; }

        hr = Chk("BeginWriting", Vt.SW_Begin(sw));
        if (hr != 0) { Mf.MFShutdown(); return 6; }

        // 写入 30 帧测试画面
        int STRIDE = W * 4;
        int written = 0;
        for (int f = 0; f < 30; f++)
        {
            Mf.MFCreateSample(out IntPtr sample);
            Vt.S_SetTime(sample, (long)(f * 10000000.0 / 30));
            Vt.S_SetDur(sample, (long)(10000000.0 / 30));
            Mf.MFCreateMemoryBuffer((uint)(STRIDE * H), out IntPtr buf);
            int lk = Vt.B_Lock(buf, out IntPtr data, out _, out _);
            if (lk == 0 && data != IntPtr.Zero)
            {
                byte val = (byte)(f * 8);
                for (int y = 0; y < H; y++)
                {
                    IntPtr row = IntPtr.Add(data, y * STRIDE);
                    for (int x = 0; x < W; x++) Marshal.WriteByte(row, x * 4, val);
                }
                Vt.B_SetCurLen(buf, (uint)(STRIDE * H));
            }
            Vt.S_AddBuf(sample, buf);
            int whr = Vt.SW_Write(sw, stream, sample);
            if (whr == 0) written++;
        }
        Chk("Finalize", Vt.SW_Finalize(sw));
        Mf.MFShutdown();

        var fi = new FileInfo(outFile);
        Console.WriteLine($"写入帧数: {written}/30");
        Console.WriteLine($"输出文件: {fi.Length} 字节 ({fi.Length / 1024.0 / 1024.0:F2} MB)");
        Console.WriteLine(written == 30 && fi.Length > 10240 ? "PASS: H.264/MP4 编码落盘成功" : "FAIL: 编码未达预期");
        return written == 30 && fi.Length > 10240 ? 0 : 7;
    }
}
