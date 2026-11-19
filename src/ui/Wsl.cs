using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

/* wsl.exe 的调用封装。
   两条流编码不同：脚本输出是 UTF-8，wsl 自己的管理消息（-l -q、发行版不存在等）是 UTF-16LE，
   所以统一按原始字节读入，再按内容判断编码。 */
internal static class Wsl
{
    private const string ScriptPath = "/opt/qjar-build/build-controller.sh";
    private const string ToolchainPath = "/opt/qjar-build/ubuntu12-i686/usr/bin/gcc-4.6";

    internal static string ExePath
    {
        get { return Path.Combine(Environment.SystemDirectory, "wsl.exe"); }
    }

    internal static string DefaultDistribution
    {
        get { return "Ubuntu"; }
    }

    /* 列出已安装发行版；wsl -l -q 的 UTF-16 输出在这里解码。读不到时返回空数组和错误说明。 */
    internal static string[] ListDistributions(out string error)
    {
        error = null;
        string output;
        int exit;
        string failure;
        if (!RunQuiet("-l -q", 30000, out output, out exit, out failure))
        {
            error = "无法运行 wsl.exe：" + failure;
            return new string[0];
        }
        if (exit != 0)
        {
            error = FirstLine(output, "WSL 未安装或没有可用的发行版。");
            return new string[0];
        }
        var list = new List<string>();
        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string name = line.Trim();
            if (name.Length > 0) { list.Add(name); }
        }
        return list.ToArray();
    }

    /* 构建环境的几项硬性条件：发行版可启动、入口脚本、隔离工具链、目标内核目录。
       发行版空闲后首次启动要十几秒，第一次调用单独给足时间并带出 wsl 自己的报错。 */
    internal static string CheckEnvironment(string distro)
    {
        string name = DistroName(distro);
        string output;
        int exit;
        string failure;
        if (!RunQuiet("-d " + name + " --exec true", 90000, out output, out exit, out failure))
        {
            return "WSL 发行版 " + name + " 启动失败：" + failure +
                (output.Length > 0 ? "（" + FirstLine(output, string.Empty) + "）" : string.Empty);
        }
        if (exit != 0)
        {
            return "WSL 发行版 " + name + " 无法启动：" + FirstLine(output, "退出码 " + exit);
        }
        if (!RunQuiet("-d " + name + " -u root --exec test -x " + ScriptPath, 30000, out output, out exit, out failure))
        {
            return "检查构建入口失败：" + failure;
        }
        if (exit != 0)
        {
            return "发行版 " + name + " 里没有构建入口 " + ScriptPath + "。";
        }
        if (!RunQuiet("-d " + name + " -u root --exec test -x " + ToolchainPath, 30000, out output, out exit, out failure))
        {
            return "检查编译工具链失败：" + failure;
        }
        if (exit != 0)
        {
            return "缺少隔离的 32 位 Xenomai 编译环境（/opt/qjar-build/ubuntu12-i686）。";
        }
        if (!RunQuiet("-d " + name + " -u root --exec test -f /opt/qjar-build/ubuntu12-i686/lib/modules/3.14.17-xenomai/build/Module.symvers",
            30000, out output, out exit, out failure) || exit != 0)
        {
            return "缺少目标内核构建目录 3.14.17-xenomai。";
        }
        return null;
    }

    internal static string BuildArguments(string distro, string projectDir, bool package, string buildId)
    {
        const string command = "printf 'QJAR build group: %s\\n' $$; bash \"$@\"; exit $?";
        var builder = new StringBuilder();
        builder.Append("-d ").Append(DistroName(distro)).Append(" -u root --exec setsid --fork --wait bash -c ")
            .Append(Quote(command.Replace("\"", "\\\""))).Append(' ').Append(buildId).Append(' ').Append(ScriptPath);
        builder.Append(' ').Append(Quote(TrimTrailingSlash(projectDir)));
        if (package) { builder.Append(" --package"); }
        return builder.ToString();
    }

    /* 先通过进程组首领的参数验证构建标识，再结束整组 bash、make 和编译器进程 */
    internal static string KillBuild(string distro, int processGroup, string buildId)
    {
        const string command =
            "if ! grep -Fxzq -- \"$2\" \"/proc/$1/cmdline\" 2>/dev/null; then " +
            "if kill -0 -- -$1 2>/dev/null; then exit 2; fi; exit 0; fi; " +
            "kill -TERM -- -$1 2>/dev/null || exit 0; " +
            "for i in {1..10}; do kill -0 -- -$1 2>/dev/null || exit 0; sleep 0.1; done; " +
            "if kill -0 -- -$1 2>/dev/null; then kill -KILL -- -$1; fi";
        string output;
        int exit;
        string failure;
        if (!RunQuiet("-d " + DistroName(distro) + " -u root --exec bash -c " +
            Quote(command.Replace("\"", "\\\"")) + " qjar-cancel " + processGroup + " " + buildId, 8000,
            out output, out exit, out failure))
        {
            return failure;
        }
        return exit == 0 ? null : "无法确认或停止本次 WSL 构建进程组（退出码 " + exit + "）。";
    }

    internal static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) { return "\"\""; }
        return "\"" + value + "\"";
    }

    /* wsl.exe 的 -d 参数不剥引号：写成 "Ubuntu" 会报 WSL_E_DISTRO_NOT_FOUND，
       所以发行版名必须原样传，不能走通用引号。 */
    internal static string DistroName(string value)
    {
        return (value ?? string.Empty).Trim().Trim('"');
    }

    internal static string TrimTrailingSlash(string path)
    {
        if (string.IsNullOrEmpty(path)) { return path; }
        string trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? path : trimmed;
    }

    /* 运行一条短命令并拿回合并输出。
   两路都要读，否则 wsl 的警告一多就把管道写满、进程永远不退出；
   超时或启动失败时通过 failure 把原因带回去。 */
    internal static bool RunQuiet(string arguments, int timeoutMs, out string output, out int exitCode,
        out string failure)
    {
        output = string.Empty;
        exitCode = -1;
        failure = null;
        Process process;
        try
        {
            var info = new ProcessStartInfo(ExePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath()
            };
            process = Process.Start(info);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            return false;
        }
        if (process == null)
        {
            failure = "无法创建 wsl.exe 进程。";
            return false;
        }
        using (process)
        {
            var stdout = new MemoryStream();
            var stderr = new MemoryStream();
            Thread outThread = Copy(process.StandardOutput.BaseStream, stdout);
            Thread errThread = Copy(process.StandardError.BaseStream, stderr);
            bool exited;
            try
            {
                exited = process.WaitForExit(timeoutMs);
            }
            catch (Exception exception)
            {
                failure = exception.Message;
                return false;
            }
            if (!exited)
            {
                try { process.Kill(); } catch (Exception) { }
                failure = "wsl.exe 在 " + (timeoutMs / 1000) + " 秒内没有返回";
                return false;
            }
            outThread.Join(2000);
            errThread.Join(2000);
            output = Decode(stdout.ToArray());
            string errors = Decode(stderr.ToArray());
            if (errors.Length > 0)
            {
                output = output.Length == 0 ? errors : output + Environment.NewLine + errors;
            }
            try { exitCode = process.ExitCode; }
            catch (Exception) { exitCode = -1; }
            return true;
        }
    }

    private static Thread Copy(Stream source, MemoryStream target)
    {
        var thread = new Thread(delegate()
        {
            try { source.CopyTo(target); }
            catch (Exception) { }
        });
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) { return string.Empty; }
        Encoding encoding = LooksLikeUtf16(bytes, bytes.Length) ? Encoding.Unicode : new UTF8Encoding(false);
        string text = encoding.GetString(bytes);
        return text.Trim('\0', '\uFEFF', '\r', '\n', ' ', '\t');
    }

    internal static bool LooksLikeUtf16(byte[] bytes, int count)
    {
        int pairs = 0;
        int zeros = 0;
        for (int i = 1; i < count; i += 2)
        {
            pairs++;
            if (bytes[i] == 0) { zeros++; }
        }
        return pairs > 0 && zeros * 10 >= pairs * 3;
    }

    private static string FirstLine(string text, string fallback)
    {
        if (string.IsNullOrEmpty(text)) { return fallback; }
        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) { return trimmed; }
        }
        return fallback;
    }
}

