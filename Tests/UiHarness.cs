using System;
using System.IO;
using System.Windows.Forms;

/* 界面级回归：用进程内模拟控制器驱动真实页面，覆盖查询、注册、命令收发与手动重启。
   与 Smoke.exe 的区别是这里走的是界面层的状态与日志更新。 */
internal static class UiHarness
{
    private static int failures;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        RegisterPage.AutoRestart = false;
        RegisterPage.PendantSshPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FakeSsh.exe");

        if (Admin.Matches("QJROBOT")) { Console.WriteLine("PASS 管理员口令 QJROBOT"); }
        else { Fail("管理员口令 QJROBOT 未通过"); }
        if (!Admin.Matches("qjrobot")) { Console.WriteLine("PASS 错误口令被拒绝"); }
        else { Fail("错误的口令被接受"); }

        // 登录状态持久化：写进去能读出来，重启后能恢复。
        string settingsPath = Path.Combine(Path.GetTempPath(), "QJAR-test-" + Guid.NewGuid().ToString("N") + ".ini");
        AddressSettings.OverridePath = settingsPath;
        try
        {
            var loopback = new System.Net.IPAddress(new byte[] { 127, 0, 0, 1 });
            AddressSettings.Save(loopback, 7625, 5000, "UTF-8", true);
            string[] saved = AddressSettings.Load();
            if (saved[4] == "1" && saved[1] == "7625") { Console.WriteLine("PASS 登录状态已写入"); }
            else { Fail("登录状态没有写入：" + string.Join("/", saved)); }

            using (var restored = new MainForm(0))
            {
                if (restored.IsAdmin) { Console.WriteLine("PASS 重启后恢复登录状态"); }
                else { Fail("重启后没有恢复管理员状态"); }
            }

            AddressSettings.Save(loopback, 7625, 5000, "UTF-8", false);
            using (var loggedOut = new MainForm(0))
            {
                if (!loggedOut.IsAdmin) { Console.WriteLine("PASS 退出后不再恢复"); }
                else { Fail("退出管理后仍然恢复成管理员"); }
            }

            // 后续界面测试从“未登录”开始。
            AddressSettings.Save(loopback, 7625, 5000, "UTF-8", false);
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

                form.Register.QueryForTest();
                if (!Wait(delegate { return !form.Register.Running; }, 10000)) { Fail("查询超时"); }
                Pump(200);
                Check("查询后状态", form.Register.PillText, "未注册");
                Check("序列号", form.Register.SerialText, MockController.Serial);

                form.Register.RegisterForTest();
                if (!Wait(delegate { return !form.Register.Running; }, 30000)) { Fail("注册超时"); }
                Pump(300);
                Check("注册后状态", form.Register.PillText, "已授权");
                Check("剩余天数", form.Register.DaysText, "365 天");

                form.Command.SendForTest("getsn", "");
                if (!Wait(delegate { return !form.Command.Running; }, 10000)) { Fail("命令超时"); }
                Pump(200);
                if (!form.Command.LogText.Contains(MockController.Serial))
                {
                    Fail("命令回包未显示");
                }
                else
                {
                    Console.WriteLine("PASS 命令收发");
                }

                // 手动重启示教器：用伪造 ssh.exe 验证 C 侧的进程、管道与判定逻辑。
                form.Register.RestartForTest();
                if (!Wait(delegate { return !form.Register.Running; }, 30000)) { Fail("重启超时"); }
                Pump(300);
                if (form.Register.LogText.Contains("重启命令已发送"))
                {
                    Console.WriteLine("PASS 手动重启示教器");
                }
                else
                {
                    Fail("手动重启未返回成功消息：" + form.Register.LogText);
                }

                form.Close();
            }
        }

        AddressSettings.OverridePath = null;
        try { File.Delete(settingsPath); } catch (IOException) { }

        Console.WriteLine(failures == 0 ? "界面回归全部通过" : failures + " 项失败");
        return failures == 0 ? 0 : 1;
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