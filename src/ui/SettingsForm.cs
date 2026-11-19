using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

/* 设置：左侧分类列表 + 右侧内容，参考用户给的列表式设置界面。
   账号（管理员登录 / 退出 / 改口令）、连接地址、SSH 登录三块分开，不再挤在一页。 */
internal sealed class SettingsForm : Form
{
    internal const string PendantIp = "192.168.1.200";
    private const int NavWidth = 200;

    /* 左侧分类列表：图标 + 文字，选中态用浅色胶囊。 */
    private sealed class Nav : Control
    {
        private readonly string[] items;
        private int selected;
        private int hovered = -1;

        internal Nav(params string[] labels)
        {
            items = labels;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            BackColor = Theme.Surface;
        }

        internal event EventHandler SelectedIndexChanged;

        internal int SelectedIndex
        {
            get { return selected; }
            set
            {
                int next = Math.Max(0, Math.Min(items.Length - 1, value));
                if (next == selected) { return; }
                selected = next;
                Invalidate();
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) { handler(this, EventArgs.Empty); }
            }
        }

        private Rectangle Row(int index)
        {
            int height = Theme.S(40);
            return new Rectangle(Theme.S(Theme.Space2), Theme.S(Theme.Space2) + index * (height + Theme.S(2)),
                Width - Theme.S(Theme.Space2) * 2, height);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = -1;
            for (int i = 0; i < items.Length; i++) { if (Row(i).Contains(e.Location)) { index = i; break; } }
            if (index != hovered) { hovered = index; Cursor = index >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovered = -1;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < items.Length; i++)
            {
                if (Row(i).Contains(e.Location)) { Focus(); SelectedIndex = i; return; }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down) { SelectedIndex = selected + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { SelectedIndex = selected - 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            for (int i = 0; i < items.Length; i++)
            {
                Rectangle row = Row(i);
                bool isSelected = i == selected;
                if (isSelected || i == hovered)
                {
                    Theme.Fill(g, row, Theme.S(Theme.RadiusElement),
                        isSelected ? Theme.OverlayPressed : Theme.OverlayHover);
                }
                int iconSize = Theme.S(16);
                var icon = new Rectangle(row.X + Theme.S(Theme.Space3), row.Y + (row.Height - iconSize) / 2,
                    iconSize, iconSize);
                DrawGlyph(g, icon, i, isSelected ? Theme.TextPrimary : Theme.TextSecondary);
                var label = new Rectangle(icon.Right + Theme.S(Theme.Space2), row.Y,
                    row.Right - icon.Right - Theme.S(Theme.Space2), row.Height);
                Theme.Text(g, items[i], label, isSelected ? Theme.Body14Medium : Theme.Body14,
                    isSelected ? Theme.TextPrimary : Theme.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        /* 0 账号（人像）、1 地址（网络）、2 SSH（终端提示符）。 */
        private static void DrawGlyph(Graphics g, Rectangle box, int index, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, Math.Max(1.2f, 1.4f * Theme.Scale)))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (index == 0)
                {
                    int r = box.Width / 5;
                    g.DrawEllipse(pen, box.X + box.Width / 2 - r, box.Y + Theme.S(2), r * 2, r * 2);
                    g.DrawArc(pen, box.X + Theme.S(2), box.Y + box.Height / 2, box.Width - Theme.S(4),
                        box.Height / 2, 200, 140);
                }
                else if (index == 1)
                {
                    int cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
                    int r = box.Width / 3;
                    g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                    g.DrawLine(pen, box.X + Theme.S(1), cy, box.Right - Theme.S(1), cy);
                    g.DrawLine(pen, cx, box.Y + Theme.S(1), cx, box.Bottom - Theme.S(1));
                }
                else
                {
                    int top = box.Y + Theme.S(3);
                    int bottom = box.Bottom - Theme.S(3);
                    g.DrawLines(pen, new[]
                    {
                        new Point(box.X + Theme.S(3), top),
                        new Point(box.X + Theme.S(6), (top + bottom) / 2),
                        new Point(box.X + Theme.S(3), bottom)
                    });
                    g.DrawLine(pen, box.X + Theme.S(8), bottom, box.Right - Theme.S(1), bottom);
                }
            }
            g.SmoothingMode = old;
        }
    }

    private readonly Nav nav = new Nav("账号", "连接地址", "SSH 登录");
    private readonly Panel content = new Panel { BackColor = Theme.Body };
    private readonly Card accountCard = new Card("账号");
    private readonly Label accountState = Caption("");
    private readonly TextField loginPassword = new TextField("管理员密码");
    private readonly ActionButton login = new ActionButton("登录", ButtonKind.Primary);
    private readonly ActionButton logout = new ActionButton("退出管理", ButtonKind.Secondary);
    private readonly TextField currentPassword = new TextField("当前口令");
    private readonly TextField newPassword = new TextField("新口令（留空表示不改）");
    private readonly TextField confirmPassword = new TextField("再次输入新口令");
    private readonly Label passwordHint = Caption("默认口令 888999；QJROBOT 始终可作为后备口令。");

    private readonly Card addressCard = new Card("连接地址");
    private readonly SegmentedTabs target = new SegmentedTabs("实体控制器", "Ubuntu 虚拟机")
        { Vertical = false, SlideSelection = true };
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
    private readonly Func<string, bool> tryLogin;
    private readonly Action doLogout;
    private readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 16 };
    private bool admin;
    private int fadeStart;
    private int testing;