/* 一次构建进程：两路输出按到达顺序合并成行，交给界面追加到日志。 */
internal sealed class WslRunner : IDisposable
{
    private readonly object gate = new object();
    private Process process;
    private Thread outThread;
    private Thread errThread;
    private bool hasOutput;

    /* 第二个参数标记这一行来自 stderr，界面据此给错误行着色。 */
    internal event Action<string, bool> Line;

    internal bool HasOutput
    {
        get { lock (gate) { return hasOutput; } }
    }

    internal int ExitCode { get; private set; }

    internal bool Start(string arguments)
    {
        var info = new ProcessStartInfo(Wsl.ExePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        };
        try
        {
            process = Process.Start(info);
        }
        catch (Exception)
        {
            return false;
        }
        if (process == null) { return false; }
        outThread = Pump(process.StandardOutput.BaseStream, false);
        errThread = Pump(process.StandardError.BaseStream, true);
        return true;
    }

    /* 等待退出；返回是否在超时前结束。 */
    internal bool WaitForExit(int timeoutMs)
    {
        if (process == null) { return true; }
        bool exited = false;
        try { exited = process.WaitForExit(timeoutMs); }
        catch (Exception) { }
        if (!exited) { return false; }
        if (outThread != null) { outThread.Join(3000); }
        if (errThread != null) { errThread.Join(3000); }
        try { ExitCode = process.ExitCode; }
        catch (Exception) { ExitCode = -1; }
        return true;
    }

