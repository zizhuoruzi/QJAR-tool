using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/* QJCore.dll 的 P/Invoke 冒烟测试：对回环模拟控制器跑一次查询、一次注册和一次裸命令收发。
   用法：Smoke.exe <ip> <port> */
internal static class SmokeProgram
{
    private const string Library = "QJCore.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NotifyCallback(IntPtr context, IntPtr notice);

    [StructLayout(LayoutKind.Sequential)]
    private struct Notice
    {
        public int Type;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] Text;   // wchar_t[256]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Serial; // char[256]
        public int Flag, Days, Progress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public int Outcome;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] Message; // wchar_t[256]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Serial;  // char[256]
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
    private static extern int qj_exchange(
        [MarshalAs(UnmanagedType.LPStr)] string ip, ushort port, uint timeoutMs,
        byte[] command, uint commandLength, byte[] data, uint dataLength,
        byte[] output, uint capacity, out uint outputLength,
        IntPtr cancel, byte[] error, uint errorCapacity);

    private static readonly NotifyCallback KeepAlive = OnNotice;
    private static int notices;

    private static void OnNotice(IntPtr context, IntPtr pointer)
    {
        Interlocked.Increment(ref notices);
    }

    private static byte[] Fixed(string text, int size, Encoding encoding)
    {
        var buffer = new byte[size];
        encoding.GetBytes(text, 0, text.Length, buffer, 0);
        return buffer;
    }

    private static string Text(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) { length = buffer.Length; }
        return Encoding.Unicode.GetString(buffer, 0, length);
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: Smoke.exe [ip port]（不带参数时自建回环模拟控制器）");
        }

        // 默认自带回环模拟控制器；显式给了地址就用外部控制器。
        string ip = args.Length >= 2 ? args[0] : "127.0.0.1";
        ushort port;
        MockController mock = null;
        if (args.Length >= 2)
        {
            port = ushort.Parse(args[1]);
        }
        else
        {
            mock = new MockController(2);
            port = (ushort)mock.Port;
            Console.WriteLine("模拟控制器端口: " + port);
        }

        int failures = 0;
        try
        {

        // 1. 裸命令收发：命令调试页走的就是这条路径。
        uint written;
        var error = new byte[512];
        var output = new byte[64];
        byte[] command = Encoding.ASCII.GetBytes("getsn");
        int rc = qj_exchange(ip, port, 5000, command, (uint)command.Length, null, 0,
            output, (uint)output.Length, out written, IntPtr.Zero, error, (uint)(error.Length / 2));
        string answer = Encoding.ASCII.GetString(output, 0, (int)written);
        if (rc == 0 && answer == "TEST-CONTROLLER-0000000001")
        {
            Console.WriteLine("PASS qj_exchange -> " + answer);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL qj_exchange rc=" + rc + " answer='" + answer + "' error='" + Text(error) + "'");
        }

        // 2. 只查询：设备尚未注册。
        var query = Run(ip, port, false, out string message, out int flag, out string serial);
        if (query == 0 && flag == 0 && serial == "TEST-CONTROLLER-0000000001")
        {
            Console.WriteLine("PASS 查询 -> " + message);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL 查询 outcome=" + query + " flag=" + flag + " message='" + message + "'");
        }

        // 3. 注册：模拟控制器两秒后生效，流程应当等到有效再返回。
        var register = Run(ip, port, true, out message, out flag, out serial);
        if (register == 0 && flag > 0)
        {
            Console.WriteLine("PASS 注册 -> " + message + " 剩余 " + flag + " 天");
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL 注册 outcome=" + register + " flag=" + flag + " message='" + message + "'");
        }

        Console.WriteLine("通知回调数: " + notices);
        Console.WriteLine(failures == 0 ? "全部通过" : failures + " 项失败");
        return failures == 0 ? 0 : 1;
        }
        finally
        {
            if (mock != null) { mock.Dispose(); }
        }
    }

    private static int Run(string ip, ushort port, bool activate, out string message, out int flag, out string serial)
    {
        var request = new Request
        {
            Ip = Fixed(ip, 16, Encoding.ASCII),
            Port = port,
            Activate = activate ? 1 : 0,
            TimeoutMs = 5000,
            VerifyMs = 15000,
            PollMs = 500,
            Notify = Marshal.GetFunctionPointerForDelegate(KeepAlive),
            Context = IntPtr.Zero
        };
        var result = new Result
        {
            Message = new byte[512],
            Serial = new byte[256]
        };
        controller_run(ref request, ref result);
        message = Text(result.Message);
        flag = result.Flag;
        int length = Array.IndexOf(result.Serial, (byte)0);
        serial = Encoding.ASCII.GetString(result.Serial, 0, length < 0 ? 0 : length);
        return result.Outcome;
    }
}