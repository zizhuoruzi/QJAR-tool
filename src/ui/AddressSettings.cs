using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class AddressSettings
{
    /* 测试程序用它指向临时文件，避免动到用户的设置。 */
    internal static string OverridePath;

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

    internal static string[] Load()
    {
        string host = "", port = "", timeout = "", encoding = "", admin = "";
        try
        {
            foreach (string line in File.ReadAllLines(SettingsPath))
            {
                if (line.StartsWith("ip=", StringComparison.Ordinal))
                {
                    host = line.Substring(3);
                }
                if (line.StartsWith("port=", StringComparison.Ordinal))
                {
                    port = line.Substring(5);
                }
                if (line.StartsWith("timeout=", StringComparison.Ordinal))
                {
                    timeout = line.Substring(8);
                }
                if (line.StartsWith("encoding=", StringComparison.Ordinal))
                {
                    encoding = line.Substring(9);
                }
                if (line.StartsWith("admin=", StringComparison.Ordinal))
                {
                    admin = line.Substring(6);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        IPAddress ip;
        int number;
        bool validAddress = TryParse(host, port, out ip, out number);
        int milliseconds;
        return new[] { validAddress ? host : "192.168.2.100", validAddress ? port : "7625",
            TryParseTimeout(timeout, out milliseconds) ? timeout : "5000",
            encoding == "GBK" ? "GBK" : "UTF-8",
            admin == "1" ? "1" : "0" };
    }

    internal static void Save(IPAddress ip, int port, int timeout, string encoding, bool admin)
    {
        string path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.ASCII.GetBytes("[connection]\r\nip=" + ip + "\r\nport=" +
                    port.ToString(CultureInfo.InvariantCulture) + "\r\ntimeout=" +
                    timeout.ToString(CultureInfo.InvariantCulture) + "\r\nencoding=" + encoding +
                    "\r\nadmin=" + (admin ? "1" : "0") + "\r\n");
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
