using System;
using System.Runtime.InteropServices;
using System.Text;

/* QJCore.dll 的托管门面：结构体、回调、取消句柄都在这里转换，
   界面层只看到字符串和 bool。结构体字节数见 src/core/qjcore.h。 */

internal enum CoreOutcome { Ok = 0, Failed = 1, Canceled = 2, Pending = 3 }

internal sealed class CoreResult
{
    internal CoreOutcome Outcome = CoreOutcome.Failed;
    internal string Message = string.Empty;
    internal string Serial = string.Empty;
    internal int Flag;
    internal int Days;
    internal bool HasState;
    internal bool Reachable;
    internal bool Submitted;
}

internal delegate void CoreNotify(int type, string text, string serial, int flag, int days, int progress);

internal sealed class CoreCancel : IDisposable
{
    private readonly System.Threading.ManualResetEvent gate = new System.Threading.ManualResetEvent(false);

    internal IntPtr Handle { get { return gate.SafeWaitHandle.DangerousGetHandle(); } }

    internal void Cancel() { gate.Set(); }

    public void Dispose()
    {
        gate.Close();
        gate.Dispose();
    }
}

internal static class Core
{
    private const string Library = "QJCore.dll";

    internal const int NoticeLog = 0;
    internal const int NoticeConnected = 1;
    internal const int NoticeState = 2;
    internal const int NoticeProgress = 3;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeNotify(IntPtr context, IntPtr notice);

    [StructLayout(LayoutKind.Sequential)]
    private struct Notice
    {
        public int Type;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] Text;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Serial;
        public int Flag, Days, Progress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public int Outcome;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] Message;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Serial;
        public int Flag, Days, HasState, Reachable, Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Request
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Ip;
        public ushort Port;
        public int Activate;
        public IntPtr Cancel;
        public uint TimeoutMs, VerifyMs, PollMs;
        public IntPtr Notify;
        public IntPtr Context;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void controller_run(ref Request request, ref Result result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pendant_restart([MarshalAs(UnmanagedType.LPWStr)] string ssh,
        ref Request request, ref Result result, int registered);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qj_exchange(
        [MarshalAs(UnmanagedType.LPStr)] string ip, ushort port, uint timeoutMs,
        byte[] command, uint commandLength, byte[] data, uint dataLength,
        byte[] output, uint capacity, out uint outputLength,
        IntPtr cancel, byte[] error, uint errorCapacity);

    internal static CoreResult Run(string ip, int port, bool activate, int timeoutMs, int verifyMs,
        int pollMs, CoreNotify notify, IntPtr cancel)
    {
        var request = new Request
        {
            Ip = Fixed(ip, 16),
            Port = (ushort)port,
            Activate = activate ? 1 : 0,
            Cancel = cancel,
            TimeoutMs = (uint)timeoutMs,
            VerifyMs = (uint)verifyMs,
            PollMs = (uint)pollMs,
            Context = IntPtr.Zero
        };
        NativeNotify callback = null;
        if (notify != null)
        {
            callback = delegate(IntPtr context, IntPtr pointer)
            {
                Notice notice = (Notice)Marshal.PtrToStructure(pointer, typeof(Notice));
                notify(notice.Type, Wide(notice.Text), Ascii(notice.Serial), notice.Flag,
                    notice.Days, notice.Progress);
            };
            request.Notify = Marshal.GetFunctionPointerForDelegate(callback);
        }

        var result = new Result { Message = new byte[512], Serial = new byte[256] };
        controller_run(ref request, ref result);
        GC.KeepAlive(callback);

        return new CoreResult
        {
            Outcome = (CoreOutcome)result.Outcome,
            Message = Wide(result.Message),
            Serial = Ascii(result.Serial),
            Flag = result.Flag,
            Days = result.Days,
            HasState = result.HasState != 0,
            Reachable = result.Reachable != 0,
            Submitted = result.Submitted != 0
        };
    }

    /* 手动重启（registered=false）与注册成功后自动重启（registered=true）共用同一实现。 */
    internal static string RestartPendant(bool registered, int port, CoreNotify notify, IntPtr cancel,
        string ssh = null)
    {
        var request = new Request
        {
            Ip = Fixed("192.168.1.200", 16),
            Port = (ushort)port,
            Activate = 1,
            Cancel = cancel,
            TimeoutMs = 5000,
            VerifyMs = 15000,
            PollMs = 500,
            Context = IntPtr.Zero
        };
        NativeNotify callback = null;
        if (notify != null)
        {
            callback = delegate(IntPtr context, IntPtr pointer)
            {
                Notice notice = (Notice)Marshal.PtrToStructure(pointer, typeof(Notice));
                notify(notice.Type, Wide(notice.Text), Ascii(notice.Serial), notice.Flag,
                    notice.Days, notice.Progress);
            };
            request.Notify = Marshal.GetFunctionPointerForDelegate(callback);
        }

        var result = new Result
        {
            Message = new byte[512],
            Serial = new byte[256],
            Outcome = (int)CoreOutcome.Ok,
            HasState = 1,
            Submitted = 1,
            Reachable = 1,
            Flag = 1
        };
        pendant_restart(ssh, ref request, ref result, registered ? 1 : 0);
        GC.KeepAlive(callback);
        return Wide(result.Message);
    }

    /* 单次命令收发；命令与数据已按界面选择的编码编成字节。 */
    internal static bool Exchange(string ip, int port, int timeoutMs, byte[] command, byte[] data,
        out byte[] reply, IntPtr cancel, out string error)
    {
        reply = new byte[65536];
        var errorBuffer = new byte[512];
        uint written;
        int rc = qj_exchange(ip, (ushort)port, (uint)timeoutMs, command,
            (uint)(command == null ? 0 : command.Length),
            data, (uint)(data == null ? 0 : data.Length),
            reply, (uint)reply.Length, out written, cancel, errorBuffer, (uint)(errorBuffer.Length / 2));
        error = Wide(errorBuffer);
        if (rc != 0)
        {
            reply = new byte[0];
            return false;
        }
        Array.Resize(ref reply, (int)written);
        return true;
    }

    private static byte[] Fixed(string text, int size)
    {
        var buffer = new byte[size];
        byte[] bytes = Encoding.ASCII.GetBytes(text ?? string.Empty);
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, size));
        return buffer;
    }

    private static string Wide(byte[] buffer)
    {
        if (buffer == null) { return string.Empty; }
        int length = 0;
        while (length + 1 < buffer.Length && !(buffer[length] == 0 && buffer[length + 1] == 0))
        {
            length += 2;
        }
        return Encoding.Unicode.GetString(buffer, 0, length);
    }

    private static string Ascii(byte[] buffer)
    {
        if (buffer == null) { return string.Empty; }
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) { length = buffer.Length; }
        return Encoding.ASCII.GetString(buffer, 0, length);
    }
}