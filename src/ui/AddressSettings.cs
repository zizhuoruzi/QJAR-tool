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
        internal string Ip2 = "192.168.1.200";
        internal string Port2 = "7625";
        internal string Timeout = "5000";
        internal string Encoding = "UTF-8";
        internal bool Admin;
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
                    case "admin": values.Admin = value == "1"; break;
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
            values.Ip2 = "192.168.1.200";
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
                    "\r\nadmin=" + (values.Admin ? "1" : "0") +
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);
}