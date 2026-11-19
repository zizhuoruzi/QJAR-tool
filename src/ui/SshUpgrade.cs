using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static class SshUpgrade
{
    internal const string ControllerPackage = "control_update.tar.gz";
    internal const string PendantPackage = "teach_update.tar.gz";
    internal const string ControllerPath = "/usr/share/robot_control/" + ControllerPackage;
    internal const string PendantPath = "/usr/share/robot_teach/" + PendantPackage;
    internal const string DefaultPendantIp = "192.168.1.200";

    internal static bool TryGetIp(string value, out string ip)
    {
        ip = null;
        IPAddress address;
        if (string.IsNullOrWhiteSpace(value) || value.Split('.').Length != 4 ||
            !IPAddress.TryParse(value.Trim(), out address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }
        ip = address.ToString();
        // 拒绝缩写、八进制等与输入显示不一致的地址
        return string.Equals(ip, value.Trim(), StringComparison.Ordinal);
    }

    internal static void Upload(string ip, string package, bool pendant, CancellationToken cancellation,
        Action<int> progress, string ssh = null, string user = "root", string password = null)
    {
        string address;
        if (!TryGetIp(ip, out address))
        {
            throw new ArgumentException("请输入有效的 IPv4 地址。");
        }
        string target = pendant ? PendantPath : ControllerPath;
        if (ssh == null)
        {
            ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        }
        if (!File.Exists(ssh))
        {
            throw new FileNotFoundException("未找到 Windows OpenSSH 客户端。", ssh);
        }

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using (var source = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            if (source.Length < 18 || source.ReadByte() != 0x1f || source.ReadByte() != 0x8b || source.ReadByte() != 8)
            {
                throw new InvalidDataException("所选文件不是有效的 gzip 升级包。");
            }
            source.Position = 0;
            string checksum;
            using (var sha = SHA256.Create())
            {
                checksum = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").ToLowerInvariant();
            }
            source.Position = 0;
            string script = "set -e; dst='" + target + "'; umask 022; " +
                "tmp=$(mktemp \"$dst.qjar.XXXXXX\"); trap 'rm -f \"$tmp\"' EXIT; " +
                "trap 'exit 1' HUP INT TERM; cat > \"$tmp\"; " +
                "printf '%s  %s\\n' '" + checksum + "' \"$tmp\" | sha256sum -c - >/dev/null; " +
                "chmod 644 \"$tmp\"; mv -fT \"$tmp\" \"$dst\"; printf 'QJ_UPLOAD_OK\\n'";
            var info = new ProcessStartInfo(ssh,
                "-T -o StrictHostKeyChecking=accept-new -o ConnectTimeout=5 " +
                "-o ConnectionAttempts=1 -o ServerAliveInterval=5 -o ServerAliveCountMax=2 " +
                (SshClient.HasPassword(password)
                    ? "-o BatchMode=no -o NumberOfPasswordPrompts=1 " +
                      "-o PreferredAuthentications=publickey,password,keyboard-interactive "
                    : "-o BatchMode=yes ") +
                (string.IsNullOrWhiteSpace(user) ? "root" : user) + "@" + address + " \"" +
                script.Replace("\"", "\\\"") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (SshClient.HasPassword(password))
            {
                info.EnvironmentVariables["SSH_ASKPASS"] = SshClient.AskPassPath;
                info.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                info.EnvironmentVariables[SshClient.PasswordVariable] = password;
            }
            timeout.Token.ThrowIfCancellationRequested();
            using (Process process = Process.Start(info))
            using (timeout.Token.Register(delegate { Stop(process); }))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                IOException transferError = null;
                try
                {
                    using (Stream destination = process.StandardInput.BaseStream)
                    {
                        var buffer = new byte[128 * 1024];
                        int percent = 0;
                        int count;
                        while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            destination.Write(buffer, 0, count);
                            int next = (int)(source.Position * 99.0 / source.Length);
                            if (next != percent)
                            {
                                percent = next;
                                progress(percent);
                            }
                        }
                    }
                }
                catch (IOException exception)
                {
                    transferError = exception;
                }
                catch (OperationCanceledException)
                {
                    Stop(process);
                }
                process.WaitForExit();
                cancellation.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("SSH 上传超时。");
                }
                string details = error.GetAwaiter().GetResult().Trim();
                string result = output.GetAwaiter().GetResult().Trim();
                if (process.ExitCode != 0 || transferError != null || !result.EndsWith("QJ_UPLOAD_OK", StringComparison.Ordinal))
                {
                    throw new IOException("SSH 退出码 " + process.ExitCode.ToString(CultureInfo.InvariantCulture) +
                        "，未确认升级包已写入。" +
                        (details.Length == 0 ? "" : Environment.NewLine + details), transferError);
                }
            }
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
