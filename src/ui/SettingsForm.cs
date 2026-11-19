using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

/* 设置弹框：管理员口令、两套连接地址、SSH 用户名与密码，并可直接做联通测试。
   地址与 SSH 凭据保存后由主窗口重新读取；口令改动必须先通过当前口令校验。 */
internal sealed class SettingsForm : Form
{
    internal const string PendantIp = "192.168.1.200";

    private readonly Panel scroll = new Panel();
    private readonly Card passwordCard = new Card("管理员口令");
    private readonly TextField currentPassword = new TextField("当前口令");
    private readonly TextField newPassword = new TextField("新口令（留空表示不改）");
    private readonly TextField confirmPassword = new TextField("再次输入新口令");
    private readonly Label passwordHint = Caption("默认口令 888999；QJROBOT 始终可作为后备口令。");

    private readonly Card addressCard = new Card("连接地址");
    private readonly Label controllerLabel = Caption("实体控制器");
    private readonly TextField controllerIp = new TextField("192.168.2.100");
    private readonly TextField controllerPort = new TextField("端口");
    private readonly Label ubuntuLabel = Caption("Ubuntu 虚拟机");
    private readonly TextField ubuntuIp = new TextField("192.168.247.130");
    private readonly TextField ubuntuPort = new TextField("端口");

    private readonly Card sshCard = new Card("SSH 登录");
    private readonly Label controllerSshLabel = Caption("实体控制器");
    private readonly TextField controllerUser = new TextField("用户名");
    private readonly TextField controllerPassword = new TextField("密码（留空沿用密钥登录）");
    private readonly ActionButton controllerTest = new ActionButton("测试", ButtonKind.Quiet);
    private readonly Label controllerResult = Caption("");
    private readonly Label pendantSshLabel = Caption("示教器");
    private readonly TextField pendantUser = new TextField("用户名");
    private readonly TextField pendantPassword = new TextField("密码（留空沿用密钥登录）");
    private readonly ActionButton pendantTest = new ActionButton("测试", ButtonKind.Quiet);
    private readonly Label pendantResult = Caption("");
    private readonly Label ubuntuSshLabel = Caption("Ubuntu 虚拟机");
    private readonly TextField ubuntuUser = new TextField("用户名");
    private readonly TextField ubuntuPassword = new TextField("密码（留空沿用密钥登录）");
    private readonly ActionButton ubuntuTest = new ActionButton("测试", ButtonKind.Quiet);
    private readonly Label ubuntuResult = Caption("");
    private readonly Label sshHint = Caption("密码按当前 Windows 用户加密保存；留空时仍用 OpenSSH 密钥或免密码登录。");

    private readonly Banner banner = new Banner();
    private readonly ActionButton save = new ActionButton("保存", ButtonKind.Primary);
    private readonly ActionButton cancel = new ActionButton("取消", ButtonKind.Quiet);

