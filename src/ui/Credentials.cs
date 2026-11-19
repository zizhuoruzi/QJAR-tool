using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

/* SSH 凭据：用户名与密码。密码属于敏感信息，整体用 Windows DPAPI 按当前用户封存，
   换用户或换机器解不开，需要重新填写。 */
internal static class Credentials
{
    internal sealed class Values
    {
        internal string ControllerUser = "root";
        internal string ControllerPassword = "";
        internal string PendantUser = "root";
        internal string PendantPassword = "";
        internal string UbuntuUser = "qjrobot";
        internal string UbuntuPassword = "";
    }

    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("QJAR/SshCredentials/v1");

    private static string Path_
    {
        get { return System.IO.Path.Combine(AddressSettings.SettingsDirectory, "ssh-credentials.dat"); }
    }

    internal static Values Load()
    {
        var values = new Values();
        try
        {
            if (!File.Exists(Path_)) { return values; }
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(Path_), Purpose, DataProtectionScope.CurrentUser);
            foreach (string line in Encoding.UTF8.GetString(plain).Split('\n'))
            {
                int split = line.IndexOf('=');
                if (split <= 0) { continue; }
                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                switch (key)
                {
                    case "controller.user": values.ControllerUser = value; break;
                    case "controller.password": values.ControllerPassword = value; break;
                    case "pendant.user": values.PendantUser = value; break;
                    case "pendant.password": values.PendantPassword = value; break;
                    case "ubuntu.user": values.UbuntuUser = value; break;
                    case "ubuntu.password": values.UbuntuPassword = value; break;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (CryptographicException) { }
        return Normalize(values);
    }

    private static Values Normalize(Values values)
    {
        if (string.IsNullOrWhiteSpace(values.ControllerUser)) { values.ControllerUser = "root"; }
        if (string.IsNullOrWhiteSpace(values.PendantUser)) { values.PendantUser = "root"; }
        if (string.IsNullOrWhiteSpace(values.UbuntuUser)) { values.UbuntuUser = "qjrobot"; }
        return values;
    }

    internal static void Save(Values values)
    {
        Normalize(values);
        string text = "controller.user=" + values.ControllerUser + "\n" +
            "controller.password=" + values.ControllerPassword + "\n" +
            "pendant.user=" + values.PendantUser + "\n" +
            "pendant.password=" + values.PendantPassword + "\n" +
            "ubuntu.user=" + values.UbuntuUser + "\n" +
            "ubuntu.password=" + values.UbuntuPassword + "\n";
        byte[] sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Purpose,
            DataProtectionScope.CurrentUser);
        AddressSettings.WriteAtomic(Path_, sealedBytes);
    }
}