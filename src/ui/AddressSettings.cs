using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

/* 连接设置：两套控制器地址（各带端口）＋超时、编码、管理员登录状态。
   地址按档位保存，切换只影响当前使用哪一档，不改动另一档。 */
internal static class AddressSettings
{
    /* 测试程序用它指向临时文件，避免动到用户的设置。 */
    internal static string OverridePath;

    internal sealed class Values
    {
        internal string Ip1 = "192.168.2.100";
        internal string Port1 = "7625";
        internal string Ip2 = "192.168.247.130";
        internal string Port2 = "7625";
        internal string Timeout = "5000";
        internal string Encoding = "UTF-8";
        internal int Page = 2;
        internal int BuildMode;
        internal bool VirtualPendant;
        internal int Profile;

        internal string Ip { get { return Profile == 1 ? Ip2 : Ip1; } }
        internal string Port { get { return Profile == 1 ? Port2 : Port1; } }
    }

    private static string SettingsPath
    {
        get
        {
            if (!string.IsNullOrEmpty(OverridePath)) { return OverridePath; }
            return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QJAR", "settings.ini");
        }
    }

    internal static string SettingsDirectory { get { return Path.GetDirectoryName(SettingsPath); } }

    internal static bool TryParse(string host, string port, out IPAddress ip, out int number)
    {
        number = 0;
        return IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
            host.Split('.').Length == 4 && IsDecimalAddress(host) &&
            int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
            number >= 1 && number <= 65535;
    }

    private static bool IsDecimalAddress(string host)
    {
        foreach (string part in host.Split('.'))
        {
            int value;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value < 0 || value > 255 || part != value.ToString(CultureInfo.InvariantCulture))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool TryParseTimeout(string text, out int timeout)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) &&
            timeout >= 1 && timeout <= 600000;
    }

    internal static Values Load()
    {
        var values = new Values();
        bool hasBuildMode = false;
        try
        {
            if (!File.Exists(SettingsPath)) { return Normalize(values); }
            foreach (string line in File.ReadAllLines(SettingsPath))
            {
                int split = line.IndexOf('=');
                if (split <= 0) { continue; }
                string key = line.Substring(0, split);
                string value = line.Substring(split + 1).Trim();
                switch (key)
                {
                    case "ip": values.Ip1 = value; break;
                    case "port": values.Port1 = value; break;
                    case "ip2": values.Ip2 = value; break;
                    case "port2": values.Port2 = value; break;
                    case "timeout": values.Timeout = value; break;
                    case "encoding": values.Encoding = value; break;
                    case "page": int page; if (int.TryParse(value, out page) && page >= 0 && page <= 3) { values.Page = page; } break;
                    case "buildMode": hasBuildMode = true; values.BuildMode = value == "1" ? 1 : 0; break;
                    case "virtualPendant": values.VirtualPendant = value == "1"; break;
                    case "profile": values.Profile = value == "1" ? 1 : 0; break;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        // 旧版的第二档默认是实体示教器地址，不能拿它连接 Ubuntu。
        if (!hasBuildMode && values.Ip2 == "192.168.1.200") { values.Ip2 = "192.168.247.130"; }
        return Normalize(values);
    }

    /* 旧配置只有 ip/port：第一档沿用，第二档给默认值；非法值一律回退。 */
    private static Values Normalize(Values values)
    {
        IPAddress ip;
        int port;
        if (!TryParse(values.Ip1, values.Port1, out ip, out port))
        {
            values.Ip1 = "192.168.2.100";
            values.Port1 = "7625";
        }
        if (!TryParse(values.Ip2, values.Port2, out ip, out port))
        {
            values.Ip2 = "192.168.247.130";
            values.Port2 = "7625";
        }
        int timeout;
        if (!TryParseTimeout(values.Timeout, out timeout)) { values.Timeout = "5000"; }
        if (values.Encoding != "GBK") { values.Encoding = "UTF-8"; }
        values.Profile = values.Profile == 1 ? 1 : 0;
        return values;
    }

    internal static void Save(Values values)
    {
        string path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                string text = "[connection]\r\nip=" + values.Ip1 + "\r\nport=" + values.Port1 +
                    "\r\nip2=" + values.Ip2 + "\r\nport2=" + values.Port2 +
                    "\r\ntimeout=" + values.Timeout + "\r\nencoding=" + values.Encoding +
                    "\r\npage=" + values.Page + "\r\nbuildMode=" + values.BuildMode +
                    "\r\nvirtualPendant=" + (values.VirtualPendant ? "1" : "0") +
                    "\r\nprofile=" + (values.Profile == 1 ? "1" : "0") + "\r\n";
                byte[] bytes = Encoding.ASCII.GetBytes(text);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            if (!MoveFileExW(temp, path, 9))
            {
                throw new IOException("保存连接地址失败。", new System.ComponentModel.Win32Exception());
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string BuildSourcePath { get { return Path.Combine(SettingsDirectory, "build-source.txt"); } }

    internal static string LoadBuildSource()
    {
        try
        {
            string path = File.ReadAllText(BuildSourcePath, Encoding.UTF8);
            return GetBuildSourceRoot(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return null;
    }

    internal static string GetBuildSourceRoot(string path)
    {
        if (!Path.IsPathRooted(path)) { return null; }
        var directory = new DirectoryInfo(path);
        if (string.Equals(directory.Name, "cx-v3.0", StringComparison.OrdinalIgnoreCase))
        {
            return directory.Exists && directory.Parent != null ? directory.Parent.FullName : null;
        }
        return Directory.Exists(Path.Combine(directory.FullName, "cx-v3.0")) ? directory.FullName : null;
    }

    internal static void SaveBuildSource(string path)
    {
        WriteAtomic(BuildSourcePath, Encoding.UTF8.GetBytes(path));
    }

    private static string PendantPath { get { return Path.Combine(SettingsDirectory, "virtual-pendant.txt"); } }

    internal static string LoadPendantPath()
    {
        try { return File.ReadAllText(PendantPath, Encoding.UTF8); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    internal static void SavePendantPath(string path)
    {
        WriteAtomic(PendantPath, Encoding.UTF8.GetBytes(path));
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            if (!MoveFileExW(temp, path, 9))
            {
                throw new IOException("保存设置失败。", new System.ComponentModel.Win32Exception());
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);
}