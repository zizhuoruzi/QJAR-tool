using System;
using System.IO;
using System.Text;

/* Makefile 读写的黑盒检查：版本号、DRIVER、行尾与注释都必须原样保留。
   只依赖 ProjectMakefile.cs，不加载界面。 */
internal static class BuildCheck
{
    private static int failures;

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "qj-makefile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            string path = Path.Combine(root, "src", "Makefile");
            string original =
                "# 编译选项\r\n" +
                "DEBUG \t\t\t= -g\r\n" +
                "BUILD_MSG\t\t= \"\\\"V4.0.5_dev186_ConveyorTrack03_T52\\\"\" #$(shell svnversion)\\\"\"\r\n" +
                "BUILD_KERNEL \t= xeno-x86\r\n" +
                "SPACE \t\t\t= USER\r\n" +
                "DRIVER \t\t\t= ETHERCAT_X86\r\n" +
                "FLOAT \t\t\t= HARD_FLOAT\r\n" +
                "\r\n" +
                "modules:\r\n" +
                "\t$(Q)echo $(DRIVER)\r\n";
            File.WriteAllText(path, original, new UTF8Encoding(false));

            ProjectMakefile model = ProjectMakefile.Load(root);
            Check("版本号解析", model.Version, "V4.0.5_dev186_ConveyorTrack03_T52");
            Check("驱动解析", model.Driver, "ETHERCAT_X86");
            Check("只读字段解析", model.Value("SPACE"), "USER");

            string error;
            if (!model.TrySetVersion("V4.0.6_dev1", out error)) { Fail("合法版本号被拒绝：" + error); }
            else { model.Save(); }

            ProjectMakefile after = ProjectMakefile.Load(root);
            Check("版本号写回", after.Version, "V4.0.6_dev1");
            string text = File.ReadAllText(path);
            if (text.Contains("BUILD_MSG\t\t= \"\\\"V4.0.6_dev1\\\"\" #$(shell svnversion)\\\"\""))
            {
                Console.WriteLine("PASS 版本号字面量与行尾注释保留");
            }
            else
            {
                Fail("版本号行写坏：" + Line(text, "BUILD_MSG"));
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                Fail("写回时添加了 BOM");
            }
            else
            {
                Console.WriteLine("PASS 保持无 BOM");
            }
            int crlf = 0;
            for (int i = 1; i < bytes.Length; i++)
            {
                if (bytes[i] == '\n' && bytes[i - 1] == '\r') { crlf++; }
            }
            if (crlf == Count(original, "\r\n")) { Console.WriteLine("PASS 保持 CRLF 行尾"); }
            else { Fail("行尾被改写：" + crlf + " / " + Count(original, "\r\n")); }

            // 非法版本号必须被拒绝，且文件不变。
            string before = File.ReadAllText(path);
            foreach (string bad in new[] { "", "V4\"bad", "V4$(x)", "V4#x", new string('v', 65) })
            {
                if (model.TrySetVersion(bad, out error)) { Fail("非法版本号被接受：" + bad); }
            }
            if (File.ReadAllText(path) == before) { Console.WriteLine("PASS 非法版本号不改文件"); }
            else { Fail("非法版本号仍写入了文件"); }

            if (!model.TrySetDriver("VIRTUAL_SERVO", out error)) { Fail("切换虚拟轴失败：" + error); }
            else { model.Save(); }
            Check("驱动切换", ProjectMakefile.Load(root).Driver, "VIRTUAL_SERVO");

            ProjectMakefile reloaded = ProjectMakefile.Load(root);
            if (!reloaded.TrySetDriver("RTEX_X86", out error)) { Console.WriteLine("PASS 拒绝其它驱动"); }
            else { Fail("接受了不支持的驱动 RTEX_X86"); }

            // LF + 中文注释 + 无引号版本号，也不能被格式假设弄坏。
            string plain = "# 版本\nBUILD_MSG = V4.0.7\nDRIVER = VIRTUAL_SERVO\n";
            Directory.CreateDirectory(Path.Combine(root, "plain", "src"));
            File.WriteAllText(Path.Combine(root, "plain", "src", "Makefile"), plain, new UTF8Encoding(false));
            ProjectMakefile simple = ProjectMakefile.Load(Path.Combine(root, "plain"));
            Check("无引号版本号", simple.Version, "V4.0.7");
            if (simple.TrySetVersion("V4.0.8", out error)) { simple.Save(); }
            Check("无引号版本号写回", ProjectMakefile.Load(Path.Combine(root, "plain")).Version, "V4.0.8");
            if (File.ReadAllText(Path.Combine(root, "plain", "src", "Makefile")).Contains("BUILD_MSG = \"\\\"V4.0.8\\\"\""))
            {
                Console.WriteLine("PASS 无引号版本号改成编译期字面量");
            }
            else
            {
                Fail("无引号版本号写回格式不对");
            }

            if (ProjectMakefile.Unwrap("\"\\\"V4.0.5\\\"\"") == "V4.0.5" &&
                ProjectMakefile.Unwrap("V4.0.5") == "V4.0.5")
            {
                Console.WriteLine("PASS 版本号还原");
            }
            else
            {
                Fail("版本号还原不正确");
            }
        }
        catch (Exception error)
        {
            Fail("检查异常：" + error);
        }
        finally
        {
            try { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
            catch (IOException) { }
        }
        Console.WriteLine(failures == 0 ? "Makefile 读写全部通过" : failures + " 项失败");
        return failures == 0 ? 0 : 1;
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string Line(string text, string key)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.Contains(key)) { return line.Trim(); }
        }
        return "(找不到 " + key + ")";
    }

    private static void Check(string name, string actual, string expected)
    {
        if (actual == expected) { Console.WriteLine("PASS " + name + " -> " + actual); }
        else { Fail(name + " 期望 '" + expected + "' 实际 '" + actual + "'"); }
    }

    private static void Fail(string message)
    {
        failures++;
        Console.WriteLine("FAIL " + message);
    }
}