    internal SettingsForm(string ip1, string port1, string ip2, string port2, int profile,
        Credentials.Values credentials, bool isAdmin, Func<string, bool> tryLogin, Action doLogout)
    {
        admin = isAdmin;
        this.tryLogin = tryLogin;
        this.doLogout = doLogout;
        Text = "设置";
        BackColor = Theme.Body;
        Font = Theme.Body14;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        int width = Theme.S(760);
        int height = Theme.S(600);
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        height = Math.Min(height, work.Height - Theme.S(70));
        ClientSize = new Size(width, height);
        MinimumSize = new Size(Theme.S(640), Theme.S(440));
        try
        {
            using (System.IO.Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("registration-tool.ico"))
            {
                if (stream != null) { Icon = new Icon(stream); }
            }
        }
        catch (Exception) { }

        Controls.AddRange(new Control[] { nav, content, banner, save, cancel });
        content.Controls.AddRange(new Control[] { accountCard, addressCard, sshCard });

        foreach (TextField field in new[]
        {
            loginPassword, currentPassword, newPassword, confirmPassword,
            controllerPassword, pendantPassword, ubuntuPassword
        })
        {
            field.UseSystemPasswordChar = true;
        }
        foreach (TextField field in new[] { controllerPort, ubuntuPort }) { field.MaxLength = 5; }

        target.SelectedIndex = profile == 1 ? 1 : 0;
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

        accountCard.Controls.AddRange(new Control[]
        {
            accountState, loginPassword, login, logout, currentPassword, newPassword, confirmPassword, passwordHint
        });
        addressCard.Controls.AddRange(new Control[]
        {
            target, controllerLabel, controllerIp, controllerPort, ubuntuLabel, ubuntuIp, ubuntuPort
        });
        sshCard.Controls.AddRange(new Control[]
        {
            controllerSshLabel, controllerUser, controllerPassword, controllerTest, controllerResult,
            pendantSshLabel, pendantUser, pendantPassword, pendantTest, pendantResult,
            ubuntuSshLabel, ubuntuUser, ubuntuPassword, ubuntuTest, ubuntuResult, sshHint
        });

        nav.SelectedIndexChanged += delegate { ShowSection(nav.SelectedIndex); };
        login.Click += delegate { DoLogin(); };
        logout.Click += delegate { DoLogout(); };
        loginPassword.Accepted += delegate { DoLogin(); };
        controllerTest.Click += delegate { Test(controllerUser, controllerPassword, controllerIp.Text, controllerResult, controllerTest); };
        pendantTest.Click += delegate { Test(pendantUser, pendantPassword, PendantIp, pendantResult, pendantTest); };
        ubuntuTest.Click += delegate { Test(ubuntuUser, ubuntuPassword, ubuntuIp.Text, ubuntuResult, ubuntuTest); };
        save.Click += delegate { Save(); };
        cancel.Click += delegate { Close(); };

        RefreshAccount();
        ShowSection(0);
        HideBanner();
    }

