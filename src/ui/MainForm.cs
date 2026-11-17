using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

/* 窗口外壳：左侧栏（导航 / 控制器地址 / 管理员）＋右侧工作区（两个页面）。
   UI 规格见 Docs/astryx-ui-spec.md。 */
internal sealed class MainForm : Form
{
    private const int DefaultPort = 7625;
    private const int DefaultTimeout = 5000;

    private readonly Panel sidebar = new Panel();
    private readonly Panel workspace = new Panel();
    private readonly SegmentedTabs tabs = new SegmentedTabs("注册", "命令调试");
    private readonly Label addressCaption = new Label();
    private readonly TextField[] octets = new TextField[4];
    private readonly int[] dotLeft = new int[3];
    private int dotTop;
    private readonly TextField portField = new TextField("端口");
    private readonly Label portCaption = new Label();
    private readonly ActionButton editAddress = new ActionButton("修改", ButtonKind.Secondary);
    private readonly ActionButton saveAddress = new ActionButton("保存", ButtonKind.Primary);
    private readonly ActionButton cancelAddress = new ActionButton("取消", ButtonKind.Quiet);
    private readonly string[] savedOctets = new string[4];
    private string savedPort = string.Empty;
    private bool editingAddress;
    private readonly ActionButton adminButton = new ActionButton("管理员登录", ButtonKind.Secondary);
    private readonly TextField passwordField = new TextField("管理员密码");
    private readonly ActionButton passwordOk = new ActionButton("登录", ButtonKind.Primary);
    private readonly ActionButton passwordCancel = new ActionButton("取消", ButtonKind.Quiet);
    private readonly RegisterPage registerPage;
    private readonly CommandPage commandPage;

    private bool built;
    private bool restoring;
    private bool saveEnabled = true;
    private bool admin;
    private bool logging;
    private bool busy;
    private int timeoutMs = DefaultTimeout;
    private string encodingName = "UTF-8";

