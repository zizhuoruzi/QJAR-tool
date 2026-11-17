using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

/* 按物理像素抓取目标窗口：本程序自身声明 PerMonitorV2，PrintWindow 不会被 DPI 虚拟化。
   用法：Capture.exe <exe> <out.png> [arguments] */
internal static class CaptureProgram
{
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private struct Point { public int X, Y; }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: Capture.exe <exe> <out.png> [arguments]");
            return 2;
        }
        var info = new ProcessStartInfo(args[0]);
        if (args.Length > 2) { info.Arguments = args[2]; }
        using (Process process = Process.Start(info))
        {
            IntPtr window = IntPtr.Zero;
            for (int i = 0; i < 150 && window == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) { window = process.MainWindowHandle; }
            }
            if (window == IntPtr.Zero)
            {
                Console.WriteLine("窗口未出现");
                return 1;
            }
            Thread.Sleep(1200);
            Rect client, frame;
            GetClientRect(window, out client);
            GetWindowRect(window, out frame);
            var origin = new Point();
            ClientToScreen(window, ref origin);
            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;
            int offsetX = origin.X - frame.Left;
            int offsetY = origin.Y - frame.Top;
            int frameWidth = frame.Right - frame.Left;
            int frameHeight = frame.Bottom - frame.Top;
            using (var full = new Bitmap(frameWidth, frameHeight))
            {
                using (Graphics graphics = Graphics.FromImage(full))
                {
                    IntPtr dc = graphics.GetHdc();
                    PrintWindow(window, dc, 2);
                    graphics.ReleaseHdc(dc);
                }
                using (var cropped = new Bitmap(width, height))
                using (Graphics target = Graphics.FromImage(cropped))
                {
                    target.DrawImage(full, new Rectangle(0, 0, width, height),
                        new Rectangle(offsetX, offsetY, width, height), GraphicsUnit.Pixel);
                    cropped.Save(args[1]);
                }
            }
            Console.WriteLine(args[1] + " " + width + "x" + height);
            process.CloseMainWindow();
            Thread.Sleep(300);
            if (!process.HasExited) { process.Kill(); }
        }
        return 0;
    }
}