    internal int Profile { get; private set; }
    internal string Ip1 { get; private set; }
    internal string Port1 { get; private set; }
    internal string Ip2 { get; private set; }
    internal string Port2 { get; private set; }
    internal Credentials.Values SshCredentials { get; private set; }

    internal void OpenSectionForTest(int index) { nav.SelectedIndex = index; }

    private void ShowSection(int index)
    {
        accountCard.Visible = index == 0;
        addressCard.Visible = index == 1;
        sshCard.Visible = index == 2;
        LayoutDialog();
    }

    private void RefreshAccount()
    {
        accountState.Text = admin ? "当前：管理员" : "当前：普通用户";
        loginPassword.Visible = !admin;
        login.Visible = !admin;
        logout.Visible = admin;
        currentPassword.Enabled = admin;
        newPassword.Enabled = admin;
        confirmPassword.Enabled = admin;
        passwordHint.Text = admin
            ? "默认口令 888999；QJROBOT 始终可作为后备口令。"
            : "登录管理员后才能修改口令。";
        LayoutDialog();
    }

    private void DoLogin()
    {
        if (tryLogin == null) { return; }
        if (tryLogin(loginPassword.Text))
        {
            loginPassword.Text = "";
            admin = true;
            RefreshAccount();
            HideBanner();
        }
        else
        {
            loginPassword.Invalid = true;
            loginPassword.Text = "";
            ShowBanner(Tone.Error, "管理员口令不正确。");
        }
    }

    private void DoLogout()
    {
        if (doLogout != null) { doLogout(); }
        admin = false;
        RefreshAccount();
    }

