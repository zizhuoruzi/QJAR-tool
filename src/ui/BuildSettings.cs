using System;
using System.IO;
using System.Text;

/* 编译页的少量本机设置：上次的工程目录、WSL 发行版、是否打包。
   与连接设置分开存，避免编译设置影响现场工具的行为。 */
internal static class BuildSettings
{
    internal static string OverridePath;

    private static string SettingsPath
    {
        get
        {
            if (!string.IsNullOrEmpty(OverridePath)) { return OverridePath; }
            return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QJAR", "build.ini");
        }
    }

    internal static void Load(out string project, out string distro, out bool package)
    {
        project = string.Empty;
        distro = "Ubuntu";
        package = true;
        try
        {
            if (!File.Exists(SettingsPath)) { return; }
            foreach (string line in File.ReadAllLines(SettingsPath))
            {
                if (line.StartsWith("project=", StringComparison.Ordinal))
                {
                    project = line.Substring(8).Trim();
                }
                else if (line.StartsWith("distro=", StringComparison.Ordinal))
                {
                    string value = line.Substring(7).Trim();
                    if (value.Length > 0) { distro = value; }
                }
                else if (line.StartsWith("package=", StringComparison.Ordinal))
                {
                    package = line.Substring(8).Trim() != "0";
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static void Save(string project, string distro, bool package)
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string text = "[build]\r\nproject=" + (project ?? string.Empty) + "\r\ndistro=" +
                (distro ?? string.Empty) + "\r\npackage=" + (package ? "1" : "0") + "\r\n";
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path)) { File.Replace(temp, path, null, true); }
                else { File.Move(temp, path); }
            }
            finally
            {
                try { if (File.Exists(temp)) { File.Delete(temp); } }
                catch (IOException) { }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}