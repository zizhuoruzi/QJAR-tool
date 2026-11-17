using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

/* 注册页：设备状态、查询/注册/取消、进度、日志。
   动作走 QJCore.dll 的 controller_run，注册成功后按原行为自动 SSH 重启示教器。 */
internal sealed class RegisterPage : Panel
{
    private readonly MainForm shell;
    private readonly Card deviceCard = new Card("设备状态");
    private readonly StatusPill statePill = new StatusPill();
    private readonly Label serialCaption = Caption("序列号");
    private readonly Label serialValue = Value("—");
    private readonly Label daysCaption = Caption("授权剩余");
    private readonly Label daysValue = Value("—");
    private readonly Card actionCard = new Card("操作");
    private readonly ActionButton query = new ActionButton("查询状态", ButtonKind.Secondary);
    private readonly ActionButton register = new ActionButton("注册设备", ButtonKind.Primary);
    private readonly ActionButton restart = new ActionButton("重启示教器", ButtonKind.Secondary);
    private readonly ActionButton cancel = new ActionButton("取消", ButtonKind.Quiet);
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Label progressText = Caption("等待操作");
    private readonly Label progressPercent = Caption("0%");
    private readonly Banner banner = new Banner();
    private readonly Card logCard = new Card("操作日志");
    private readonly LogView log = new LogView("暂无记录。点击“查询状态”开始。");
    private readonly ActionButton exportLog = new ActionButton("导出日志", ButtonKind.Quiet);
    private readonly ActionButton clearLog = new ActionButton("清空", ButtonKind.Quiet);

    /* 测试程序会关掉注册成功后的自动重启，避免真的去连现场示教器。 */
    internal static bool AutoRestart = true;

    /* 测试时指向伪造的 ssh.exe；发布版为 null，走系统 OpenSSH。 */
    internal static string PendantSshPath;

    private CoreCancel cancelHandle;
    private bool running;

    internal RegisterPage(MainForm shell)
    {
        this.shell = shell;
        BackColor = Theme.Body;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        query.Click += delegate { Start(false); };
        register.Click += delegate { Start(true); };
        restart.Click += delegate { Restart(); };
        cancel.Click += delegate { CancelOperation(); };
        clearLog.Click += delegate { log.Clear(); };
        exportLog.Click += delegate { ExportLog(); };

        Controls.AddRange(new Control[]
        {
            deviceCard, actionCard, logCard
        });
        deviceCard.Controls.AddRange(new Control[]
        {
            statePill, serialCaption, serialValue, daysCaption, daysValue
        });
        actionCard.Controls.AddRange(new Control[]
        {
            query, register, restart, cancel, progress, progressText, progressPercent, banner
        });
        logCard.Controls.AddRange(new Control[] { log, exportLog, clearLog });

        HideBanner();
        statePill.Caption = "等待查询";
        statePill.Tone = Tone.Neutral;
    }

    internal void FocusFirst() { query.Focus(); }

    /* 测试钩子：驱动同一条真实路径，不另写一套逻辑。 */
    internal bool Running { get { return running; } }

    internal void QueryForTest() { Start(false); }

    internal void RegisterForTest() { Start(true); }

    internal void RestartForTest() { Restart(); }

    internal string PillText { get { return statePill.Caption; } }

    internal string PillCaptionForTest { set { statePill.Caption = value; } }

    internal int PillWidth { get { return statePill.Width; } }

    internal string SerialText { get { return serialValue.Text; } }

    internal string DaysText { get { return daysValue.Text; } }

    internal string LogText { get { return log.AllText; } }

    internal void SetAdmin(bool admin)
    {
        if (running) { return; }
        register.Enabled = admin;
        restart.Enabled = admin;
    }

    internal void SetBusy(bool busy)
    {
        running = busy;
        query.Enabled = !busy;
        register.Enabled = !busy && shell.IsAdmin;
        restart.Enabled = !busy && shell.IsAdmin;
        cancel.Enabled = busy;
    }

    /* 换了一台控制器（切换地址档位或手改地址）后清掉上一台设备的状态，
       避免把旧序列号、旧授权状态当成当前地址的结果。 */
    internal void ClearForAddressChange()
    {
        if (running) { return; }
        serialValue.Text = "—";
        daysValue.Text = "—";
        statePill.Caption = "等待查询";
        statePill.Tone = Tone.Neutral;
        progress.Value = 0;
        progress.Tone = Tone.Neutral;
        progressText.Text = "等待操作";
        progressPercent.Text = "0%";
        HideBanner();
        log.Clear();
    }