    private void Test(TextField user, TextField password, string host, Label result, ActionButton button)
    {
        if (Interlocked.CompareExchange(ref testing, 1, 0) != 0) { return; }
        button.Enabled = false;
        result.ForeColor = Theme.TextSecondary;
        result.Text = "正在连接…";
        string userText = user.Text.Trim();
        string passwordText = password.Text;
        new Thread(delegate()
        {
            int milliseconds;
            string error = SshClient.Test(userText, host, passwordText, out milliseconds);
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
            nav.SelectedIndex = 1;
            return;
        }
        if (!AddressSettings.TryParse(ip2, port2, out parsedIp, out parsedPort))
        {
            ShowBanner(Tone.Error, "Ubuntu 虚拟机地址或端口无效。");
            nav.SelectedIndex = 1;
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
                    nav.SelectedIndex = 0;
                    return;
                }
                if (fresh != confirmPassword.Text)
                {
                    ShowBanner(Tone.Error, "两次输入的新口令不一致。");
                    nav.SelectedIndex = 0;
                    return;
                }
                if (!Admin.Matches(currentPassword.Text))
                {
                    currentPassword.Invalid = true;
                    ShowBanner(Tone.Error, "当前口令不正确。");
                    nav.SelectedIndex = 0;
                    return;
                }
                try { Admin.SetPassword(fresh); }
                catch (Exception error)
                {
                    ShowBanner(Tone.Error, "保存口令失败：" + error.Message);
                    return;
                }
            }
            foreach (TextField field in new[] { controllerUser, pendantUser, ubuntuUser })
            {
                if (field.Text.Trim().Length == 0)
                {
                    field.Invalid = true;
                    ShowBanner(Tone.Error, "SSH 用户名不能为空。");
                    nav.SelectedIndex = 2;
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
        Profile = target.SelectedIndex == 1 ? 1 : 0;
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

    /* 入场用 Astryx 的 medium 时长（410 ms）淡入；系统要求减少动态效果时直接呈现。 */
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
        int footer = Theme.S(96);
        int navWidth = Theme.S(NavWidth);
        nav.SetBounds(pad, pad, navWidth, ClientSize.Height - pad * 2 - footer);
        content.SetBounds(nav.Right + Theme.S(Theme.Space2), pad,
            ClientSize.Width - nav.Right - Theme.S(Theme.Space2) - pad,
            Math.Max(Theme.S(240), ClientSize.Height - pad * 2 - footer));
        banner.SetBounds(pad, nav.Bottom + Theme.S(Theme.Space3), ClientSize.Width - pad * 2, Theme.S(34));
        cancel.SetBounds(ClientSize.Width - pad - Theme.S(88), banner.Bottom + Theme.S(Theme.Space3),
            Theme.S(88), Theme.S(Theme.SizeLg));
        save.SetBounds(cancel.Left - Theme.S(Theme.Space2) - Theme.S(88), cancel.Top,
            Theme.S(88), Theme.S(Theme.SizeLg));
        LayoutSection();
    }

    private void LayoutSection()
    {
        int cardPad = Theme.S(Theme.Space5);
        int field = Theme.S(Theme.SizeMd);
        int gap = Theme.S(Theme.Space2);
        int width = Math.Max(Theme.S(300), content.Width);
        int inner = width - cardPad * 2;
        if (accountCard.Visible)
        {
            int top = accountCard.ContentTop;
            accountState.SetBounds(cardPad, top, inner, Theme.S(20));
            int y = accountState.Bottom + gap;
            loginPassword.SetBounds(cardPad, y, inner - Theme.S(88) - gap, field);
            login.SetBounds(width - cardPad - Theme.S(88), y, Theme.S(88), field);
            logout.SetBounds(cardPad, y, Theme.S(88), field);
            y += field + Theme.S(Theme.Space3);
            currentPassword.SetBounds(cardPad, y, inner, field);
            newPassword.SetBounds(cardPad, currentPassword.Bottom + gap, inner, field);
            confirmPassword.SetBounds(cardPad, newPassword.Bottom + gap, inner, field);
            passwordHint.SetBounds(cardPad, confirmPassword.Bottom + gap, inner, Theme.S(20));
            accountCard.SetBounds(0, 0, width, passwordHint.Bottom + cardPad);
        }
        if (addressCard.Visible)
        {
            int top = addressCard.ContentTop;
            target.SetBounds(cardPad, top, Theme.S(280), Theme.S(Theme.SizeSm));
            top = target.Bottom + Theme.S(Theme.Space3);
            controllerLabel.SetBounds(cardPad, top, Theme.S(96), field);
            controllerPort.SetBounds(width - cardPad - Theme.S(96), top, Theme.S(96), field);
            controllerIp.SetBounds(controllerLabel.Right + gap, top,
                controllerPort.Left - gap - controllerLabel.Right - gap, field);
            int row2 = top + field + gap;
            ubuntuLabel.SetBounds(cardPad, row2, Theme.S(96), field);
            ubuntuPort.SetBounds(width - cardPad - Theme.S(96), row2, Theme.S(96), field);
            ubuntuIp.SetBounds(ubuntuLabel.Right + gap, row2,
                ubuntuPort.Left - gap - ubuntuLabel.Right - gap, field);
            addressCard.SetBounds(0, 0, width, ubuntuIp.Bottom + cardPad);
        }
        if (sshCard.Visible)
        {
            int top = sshCard.ContentTop;
            int rowHeight = field + Theme.S(24);
            LayoutSshRow(controllerSshLabel, controllerUser, controllerPassword, controllerTest, controllerResult,
                cardPad, inner, top, field);
            top += rowHeight;
            LayoutSshRow(pendantSshLabel, pendantUser, pendantPassword, pendantTest, pendantResult,
                cardPad, inner, top, field);
            top += rowHeight;
            LayoutSshRow(ubuntuSshLabel, ubuntuUser, ubuntuPassword, ubuntuTest, ubuntuResult,
                cardPad, inner, top, field);
            sshHint.SetBounds(cardPad, top + field + Theme.S(4), inner, Theme.S(20));
            sshCard.SetBounds(0, 0, width, sshHint.Bottom + cardPad);
        }
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
        result.SetBounds(cardPad + labelWidth + Theme.S(Theme.Space2), top + field - Theme.S(2),
            inner - labelWidth, Theme.S(18));
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