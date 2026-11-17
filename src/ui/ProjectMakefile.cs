using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/* 控制器工程 src/Makefile 的读写。
   只替换 DRIVER / BUILD_MSG 两个赋值行的值，其余字符、行尾和编码原样保留；
   版本号在 Makefile 里是编译期 C 字符串（"\"V4.0.5_dev186...\""），界面只显示裸版本号。 */
internal sealed class ProjectMakefile
{
    internal const string DriverEthercat = "ETHERCAT_X86";
    internal const string DriverVirtual = "VIRTUAL_SERVO";

    private static readonly Regex Assignment = new Regex(
        @"^(?<head>[ \t]*(?<key>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*)" +
        @"(?<value>""(?:[^""\\]|\\.)*""|[^#\r\n]*?)(?<tail>[ \t]*#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string path;
    private readonly Encoding encoding;
    private readonly string newline;
    private readonly bool endsWithNewline;
    private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
    private string[] lines;

    private ProjectMakefile(string path, Encoding encoding, string newline, bool endsWithNewline, string[] lines)
    {
        this.path = path;
        this.encoding = encoding;
        this.newline = newline;
        this.endsWithNewline = endsWithNewline;
        this.lines = lines;
        foreach (string line in lines)
        {
            Match match = Assignment.Match(line);
            if (!match.Success) { continue; }
            string key = match.Groups["key"].Value;
            if (!values.ContainsKey(key)) { values.Add(key, match.Groups["value"].Value.Trim()); }
        }
    }

    /* 读取工程目录下的 src/Makefile；目录不像控制器工程时抛 IOException。 */
    internal static ProjectMakefile Load(string projectDir)
    {
        if (string.IsNullOrEmpty(projectDir)) { throw new IOException("未选择工程目录。"); }
        string file = System.IO.Path.Combine(projectDir, "src", "Makefile");
        if (!File.Exists(file)) { throw new IOException("工程目录下没有 src/Makefile。"); }
        byte[] bytes = File.ReadAllBytes(file);
        Encoding encoding;
        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(false);
            offset = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            offset = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            offset = 2;
        }
        else
        {
            encoding = new UTF8Encoding(false);
        }
        string text = encoding.GetString(bytes, offset, bytes.Length - offset);
        bool newlineCrLf = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
        bool endsWithNewline = text.EndsWith("\n", StringComparison.Ordinal);
        string[] lines = text.Replace("\r\n", "\n").Split(new[] { '\n' });
        if (endsWithNewline) { Array.Resize(ref lines, lines.Length - 1); }
        var result = new ProjectMakefile(file, encoding, newlineCrLf ? "\r\n" : "\n", endsWithNewline, lines);
        if (!result.values.ContainsKey("BUILD_MSG")) { throw new IOException("src/Makefile 里没有 BUILD_MSG 赋值。"); }
        return result;
    }

    internal string FilePath { get { return path; } }

    /* 裸版本号（去掉 Makefile 里的 C 字符串转义和引号）。 */
    internal string Version { get { return Unwrap(Value("BUILD_MSG")); } }

    internal string Driver { get { return Value("DRIVER"); } }

    internal string Value(string key)
    {
        string value;
        return values.TryGetValue(key, out value) ? value : string.Empty;
    }

    internal bool IsEthercat { get { return Driver == DriverEthercat; } }

    /* 版本号只允许普通标识符字符，避免写进 Makefile 后破坏赋值或 shell 展开。 */
    internal static bool IsValidVersion(string version, out string error)
    {
        error = null;
        string value = (version ?? string.Empty).Trim();
        if (value.Length == 0) { error = "版本号不能为空。"; return false; }
        if (value.Length > 64) { error = "版本号最多 64 个字符。"; return false; }
        foreach (char c in value)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                c == '.' || c == '_' || c == '-' || c == '+' || c == ' ';
            if (!ok)
            {
                error = "版本号只能包含字母、数字和 . _ - + 空格。";
                return false;
            }
        }
        return true;
    }

    /* 改成新的版本号；返回 false 时原文件不动。 */
    internal bool TrySetVersion(string version, out string error)
    {
        if (!IsValidVersion(version, out error)) { return false; }
        string bare = version.Trim();
        string literal = "\"\\\"" + bare + "\\\"\"";
        if (!ReplaceValue("BUILD_MSG", literal))
        {
            error = "没有找到 BUILD_MSG 赋值行。";
            return false;
        }
        return true;
    }

    /* 切换编译目标；只接受本工具支持的两个驱动。 */
    internal bool TrySetDriver(string driver, out string error)
    {
        error = null;
        if (driver != DriverEthercat && driver != DriverVirtual)
        {
            error = "只支持 ETHERCAT_X86 和 VIRTUAL_SERVO 两个编译目标。";
            return false;
        }
        if (!ReplaceValue("DRIVER", driver))
        {
            error = "没有找到 DRIVER 赋值行。";
            return false;
        }
        return true;
    }

    private bool ReplaceValue(string key, string value)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            Match match = Assignment.Match(lines[i]);
            if (!match.Success || match.Groups["key"].Value != key) { continue; }
            string tail = match.Groups["tail"].Success ? match.Groups["tail"].Value : string.Empty;
            lines[i] = match.Groups["head"].Value + value + tail;
            values[key] = value;
            return true;
        }
        return false;
    }

    /* 写回原文件：先写同目录临时文件，再用 File.Replace 原子替换，避免编辑器占用时写坏。 */
    internal void Save()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) { builder.Append(newline); }
            builder.Append(lines[i]);
        }
        if (endsWithNewline) { builder.Append(newline); }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = encoding.GetBytes(builder.ToString());
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) { File.Replace(temp, path, null, true); }
            else { File.Move(temp, path); }
        }
        catch (IOException error)
        {
            throw new IOException("写入 src/Makefile 失败：" + error.Message, error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new IOException("没有写入 src/Makefile 的权限：" + error.Message, error);
        }
        finally
        {
            try { if (File.Exists(temp)) { File.Delete(temp); } }
            catch (IOException) { }
        }
    }

    /* Makefile 里的 "\"V4.0.5\"" 还原为 V4.0.5；普通赋值原样返回。 */
    internal static string Unwrap(string value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
        {
            text = text.Substring(1, text.Length - 2).Replace("\\\"", "\"").Trim();
        }
        if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
        {
            text = text.Substring(1, text.Length - 2).Trim();
        }
        return text;
    }
}