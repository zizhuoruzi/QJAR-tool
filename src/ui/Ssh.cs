using System;
using System.Diagnostics;
using System.IO;

/* SSH 调用的统一入口：无密码时沿用老路径（BatchMode），
   有密码时让 OpenSSH 通过 SSH_ASKPASS 向 QJAskPass.exe 取口令，口令不出现在命令行。 */
internal static class SshClient
{
    internal const string PasswordVariable = "QJAR_SSH_PASSWORD";
    private const string AskPassName = "QJAskPass.exe";

    internal static string ExePath
    {
        get { return Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"); }
    }

    internal static string AskPassPath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AskPassName); }
    }

    internal static bool HasPassword(string password)
    {
        return !string.IsNullOrEmpty(password);
    }

    /* 有密码：允许交互并强制 askpass；无密码：保持原来的 BatchMode。 */
    internal static string Options(bool password)
    {
        const string common = "-T -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 " +
            "-o ConnectionAttempts=1 -o ServerAliveInterval=5 -o ServerAliveCountMax=2";
        return password
            ? common + " -o BatchMode=no -o NumberOfPasswordPrompts=1 " +
                "-o PreferredAuthentications=publickey,password,keyboard-interactive"
            : common + " -o BatchMode=yes";
    }

    internal static ProcessStartInfo Start(string user, string host, string password, string remoteCommand,
        bool redirectInput)
    {
        if (!File.Exists(ExePath))
        {
            throw new FileNotFoundException("未找到 Windows OpenSSH 客户端。", ExePath);
        }
        var info = new ProcessStartInfo(ExePath,
            Options(HasPassword(password)) + " -l " + Quote(user) + " -- " + Quote(host) + " " + Quote(remoteCommand))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (HasPassword(password))
        {
            if (!File.Exists(AskPassPath))
            {
                throw new FileNotFoundException("缺少 SSH 口令助手 QJAskPass.exe，请重新解压完整安装包。", AskPassPath);
            }
            info.EnvironmentVariables["SSH_ASKPASS"] = AskPassPath;
            info.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
            info.EnvironmentVariables[PasswordVariable] = password;
        }
        return info;
    }

    /* 联通测试：登录并回显标记，返回 null 表示成功。 */
    internal static string Test(string user, string host, string password, out int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        milliseconds = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(host)) { return "目标地址为空。"; }
            using (Process process = Process.Start(Start(user, host, password, "printf QJ_SSH_OK", false)))
            {
                if (process == null) { return "无法启动 ssh.exe。"; }
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(12000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    return "连接超时（12 秒）。";
                }
                milliseconds = (int)clock.ElapsedMilliseconds;
                if (process.ExitCode == 0 && output.Contains("QJ_SSH_OK")) { return null; }
                string details = (error.Length > 0 ? error : output).Trim();
                if (details.Length > 180) { details = details.Substring(0, 180) + "…"; }
                return details.Length == 0 ? "SSH 退出码 " + process.ExitCode + "。" : details;
            }
        }
        catch (Exception exception)
        {
            milliseconds = (int)clock.ElapsedMilliseconds;
            return exception.Message;
        }
    }

    internal static string Quote(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}