using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

/* 编译页的集成检查：用真实窗口驱动一次“读 Makefile → 调 WSL 编译 → 打包”，
   需要本机 WSL 已装好 /opt/qjar-build 环境，所以不放进自动回归。
   用法：BuildRun.exe <控制器工程目录> */
internal static class BuildRun
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: BuildRun.exe <控制器工程目录> [--cancel]");
            return 2;
        }
        bool cancelTest = Array.IndexOf(args, "--cancel") >= 0;
        bool earlyCancel = Array.IndexOf(args, "--cancel-early") >= 0;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        BuildSettings.OverridePath = Path.Combine(Path.GetTempPath(),
            "QJAR-buildrun-" + Guid.NewGuid().ToString("N") + ".ini");
        string testDistro = Environment.GetEnvironmentVariable("QJAR_TEST_WSL_DISTRO");
        if (!string.IsNullOrWhiteSpace(testDistro))
        {
            BuildSettings.Save(string.Empty, testDistro, true);
        }
        try
        {
            using (var form = new MainForm(2))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);
                form.Show();
                // 显示后再挪到屏幕外：Show 前设置会被系统按可用区域压缩窗口尺寸。
                form.Location = new Point(-32000, -32000);
                form.SetAdminForTest(true);
                form.Build.LoadProjectForTest(args[0]);
                Console.WriteLine("工程 " + form.Build.ProjectText);
                Console.WriteLine("版本 " + form.Build.VersionText);
                form.Build.BuildForTest();
                var cancelClock = new System.Diagnostics.Stopwatch();
                if (cancelTest || earlyCancel)
                {
                    if (cancelTest && !Wait(delegate
                    {
                        return !form.Build.Running || form.Build.LogText.Contains("编译：make modules");
                    }, 60000))
                    {
                        Console.WriteLine("FAIL 未进入编译阶段");
                        return 1;
                    }
                    Console.WriteLine("取消构建…");
                    cancelClock.Start();
                    form.Build.CancelForTest();
                }
                if (!Wait(delegate { return !form.Build.Running; }, cancelTest || earlyCancel ? 12000 : 900000))
                {
                    Console.WriteLine("FAIL 构建超时");
                    return 1;
                }
                cancelClock.Stop();
                Pump(600);
                Console.WriteLine("状态 " + form.Build.PillText);
                string output = form.Build.OutputDirForTest;
                Console.WriteLine("产物目录 " + (output ?? "(无)"));
                string[] lines = form.Build.LogText.Split('\n');
                int start = Math.Max(0, lines.Length - 12);
                for (int i = start; i < lines.Length; i++) { Console.WriteLine(lines[i].TrimEnd('\r')); }
                if (cancelTest || earlyCancel)
                {
                    if (form.Build.PillText != "已取消" || form.Build.LogText.Contains("WSL build passed:"))
                    {
                        return 1;
                    }
                    Console.WriteLine("取消检查通过，用时 " + cancelClock.ElapsedMilliseconds + " ms");
                    form.Close();
                    return 0;
                }
                if (form.Build.PillText != "构建成功") { return 1; }
                if (string.IsNullOrEmpty(output) ||
                    !File.Exists(Path.Combine(output, "control_update.tar.gz")))
                {
                    Console.WriteLine("FAIL 没有生成 control_update.tar.gz");
                    return 1;
                }
                Console.WriteLine("构建页集成检查通过");
                form.Close();
            }
            return 0;
        }
        finally
        {
            try { File.Delete(BuildSettings.OverridePath); }
            catch (IOException) { }
        }
    }

    private static bool Wait(Func<bool> done, int timeoutMs)
    {
        var clock = new System.Diagnostics.Stopwatch();
        clock.Start();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (done()) { return true; }
            System.Threading.Thread.Sleep(30);
        }
        return done();
    }

    private static void Pump(int milliseconds)
    {
        var clock = new System.Diagnostics.Stopwatch();
        clock.Start();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(10);
        }
    }
}
