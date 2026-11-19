using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

/* 管理员口令校验：PBKDF2-SHA256，60 万轮，口令按 UTF-16LE 编码。
   默认口令 888999；QJROBOT 作为固定后备口令，始终可用。
   在设置里改过口令后，以设置的为准，QJROBOT 后备仍然有效；
   忘记口令时删除 %LOCALAPPDATA%\QJAR\admin-password.dat 即可回到默认。 */
internal static class Admin
{
    internal const string DefaultPassword = "888999";
    internal const string BackupPassword = "QJROBOT";
    private const int Rounds = 600000;

    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("QJAR/AdminPassword/v1");
    private static readonly byte[] SessionPurpose = Encoding.ASCII.GetBytes("QJAR/AdminSession/v2");

    private static string PasswordPath { get { return Path.Combine(AddressSettings.SettingsDirectory, "admin-password.dat"); } }
    private static string SessionPath { get { return Path.Combine(AddressSettings.SettingsDirectory, "admin-session.dat"); } }

    internal static bool HasCustom
    {
        get
        {
            try { return File.Exists(PasswordPath); }
            catch (IOException) { return false; }
        }
    }

    internal static bool Matches(string value)
    {
        if (string.IsNullOrEmpty(value)) { return false; }
        if (string.Equals(value, BackupPassword, StringComparison.Ordinal)) { return true; }
        byte[] salt, expected;
        if (TryRead(out salt, out expected))
        {
            return FixedEquals(Hash(value, salt), expected);
        }
        return string.Equals(value, DefaultPassword, StringComparison.Ordinal);
    }

    /* 设置新口令；当前口令由界面先行校验。 */
    internal static void SetPassword(string value)
    {
        if (string.IsNullOrEmpty(value)) { throw new ArgumentException("口令不能为空。"); }
        var salt = new byte[16];
        using (var random = RandomNumberGenerator.Create()) { random.GetBytes(salt); }
        byte[] hash = Hash(value, salt);
        byte[] payload = new byte[16 + 32];
        Buffer.BlockCopy(salt, 0, payload, 0, 16);
        Buffer.BlockCopy(hash, 0, payload, 16, 32);
        AddressSettings.WriteAtomic(PasswordPath, ProtectedData.Protect(payload, Purpose,
            DataProtectionScope.CurrentUser));
        Forget();
    }

    /* 登录状态：把这台机器当前的口令身份封存起来，改口令或换用户后自动失效。 */
    internal static bool IsRemembered()
    {
        try
        {
            byte[] session = ProtectedData.Unprotect(File.ReadAllBytes(SessionPath), SessionPurpose,
                DataProtectionScope.CurrentUser);
            return FixedEquals(session, Identity());
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (CryptographicException) { return false; }
    }

    internal static void Remember()
    {
        AddressSettings.WriteAtomic(SessionPath, ProtectedData.Protect(Identity(), SessionPurpose,
            DataProtectionScope.CurrentUser));
    }

    internal static void Forget()
    {
        try { File.Delete(SessionPath); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    private static byte[] Identity()
    {
        byte[] salt, hash;
        if (TryRead(out salt, out hash)) { return hash; }
        return Hash(DefaultPassword, Encoding.ASCII.GetBytes("QJAR/DefaultSalt"));
    }

    private static bool TryRead(out byte[] salt, out byte[] hash)
    {
        salt = null;
        hash = null;
        try
        {
            if (!File.Exists(PasswordPath)) { return false; }
            byte[] payload = ProtectedData.Unprotect(File.ReadAllBytes(PasswordPath), Purpose,
                DataProtectionScope.CurrentUser);
            if (payload.Length != 48) { return false; }
            salt = new byte[16];
            hash = new byte[32];
            Buffer.BlockCopy(payload, 0, salt, 0, 16);
            Buffer.BlockCopy(payload, 16, hash, 0, 32);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (CryptographicException) { return false; }
    }

    private static byte[] Hash(string value, byte[] salt)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        var result = new byte[32];
        IntPtr algorithm;
        try
        {
            if (BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 8) != 0)
            {
                throw new CryptographicException("无法初始化口令校验。");
            }
            try
            {
                if (BCryptDeriveKeyPBKDF2(algorithm, bytes, bytes.Length, salt, salt.Length,
                    Rounds, result, result.Length, 0) != 0)
                {
                    throw new CryptographicException("口令校验失败。");
                }
            }
            finally { BCryptCloseAlgorithmProvider(algorithm, 0); }
        }
        finally { Array.Clear(bytes, 0, bytes.Length); }
        return result;
    }

    private static bool FixedEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length) { return false; }
        int difference = 0;
        for (int i = 0; i < left.Length; i++) { difference |= left[i] ^ right[i]; }
        return difference == 0;
    }

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, uint flags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptDeriveKeyPBKDF2(IntPtr algorithm, byte[] password, int passwordSize,
        byte[] salt, int saltSize, ulong rounds, byte[] result, int resultSize, uint flags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
}