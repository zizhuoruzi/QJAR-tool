using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal sealed class ControllerBuild
{
    private const string ScriptName = "ETHERCAT_X86.sh";
    private const string StagePattern = @"\A/tmp/qj-controller-build\.[A-Za-z0-9]{8}\z";
    private readonly string sshPath = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
    private readonly string sshOptions;
    private readonly Action<string> log;

    internal ControllerBuild(string host, string user, int port, Action<string> log)
    {
        sshOptions = "-T -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 " +
            "-o ServerAliveInterval=15 -o ServerAliveCountMax=3 -p " + port.ToString(CultureInfo.InvariantCulture) +
            " -l " + user + " -- " + host + " ";
        this.log = log;
    }

    internal string[] ListProjects()
    {
        var names = new List<string>();
        const string command = "for dir in \"$HOME/expspace\"/[0-9]*; do " +
            "[ -d \"$dir\" ] && [ ! -L \"$dir\" ] || continue; " +
            "printf 'QJDIR:'; printf '%s' \"${dir##*/}\" | base64 -w0 || exit 1; printf '\\n'; done";
        Ssh("读取 Ubuntu 项目目录", command, null, delegate(string line)
        {
            if (!line.StartsWith("QJDIR:", StringComparison.Ordinal)) { return; }
            string name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(line.Substring(6)));
            if (Regex.IsMatch(name, @"\A[0-9][^/\x00\r\n]*\z")) { names.Add(name); }
        });
        return names.ToArray();
    }

    internal void CreateProject(string name, string preferredNumber)
    {
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"\A[^0-9/\x00-\x1f][^/\x00-\x1f]*\z") ||
            Encoding.UTF8.GetByteCount(name) > 240)
        {
            throw new ArgumentException("无法从 Windows 路径得到有效的 Ubuntu 项目名称。");
        }
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
        string number = Regex.IsMatch(preferredNumber, @"\A[0-9]{1,9}\z") ? preferredNumber : "";
        string command = "set -e; root=\"$HOME/expspace\"; [ ! -L \"$root\" ]; mkdir -p -- \"$root\"; " +
            "exec 9>\"$root/.qj-project-create.lock\"; flock -x 9; " +
            "name=$(printf '%s' " + ShellArgument(token) + " | base64 -d); number=" + ShellArgument(number) + "; " +
            "declare -A occupied; shopt -s nullglob nocasematch; " +
            "for item in \"$root\"/[0-9]*; do entry=${item##*/}; " +
            "if [[ $entry =~ ^([0-9]+) ]]; then occupied[${BASH_REMATCH[1]}]=1; fi; " +
            "[ -d \"$item\" ] && [ ! -L \"$item\" ] || continue; " +
            "label=$(printf '%s' \"$entry\" | sed -r 's/^[0-9]+([[:space:]_.-]|、)*//; s/[[:space:]]+$//'); " +
            "if [[ $label == \"$name\" ]]; then printf '已存在同名项目：%s\\n' \"$entry\"; exit 0; fi; done; " +
            "if [[ -z $number || -n ${occupied[$number]+x} ]]; then " +
            "i=1; while :; do printf -v number '%02d' \"$i\"; " +
            "[[ -z ${occupied[$number]+x} ]] && break; i=$((i+1)); done; fi; " +
            "mkdir -- \"$root/$number$name\"; printf '已新建 Ubuntu 项目目录：%s\\n' \"$number$name\"";
        Ssh("新建 Ubuntu 项目目录", command, null, log);
    }

    internal void Build(string sourceRoot, string project, string driver, bool startAfter, bool packageAfter,
        Func<string> requestPassword, Func<string, bool> retryOccupiedPackage, Action<string> consoleReady, Action<string> packageSaved)
    {
        string localStage = Path.Combine(Path.GetTempPath(), "QJAR-ControllerBuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localStage);
        string archive = Path.Combine(localStage, "cx-v3.0.zip");
        string stage = null;
        try
        {
            bool driverChanged = SyncSourceDriver(sourceRoot, driver);
            log(driverChanged ? "已将 Windows Makefile 的 DRIVER 改为 " + driver + "。"
                : "已确认 Windows Makefile 的 DRIVER 为 " + driver + "。");
            log("正在压缩 " + Path.Combine(sourceRoot, "cx-v3.0") + " …");
            Run("压缩源码", Path.Combine(Environment.SystemDirectory, "tar.exe"),
                "-a -cf " + WindowsArgument(archive) + " cx-v3.0", sourceRoot, null, log);

            stage = CreateStage();

            log("正在上传源码包（" + (new FileInfo(archive).Length / 1048576.0).ToString("F1") + " MB）…");
            using (var input = File.OpenRead(archive))
            {
                Ssh("上传源码包", "cat > " + stage + "/cx-v3.0.zip", input, log);
            }
            try
            {
                File.Delete(archive);
                log("源码包已上传，已删除 Windows 临时压缩包。");
            }
            catch (IOException) { log("Windows 临时压缩包暂时无法删除，结束时重试。"); }
            catch (UnauthorizedAccessException) { log("Windows 临时压缩包暂时无法删除，结束时重试。"); }
            UploadScript(stage);

            log("正在编译 ~/expspace/" + project + "/cx-v3.0 …");
            // 持有临时目录锁，避免断线后的清理与仍在进行的编译并发。
            string archiveEnv = "export QJ_CONTROLLER_BUILD_ARCHIVE=" + stage + "/cx-v3.0.zip " +
                "QJ_CONTROLLER_BUILD_RECEIPT=" + stage + "/build-receipt; ";
            RunWithSudoPrompt("Ubuntu 编译", archiveEnv + ScriptCommand(stage, project, "--stop-and-build", driver), requestPassword);
            log("编译完成。目标目录为 ~/expspace/" + project + "/cx-v3.0。");
            if (packageAfter)
            {
                try
                {
                    string path = PackageForWindows(stage, project, driver, "--package-built", archiveEnv, sourceRoot, retryOccupiedPackage);
                    packageSaved(path);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    throw new IOException("编译成功，更新包处理失败：" + error.Message, error);
                }
            }
            if (startAfter)
            {
                try { StartVirtual(stage, project, archiveEnv, requestPassword, consoleReady); }
                catch (IOException error) { throw new IOException("编译成功，但" + error.Message, error); }
                catch (OperationCanceledException) { throw new OperationCanceledException("编译成功，已取消启动。"); }
            }
        }
        finally
        {
            if (stage != null) { CleanStage(stage); }
            try
            {
                File.Delete(archive);
                Directory.Delete(localStage);
            }
            catch (IOException) { log("Windows 临时目录待清理：" + localStage); }
            catch (UnauthorizedAccessException) { log("Windows 临时目录待清理：" + localStage); }
        }
    }

    internal string PackageExisting(string project, string driver, string directory, Func<string, bool> retryOccupiedPackage)
    {
        string stage = CreateStage();
        try
        {
            UploadScript(stage);
            return PackageForWindows(stage, project, driver, "--package-installed", "", directory, retryOccupiedPackage);
        }
        finally { CleanStage(stage); }
    }

    private string PackageForWindows(string stage, string project, string driver, string mode, string archiveEnv,
        string directory, Func<string, bool> retryOccupiedPackage)
    {
        const string name = "control_update.tar.gz";
        string remote = stage + "/" + name;
        string checksum = null;
        log("正在生成更新包…");
        Ssh("Ubuntu 打包", archiveEnv + "export QJ_CONTROLLER_BUILD_DOWNLOAD=" + remote + "; " +
            ScriptCommand(stage, project, mode, driver), null, line =>
            {
                if (Regex.IsMatch(line, @"\AQJPKG:[a-f0-9]{64}\z")) { checksum = line.Substring(6); }
                else { log(line); }
            });
        if (checksum == null) { throw new IOException("Ubuntu 未返回更新包校验值。"); }

        string path = Path.Combine(directory, name);
        string transfer = path + "." + Guid.NewGuid().ToString("N");
        string temp = transfer + ".part";
        string previous = transfer + ".previous";
        bool hadPrevious = false;
        bool saved = false;
        try
        {
            hadPrevious = MovePreviousPackage(path, previous, retryOccupiedPackage);
            if (hadPrevious) { log("已移开 Windows 目录中的同名旧包。"); }
            log("正在下载更新包到 Windows…");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var sha256 = SHA256.Create())
            {
                Ssh("下载更新包", "cat -- " + remote, null, null, binaryOutput: file);
                file.Position = 0;
                string actual = BitConverter.ToString(sha256.ComputeHash(file)).Replace("-", "");
                if (!string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("下载的更新包 SHA-256 校验失败。");
                }
                file.Flush(true);
            }
            File.Move(temp, path);
            saved = true;
            log("更新包已保存：" + path);
            return path;
        }
        finally
        {
            if (hadPrevious)
            {
                if (saved) { DeleteLocalTransferFile(previous); }
                else
                {
                    try
                    {
                        File.Move(previous, path);
                        log("已恢复 Windows 目录中的旧包。");
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        log("旧包自动恢复失败，仍保存在：" + previous);
                    }
                }
            }
            DeleteLocalTransferFile(temp);
        }
    }

    private static bool MovePreviousPackage(string path, string previous, Func<string, bool> retryOccupiedPackage)
    {
        const int sharingViolation = unchecked((int)0x80070020);
        const int lockViolation = unchecked((int)0x80070021);
        while (File.Exists(path))
        {
            try
            {
                File.Move(path, previous);
                return true;
            }
            catch (FileNotFoundException) when (!File.Exists(path)) { return false; }
            catch (IOException error) when (error.HResult == sharingViolation || error.HResult == lockViolation)
            {
                if (!retryOccupiedPackage(path))
                {
                    throw new IOException("已取消下载更新包，Windows 原有文件保留。", error);
                }
            }
        }
        return false;
    }

    private void DeleteLocalTransferFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            log("Windows 临时文件待清理：" + path);
        }
    }

    internal string ReadInstalledVersion(string project)
    {
        string version = null;
        Ssh("读取 Ubuntu 已部署版本", ProjectVariable(project) +
            "root=\"$HOME/expspace/$project\"; file=\"$root/cx-v3.0/src/version_info\"; " +
            "[ -d \"$root\" ] && [ ! -L \"$root\" ] && [ -r \"$file\" ] || exit 0; " +
            "sed -n '2p' \"$file\"", null, line => version = line.Trim(' ', '\t', '"', '\''));
        return version;
    }

    internal void StartExisting(string project, Func<string> requestPassword, Action<string> consoleReady)
    {
        string stage = CreateStage();
        try
        {
            UploadScript(stage);
            StartVirtual(stage, project, "unset QJ_CONTROLLER_BUILD_ARCHIVE QJ_CONTROLLER_BUILD_RECEIPT; ", requestPassword, consoleReady);
        }
        finally { CleanStage(stage); }
    }

    private void StartVirtual(string stage, string project, string archiveEnv, Func<string> requestPassword, Action<string> consoleReady)
    {
        log("正在启动虚拟控制器 ~/expspace/" + project + "/cx-v3.0 …");
        string console = CreateStage();
        bool handedOff = false;
        try
        {
            RunWithSudoPrompt("启动虚拟控制器", archiveEnv + "export QJ_CONTROLLER_CONSOLE=" + console + "; " +
                ScriptCommand(stage, project, "--start-ssh", "VIRTUAL_SERVO"), requestPassword);
            consoleReady(console);
            handedOff = true;
        }
        finally
        {
            if (!handedOff) { CleanStage(console); }
        }
    }

    internal void FollowConsole(string console, Action<string> output, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(console, StagePattern)) { throw new ArgumentException("无效的 Ubuntu 日志路径。"); }
        string command = "pid=$(cat -- " + console + "/pid) || exit 1; " +
            "[[ $pid =~ ^[1-9][0-9]+$ ]] || exit 1; " +
            "tail -n +1 -F --sleep-interval=0.2 --max-unchanged-stats=1 --pid=\"$pid\" " + console + "/console.log & reader=$!; " +
            "exec 3<&0; (while IFS= read -r line; do :; done <&3; kill \"$reader\" 2>/dev/null || true) & watcher=$!; " +
            "trap 'kill \"$reader\" \"$watcher\" 2>/dev/null || true; rm -rf -- " + console + "' EXIT; " +
            "trap 'exit 0' HUP TERM; wait \"$reader\"; result=$?; " +
            "if [ \"$result\" -ne 0 ] && [ \"$result\" -ne 143 ]; then exit \"$result\"; fi";
        try { Ssh("实时日志", command, null, output, cancellation: cancellation); }
        finally { CleanStage(console); }
    }

    private static string ProjectVariable(string project)
    {
        if (!Regex.IsMatch(project, @"\A[0-9][^/\x00\r\n]*\z")) { throw new ArgumentException("无效的 Ubuntu 项目目录。"); }
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(project));
        return "project=$(printf '%s' " + ShellArgument(token) + " | base64 -d) || exit 1; ";
    }

    private static string ScriptCommand(string stage, string project, string mode, string driver)
    {
        return "exec 8>" + stage + "/.lock; flock -n 8 || exit 1; " + ProjectVariable(project) +
            "/bin/bash " + stage + "/" + ScriptName + " " + mode + " " + ShellArgument(driver) + " --directory \"$project\"";
    }

    private string CreateStage()
    {
        string stage = null;
        Ssh("创建 Ubuntu 临时目录", "umask 077; mktemp -d /tmp/qj-controller-build.XXXXXXXX", null,
            line => { if (Regex.IsMatch(line, StagePattern)) { stage = line; } });
        if (stage == null) { throw new IOException("Ubuntu 未返回有效的临时目录。"); }
        return stage;
    }

    private void UploadScript(string stage)
    {
        using (Stream script = typeof(ControllerBuild).Assembly.GetManifestResourceStream(ScriptName))
        {
            if (script == null) { throw new IOException("程序内缺少 Ubuntu 编译脚本。"); }
            Ssh("上传脚本", "cat > " + stage + "/" + ScriptName, script, log);
        }
    }

    private void CleanStage(string stage)
    {
        try
        {
            Ssh("清理 Ubuntu 临时目录", "[ -d " + stage + " ] || exit 0; exec 8>" + stage + "/.lock; " +
                "flock -n 8 && rm -rf -- " + stage, null, log);
        }
        catch (IOException) { log("Ubuntu 临时目录待清理：" + stage); }
    }

    private void RunWithSudoPrompt(string operation, string command, Func<string> requestPassword)
    {
        try { Ssh(operation, "export QJ_CONTROLLER_SUDO_STDIN=0; " + command, null, log, true); }
        catch (SudoPasswordRequiredException)
        {
            string password = requestPassword();
            if (string.IsNullOrEmpty(password)) { throw new OperationCanceledException("已取消" + operation + "。"); }
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password + "\n");
            try
            {
                using (var input = new MemoryStream(passwordBytes, false))
                {
                    Ssh(operation, "export QJ_CONTROLLER_SUDO_STDIN=1; " + command, input, log);
                }
            }
            finally { Array.Clear(passwordBytes, 0, passwordBytes.Length); }
        }
    }

    internal static bool IsValidVersion(string version)
    {
        return Regex.IsMatch(version, @"\A[A-Za-z0-9][A-Za-z0-9._+\-]{0,95}\z");
    }

    internal static void SaveSourceVersion(string sourceRoot, string version)
    {
        if (!IsValidVersion(version)) { throw new ArgumentException("版本号包含不支持的字符。"); }
        Encoding bytes = Encoding.GetEncoding(28591);
        string path = Path.Combine(sourceRoot, "cx-v3.0", "src", "Makefile");
        string contents = bytes.GetString(File.ReadAllBytes(path));
        const string assignment = @"(?m)^(?<prefix>(?:\u00ef\u00bb\u00bf)?[ \t]*BUILD_MSG[ \t]*[:?]?=[ \t]*)(?:[^\r\n]*\\\r?\n)*[^#\r\n]*?(?<comment>[ \t]*#[^\r\n]*)?(?=\r?$)";
        string value = "\"\\\"" + version + "\\\"\"";
        if (Regex.IsMatch(contents, assignment))
        {
            contents = Regex.Replace(contents, assignment, match => match.Groups["prefix"].Value + value + match.Groups["comment"].Value);
        }
        else
        {
            string newline = contents.Contains("\r\n") ? "\r\n" : "\n";
            contents += newline + "BUILD_MSG = " + value + newline;
        }
        AddressSettings.WriteAtomic(path, bytes.GetBytes(contents));
    }

    internal static bool SyncSourceDriver(string sourceRoot, string driver)
    {
        if (driver != "ETHERCAT_X86" && driver != "VIRTUAL_SERVO") { throw new ArgumentException("不支持的轴型。"); }
        string path = Path.Combine(sourceRoot, "cx-v3.0", "src", "Makefile");
        Encoding bytes = Encoding.GetEncoding(28591);
        string contents = bytes.GetString(File.ReadAllBytes(path));
        const string assignment = @"(?m)^(?<prefix>(?:\u00ef\u00bb\u00bf)? *(?:(?:override|export)[ \t]+)*DRIVER[ \t]*[:?+]?=[ \t]*)(?<value>[^#\r\n]*)(?<comment>#[^\r\n]*)?(?=\r?$)";
        string updated = Regex.Replace(contents, assignment, match =>
        {
            string value = match.Groups["value"].Value;
            if (value.TrimEnd(' ', '\t').EndsWith("\\", StringComparison.Ordinal))
            {
                throw new IOException("Windows Makefile 的 DRIVER 使用多行赋值，请改成单行后再编译。");
            }
            string prefix = Regex.Replace(match.Groups["prefix"].Value, @"\+=[ \t]*$", "= ");
            if (prefix.EndsWith("=", StringComparison.Ordinal)) { prefix += " "; }
            return prefix + driver + match.Groups["comment"].Value;
        });
        if (!Regex.IsMatch(contents, assignment))
        {
            string newline = contents.Contains("\r\n") ? "\r\n" : "\n";
            updated += newline + "DRIVER = " + driver + newline;
        }
        if (updated == contents) { return false; }
        AddressSettings.WriteAtomic(path, bytes.GetBytes(updated));
        return true;
    }

    private sealed class SudoPasswordRequiredException : IOException { }

    private void Ssh(string operation, string command, Stream input, Action<string> output, bool allowSudoPrompt = false,
        Stream binaryOutput = null, CancellationToken cancellation = default(CancellationToken))
    {
        Run(operation, sshPath, sshOptions + WindowsArgument("/bin/bash -c " + ShellArgument(command)),
            null, input, output, allowSudoPrompt, binaryOutput, cancellation);
    }

    private void Run(string operation, string file, string arguments, string directory, Stream input, Action<string> output,
        bool allowSudoPrompt = false, Stream binaryOutput = null, CancellationToken cancellation = default(CancellationToken))
    {
        if (!File.Exists(file)) { throw new FileNotFoundException("未找到 " + Path.GetFileName(file) + "。"); }
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (directory != null) { start.WorkingDirectory = directory; }
        using (var process = new Process { StartInfo = start })
        {
            Exception outputError = null;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null || outputError != null) { return; }
                try { output(e.Data); }
                catch (Exception error) { outputError = error; }
            };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data)) { log(e.Data); }
            };
            cancellation.ThrowIfCancellationRequested();
            process.Start();
            using var registration = cancellation.Register(delegate
            {
                try { if (!process.HasExited) { process.StandardInput.Close(); } }
                catch (InvalidOperationException) { }
                catch (IOException) { }
            });
            if (binaryOutput == null) { process.BeginOutputReadLine(); }
            process.BeginErrorReadLine();
            IOException inputError = null;
            if (!cancellation.CanBeCanceled)
            {
                try
                {
                    using (Stream stdin = process.StandardInput.BaseStream)
                    {
                        if (input != null) { input.CopyTo(stdin); }
                    }
                }
                catch (IOException error) { inputError = error; }
            }
            if (binaryOutput != null)
            {
                try { process.StandardOutput.BaseStream.CopyTo(binaryOutput); }
                catch (IOException error)
                {
                    process.StandardOutput.Close();
                    throw new IOException(operation + "时传输中断。", error);
                }
            }
            process.WaitForExit();
            cancellation.ThrowIfCancellationRequested();
            if (allowSudoPrompt && process.ExitCode == 73) { throw new SudoPasswordRequiredException(); }
            if (process.ExitCode != 0)
            {
                string detail = process.ExitCode == 255
                    ? "。请检查 Ubuntu 地址、主机密钥和 SSH 登录配置。"
                    : "，退出码 " + process.ExitCode + "。";
                throw new IOException(operation + "失败" + detail);
            }
            if (inputError != null) { throw new IOException(operation + "时传输中断。", inputError); }
            if (outputError != null) { throw new IOException(operation + "返回的数据无效。", outputError); }
        }
    }

    private static string ShellArgument(string value) { return "'" + value.Replace("'", "'\\''") + "'"; }

    private static string WindowsArgument(string value)
    {
        return "\"" + Regex.Replace(value, "(\\\\*)(\"|$)", match =>
            new string('\\', match.Groups[1].Length * 2) + (match.Groups[2].Length == 0 ? "" : "\\\"")) + "\"";
    }
}
