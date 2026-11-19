using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

/* 窗口外壳：左侧栏（导航 / 控制器地址 / 管理员）＋右侧工作区（两个页面）。
   UI 规格见 Docs/astryx-ui-spec.md。 */
internal sealed class MainForm : Form
{
    internal const string WindowTitle = "QJAR 工具";
    private static readonly int[] PageOrder = { 2, 0, 1, 3 };
    private const int DefaultPort = 7625;
    private const int DefaultTimeout = 5000;

    private readonly Panel sidebar = new Panel();
    private readonly Panel workspace = new Panel();
    private readonly SegmentedTabs tabs = new SegmentedTabs("编译", "注册", "命令调试", "升级") { SlideSelection = true };
    private readonly SegmentedTabs profiles = new SegmentedTabs("实体控制器", "Ubuntu 虚拟机") { SlideSelection = true };
    private readonly TextField[] octets = new TextField[4];
    private readonly int[] dotLeft = new int[3];
    private int dotTop;
    private readonly TextField portField = new TextField("端口");
    private readonly Label ipCaption = new Label();
    private readonly Label portCaption = new Label();
    private readonly GearButton settingsButton = new GearButton();
    private Credentials.Values credentials = new Credentials.Values();
    private readonly string[] profileIp = new string[2];
    private readonly string[] profilePort = new string[2];
    private int profile;
    private bool applyingProfile;
    private readonly ActionButton adminButton = new ActionButton("管理员登录", ButtonKind.Secondary);
    private readonly TextField passwordField = new TextField("管理员密码");
    private readonly ActionButton passwordOk = new ActionButton("登录", ButtonKind.Primary);
    private readonly ActionButton passwordCancel = new ActionButton("取消", ButtonKind.Quiet);
    private readonly RegisterPage registerPage;
    private readonly CommandPage commandPage;
    private readonly BuildPage buildPage;
    private readonly RemoteBuildPage remoteBuild;
    private readonly Panel buildWorkspace = new Panel { BackColor = Theme.Body };
    private readonly SegmentedTabs buildModes = new SegmentedTabs("本机 WSL", "Ubuntu 远程") { Vertical = false, SlideSelection = true };
    private int currentPage = 2;
    private readonly UpgradePage upgradePage;

    private bool built;
    private bool restoring;
    private bool saveEnabled = true;
    private bool admin;
    private bool logging;
    private bool busy;
    private bool minimized;
    private bool restoreCloaked;
    private int timeoutMs = DefaultTimeout;
    private string encodingName = "UTF-8";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private bool Cloak(bool hidden)
    {
        int value = hidden ? 1 : 0;
        return DwmSetWindowAttribute(Handle, 13, ref value, sizeof(int)) == 0;
    }

    private void PaintFrame()
    {
        // 系统缩放动画读取窗口缓存前，先完成整棵子窗口的绘制与 GDI 提交。
        RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
        GdiFlush();
    }

    protected override void WndProc(ref Message message)
    {
        if (built && minimized && !restoreCloaked && message.Msg == 0x0046 && !IsIconic(Handle))
        { restoreCloaked = Cloak(true); }
        try
        {
            base.WndProc(ref message);
            if (restoreCloaked && message.Msg == 0x0047 && !IsIconic(Handle))
            {
                try { PaintFrame(); }
                finally { Cloak(false); restoreCloaked = false; minimized = false; }
            }
        }
        catch
        {
            if (restoreCloaked) { Cloak(false); restoreCloaked = false; }
            throw;
        }
    }

    internal MainForm(int initialPage)
    {
        Text = WindowTitle;
        BackColor = Theme.Body;
        Font = Theme.Body14;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(Theme.WindowWidth), Theme.S(Theme.WindowHeight));
        MinimumSize = new Size(Theme.S(Theme.WindowMinWidth) + 16, Theme.S(Theme.WindowMinHeight) + 39);
        // 自绘控件各自缓冲，主窗体保留完整的原生窗口画面供恢复动画使用。
        DoubleBuffered = false;
        try
        {
            using (System.IO.Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("registration-tool.ico"))
            {
                if (stream != null) { Icon = new Icon(stream); }
            }
        }
        catch (Exception) { }