    internal void Kill()
    {
        try { if (process != null && !process.HasExited) { process.Kill(); } }
        catch (Exception) { }
    }

    public void Dispose()
    {
        try { if (process != null) { process.Dispose(); } }
        catch (Exception) { }
        process = null;
    }

    private Thread Pump(Stream stream, bool isError)
    {
        var thread = new Thread(delegate() { Read(stream, isError); });
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    private void Read(Stream stream, bool isError)
    {
        Decoder decoder = null;
        var pending = new List<byte>();
        var chars = new StringBuilder();
        var charBuffer = new char[16384];
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (decoder == null)
                {
                    // 用第一段数据判断编码，不再等凑满固定长度：否则构建开头的几行会延迟显示。
                    if (pending.Count == 0 && read >= 2)
                    {
                        var head = new byte[read];
                        Array.Copy(buffer, head, read);
                        decoder = PickDecoder(head, head.Length);
                        Drain(head, head.Length, decoder, charBuffer, chars, isError);
                        continue;
                    }
                    for (int i = 0; i < read; i++) { pending.Add(buffer[i]); }
                    if (pending.Count < 2) { continue; }
                    byte[] first = pending.ToArray();
                    pending.Clear();
                    decoder = PickDecoder(first, first.Length);
                    Drain(first, first.Length, decoder, charBuffer, chars, isError);
                    continue;
                }
                Drain(buffer, read, decoder, charBuffer, chars, isError);
            }
            if (decoder == null)
            {
                byte[] tail = pending.ToArray();
                decoder = PickDecoder(tail, tail.Length);
                Drain(tail, tail.Length, decoder, charBuffer, chars, isError);
            }
            Flush(chars, isError);
            FlushDecoder(decoder, charBuffer, chars);
            Flush(chars, isError);
        }
        catch (Exception)
        {
        }
        finally
        {
            try { stream.Dispose(); }
            catch (Exception) { }
        }
    }

    private static Decoder PickDecoder(byte[] bytes, int count)
    {
        return Wsl.LooksLikeUtf16(bytes, count)
            ? Encoding.Unicode.GetDecoder()
            : new UTF8Encoding(false).GetDecoder();
    }

    private void Drain(byte[] bytes, int count, Decoder decoder, char[] charBuffer, StringBuilder chars, bool isError)
    {
        if (count <= 0) { return; }
        int read = decoder.GetChars(bytes, 0, count, charBuffer, 0);
        if (read > 0)
        {
            chars.Append(charBuffer, 0, read);
            Flush(chars, isError);
        }
    }

    private void FlushDecoder(Decoder decoder, char[] charBuffer, StringBuilder chars)
    {
        int read;
        try { read = decoder.GetChars(new byte[0], 0, 0, charBuffer, 0, true); }
        catch (ArgumentException) { read = 0; }
        if (read > 0) { chars.Append(charBuffer, 0, read); }
    }

    private void Flush(StringBuilder chars, bool isError)
    {
        int start = 0;
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\n') { continue; }
            Emit(chars.ToString(start, i - start), isError);
            start = i + 1;
        }
        if (start > 0) { chars.Remove(0, start); }
    }

    private void Emit(string line, bool isError)
    {
        string text = line.TrimEnd('\r').TrimStart('\uFEFF');
        if (text.Length == 0) { return; }
        lock (gate)
        {
            hasOutput = true;
            Action<string, bool> handler = Line;
            if (handler != null) { handler(text, isError); }
        }
    }
}
