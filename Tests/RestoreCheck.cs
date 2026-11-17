using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class RestoreCheck
{
    private delegate bool WindowCallback(IntPtr window, IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    private static IntPtr Find(int process)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr context)
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != process) { return true; }
            var title = new StringBuilder(128);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "QJAR 工具") { return true; }
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("用法：RestoreCheck.exe <工具exe> <截图目录> [次数] [页面] [maximized] [api]"); return 2; }
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        System.Windows.Forms.Form backdrop = null;
        using (var ready = new ManualResetEvent(false))
        {
            var desktop = new Thread(delegate()
            {
                backdrop = new System.Windows.Forms.Form
                {
                    Text = "恢复检查背景", BackColor = Color.White,
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Bounds = System.Windows.Forms.SystemInformation.VirtualScreen
                };
                backdrop.Shown += delegate { ready.Set(); };
                System.Windows.Forms.Application.Run(backdrop);
                backdrop.Dispose();
            });
            desktop.SetApartmentState(ApartmentState.STA);
            desktop.Start();
            ready.WaitOne();
            try { return Run(args); }
            finally
            {
                backdrop.BeginInvoke((Action)backdrop.Close);
                desktop.Join();
            }
        }
    }

    private static int Run(string[] args)
    {
        int cycles = args.Length > 2 ? int.Parse(args[2]) : 10;
        string page = args.Length > 3 ? args[3] : "register";
        Directory.CreateDirectory(args[1]);
        string profile = Path.Combine(Path.GetFullPath(args[1]), "profile");
        string settings = Path.Combine(profile, "QJAR");
        Directory.CreateDirectory(settings);
        string userSettings = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QJAR");
        foreach (string name in new[] { "settings.ini", "build.ini" })
        {
            string source = Path.Combine(userSettings, name);
            if (File.Exists(source)) { File.Copy(source, Path.Combine(settings, name), true); }
        }
        var start = new ProcessStartInfo(Path.GetFullPath(args[0]), "--page=" + page)
            { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        // 隔离连接与工程设置，避免旧版本回归覆盖用户配置。
        start.EnvironmentVariables["LOCALAPPDATA"] = profile;
        using (var process = Process.Start(start))
        {
            IntPtr window = IntPtr.Zero;
            var startup = Stopwatch.StartNew();
            while (window == IntPtr.Zero && startup.ElapsedMilliseconds < 10000 && !process.HasExited)
            {
                window = Find(process.Id);
                Thread.Sleep(20);
            }
            if (window == IntPtr.Zero) { if (!process.HasExited) { process.Kill(); } throw new Exception("测试窗口未出现"); }
            ShowWindow(window, 9);
            SetForegroundWindow(window);
            Thread.Sleep(600);
            if (args.Length > 4 && args[4] == "maximized")
            {
                SendMessage(window, 0x0112, new IntPtr(0xF030), IntPtr.Zero);
                Thread.Sleep(400);
            }
            using (Bitmap baseline = Capture(window))
            {
                Color body = baseline.GetPixel(baseline.Width * 3 / 4, 5);
                if (Math.Abs(body.R - 241) > 3 || Math.Abs(body.G - 244) > 3 || Math.Abs(body.B - 247) > 3)
                {
                    baseline.Save(Path.Combine(args[1], "unexpected-start.png"));
                    SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine("FAIL 未抓到工具的实际画面");
                    return 1;
                }
            }
            var children = new List<IntPtr>();
            for (IntPtr child = GetWindow(window, 5); child != IntPtr.Zero; child = GetWindow(child, 2))
            {
                Rect bounds;
                if (GetClientRect(child, out bounds) && bounds.Right > 0 && bounds.Bottom > 0) { children.Add(child); }
            }
            int blackFrames = 0, frames = 0, zeroLayouts = 0;
            int phase = 0;
            Exception captureError = null;
            bool running = true;
            var recorder = new Thread(delegate()
            {
                try
                {
                    while (Volatile.Read(ref running))
                    {
                        int capturePhase = Volatile.Read(ref phase);
                        if (capturePhase != 2) { Thread.Sleep(2); continue; }
                        if (IsIconic(window)) { Thread.Sleep(2); continue; }
                        using (Bitmap bitmap = Capture(window))
                        {
                            if (bitmap == null) { Thread.Sleep(2); continue; }
                            if (Volatile.Read(ref phase) != 2) { continue; }
                            int black = 0, total = 0;
                            for (int y = 12; y < bitmap.Height - 12; y += 12)
                            for (int x = 12; x < bitmap.Width - 12; x += 12)
                            {
                                Color color = bitmap.GetPixel(x, y);
                                total++;
                                if (color.R < 3 && color.G < 3 && color.B < 3) { black++; }
                            }
                            frames++;
                            // 必须覆盖恢复动画中的首帧；等窗口稳定后截图会漏掉短暂黑块。
                            if (total > 0 && black > total / 10)
                            {
                                blackFrames++;
                                if (blackFrames <= 10) { bitmap.Save(Path.Combine(args[1], "black-" + blackFrames + ".png")); }
                            }
                        }
                        Thread.Sleep(2);
                    }
                }
                catch (Exception error) { captureError = error; }
            });
            recorder.IsBackground = true;
            recorder.Start();
            try
            {
                for (int i = 0; i < cycles; i++)
                {
                    Volatile.Write(ref phase, 1);
                    SendMessage(window, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
                    foreach (IntPtr child in children)
                    {
                        Rect bounds;
                        if (!GetClientRect(child, out bounds) || bounds.Right <= 0 || bounds.Bottom <= 0)
                        { zeroLayouts++; break; }
                    }
                    Thread.Sleep(100);
                    Volatile.Write(ref phase, 2);
                    if (Array.IndexOf(args, "api") >= 0) { ShowWindow(window, 9); }
                    else { SendMessage(window, 0x0112, new IntPtr(0xF120), IntPtr.Zero); }
                    SetForegroundWindow(window);
                    Thread.Sleep(400);
                    using (Bitmap restored = Capture(window))
                    {
                        Color body = restored.GetPixel(restored.Width * 3 / 4, 5);
                        if (Math.Abs(body.R - 241) > 3 || Math.Abs(body.G - 244) > 3 || Math.Abs(body.B - 247) > 3)
                        { throw new Exception("恢复后工具画面未显示"); }
                    }
                }
            }
            finally
            {
                Volatile.Write(ref running, false);
                recorder.Join();
                SendMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!process.WaitForExit(2000)) { process.Kill(); }
            }
            if (captureError != null) { throw new Exception("恢复画面抓取失败", captureError); }
            bool passed = blackFrames == 0 && zeroLayouts == 0 && frames > cycles && children.Count > 0;
            Console.WriteLine("{0} {1}: {2} 次恢复，{3} 帧，{4} 帧黑块，{5} 次零尺寸布局", passed ? "PASS" : "FAIL", page, cycles, frames, blackFrames, zeroLayouts);
            return passed ? 0 : 1;
        }
    }

    private static Bitmap Capture(IntPtr window)
    {
        DwmFlush();
        Rect rect;
        GetClientRect(window, out rect);
        var point = new Point();
        ClientToScreen(window, ref point);
        if (rect.Right < 20 || rect.Bottom < 20) { return null; }
        var bitmap = new Bitmap(rect.Right, rect.Bottom, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                IntPtr source = GetDC(IntPtr.Zero), target = graphics.GetHdc();
                try
                {
                    // CAPTUREBLT 包含分层窗口，避免把未捕获到窗口误判为没有黑帧。
                    if (!BitBlt(target, 0, 0, rect.Right, rect.Bottom, source, point.X, point.Y, 0x40CC0020))
                    { throw new System.ComponentModel.Win32Exception(); }
                }
                finally { graphics.ReleaseHdc(target); ReleaseDC(IntPtr.Zero, source); }
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