    // ---- 布局 ---------------------------------------------------------------
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutPage();
    }

    private void LayoutPage()
    {
        int pad = Theme.S(Theme.Space6);
        int gap = Theme.S(Theme.Space4);
        int width = Math.Max(Theme.S(320), Width - pad * 2);
        int cardPad = Theme.S(Theme.Space5);
        int bar = Theme.S(Theme.SizeLg);
        int textHeight = Theme.S(Theme.FontSm) + Theme.S(8);

        deviceCard.SetBounds(pad, pad, width, Theme.S(133));
        int contentTop = deviceCard.ContentTop;
        statePill.SetBounds(cardPad, contentTop, statePill.Width, Theme.S(Theme.SizeSm));
        int daysRight = width - cardPad;
        daysValue.SetBounds(daysRight - Theme.S(64), contentTop, Theme.S(64), Theme.S(Theme.FontXl) + Theme.S(6));
        daysValue.TextAlign = ContentAlignment.TopRight;
        daysCaption.SetBounds(daysValue.Left - Theme.S(88), contentTop + Theme.S(9), Theme.S(88), textHeight);
        daysCaption.TextAlign = ContentAlignment.TopRight;
        daysValue.Font = Theme.Heading20;
        int serialTop = contentTop + Theme.S(Theme.SizeSm) + Theme.S(Theme.Space3);
        serialCaption.SetBounds(cardPad, serialTop, Theme.S(56), Theme.S(Theme.FontBase) + Theme.S(6));
        serialValue.SetBounds(cardPad + Theme.S(56), serialTop, width - cardPad * 2 - Theme.S(56),
            Theme.S(Theme.FontBase) + Theme.S(6));

        int actionHeight = banner.Visible ? Theme.S(187) : Theme.S(153);
        actionCard.SetBounds(pad, deviceCard.Bottom + gap, width, actionHeight);
        int actionTop = actionCard.ContentTop;
        int x = cardPad;
        query.SetBounds(x, actionTop, Theme.S(108), bar);
        x = query.Right + Theme.S(Theme.Space2);
        register.SetBounds(x, actionTop, Theme.S(108), bar);
        x = register.Right + Theme.S(Theme.Space2);
        restart.SetBounds(x, actionTop, Theme.S(128), bar);
        x = restart.Right + Theme.S(Theme.Space2);
        cancel.SetBounds(x, actionTop, Theme.S(84), bar);

        int progressTop = actionTop + bar + Theme.S(Theme.Space5);
        progress.SetBounds(cardPad, progressTop, width - cardPad * 2, Theme.S(6));
        progressText.SetBounds(cardPad, progress.Bottom + Theme.S(Theme.Space1),
            width - cardPad * 2 - Theme.S(64), textHeight);
        progressPercent.SetBounds(width - cardPad - Theme.S(60), progressText.Top, Theme.S(60), textHeight);
        progressPercent.TextAlign = ContentAlignment.TopRight;
        banner.SetBounds(cardPad, progressText.Bottom + Theme.S(Theme.Space2), width - cardPad * 2, Theme.S(34));

        int logTop = actionCard.Bottom + gap;
        logCard.SetBounds(pad, logTop, width, Math.Max(Theme.S(120), Height - pad - logTop));
        int toolbarY = Theme.S(Theme.Space5) - Theme.S(2);
        clearLog.SetBounds(logCard.Width - cardPad - Theme.S(64), toolbarY, Theme.S(64), Theme.S(Theme.SizeSm));
        exportLog.SetBounds(clearLog.Left - Theme.S(Theme.Space2) - Theme.S(88), toolbarY, Theme.S(88), Theme.S(Theme.SizeSm));
        int logY = Theme.S(Theme.Space5) + Theme.S(Theme.FontLg) + Theme.S(Theme.Space3);
        log.SetBounds(cardPad, logY, logCard.Width - cardPad * 2,
            Math.Max(Theme.S(60), logCard.Height - logY - cardPad));
    }
    // ---- 操作 ---------------------------------------------------------------
    private void Start(bool activate)
    {
        if (running) { return; }
        if (activate && !shell.IsAdmin)
        {
            ShowBanner(Tone.Error, "请先登录管理员。");
            return;
        }
        string ip;
        int port;
        if (!shell.TryGetAddress(out ip, out port)) { return; }

        HideBanner();
        log.Clear();
        Append("连接 " + ip + ":" + port);
        statePill.Caption = "正在连接控制器";
        statePill.Tone = Tone.Info;
        progress.Value = 5;
        progress.Tone = Tone.Info;
        progressText.Text = "正在连接控制器";
        progressPercent.Text = "5%";
        serialValue.Text = "—";
        daysValue.Text = "—";
        shell.SetBusy(true);
        cancelHandle = new CoreCancel();
        CoreCancel handle = cancelHandle;

        var thread = new Thread(delegate()
        {
            CoreResult result;
            string restartMessage = null;
            try
            {
                result = Core.Run(ip, port, activate, 5000, 110000, 2000,
                    Notify, handle.Handle);
                if (activate && AutoRestart && result.Outcome == CoreOutcome.Ok && result.Submitted)
                {
                    restartMessage = Core.RestartPendant(true, port, Notify, handle.Handle, PendantSshPath);
                }
            }
            catch (Exception error)
            {
                result = new CoreResult { Message = "界面异常：" + error.Message };
            }
            try
            {
                BeginInvoke((Action)delegate { Finish(result, restartMessage); });
            }
            catch (Exception)
            {
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private void Finish(CoreResult result, string restartMessage)
    {
        if (cancelHandle != null) { cancelHandle.Dispose(); cancelHandle = null; }
        shell.SetBusy(false);

        int days = result.Days;
        serialValue.Text = string.IsNullOrEmpty(result.Serial) ? "—" : result.Serial;
        daysValue.Text = result.HasState && result.Flag > 0 ? days + " 天" : "—";
        if (result.HasState && result.Flag > 0)
        {
            statePill.Caption = "已授权";
            statePill.Tone = Tone.Success;
            progress.Value = 100;
            progress.Tone = Tone.Success;
            progressText.Text = "已授权";
            progressPercent.Text = "100%";
        }
        else if (result.Outcome == CoreOutcome.Pending)
        {
            statePill.Caption = "等待查询";
            statePill.Tone = Tone.Warning;
            progress.Value = 80;
            progress.Tone = Tone.Warning;
            progressText.Text = "注册结果待确认，请再查询一次";
            progressPercent.Text = "80%";
            ShowBanner(Tone.Warning, "注册请求已发出，结果未确认；请用“查询状态”复查。");
        }
        else if (result.Outcome == CoreOutcome.Failed)
        {
            statePill.Caption = "操作失败";
            statePill.Tone = Tone.Error;
            progress.Value = 0;
            progress.Tone = Tone.Error;
            progressText.Text = result.Message;
            progressPercent.Text = "0%";
            ShowBanner(Tone.Error, result.Message);
        }
        else
        {
            statePill.Caption = result.HasState && result.Flag > 0 ? "已授权" : "未注册";
            statePill.Tone = result.HasState ? Tone.Neutral : Tone.Neutral;
            progress.Value = 100;
            progress.Tone = Tone.Neutral;
            progressText.Text = result.Message;
            progressPercent.Text = "100%";
        }

        Append(result.Message, result.Outcome == CoreOutcome.Failed ? Theme.Error
            : result.Outcome == CoreOutcome.Pending ? Theme.Warning
            : Theme.TextPrimary);
        if (!string.IsNullOrEmpty(restartMessage))
        {
            Append(restartMessage, restartMessage.Contains("失败") || restartMessage.Contains("未")
                ? Theme.Warning : Theme.TextSecondary);
        }
    }

    private void Restart()
    {
        if (running) { return; }
        if (!shell.IsAdmin)
        {
            ShowBanner(Tone.Error, "请先登录管理员。");
            return;
        }
        shell.SetBusy(true);
        Append("正在重启示教器 192.168.1.200。", Theme.TextSecondary);
        statePill.Caption = "正在重启示教器";
        statePill.Tone = Tone.Info;
        cancelHandle = new CoreCancel();
        CoreCancel handle = cancelHandle;
        var thread = new Thread(delegate()
        {
            string message;
            try
            {
                message = Core.RestartPendant(false, 7625, Notify, handle.Handle, PendantSshPath);
            }
            catch (Exception error)
            {
                message = "重启示教器失败：" + error.Message;
            }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (cancelHandle != null) { cancelHandle.Dispose(); cancelHandle = null; }
                    shell.SetBusy(false);
                    Append(message, Theme.TextPrimary);
                    statePill.Caption = message;
                    statePill.Tone = Tone.Neutral;
                });
            }
            catch (Exception)
            {
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private void CancelOperation()
    {
        if (cancelHandle != null) { cancelHandle.Cancel(); }
        progressText.Text = "正在停止本地等待";
    }

    private void Notify(int type, string text, string serial, int flag, int days, int progressValue)
    {
        try
        {
            BeginInvoke((Action)delegate
            {
                switch (type)
                {
                    case Core.NoticeLog:
                        Append(text);
                        break;
                    case Core.NoticeConnected:
                        statePill.Caption = "已连接";
                        statePill.Tone = Tone.Info;
                        break;
                    case Core.NoticeState:
                        if (flag > 0)
                        {
                            statePill.Caption = "已授权";
                            statePill.Tone = Tone.Success;
                        }
                        break;
                    case Core.NoticeProgress:
                        if (progressValue > 0) { progress.Value = progressValue; }
                        if (!string.IsNullOrEmpty(text)) { progressText.Text = text; }
                        progressPercent.Text = Math.Max(progress.Value, progressValue) + "%";
                        break;
                }
            });
        }
        catch (Exception)
        {
        }
    }

    private void Append(string line)
    {
        Append(line, Theme.TextPrimary);
    }

    private void Append(string line, Color color)
    {
        if (string.IsNullOrEmpty(line)) { return; }
        log.Append(line, color);
    }

    private void ShowBanner(Tone tone, string text)
    {
        banner.Tone = tone;
        banner.Text = text;
        banner.Visible = true;
        LayoutPage();
    }

    private void HideBanner()
    {
        if (!banner.Visible) { return; }
        banner.Visible = false;
        LayoutPage();
    }

    private void ExportLog()
    {
        if (!log.SaveTo(this, "QJAR-注册日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"))
        {
            if (!log.IsEmpty) { ShowBanner(Tone.Error, "日志导出失败，请检查目标位置。"); }
        }
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

    private static Label Value(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = Theme.TextPrimary,
            Font = Theme.Body14Medium,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }
}