using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

/* 界面级回归：用进程内模拟控制器驱动真实页面，覆盖查询、注册、命令收发与自动重启。
   与 Smoke.exe 的区别是这里走的是界面层的状态与日志更新。 */
internal static class UiHarness
{
    private static int failures;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CheckProjectDiscovery();
        CheckAgentPrompt();
        CheckGuideForm();
        CheckLogFollow();
        RegisterPage.AutoRestart = true;
        RegisterPage.PendantSshPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeSsh.exe");

        if (Admin.Matches("QJROBOT")) { Console.WriteLine("PASS 管理员认证"); }
        else { Fail("管理员认证失败"); }
        if (!Admin.Matches("qjrobot")) { Console.WriteLine("PASS 错误口令被拒绝"); }
        else { Fail("错误的口令被接受"); }

        // 登录状态持久化：写进去能读出来，重启后能恢复。
        string settingsDir = Path.Combine(Path.GetTempPath(), "QJAR-test-" + Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(settingsDir, "settings.ini");
        AddressSettings.OverridePath = settingsPath;
        BuildSettings.OverridePath = Path.Combine(Path.GetTempPath(), "QJAR-build-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            var loopback = System.Net.IPAddress.Loopback;
            AddressSettings.Save(new AddressSettings.Values
            {
                Ip1 = loopback.ToString(),
                Port1 = "7625",
                BuildMode = 1,
                Page = 3,
                Profile = 0
            });
            Admin.Remember();
            AddressSettings.Values saved = AddressSettings.Load();
            if (saved.BuildMode == 1 && saved.Page == 3 && saved.Port1 == "7625" && saved.Ip2 == "192.168.247.130")
            {
                Console.WriteLine("PASS 登录状态与两套地址已写入");
            }
            else
            {
                Fail("设置没有写入：" + saved.Ip1 + "/" + saved.Port1 + "/" + saved.Ip2);
            }

            using (var restored = new MainForm(0))
            {
                if (restored.IsAdmin) { Console.WriteLine("PASS 重启后恢复登录状态"); }
                else { Fail("重启后没有恢复管理员状态"); }
            }

            Admin.Forget();
            AddressSettings.Save(new AddressSettings.Values { Ip1 = loopback.ToString() });
            using (var loggedOut = new MainForm(0))
            {
                if (!loggedOut.IsAdmin) { Console.WriteLine("PASS 退出后不再恢复"); }
                else { Fail("退出管理后仍然恢复成管理员"); }
            }

            // 后续界面测试从“未登录”开始。
            AddressSettings.Save(new AddressSettings.Values { Ip1 = loopback.ToString() });
        }
        catch (Exception error)
        {
            Fail("登录状态测试异常：" + error.Message);
        }

        using (var mock = new MockController(1))
        {
            using (var form = new MainForm(0))
            {
                form.Show();
                form.SetAddressForTest("127.0.0.1", mock.Port);
                form.SetAdminForTest(false);
                Pump(150);

                form.Register.FocusFirst();
                bool queryHadFocus = form.Register.ContainsFocus;
                // 两套地址：切到地址 2 应换成默认的 192.168.247.130，切回来要还原。
                form.SwitchProfileForTest(1);
                if (!queryHadFocus || form.Register.ContainsFocus) { Console.WriteLine("PASS 切换地址不抢输入焦点"); }
                else { Fail("切换地址抢走了查询按钮焦点"); }
                string profileIp;
                int profilePort;
                if (form.TryGetAddress(out profileIp, out profilePort) && profileIp == "192.168.247.130")
                {
                    Console.WriteLine("PASS 地址 2 默认 192.168.247.130");
                }
                else
                {
                    Fail("地址 2 不对：" + profileIp + ":" + profilePort);
                }
                form.SwitchProfileForTest(0);
                if (form.TryGetAddress(out profileIp, out profilePort) &&
                    profileIp == "127.0.0.1" && profilePort == mock.Port)
                {
                    Console.WriteLine("PASS 切回地址 1 后还原");
                }
                else
                {
                    Fail("切回地址 1 没有还原：" + profileIp + ":" + profilePort);
                }

                // 普通用户：命令页必须拒绝发送。
                form.Command.SendForTest("getsn", "");
                Pump(200);
                if (form.Command.LogText.Contains("请先登录管理员"))
                {
                    Console.WriteLine("PASS 未登录时命令被拒绝");
                }
                else
                {
                    Fail("未登录仍然发出了命令");
                }

                // 状态徽标随文字自适应宽度
                form.Register.PillCaptionForTest = "正在连接控制器";
                int wide = form.Register.PillWidth;
                form.Register.PillCaptionForTest = "等待查询";
                int narrow = form.Register.PillWidth;
                if (wide > narrow) { Console.WriteLine("PASS 状态徽标自适应 " + narrow + " -> " + wide); }
                else { Fail("状态徽标没有随文字变宽：" + narrow + " / " + wide); }

                form.SetAdminForTest(true);
                Pump(150);

                // 编译页：Makefile 读取、版本号写回、目标切换都走真实文件。
                string projectDir = Path.Combine(Path.GetTempPath(), "QJAR-project-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(projectDir, "src"));
                Directory.CreateDirectory(Path.Combine(projectDir, "lib"));
                File.WriteAllText(Path.Combine(projectDir, "src", "Makefile"),
                    "BUILD_MSG = \"\\\"V4.0.5_test\\\"\"\nDRIVER = ETHERCAT_X86\nSPACE = USER\nMOTION_TYPE = COORD_TYPE_ARC\n",
                    new UTF8Encoding(false));
                form.Build.LoadProjectForTest(projectDir);
                Check("编译页版本号", form.Build.VersionText, "V4.0.5_test");
                string rememberedProject, rememberedDistro;
                bool rememberedPackage;
                BuildSettings.Load(out rememberedProject, out rememberedDistro, out rememberedPackage);
                Check("工程目录持久化", rememberedProject, projectDir);
                using (var reopened = new MainForm(2))
                {
                    Check("重开后工程目录还原", reopened.Build.ProjectText, projectDir);
                }
                form.Build.SaveVersionForTest("V4.0.6_test");
                Check("编译页写回版本号", form.Build.VersionText, "V4.0.6_test");
                var versionEdit = form.Build.VersionEditForTest;
                versionEdit.Text = "V4.0.7_enter";
                NativeMethods.SendMessage(versionEdit.Handle, 0x0100, new IntPtr((int)Keys.Enter), IntPtr.Zero);
                Check("回车保存版本号", ProjectMakefile.Load(projectDir).Version, "V4.0.7_enter");
                form.Build.SetTargetForTest("VIRTUAL_SERVO");
                if (form.Build.LogText.Contains("VIRTUAL_SERVO")) { Console.WriteLine("PASS 编译页切换目标"); }
                else { Fail("编译页切换目标没有生效：" + form.Build.LogText); }
                try { Directory.Delete(projectDir, true); } catch (IOException) { }

                form.Register.QueryForTest();
                if (!Wait(delegate { return !form.Register.Running; }, 10000)) { Fail("查询超时"); }
                Pump(200);
                Check("查询后状态", form.Register.PillText, "未注册");
                Check("序列号", form.Register.SerialText, MockController.Serial);

                form.Register.RegisterForTest();
                if (!Wait(delegate { return !form.Register.Running; }, 30000)) { Fail("注册超时"); }
                Pump(300);
                if (form.Register.PillText != "已授权")
                {
                    Console.WriteLine("注册诊断日志：" + form.Register.LogText);
                    Console.WriteLine("管理员=" + form.IsAdmin + "；模拟器连接数=" + mock.Connections);
                }
                Check("注册后状态", form.Register.PillText, "已授权");
                Check("剩余天数", form.Register.DaysText, "365 天");
                if (!form.Register.LogText.Contains("重启命令已发送"))
                {
                    Fail("注册后的示教器重启没有确认");
                }
                else { Console.WriteLine("PASS 注册后自动重启示教器（SSH 替身）"); }

                form.Command.SendForTest("getsn", "");
                if (!Wait(delegate { return !form.Command.Running; }, 10000)) { Fail("命令超时"); }
                Pump(200);
                if (!form.Command.LogText.Contains(MockController.Serial))
                {
                    Fail("命令回包未显示：" + form.Command.LogText);
                }
                else
                {
                    Console.WriteLine("PASS 命令收发");
                }

                form.Close();
            }
        }

        AddressSettings.OverridePath = null;
        BuildSettings.OverridePath = null;
        try { File.Delete(settingsPath); Directory.Delete(settingsDir); } catch (IOException) { }

        Console.WriteLine(failures == 0 ? "界面回归全部通过" : failures + " 项失败");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckLogFollow()
    {
        using (var form = new Form())
        using (var log = new LogView(""))
        {
            log.Dock = DockStyle.Fill;
            form.Controls.Add(log);
            form.ClientSize = new System.Drawing.Size(400, 160);
            form.Show();
            for (int i = 0; i < 45; i++) { log.Append("构建日志 " + i, Theme.TextPrimary); }
            Pump(60);
            var origin = new System.Drawing.Point(5, 5);
            if (log.Box.GetCharIndexFromPosition(origin) == 0) { Fail("追加日志没有跟随底部"); }
            NativeMethods.SendMessage(log.Box.Handle, 0x0115, new IntPtr(6), IntPtr.Zero);
            Pump(60);
            int position = log.Box.GetCharIndexFromPosition(origin);
            log.Append("向上翻阅时追加一行", Theme.TextPrimary);
            Pump(60);
            if (log.Box.GetCharIndexFromPosition(origin) != position) { Fail("追加日志抢走了阅读位置"); }
            else { Console.WriteLine("PASS 日志跟随底部并保留翻阅位置"); }
        }
    }

    /* 指引窗口：链接可点、停在命令框上时滚轮仍能带动整页。 */
    private static void CheckGuideForm()
    {
        try
        {
            string openedUrl = null;
            using (var guide = new WslGuideForm("Ubuntu", delegate(string url) { openedUrl = url; }))
            {
                guide.Show();
                Pump(300);
                LinkLabel link = guide.ReleaseLinkForTest;
                if (link == null) { Fail("环境包链接控件缺失"); guide.Close(); return; }
                guide.ScrollHostForTest.ScrollControlIntoView(link.Parent);
                Pump(100);
                IntPtr point = new IntPtr((link.Height / 2 << 16) | 10);
                NativeMethods.SendMessage(link.Handle, 0x0200, IntPtr.Zero, point);
                NativeMethods.SendMessage(link.Handle, 0x0201, new IntPtr(1), point);
                NativeMethods.SendMessage(link.Handle, 0x0202, IntPtr.Zero, point);
                Pump(100);
                if (openedUrl == WslGuideForm.ReleaseUrl)
                {
                    Console.WriteLine("PASS 点击链接触发 GitHub 浏览器跳转");
                }
                else { Fail("链接点击未触发正确跳转：" + openedUrl); }
                guide.ScrollHostForTest.AutoScrollPosition = new System.Drawing.Point(0, 0);
                Pump(100);

                Control box = guide.CommandBoxForTest;
                ScrollableControl host = guide.ScrollHostForTest;
                if (box == null || host == null) { Fail("指引窗口缺少命令框或滚动区"); guide.Close(); return; }
                int before = host.VerticalScroll.Value;
                // 直接把滚轮消息发给命令框，验证它转交给外层滚动区。
                NativeMethods.SendMessage(box.Handle, 0x020A, new IntPtr(-120 << 16), IntPtr.Zero);
                Pump(250);
                int after = host.VerticalScroll.Value;
                if (after > before)
                {
                    Console.WriteLine("PASS 命令框上滚轮可带动整页（" + before + " -> " + after + "）");
                }
                else
                {
                    Fail("滚轮停在命令框上时整页没有滚动（" + before + " -> " + after + "）");
                }
                guide.Close();
            }
        }
        catch (Exception error)
        {
            Fail("指引窗口检查异常：" + error.Message);
        }
    }

    private static void CheckAgentPrompt()
    {
        string prompt = WslGuideForm.GetAgentPrompt("Ubuntu");
        if (prompt.Contains(WslGuideForm.DownloadUrl) && prompt.Contains(WslGuideForm.ArchiveSha256) &&
            prompt.Contains("由我授权提供解压密码") && !prompt.Contains("{{"))
        {
            Console.WriteLine("PASS 提示词含真实下载链接、校验与密码授权步骤");
        }
        else { Fail("提示词缺少下载/校验/授权信息或含未展开的变量"); }
        if (WslGuideForm.GetAgentPrompt("Ubuntu-22.04").Contains("-d Ubuntu-22.04"))
        {
            Console.WriteLine("PASS 提示词随所选发行版生成");
        }
        else { Fail("提示词没有使用实际发行版"); }
    }

    private static void CheckProjectDiscovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "QJAR-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string one = CreateProject(Path.Combine(root, "单工程", "中文 含空格", "cx-v3.0"));
            string note;
            Check("工程本层定位", BuildPage.ResolveProjectForTest(one, out note), one);
            Check("子目录自动定位", BuildPage.ResolveProjectForTest(Path.Combine(root, "单工程"), out note), one);
            string third = CreateProject(Path.Combine(root, "三级", "1", "2", "3"));
            Check("三级目录定位", BuildPage.ResolveProjectForTest(Path.Combine(root, "三级"), out note), third);
            CreateProject(Path.Combine(root, "过深", "1", "2", "3", "4"));
            if (BuildPage.ResolveProjectForTest(Path.Combine(root, "过深"), out note) == null)
            { Console.WriteLine("PASS 超过三级不扫描"); } else { Fail("目录搜索越过三级预算"); }
            string missingLib = Path.Combine(root, "缺lib");
            Directory.CreateDirectory(Path.Combine(missingLib, "src"));
            File.WriteAllText(Path.Combine(missingLib, "src", "Makefile"), "BUILD_KERNEL=xeno-x86\n");
            if (BuildPage.ResolveProjectForTest(missingLib, out note) == null)
            { Console.WriteLine("PASS 缺 lib 不算工程"); } else { Fail("缺 lib 被误识别为工程"); }
            string multiple = Path.Combine(root, "多工程");
            CreateProject(Path.Combine(multiple, "甲"));
            CreateProject(Path.Combine(multiple, "乙"));
            if (BuildPage.ResolveProjectForTest(multiple, out note) == null && note.Contains("多个"))
            { Console.WriteLine("PASS 多个工程不默选"); } else { Fail("多个工程被默选"); }
            string ignored = Path.Combine(root, "输出与隐藏");
            CreateProject(Path.Combine(ignored, "build-wsl", "old"));
            CreateProject(Path.Combine(ignored, ".git", "hidden"));
            CreateProject(Path.Combine(ignored, "dist", "old"));
            if (BuildPage.ResolveProjectForTest(ignored, out note) == null)
            { Console.WriteLine("PASS 跳过隐藏和构建输出目录"); } else { Fail("定位到了隐藏/旧构建结果"); }
            string large = Path.Combine(root, "预算");
            Directory.CreateDirectory(large);
            for (int i = 0; i < 405; i++) { Directory.CreateDirectory(Path.Combine(large, "d" + i)); }
            if (BuildPage.ResolveProjectForTest(large, out note) == null && note.Contains("范围太大"))
            { Console.WriteLine("PASS 400 目录预算生效"); } else { Fail("目录搜索预算未生效"); }
        }
        finally
        {
            // 仅移除上文创建的唯一测试目录，不接触用户工程。
            Directory.Delete(root, true);
        }
    }

    private static string CreateProject(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "src"));
        Directory.CreateDirectory(Path.Combine(path, "lib"));
        File.WriteAllText(Path.Combine(path, "src", "Makefile"), "BUILD_KERNEL=xeno-x86\n");
        return path;
    }

    private static bool Wait(Func<bool> done, int timeoutMs)
    {
        var clock = new System.Diagnostics.Stopwatch();
        clock.Start();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (done()) { return true; }
            System.Threading.Thread.Sleep(20);
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

    private static void Check(string name, string actual, string expected)
    {
        if (actual == expected)
        {
            Console.WriteLine("PASS " + name + " -> " + actual);
        }
        else
        {
            Fail(name + " 期望 '" + expected + "' 实际 '" + actual + "'");
        }
    }

    private static void Fail(string message)
    {
        failures++;
        Console.WriteLine("FAIL " + message);
    }
}
