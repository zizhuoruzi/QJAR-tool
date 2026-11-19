using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System;
using System.IO;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            Report(e.Exception);
        };
        try
        {
            int page = -1;
            bool guide = false;
            foreach (string arg in args)
            {
                if (arg == "--page=command") { page = 1; }
                if (arg == "--page=build") { page = 2; }
                if (arg == "--page=register") { page = 0; }
                if (arg == "--page=upgrade") { page = 3; }
                if (arg == "--guide") { guide = true; }
            }
            if (guide)
            {
                string project, distribution;
                bool package;
                BuildSettings.Load(out project, out distribution, out package);
                Application.Run(new WslGuideForm(distribution));
                return;
            }
            string profile = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            string key;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                key = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(profile))).Replace('/', '_');
            }
            using (var instance = new Mutex(true, @"Local\QJAR-ControllerTool-" + key, out bool created))
            {
                if (created) { Application.Run(new MainForm(page)); }
                else { ActivateExistingWindow(); }
            }
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    private static void ActivateExistingWindow()
    {
        using (Process current = Process.GetCurrentProcess())
        {
            foreach (Process candidate in Process.GetProcessesByName(current.ProcessName))
            {
                using (candidate)
                {
                    if (candidate.Id == current.Id || candidate.SessionId != current.SessionId) { continue; }
                    IntPtr window = IntPtr.Zero;
                    for (int attempt = 0; attempt < 15 && !candidate.HasExited; attempt++)
                    {
                        candidate.Refresh();
                        window = candidate.MainWindowHandle;
                        if (window == IntPtr.Zero) { window = FindHiddenMainWindow(candidate.Id); }
                        if (window != IntPtr.Zero) { break; }
                        Thread.Sleep(100);
                    }
                    if (window == IntPtr.Zero) { continue; }
                    if (IsIconic(window)) { ShowWindowAsync(window, 9); }
                    else if (!IsWindowVisible(window)) { ShowWindowAsync(window, 5); }
                    SetForegroundWindow(window);
                    return;
                }
            }
        }
    }

    private static IntPtr FindHiddenMainWindow(int processId)
    {
        IntPtr mainWindow = IntPtr.Zero;
        var title = new StringBuilder(64);
        EnumWindows(delegate(IntPtr window, IntPtr unused)
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != (uint)processId) { return true; }
            GetWindowText(window, title, title.Capacity);
            if (!string.Equals(title.ToString(), MainForm.WindowTitle, StringComparison.Ordinal)) { return true; }
            mainWindow = window;
            return false;
        }, IntPtr.Zero);
        return mainWindow;
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr unused);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr unused);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    /* 启动或绘制异常时留下痕迹，避免“窗口没出现”无从查起。 */
    private static void Report(Exception error)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "QJAR-error.log");
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                error + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}