    internal MainForm(int initialPage)
    {
        Text = "QJAR 工具";
        BackColor = Theme.Body;
        Font = Theme.Body14;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(Theme.WindowWidth), Theme.S(Theme.WindowHeight));
        MinimumSize = new Size(Theme.S(Theme.WindowMinWidth) + 16, Theme.S(Theme.WindowMinHeight) + 39);
        DoubleBuffered = true;
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
        BuildUi();
        LoadSettings();
        SetBusy(false);
        SelectPage(initialPage == 1 ? 1 : 0, true);
    }

    /* 仅供测试程序驱动界面，发布版不会用到。 */
    internal RegisterPage Register { get { return registerPage; } }

    internal CommandPage Command { get { return commandPage; } }

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
        string[] parts = new string[4];
        for (int i = 0; i < 4; i++)
        {
            parts[i] = octets[i].Text.Trim();
            int value;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
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
        ip = string.Join(".", parts);
        port = number;
        SaveSettings();
        return true;
    }

    internal void SetBusy(bool value)
    {
        busy = value;
        registerPage.SetBusy(value);
        commandPage.SetBusy(value);
        editAddress.Enabled = !value;
        saveAddress.Enabled = !value;
        cancelAddress.Enabled = !value;
    }

    // ---- 界面搭建 -----------------------------------------------------------
    private void BuildUi()
    {
        sidebar.Dock = DockStyle.Left;
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


        tabs.SelectedIndexChanged += delegate { SelectPage(tabs.SelectedIndex); };

        addressCaption.Text = "控制器地址";
        addressCaption.ForeColor = Theme.TextSecondary;
        addressCaption.BackColor = Color.Transparent;

        for (int i = 0; i < 4; i++)
        {
            var field = new TextField(i == 0 ? "IP" : "");
            field.InnerPad = Theme.Space2;
            field.MaxLength = 3;
            field.Edit.TextAlign = HorizontalAlignment.Center;
            field.Edit.TextChanged += OnOctetChanged;
            field.Edit.KeyDown += OnOctetKeyDown;
            octets[i] = field;
            sidebar.Controls.Add(field);
        }
        portCaption.Text = "端口";
        portCaption.ForeColor = Theme.TextSecondary;
        portCaption.BackColor = Color.Transparent;

        portField.MaxLength = 5;
        portField.Edit.TextChanged += delegate { portField.Invalid = false; };
        portField.Accepted += delegate
        {
            if (editingAddress) { CommitAddressEdit(); } else { SaveSettings(); }
        };
        portField.EditKeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && editingAddress) { e.Handled = true; e.SuppressKeyPress = true; CancelAddressEdit(); }
        };
        editAddress.Click += delegate { BeginAddressEdit(); };
        saveAddress.Click += delegate { CommitAddressEdit(); };
        cancelAddress.Click += delegate { CancelAddressEdit(); };

        adminButton.Click += delegate
        {
            if (admin) { SetAdmin(false); }
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
            tabs, addressCaption, portCaption, portField, editAddress, saveAddress, cancelAddress,
            adminButton, passwordField, passwordOk, passwordCancel
        });

        workspace.Dock = DockStyle.Fill;
        workspace.BackColor = Theme.Body;
        workspace.Controls.Add(registerPage);
        workspace.Controls.Add(commandPage);

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
        addressCaption.Font = Theme.Caption12;
        portCaption.Font = Theme.Caption12;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Theme.SetScale(DeviceDpi / 96f);
        ClientSize = new Size(Theme.S(Theme.WindowWidth), Theme.S(Theme.WindowHeight));
        MinimumSize = new Size(Theme.S(Theme.WindowMinWidth) + 16, Theme.S(Theme.WindowMinHeight) + 39);
        ApplyFonts();
        LayoutUi();

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
        LayoutUi();
    }

    private void LayoutUi()
    {
        if (!built) { return; }
        int pad = Theme.S(Theme.Space6);
        int width = Theme.S(Theme.SidebarWidth);
        sidebar.Width = width;
        int inner = width - pad * 2;
        int bar = Theme.S(Theme.SizeLg);
        int captionHeight = Theme.Line(Theme.Caption12);

        tabs.Vertical = true;
        tabs.SetBounds(Theme.S(Theme.Space3), Theme.S(Theme.Space4), width - Theme.S(Theme.Space3) * 2, Theme.S(Theme.SizeLg) * 2 + Theme.S(Theme.Space1));
        addressCaption.SetBounds(pad, tabs.Bottom + Theme.S(Theme.Space6), inner - Theme.S(64), captionHeight);

        int octetWidth = Theme.S(54);
        int dotWidth = Theme.S(8);
        int top = addressCaption.Bottom + Theme.S(Theme.Space3);
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

        // 地址行右侧的“修改 / 保存 / 取消”，高 28（size-element-sm）。
        int small = Theme.S(Theme.SizeSm);
        int editWidth = Theme.S(52);
        int smallTop = addressCaption.Top + (captionHeight - small) / 2 - Theme.S(2);
        editAddress.SetBounds(width - pad - editWidth, smallTop, editWidth, small);

        // 保存 / 取消放在端口下方，避免和标题行挤在一起。
        int actionTop = portField.Bottom + Theme.S(Theme.Space2);
        int saveWidth = Theme.S(72);
        int cancelWidth = Theme.S(60);
        saveAddress.SetBounds(width - pad - saveWidth, actionTop, saveWidth, small);
        cancelAddress.SetBounds(saveAddress.Left - Theme.S(Theme.Space2) - cancelWidth, actionTop, cancelWidth, small);

        // 管理员区：登录时用密码框替换状态行，避免和按钮叠在一起。
        int bottom = sidebar.Height - Theme.S(Theme.Space6);
        passwordCancel.SetBounds(width - pad - Theme.S(72), bottom - bar, Theme.S(72), bar);
        passwordOk.SetBounds(passwordCancel.Left - Theme.S(Theme.Space2) - Theme.S(88), bottom - bar, Theme.S(88), bar);
        passwordField.SetBounds(pad, passwordCancel.Top - Theme.S(Theme.Space2) - Theme.S(Theme.SizeMd),
            inner, Theme.S(Theme.SizeMd));
        adminButton.SetBounds(pad, bottom - bar, inner, bar);

        // 工作区：顶部标签条，下面放页面。

        workspace.SetBounds(sidebar.Width, 0, ClientSize.Width - sidebar.Width, ClientSize.Height);
        registerPage.SetBounds(0, 0, workspace.Width, workspace.Height);
        commandPage.SetBounds(0, 0, workspace.Width, workspace.Height);
        Invalidate(true);
    }
    private void SelectPage(int index)
    {
        SelectPage(index, false);
    }

    private void SelectPage(int index, bool startup)
    {
        if (busy) { return; }
        if (tabs.SelectedIndex != index) { tabs.SelectedIndex = index; }
        registerPage.Visible = index == 0;
        commandPage.Visible = index == 1;
        if (!startup) { if (index == 0) { registerPage.FocusFirst(); } else { commandPage.FocusFirst(); } }
    }

    // ---- 地址输入 -----------------------------------------------------------
    private void OnOctetChanged(object sender, EventArgs e)
    {
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
            if (e.KeyCode == Keys.Escape && editingAddress)
            {
                e.Handled = e.SuppressKeyPress = true;
                CancelAddressEdit();
                return;
            }
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
        editingAddress = value;
        for (int i = 0; i < 4; i++)
        {
            octets[i].IsReadOnly = !value;
            octets[i].Invalid = false;
        }
        portField.IsReadOnly = !value;
        portField.Invalid = false;
        editAddress.Visible = !value;
        saveAddress.Visible = value;
        cancelAddress.Visible = value;
    }

    private void BeginAddressEdit()
    {
        if (busy) { return; }
        for (int i = 0; i < 4; i++) { savedOctets[i] = octets[i].Text; }
        savedPort = portField.Text;
        SetAddressEditing(true);
        octets[0].Edit.Focus();
        octets[0].Edit.SelectAll();
    }

    private void CommitAddressEdit()
    {
        string ip;
        int port;
        if (!TryGetAddress(out ip, out port)) { return; }
        SetAddressEditing(false);
    }

    private void CancelAddressEdit()
    {
        for (int i = 0; i < 4; i++) { octets[i].Text = savedOctets[i]; }
        portField.Text = savedPort;
        SetAddressEditing(false);
    }

    private void LoadSettings()
    {
        restoring = true;
        try
        {
            string[] values = AddressSettings.Load();
            string[] parts = values[0].Split('.');
            for (int i = 0; i < 4; i++)
            {
                octets[i].Text = i < parts.Length ? parts[i] : "0";
            }
            portField.Text = values[1];
            int parsed;
            TimeoutMs = int.TryParse(values[2], out parsed) ? parsed : DefaultTimeout;
            EncodingName = values[3];
            // 记住上一次的登录状态：登录过就一直是管理员，直到点“退出管理”。
            SetAdmin(values[4] == "1");
        }
        finally
        {
            restoring = false;
        }
    }

    internal void SaveSettings()
    {
        if (!saveEnabled) { return; }
        try
        {
            string ip;
            int port;
            string[] parts = new string[4];
            bool valid = true;
            for (int i = 0; i < 4; i++)
            {
                parts[i] = octets[i].Text.Trim();
                int value;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                    value < 0 || value > 255)
                {
                    valid = false;
                }
            }
            int number;
            if (!int.TryParse(portField.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                number < 1 || number > 65535)
            {
                valid = false;
            }
            if (!valid) { return; }
            ip = string.Join(".", parts);
            port = number;
            AddressSettings.Save(System.Net.IPAddress.Parse(ip), port, timeoutMs, encodingName, admin);
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
        // 登录 / 退出管理后立刻记住状态；启动时恢复的那次不写回。
        if (built && !restoring) { SaveSettings(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        base.OnFormClosing(e);
    }
}