    private readonly bool admin;
    private int testing;
    private readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 16 };
    private int fadeStart;

    internal SettingsForm(string ip1, string port1, string ip2, string port2,
        Credentials.Values credentials, bool isAdmin)
    {
        admin = isAdmin;
        Text = "设置";
        BackColor = Theme.Body;
        Font = Theme.Body14;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        int width = Theme.S(700);
        int height = Theme.S(620);
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        height = Math.Min(height, work.Height - Theme.S(70));
        ClientSize = new Size(width, height);
        MinimumSize = new Size(Theme.S(600), Theme.S(460));
        try
        {
            using (System.IO.Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("registration-tool.ico"))
            {
                if (stream != null) { Icon = new Icon(stream); }
            }
        }
        catch (Exception) { }

        scroll.AutoScroll = true;
        scroll.BackColor = Theme.Body;
        Controls.AddRange(new Control[] { scroll, banner, save, cancel });

        currentPassword.UseSystemPasswordChar = true;
        newPassword.UseSystemPasswordChar = true;
        confirmPassword.UseSystemPasswordChar = true;
        controllerPassword.UseSystemPasswordChar = true;
        pendantPassword.UseSystemPasswordChar = true;
        ubuntuPassword.UseSystemPasswordChar = true;

        foreach (TextField field in new[] { controllerPort, ubuntuPort }) { field.MaxLength = 5; }
        controllerIp.Text = ip1;
        controllerPort.Text = port1;
        ubuntuIp.Text = ip2;
        ubuntuPort.Text = port2;

        controllerUser.Text = credentials.ControllerUser;
        controllerPassword.Text = credentials.ControllerPassword;
        pendantUser.Text = credentials.PendantUser;
        pendantPassword.Text = credentials.PendantPassword;
        ubuntuUser.Text = credentials.UbuntuUser;
        ubuntuPassword.Text = credentials.UbuntuPassword;

        passwordCard.Controls.AddRange(new Control[]
        {
            currentPassword, newPassword, confirmPassword, passwordHint
        });
        addressCard.Controls.AddRange(new Control[]
        {
            controllerLabel, controllerIp, controllerPort, ubuntuLabel, ubuntuIp, ubuntuPort
        });
        sshCard.Controls.AddRange(new Control[]
        {
            controllerSshLabel, controllerUser, controllerPassword, controllerTest, controllerResult,
            pendantSshLabel, pendantUser, pendantPassword, pendantTest, pendantResult,
            ubuntuSshLabel, ubuntuUser, ubuntuPassword, ubuntuTest, ubuntuResult, sshHint
        });
        scroll.Controls.AddRange(new Control[] { passwordCard, addressCard, sshCard });

        controllerTest.Click += delegate { Test(controllerUser, controllerPassword, controllerIp.Text, controllerResult, controllerTest); };
        pendantTest.Click += delegate { Test(pendantUser, pendantPassword, PendantIp, pendantResult, pendantTest); };
        ubuntuTest.Click += delegate { Test(ubuntuUser, ubuntuPassword, ubuntuIp.Text, ubuntuResult, ubuntuTest); };
        save.Click += delegate { Save(); };
        cancel.Click += delegate { Close(); };

        if (!admin) { SetSectionsEnabled(false); }
        HideBanner();
    }

    internal string Ip1 { get; private set; }
    internal string Port1 { get; private set; }
    internal string Ip2 { get; private set; }
    internal string Port2 { get; private set; }
    internal string NewPassword { get; private set; }
    internal Credentials.Values SshCredentials { get; private set; }

    internal void TestForTest(string user, string password, string host, Label result)
    {
        int milliseconds;
        string error = SshClient.Test(user, host, password, out milliseconds);
        result.ForeColor = error == null ? Theme.Success : Theme.Error;
        result.Text = error == null ? "连接成功（" + milliseconds + " ms）" : "失败：" + error;
    }

    private void SetSectionsEnabled(bool enabled)
    {
        passwordCard.Enabled = enabled;
        sshCard.Enabled = enabled;
        passwordHint.Text = enabled ? "默认口令 888999；QJROBOT 始终可作为后备口令。" : "口令与 SSH 凭据需要先登录管理员。";
    }

    private void Test(TextField user, TextField password, string host, Label result, ActionButton button)
    {
        if (Interlocked.CompareExchange(ref testing, 1, 0) != 0) { return; }
        button.Enabled = false;
        result.ForeColor = Theme.TextSecondary;
        result.Text = "正在连接…";
        string userText = user.Text.Trim();
        string passwordText = password.Text;
        string target = host;
        new Thread(delegate()
        {
            int milliseconds;
            string error = SshClient.Test(userText, target, passwordText, out milliseconds);
            try
            {
                BeginInvoke((Action)delegate
                {
                    button.Enabled = true;
                    if (error == null)
                    {
                        result.ForeColor = Theme.Success;
                        result.Text = "连接成功（" + milliseconds + " ms）";
                    }
                    else
                    {
                        result.ForeColor = Theme.Error;
                        result.Text = "失败：" + error;
                    }
                    Interlocked.Exchange(ref testing, 0);
                });
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref testing, 0);
            }
        }) { IsBackground = true }.Start();
    }

    private void Save()
    {
        string ip1 = controllerIp.Text.Trim();
        string ip2 = ubuntuIp.Text.Trim();
        string port1 = controllerPort.Text.Trim();
        string port2 = ubuntuPort.Text.Trim();
        System.Net.IPAddress parsedIp;
        int parsedPort;
        if (!AddressSettings.TryParse(ip1, port1, out parsedIp, out parsedPort))
        {
            ShowBanner(Tone.Error, "实体控制器地址或端口无效。");
            return;
        }
        if (!AddressSettings.TryParse(ip2, port2, out parsedIp, out parsedPort))
        {
            ShowBanner(Tone.Error, "Ubuntu 虚拟机地址或端口无效。");
            return;
        }
        if (admin)
        {
            string fresh = newPassword.Text;
            if (fresh.Length > 0)
            {
                if (fresh.Length < 4)
                {
                    ShowBanner(Tone.Error, "新口令至少 4 个字符。");
                    return;
                }
                if (fresh != confirmPassword.Text)
                {
                    ShowBanner(Tone.Error, "两次输入的新口令不一致。");
                    return;
                }
                if (!Admin.Matches(currentPassword.Text))
                {
                    currentPassword.Invalid = true;
                    ShowBanner(Tone.Error, "当前口令不正确。");
                    return;
                }
                NewPassword = fresh;
            }
            foreach (TextField field in new[] { controllerUser, pendantUser, ubuntuUser })
            {
                if (field.Text.Trim().Length == 0)
                {
                    field.Invalid = true;
                    ShowBanner(Tone.Error, "SSH 用户名不能为空。");
                    return;
                }
            }
            SshCredentials = new Credentials.Values
            {
                ControllerUser = controllerUser.Text.Trim(),
                ControllerPassword = controllerPassword.Text,
                PendantUser = pendantUser.Text.Trim(),
                PendantPassword = pendantPassword.Text,
                UbuntuUser = ubuntuUser.Text.Trim(),
                UbuntuPassword = ubuntuPassword.Text
            };
        }
        Ip1 = ip1;
        Port1 = port1;
        Ip2 = ip2;
        Port2 = port2;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LayoutDialog();
    }

    /* 入场用 Astryx 的 medium 时长淡入（410 ms），系统要求减少动态效果时直接呈现。 */
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Theme.ReduceMotion()) { return; }
        Opacity = 0;
        fadeStart = Environment.TickCount;
        fade.Tick += delegate
        {
            float t = (Environment.TickCount - fadeStart) / (float)Theme.DurationMedium;
            Opacity = Theme.Ease(Math.Min(1f, t));
            if (t >= 1f) { fade.Stop(); }
        };
        fade.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { fade.Dispose(); }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutDialog();
    }

    private void LayoutDialog()
    {
        int pad = Theme.S(Theme.Space6);
        int footer = Theme.S(104);
        scroll.SetBounds(pad, pad, ClientSize.Width - pad * 2, ClientSize.Height - pad * 2 - footer);
        banner.SetBounds(pad, scroll.Bottom + Theme.S(Theme.Space3), ClientSize.Width - pad * 2, Theme.S(34));
        cancel.SetBounds(ClientSize.Width - pad - Theme.S(88), banner.Bottom + Theme.S(Theme.Space3),
            Theme.S(88), Theme.S(Theme.SizeLg));
        save.SetBounds(cancel.Left - Theme.S(Theme.Space2) - Theme.S(88), cancel.Top,
            Theme.S(88), Theme.S(Theme.SizeLg));

        int width = Math.Max(Theme.S(420), scroll.ClientSize.Width - Theme.S(Theme.Space3));
        int cardPad = Theme.S(Theme.Space5);
        int field = Theme.S(Theme.SizeMd);
        int gap = Theme.S(Theme.Space2);
        int inner = width - cardPad * 2;
        int y = 0;

        passwordCard.SetBounds(0, y, width, passwordCard.ContentTop + field * 3 + gap * 2 + Theme.S(22) + cardPad);
        int top = passwordCard.ContentTop;
        currentPassword.SetBounds(cardPad, top, inner, field);
        newPassword.SetBounds(cardPad, currentPassword.Bottom + gap, inner, field);
        confirmPassword.SetBounds(cardPad, newPassword.Bottom + gap, inner, field);
        passwordHint.SetBounds(cardPad, confirmPassword.Bottom + gap, inner, Theme.S(20));
        y = passwordCard.Bottom + Theme.S(Theme.Space4);

        addressCard.SetBounds(0, y, width, addressCard.ContentTop + (field + gap) * 2 + Theme.S(8) + cardPad);
        top = addressCard.ContentTop;
        controllerLabel.SetBounds(cardPad, top, Theme.S(96), field);
        controllerPort.SetBounds(width - cardPad - Theme.S(96), top, Theme.S(96), field);
        controllerIp.SetBounds(controllerLabel.Right + gap, top,
            controllerPort.Left - gap - controllerLabel.Right - gap, field);
        int row2 = top + field + gap;
        ubuntuLabel.SetBounds(cardPad, row2, Theme.S(96), field);
        ubuntuPort.SetBounds(width - cardPad - Theme.S(96), row2, Theme.S(96), field);
        ubuntuIp.SetBounds(ubuntuLabel.Right + gap, row2,
            ubuntuPort.Left - gap - ubuntuLabel.Right - gap, field);
        y = addressCard.Bottom + Theme.S(Theme.Space4);

        int rowHeight = field + Theme.S(24);
        sshCard.SetBounds(0, y, width, sshCard.ContentTop + rowHeight * 3 + Theme.S(20) + cardPad);
        top = sshCard.ContentTop;
        LayoutSshRow(controllerSshLabel, controllerUser, controllerPassword, controllerTest, controllerResult,
            cardPad, inner, top, field);
        top += rowHeight;
        LayoutSshRow(pendantSshLabel, pendantUser, pendantPassword, pendantTest, pendantResult,
            cardPad, inner, top, field);
        top += rowHeight;
        LayoutSshRow(ubuntuSshLabel, ubuntuUser, ubuntuPassword, ubuntuTest, ubuntuResult,
            cardPad, inner, top, field);
        sshHint.SetBounds(cardPad, top + field + Theme.S(4), inner, Theme.S(20));
        y = sshCard.Bottom + Theme.S(Theme.Space4);
        scroll.AutoScrollMinSize = new Size(0, y);
    }

    private static void LayoutSshRow(Label label, TextField user, TextField password, ActionButton test,
        Label result, int cardPad, int inner, int top, int field)
    {
        int labelWidth = Theme.S(96);
        int testWidth = Theme.S(64);
        label.SetBounds(cardPad, top, labelWidth, field);
        test.SetBounds(cardPad + inner - testWidth, top, testWidth, Theme.S(Theme.SizeSm));
        int right = test.Left - Theme.S(Theme.Space2);
        int half = (right - (cardPad + labelWidth + Theme.S(Theme.Space2))) / 2 - Theme.S(Theme.Space2);
        user.SetBounds(cardPad + labelWidth + Theme.S(Theme.Space2), top, half, field);
        password.SetBounds(user.Right + Theme.S(Theme.Space2), top, right - user.Right - Theme.S(Theme.Space2), field);
        result.SetBounds(cardPad + labelWidth + Theme.S(Theme.Space2), top + field - Theme.S(2), inner - labelWidth, Theme.S(18));
    }

    private void ShowBanner(Tone tone, string text)
    {
        banner.Tone = tone;
        banner.Text = text;
        banner.Visible = true;
        LayoutDialog();
    }

    private void HideBanner()
    {
        banner.Visible = false;
        LayoutDialog();
    }

    private static Label Caption(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Caption12,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }
}