        registerPage = new RegisterPage(this);
        commandPage = new CommandPage(this);
        buildPage = new BuildPage(this);
        remoteBuild = new RemoteBuildPage(this);
        upgradePage = new UpgradePage(this);
        BuildUi();
        LoadSettings();
        SetBusy(false);
        SelectPage(initialPage >= 0 && initialPage <= 3 ? initialPage : currentPage, true);
    }

    /* 仅供测试程序驱动界面，发布版不会用到。 */
    internal RegisterPage Register { get { return registerPage; } }

    internal CommandPage Command { get { return commandPage; } }

    internal BuildPage Build { get { return buildPage; } }

    internal void SetAddressForTest(string ip, int port)
    {
        saveEnabled = false;
        string[] parts = ip.Split('.');
        for (int i = 0; i < 4 && i < parts.Length; i++) { octets[i].Text = parts[i]; }
        portField.Text = port.ToString(CultureInfo.InvariantCulture);
    }

    internal void SetAdminForTest(bool value) { SetAdmin(value); }

    // ---- 供页面读取的共享状态 -----------------------------------------------
    internal bool IsAdmin { get { return admin; } }

    internal string ControllerIp { get { return ReadAddressText(); } }

    internal bool IsBusy { get { return busy; } }
    internal void SavePreferences() { if (built && !restoring) { SaveSettings(); } }

    internal void UpgradeBuildPackage(string path)
    {
        if (admin && !busy)
        {
            SelectPage(3);
            upgradePage.UpgradeBuildPackage(path);
        }
    }

    internal int TimeoutMs
    {
        get { return timeoutMs; }
        set { timeoutMs = Math.Max(1, Math.Min(600000, value)); }
    }

    internal string EncodingName
    {
        get { return encodingName; }
        set { encodingName = value == "GBK" ? "GBK" : "UTF-8"; }
    }

    internal bool TryGetAddress(out string ip, out int port)
    {
        ip = null;
        port = 0;
        for (int i = 0; i < 4; i++)
        {
            string part = octets[i].Text.Trim();
            int value;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value < 0 || value > 255)
            {
                octets[i].Invalid = true;
                octets[i].Edit.Focus();
                octets[i].Edit.SelectAll();
                return false;
            }
        }
        int number;
        if (!int.TryParse(portField.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
            number < 1 || number > 65535)
        {
            portField.Invalid = true;
            portField.Edit.Focus();
            portField.Edit.SelectAll();
            return false;
        }
        ip = ReadAddressText();
        port = number;
        SaveSettings();
        return true;
    }

    /* 不弹错误、不聚焦的读取：切换档位和保存设置时用。 */
    private bool ReadAddress(out string ip, out int port)
    {
        ip = null;
        port = 0;
        var parts = new string[4];
        for (int i = 0; i < 4; i++)
        {
            parts[i] = octets[i].Text.Trim();
            int value;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value < 0 || value > 255)
            {
                return false;
            }
        }
        int number;
        if (!int.TryParse(portField.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
            number < 1 || number > 65535)
        {
            return false;
        }
        ip = string.Join(".", parts);
        port = number;
        return true;
    }

    private string ReadAddressText()
    {
        var parts = new string[4];
        for (int i = 0; i < 4; i++) { parts[i] = octets[i].Text.Trim(); }
        return string.Join(".", parts);
    }

    internal void SetBusy(bool value)
    {
        busy = value;
        registerPage.SetBusy(value);
        commandPage.SetBusy(value);
        buildPage.SetBusy(value);
        buildModes.Enabled = !value;
        profiles.Enabled = !value;
        upgradePage.SetBusy(value);
        tabs.Enabled = !value;
    }

    // ---- 界面搭建 -----------------------------------------------------------
    private void BuildUi()
    {
        sidebar.Width = Theme.S(Theme.SidebarWidth);
        sidebar.BackColor = Theme.Surface;
        sidebar.Paint += delegate(object sender, PaintEventArgs e)
        {
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
            }
            int size = Theme.S(4);
            using (var brush = new SolidBrush(Theme.TextSecondary))
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                for (int i = 0; i < 3; i++)
                {
                    int cx = dotLeft[i] + Theme.S(8) / 2 - size / 2;
                    int cy = dotTop + Theme.S(Theme.SizeMd) / 2 - size / 2;
                    e.Graphics.FillEllipse(brush, cx, cy, size, size);
                }
            }
        };


        tabs.SelectedIndexChanged += delegate
        {
            int page = PageOrder[tabs.SelectedIndex];
            if (page != currentPage) { SelectPage(page); }
        };
        profiles.Vertical = false;
        profiles.TabStop = false;
        profiles.FocusOnClick = false;
        profiles.SelectedIndexChanged += delegate { SwitchProfile(profiles.SelectedIndex); };

        for (int i = 0; i < 4; i++)
        {
            var field = new TextField(i == 0 ? "IP" : "");
            field.InnerPad = Theme.Space2;
            field.MaxLength = 3;
            field.Edit.TextAlign = HorizontalAlignment.Center;
            field.Edit.AccessibleName = "IP 地址第 " + (i + 1) + " 段";
            field.Edit.TextChanged += OnOctetChanged;
            field.Edit.KeyDown += OnOctetKeyDown;
            octets[i] = field;
            sidebar.Controls.Add(field);
        }
        ipCaption.Text = "IP 地址";
        ipCaption.ForeColor = Theme.TextSecondary;
        ipCaption.BackColor = Color.Transparent;
        portCaption.Text = "控制器端口";
        portCaption.ForeColor = Theme.TextSecondary;
        portCaption.BackColor = Color.Transparent;

        portField.MaxLength = 5;
        portField.Edit.TextChanged += delegate { portField.Invalid = false; };
        portField.Accepted += delegate { SaveSettings(); };

        settingsButton.Click += delegate { OpenSettings(); };

        adminButton.Click += delegate
        {
            if (admin)
            {
                try { Admin.Forget(); SetAdmin(false); }
                catch (Exception error) { MessageBox.Show(this, "无法清除登录：" + error.Message, WindowTitle); }
            }
            else { ShowLogin(true); }
        };

        passwordField.UseSystemPasswordChar = true;
        passwordField.MaxLength = 64;
        passwordField.Accepted += delegate { TryLogin(); };
        passwordField.EditKeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { ShowLogin(false); }
        };
        passwordOk.Click += delegate { TryLogin(); };
        passwordCancel.Click += delegate { ShowLogin(false); };

        sidebar.Controls.AddRange(new Control[]
        {
            tabs, profiles, ipCaption, portCaption, portField, settingsButton,
            adminButton, passwordField, passwordOk, passwordCancel
        });

        workspace.BackColor = Theme.Body;
        workspace.Controls.Add(registerPage);
        workspace.Controls.Add(commandPage);
        remoteBuild.Visible = false;
        buildWorkspace.Controls.AddRange(new Control[] { buildPage, remoteBuild, buildModes });
        buildModes.SelectedIndexChanged += delegate { ApplyBuildMode(); SavePreferences(); };
        workspace.Controls.Add(buildWorkspace);
        workspace.Controls.Add(upgradePage);

        tabs.BringToFront();
        Controls.Add(workspace);
        Controls.Add(sidebar);

        ShowLogin(false);
        SetAdmin(false);
        ApplyFonts();
        built = true;
        SetAddressEditing(false);
    }

    private void ApplyFonts()
    {
        ipCaption.Font = portCaption.Font = Theme.Caption12;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Theme.SetScale(DeviceDpi / 96f);
        ClientSize = new Size(Theme.S(Theme.WindowWidth), Theme.S(Theme.WindowHeight));
        MinimumSize = new Size(Theme.S(Theme.WindowMinWidth) + 16, Theme.S(Theme.WindowMinHeight) + 39);
        ApplyFonts();
        LayoutUi();
        Invalidate(true);

    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Theme.SetScale(e.DeviceDpiNew / 96f);
        ApplyFonts();
        LayoutUi();

    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) { minimized = true; return; }
        LayoutUi();
        Invalidate(true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutUi();
        Invalidate(true);
    }

    private void LayoutUi()
    {
        if (!built || WindowState == FormWindowState.Minimized || ClientSize.Width <= 1 || ClientSize.Height <= 1) { return; }
        int pad = Theme.S(Theme.Space6);
        int width = Theme.S(Theme.SidebarWidth);
        // 统一手动布局，最小化时保留子窗口尺寸与绘图缓存。
        sidebar.SetBounds(0, 0, width, ClientSize.Height);
        int inner = width - pad * 2;
        int bar = Theme.S(Theme.SizeLg);
        int captionHeight = Theme.Line(Theme.Caption12);

        tabs.Vertical = true;
        tabs.SetBounds(Theme.S(Theme.Space3), Theme.S(Theme.Space4), width - Theme.S(Theme.Space3) * 2,
            Theme.S(Theme.SizeLg) * 4 + Theme.S(Theme.Space1));
        profiles.SetBounds(pad, tabs.Bottom + Theme.S(Theme.Space5), inner, Theme.S(Theme.SizeSm));

        int octetWidth = Theme.S(54);
        int dotWidth = Theme.S(8);
        int small = Theme.S(Theme.SizeSm);
        int addressTop = profiles.Bottom + Theme.S(Theme.Space2);
        ipCaption.SetBounds(pad, addressTop, inner, captionHeight);
        int top = ipCaption.Bottom + Theme.S(Theme.Space2);
        int x = pad;
        dotTop = top;
        for (int i = 0; i < 4; i++)
        {
            octets[i].SetBounds(x, top, octetWidth, Theme.S(Theme.SizeMd));
            x += octetWidth;
            if (i < 3)
            {
                dotLeft[i] = x;
                x += dotWidth;
            }
        }
        portCaption.SetBounds(pad, octets[0].Bottom + Theme.S(Theme.Space3), inner, captionHeight);
        portField.SetBounds(pad, portCaption.Bottom, inner, Theme.S(Theme.SizeMd));

        // 管理员区：登录时用密码框替换状态行，避免和按钮叠在一起。
        int bottom = sidebar.Height - Theme.S(Theme.Space6);
        settingsButton.SetBounds(pad, bottom - bar, bar, bar);
        passwordCancel.SetBounds(width - pad - Theme.S(72), bottom - bar, Theme.S(72), bar);
        passwordOk.SetBounds(passwordCancel.Left - Theme.S(Theme.Space2) - Theme.S(88), bottom - bar, Theme.S(88), bar);
        passwordField.SetBounds(pad, passwordCancel.Top - Theme.S(Theme.Space2) - Theme.S(Theme.SizeMd),
            inner, Theme.S(Theme.SizeMd));
        adminButton.SetBounds(settingsButton.Right + Theme.S(Theme.Space2), bottom - bar,
            width - pad - settingsButton.Right - Theme.S(Theme.Space2), bar);

        // 工作区：顶部标签条，下面放页面。

        workspace.SetBounds(sidebar.Width, 0, ClientSize.Width - sidebar.Width, ClientSize.Height);
        registerPage.SetBounds(0, 0, workspace.Width, workspace.Height);
        commandPage.SetBounds(0, 0, workspace.Width, workspace.Height);
        buildWorkspace.SetBounds(0, 0, workspace.Width, workspace.Height);
        int buildHeader = Theme.S(50);
        buildModes.SetBounds(pad, Theme.S(10), Theme.S(265), Theme.S(36));
        buildPage.SetBounds(0, buildHeader, workspace.Width, Math.Max(0, workspace.Height - buildHeader));
        remoteBuild.Bounds = buildPage.Bounds;
        upgradePage.SetBounds(0, 0, workspace.Width, workspace.Height);
        Invalidate(true);
    }
    private void SelectPage(int index)
    {
        SelectPage(index, false);
    }

    private void SelectPage(int index, bool startup)
    {
        if (busy) { return; }
        int tab = Array.IndexOf(PageOrder, index);
        currentPage = index;
        if (tabs.SelectedIndex != tab) { tabs.SelectedIndex = tab; }
        if (index != 2) { buildWorkspace.Visible = false; }
        registerPage.Visible = index == 0;
        commandPage.Visible = index == 1;
        if (index == 2)
        {
            ApplyBuildMode();
            buildWorkspace.Visible = true;
        }
        upgradePage.Visible = index == 3;
        if (!startup)
        {
            if (index == 0) { registerPage.FocusFirst(); }
            else if (index == 1) { commandPage.FocusFirst(); }
            else if (index == 2)
            {
                if (buildModes.SelectedIndex == 0) { buildPage.FocusFirst(); }
                else { remoteBuild.FocusFirst(); }
            }
            else
            {
                upgradePage.FocusFirst();
            }
            SavePreferences();
        }
    }

    private void ApplyBuildMode()
    {
        bool remote = buildModes.SelectedIndex == 1;
        buildPage.Visible = !remote;
        remoteBuild.Visible = remote;
    }

    private void UpdateTargets()
    {
        remoteBuild.SetHost(profileIp[1], credentials.UbuntuUser, credentials.UbuntuPassword);
    }

    internal Credentials.Values SshCredentials { get { return credentials; } }

    /* 齿轮按钮：地址、SSH 凭据与管理员口令都在这里改，保存后立即生效。 */
    private void OpenSettings()
    {
        if (busy) { return; }
        using (var dialog = new SettingsForm(profileIp[0], profilePort[0], profileIp[1], profilePort[1],
            credentials, admin))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
            if (!string.IsNullOrEmpty(dialog.NewPassword))
            {
                try
                {
                    Admin.SetPassword(dialog.NewPassword);
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, "保存口令失败：" + error.Message, WindowTitle);
                }
            }
            credentials = dialog.SshCredentials ?? credentials;
            try { Credentials.Save(credentials); }
            catch (Exception error)
            {
                MessageBox.Show(this, "保存 SSH 凭据失败：" + error.Message, WindowTitle);
            }
            profileIp[0] = dialog.Ip1;
            profilePort[0] = dialog.Port1;
            profileIp[1] = dialog.Ip2;
            profilePort[1] = dialog.Port2;
            ApplyProfile();
            SaveSettings();
            UpdateTargets();
            registerPage.ClearForAddressChange();
        }
    }

    // ---- 地址输入 -----------------------------------------------------------
    private void OnOctetChanged(object sender, EventArgs e)
    {
        if (applyingProfile) { return; }
        for (int i = 0; i < 4; i++)
        {
            if (octets[i].Edit != sender) { continue; }
            octets[i].Invalid = false;
            string text = octets[i].Text;
            if (text.Length == 3 && i < 3) { octets[i + 1].Edit.Focus(); octets[i + 1].Edit.SelectAll(); }
            return;
        }
    }

    private void OnOctetKeyDown(object sender, KeyEventArgs e)
    {
        for (int i = 0; i < 4; i++)
        {
            if (octets[i].Edit != sender) { continue; }
            if (e.KeyCode == Keys.OemPeriod || e.KeyCode == Keys.Decimal)
            {
                if (i < 3) { octets[i + 1].Edit.Focus(); octets[i + 1].Edit.SelectAll(); }
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Right && octets[i].Edit.SelectionStart >= octets[i].Text.Length && i < 3)
            {
                octets[i + 1].Edit.Focus();
                octets[i + 1].Edit.SelectAll();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Left && octets[i].Edit.SelectionStart == 0 && i > 0)
            {
                octets[i - 1].Edit.Focus();
                octets[i - 1].Edit.SelectAll();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                portField.Edit.Focus();
                portField.Edit.SelectAll();
            }
            else if (e.KeyCode == Keys.V && e.Control)
            {
                string clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";

                if (clip.Contains("."))
                {
                    string[] parts = clip.Replace(" ", "").Split('.', ':');
                    for (int j = 0; j < 4 && j < parts.Length; j++)
                    {
                        int value;
                        if (int.TryParse(parts[j], out value) && value >= 0 && value <= 255)
                        {
                            octets[j].Text = value.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    if (parts.Length > 4)
                    {
                        int value;
                        if (int.TryParse(parts[4], out value) && value >= 1 && value <= 65535)
                        {
                            portField.Text = value.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    e.Handled = e.SuppressKeyPress = true;
                    portField.Edit.Focus();
                }
            }
            return;
        }
    }

    // ---- 地址编辑：默认只读，点“修改”才可编辑 ----
    private void SetAddressEditing(bool value)
    {
        for (int i = 0; i < 4; i++)
        {
            octets[i].IsReadOnly = true;
            octets[i].Invalid = false;
        }
        portField.IsReadOnly = true;
        portField.Invalid = false;
    }

    private void LoadSettings()
    {
        restoring = true;
        try
        {
            AddressSettings.Values values = AddressSettings.Load();
            profileIp[0] = values.Ip1;
            profilePort[0] = values.Port1;
            profileIp[1] = values.Ip2;
            profilePort[1] = values.Port2;
            profile = values.Profile;
            profiles.SelectedIndex = profile;
            ApplyProfile();
            int parsed;
            TimeoutMs = int.TryParse(values.Timeout, out parsed) ? parsed : DefaultTimeout;
            EncodingName = values.Encoding;
            // 记住上一次的登录状态：登录过就一直是管理员，直到点“退出管理”。
            currentPage = values.Page;
            buildModes.SelectedIndex = values.BuildMode;
            registerPage.SetPendantMode(values.VirtualPendant);
            credentials = Credentials.Load();
            SetAdmin(Admin.IsRemembered());
            UpdateTargets();
        }
        finally
        {
            restoring = false;
        }
    }

    /* 把当前档位的地址写进输入框。整体赋值时屏蔽“输满自动前进”，否则焦点会被带到最后一格并全选。 */
    private void ApplyProfile()
    {
        string ip = profileIp[profile] ?? "0.0.0.0";
        string[] parts = ip.Split('.');
        applyingProfile = true;
        try
        {
            for (int i = 0; i < 4; i++)
            {
                octets[i].Text = i < parts.Length ? parts[i] : "0";
                octets[i].Invalid = false;
            }
            portField.Text = profilePort[profile] ?? "7625";
            portField.Invalid = false;
        }
        finally
        {
            applyingProfile = false;
        }
    }

    /* 切换「地址 1 / 地址 2」：先把当前输入框存回当前档，再载入另一档。 */
    private void SwitchProfile(int index)
    {
        if (!built || restoring || busy || index < 0 || index > 1 || index == profile) { return; }
        string ip;
        int port;
        if (ReadAddress(out ip, out port))
        {
            profileIp[profile] = ip;
            profilePort[profile] = port.ToString(CultureInfo.InvariantCulture);
        }
        SuspendLayout();
        try
        {
            profile = index;
            ApplyProfile();
            registerPage.ClearForAddressChange();
        }
        finally
        {
            ResumeLayout(false);
        }
        // 先让界面把新地址画出来，再落盘，避免切换瞬间有卡顿。
        BeginInvoke((Action)SaveSettings);
    }

    internal void SwitchProfileForTest(int index)
    {
        profiles.SelectedIndex = index;
        SwitchProfile(index);
    }

    internal int ActiveProfile { get { return profile; } }

    internal void SaveSettings()
    {
        if (!saveEnabled) { return; }
        try
        {
            string ip;
            int port;
            if (ReadAddress(out ip, out port))
            {
                profileIp[profile] = ip;
                profilePort[profile] = port.ToString(CultureInfo.InvariantCulture);
            }
            var values = new AddressSettings.Values
            {
                Ip1 = profileIp[0] ?? "192.168.2.100",
                Port1 = profilePort[0] ?? "7625",
                Ip2 = profileIp[1] ?? "192.168.247.130",
                Port2 = profilePort[1] ?? "7625",
                Timeout = timeoutMs.ToString(CultureInfo.InvariantCulture),
                Encoding = encodingName,
                Page = currentPage,
                BuildMode = buildModes.SelectedIndex,
                VirtualPendant = registerPage.VirtualPendant,
                Profile = profile
            };
            AddressSettings.Save(values);
        }
        catch (Exception)
        {
        }
    }

    // ---- 管理员 -------------------------------------------------------------
    private void ShowLogin(bool visible)
    {
        logging = visible;
        passwordField.Visible = visible;
        passwordOk.Visible = visible;
        passwordCancel.Visible = visible;
        adminButton.Visible = !visible;
        if (visible)
        {
            passwordField.Text = "";
            passwordField.Invalid = false;
            passwordField.Edit.Focus();
            adminButton.Text = "取消登录";
        }
        else
        {
            adminButton.Text = admin ? "退出管理" : "管理员登录";
        }
        LayoutUi();
    }

    private void TryLogin()
    {
        if (Admin.Matches(passwordField.Text))
        {
            passwordField.Text = "";
            ShowLogin(false);
            SetAdmin(true);
            try { Admin.Remember(); }
            catch (Exception error) { MessageBox.Show(this, "无法记住登录：" + error.Message, WindowTitle); }
        }
        else
        {
            passwordField.Invalid = true;
            passwordField.Text = "";
            passwordField.Edit.Focus();
        }
    }

    private void SetAdmin(bool value)
    {
        admin = value;
        if (logging) { ShowLogin(false); }
        adminButton.Text = value ? "退出管理" : "管理员登录";
        registerPage.SetAdmin(value);
        commandPage.SetAdmin(value);
        buildPage.SetAdmin(value);
        remoteBuild.SetAdmin(value);
        upgradePage.RefreshAdmin();
        // 登录 / 退出管理后立刻记住状态；启动时恢复的那次不写回。
        if (built && !restoring) { SaveSettings(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (remoteBuild.Running)
        {
            e.Cancel = true;
            return;
        }
        remoteBuild.StopWork();
        buildPage.StopWork();
        upgradePage.StopWork();
        SaveSettings();
        base.OnFormClosing(e);
    }
}
