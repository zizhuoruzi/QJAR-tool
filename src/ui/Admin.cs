using System.IO;
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

/* 管理员口令校验：PBKDF2-SHA256，60 万轮，口令按 UTF-16LE 编码，
   与旧注册工具 / 命令工具的校验方式一致，只是把口令换成了 QJROBOT。 */
internal static class Admin
{
    private static readonly byte[] Salt = Convert.FromBase64String("ONGHsJwqPIN7aW82X7NyIA==");
    private static readonly byte[] Expected = Convert.FromBase64String("Qvj/p0LUeAJAVhKEW5LCMccbrzAf2mmulzrknhi/hoI=");

    internal static bool Matches(string value)
    {
        if (string.IsNullOrEmpty(value)) { return false; }
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        var actual = new byte[Expected.Length];
        IntPtr algorithm;
        try
        {
            if (BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 8) != 0) { return false; }
            try
            {
                if (BCryptDeriveKeyPBKDF2(algorithm, bytes, bytes.Length, Salt, Salt.Length,
                    600000, actual, actual.Length, 0) != 0)
                {
                    return false;
                }
                int difference = 0;
                for (int i = 0; i < actual.Length; i++) { difference |= actual[i] ^ Expected[i]; }
                return difference == 0;
            }
            finally
            {
                BCryptCloseAlgorithmProvider(algorithm, 0);
            }
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
            Array.Clear(actual, 0, actual.Length);
        }
    }

    private static readonly byte[] SessionPurpose = Encoding.ASCII.GetBytes("QJAR/AdminSession/v1");

    private static string SessionPath { get { return Path.Combine(AddressSettings.SettingsDirectory, "admin-session.dat"); } }

    internal static bool IsRemembered()
    {
        try
        {
            byte[] protectedSession = File.ReadAllBytes(SessionPath);
            byte[] session = ProtectedData.Unprotect(protectedSession, SessionPurpose, DataProtectionScope.CurrentUser);
            return SameSession(session);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (CryptographicException) { return false; }
    }

    internal static void Remember()
    {
        byte[] session = ProtectedData.Protect(Expected, SessionPurpose, DataProtectionScope.CurrentUser);
        AddressSettings.WriteAtomic(SessionPath, session);
    }

    internal static void Forget()
    {
        try { File.Delete(SessionPath); }
        catch (DirectoryNotFoundException) { }
    }

    private static bool SameSession(byte[] session)
    {
        if (session.Length != Expected.Length) { return false; }
        int diff = 0;
        for (int i = 0; i < session.Length; i++) { diff |= session[i] ^ Expected[i]; }
        return diff == 0;
    }

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, uint flags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptDeriveKeyPBKDF2(IntPtr algorithm, byte[] password, int passwordSize,
        byte[] salt, int saltSize, ulong rounds, byte[] result, int resultSize, uint flags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
}