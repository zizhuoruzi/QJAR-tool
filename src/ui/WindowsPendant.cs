using System;
using System.Diagnostics;
using System.IO;

internal static class WindowsPendant
{
    internal const string FileName = "rbt_gui.exe";

    internal static string ResolvePath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) { throw new ArgumentException("请先选择 rbt_gui.exe。"); }
        string path = Path.GetFullPath(input.Trim().Trim('"'));
        if (Directory.Exists(path)) { path = Path.Combine(path, FileName); }
        if (!string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new FileNotFoundException("所选路径下找不到 rbt_gui.exe。", path);
        }
        return path;
    }

    internal static string Open(string input, bool restart)
    {
        string path = ResolvePath(input);
        if (FindOrStop(path, restart) && !restart) { return "所选虚拟示教器已在运行。"; }
        var start = new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path),
            UseShellExecute = true
        };
        using (Process launched = Process.Start(start))
        {
            if (launched == null || launched.HasExited) { throw new IOException("rbt_gui.exe 启动后未保持运行。"); }
        }
        return restart ? "所选虚拟示教器已重新打开。" : "所选虚拟示教器已打开。";
    }

    internal static bool IsRunning(string input)
    {
        return File.Exists(input) && FindOrStop(ResolvePath(input), false);
    }

    internal static string Toggle(string input)
    {
        string path = ResolvePath(input);
        return FindOrStop(path, true) ? "所选虚拟示教器已关闭。" : Open(path, false);
    }

    private static bool FindOrStop(string path, bool stop)
    {
        int session;
        using (Process current = Process.GetCurrentProcess()) { session = current.SessionId; }
        Process[] candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(FileName));
        bool found = false;
        try
        {
            foreach (Process candidate in candidates)
            {
                if (candidate.HasExited || candidate.SessionId != session) { continue; }
                string executable;
                try { executable = candidate.MainModule.FileName; }
                catch (InvalidOperationException) when (candidate.HasExited) { continue; }
                catch (System.ComponentModel.Win32Exception) when (candidate.HasExited) { continue; }
                if (!string.Equals(executable, path, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!stop) { return true; }
                found = true;
                try { candidate.Kill(); }
                catch (InvalidOperationException) when (candidate.HasExited) { }
                if (!candidate.WaitForExit(10000)) { throw new IOException("等待所选虚拟示教器退出超时。"); }
            }

            return found;
        }
        finally
        {
            foreach (Process candidate in candidates) { candidate.Dispose(); }
        }
    }
}
