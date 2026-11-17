using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

/* 命令调试页：命令与数据按所选编码发出，走 QJCore.dll 的同一套协议帧。 */
internal sealed class CommandPage : Panel
{
    private readonly MainForm shell;
    private readonly Card sendCard = new Card("发送命令");
    private readonly TextField commandField = new TextField("例如 getlic");
    private readonly TextField dataField = new TextField("数据（可选）");
    private readonly ActionButton send = new ActionButton("发送命令", ButtonKind.Primary);
    private readonly ActionButton encoding = new ActionButton("编码：UTF-8", ButtonKind.Secondary);
    private readonly TextField timeoutField = new TextField("超时 ms");
    private readonly Card logCard = new Card("收发记录");
    private readonly LogView log = new LogView("暂无记录。输入命令后点“发送命令”。");
    private readonly ActionButton clear = new ActionButton("清空", ButtonKind.Quiet);
    private readonly ActionButton export = new ActionButton("导出日志", ButtonKind.Quiet);

    private CoreCancel cancelHandle;
    private bool running;
    private bool admin;

    internal CommandPage(MainForm shell)
    {
        this.shell = shell;
        BackColor = Theme.Body;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        send.Click += delegate { Send(); };
        clear.Click += delegate { log.Clear(); };
        export.Click += delegate { log.SaveTo(this, "QJAR-命令日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"); };
        encoding.Click += delegate { ToggleEncoding(); };
        timeoutField.Accepted += delegate { CommitTimeout(); };
        timeoutField.Edit.TextChanged += delegate
        {
            timeoutField.Invalid = false;
            CommitTimeout();
        };
        commandField.Accepted += delegate { Send(); };

        Controls.AddRange(new Control[] { sendCard, logCard });
        sendCard.Controls.AddRange(new Control[] { commandField, dataField, send, encoding, timeoutField });
        logCard.Controls.AddRange(new Control[] { log, clear, export });
    }

    internal void FocusFirst() { commandField.Edit.Focus(); }

    internal bool Running { get { return running; } }

    internal void SendForTest(string command, string data)
    {
        commandField.Text = command;
        dataField.Text = data;
        Send();
    }

    internal string LogText { get { return log.AllText; } }

    internal void SetAdmin(bool value)
    {
        admin = value;
        send.Enabled = !running && admin;
    }

    internal void SetBusy(bool busy)
    {
        running = busy;
        send.Enabled = !busy && admin;
        commandField.Edit.Enabled = !busy;
        dataField.Edit.Enabled = !busy;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutPage();
    }

    private void LayoutPage()
    {
        int pad = Theme.S(Theme.Space6);
        int gap = Theme.S(Theme.Space4);
        int width = Math.Max(Theme.S(360), Width - pad * 2);
        int cardPad = Theme.S(Theme.Space5);
        int bar = Theme.S(Theme.SizeLg);
        int fieldHeight = Theme.S(Theme.SizeMd);

        sendCard.SetBounds(pad, pad, width, Theme.S(200));
        int innerWidth = sendCard.Width - cardPad * 2;
        int contentTop = sendCard.ContentTop;

        int buttonWidth = Theme.S(116);
        send.SetBounds(sendCard.Width - cardPad - buttonWidth, contentTop, buttonWidth, bar);
        commandField.SetBounds(cardPad, contentTop + Theme.S(2), innerWidth - buttonWidth - Theme.S(Theme.Space2), fieldHeight);
        dataField.SetBounds(cardPad, contentTop + bar + Theme.S(Theme.Space2), innerWidth, fieldHeight);
        encoding.SetBounds(cardPad, dataField.Bottom + Theme.S(Theme.Space3), Theme.S(128), bar);
        timeoutField.SetBounds(encoding.Right + Theme.S(Theme.Space2), encoding.Top + Theme.S(2),
            Theme.S(128), fieldHeight);

        int logTop = sendCard.Bottom + gap;
        logCard.SetBounds(pad, logTop, width, Math.Max(Theme.S(140), Height - pad - logTop));
        clear.SetBounds(logCard.Width - cardPad - Theme.S(64), Theme.S(Theme.Space5) - Theme.S(2),
            Theme.S(64), Theme.S(Theme.SizeSm));
        export.SetBounds(clear.Left - Theme.S(Theme.Space2) - Theme.S(88), clear.Top, Theme.S(88), Theme.S(Theme.SizeSm));
        int logY = Theme.S(Theme.Space5) + Theme.S(Theme.FontLg) + Theme.S(Theme.Space3);
        log.SetBounds(cardPad, logY, logCard.Width - cardPad * 2,
            Math.Max(Theme.S(60), logCard.Height - logY - cardPad));
    }
    private void ToggleEncoding()
    {
        shell.EncodingName = shell.EncodingName == "GBK" ? "UTF-8" : "GBK";
        encoding.Text = "编码：" + shell.EncodingName;
        shell.SaveSettings();
    }

    private void CommitTimeout()
    {
        int value;
        if (!int.TryParse(timeoutField.Text.Trim(), out value) || value < 1 || value > 600000)
        {
            if (timeoutField.Text.Length > 0) { timeoutField.Invalid = true; }
            return;
        }
        shell.TimeoutMs = value;
        shell.SaveSettings();
    }

    private void Send()
    {
        if (running) { return; }
        if (!admin)
        {
            Append("请先登录管理员后再发送命令。", Theme.Error);
            return;
        }
        string command = commandField.Text.Trim();
        if (command.Length == 0)
        {
            commandField.Invalid = true;
            commandField.Edit.Focus();
            return;
        }
        string ip;
        int port;
        if (!shell.TryGetAddress(out ip, out port)) { return; }
        int timeout = shell.TimeoutMs;
        int value;
        if (int.TryParse(timeoutField.Text.Trim(), out value) && value >= 1 && value <= 600000)
        {
            timeout = value;
        }

        Encoding encodingUsed = shell.EncodingName == "GBK"
            ? Encoding.GetEncoding(936)
            : new UTF8Encoding(false);
        byte[] payload = encodingUsed.GetBytes(command);
        string data = dataField.Text;
        byte[] extra = data.Length > 0 ? encodingUsed.GetBytes(data) : null;

        shell.SetBusy(true);
        send.Enabled = false;
        cancelHandle = new CoreCancel();
        Append("→ " + command + (data.Length > 0 ? "  [" + data + "]" : ""), Theme.TextSecondary);

        var thread = new Thread(delegate()
        {
            byte[] reply;
            string error;
            bool ok;
            try
            {
                ok = Core.Exchange(ip, port, timeout, payload, extra, out reply, cancelHandle.Handle, out error);
            }
            catch (Exception exception)
            {
                ok = false;
                reply = new byte[0];
                error = "界面异常：" + exception.Message;
            }
            string text = ok ? encodingUsed.GetString(reply) : error;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (cancelHandle != null) { cancelHandle.Dispose(); cancelHandle = null; }
                    shell.SetBusy(false);
                    send.Enabled = true;
                    Append(ok ? "← " + text : "! " + text, ok ? Theme.TextPrimary : Theme.Error);
                });
            }
            catch (Exception)
            {
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private void Append(string line, Color color)
    {
        if (string.IsNullOrEmpty(line)) { return; }
        log.Append(line, color);
    }
}