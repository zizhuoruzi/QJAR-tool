using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

/* WSL 环境准备指引：把搭建 32 位 Xenomai 编译环境的步骤做成可复制、可自检的弹窗。
   内容与 405 工程的 Docs/405_WSL编译环境.md 一致，不在这里另造一套说法。 */
internal sealed class WslGuideForm : Form
{
    internal const string ReleaseUrl = "https://github.com/zizhuoruzi/qj-wsl-env/releases/tag/wsl-env-v1";
    internal const string DownloadUrl = "https://github.com/zizhuoruzi/qj-wsl-env/releases/download/wsl-env-v1/qj-wsl-env.7z";
    internal const string ArchiveSha256 = "e00dfcabc34243ee10072a0871a365b3b87ee47c44fede6808be6658703d3084";

    // 界面与可交付文档共享同一份内嵌提示词，避免两套步骤漂移。
    internal static string GetAgentPrompt(string distribution)
    {
        using (var stream = typeof(WslGuideForm).Assembly.GetManifestResourceStream("wsl-agent-prompt.txt"))
        {
            if (stream == null) { throw new System.IO.IOException("缺少内嵌 WSL 提示词，请重新构建工具。"); }
            using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
            {
                return reader.ReadToEnd().Replace("{{DISTRO}}", Wsl.DistroName(distribution));
            }
        }
    }

    /* 只读命令框：样式像代码块，但把滚轮交给外层滚动区。
       否则鼠标停在框上时页面滚不动，RichTextBox 会吞掉 WM_MOUSEWHEEL。 */
    private sealed class GuideTextBox : RichTextBox
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        internal Control WheelHost;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WM_MOUSEWHEEL && WheelHost != null && WheelHost.IsHandleCreated)
            {
                NativeMethods.SendMessage(WheelHost.Handle, WM_MOUSEWHEEL, message.WParam, message.LParam);
                return;
            }
            base.WndProc(ref message);
        }
    }

    private sealed class Step
    {
        internal Card Card;
        internal Label Note;
        internal LinkLabel Link;
        internal readonly List<GuideTextBox> Boxes = new List<GuideTextBox>();
        internal readonly List<ActionButton> Copies = new List<ActionButton>();
    }

    private readonly string distro;
    private readonly string agentPrompt;
    private readonly Action<string> openUrl;
    private readonly Panel scroll = new Panel();
    private readonly List<Step> steps = new List<Step>();
    private readonly Card agentCard = new Card("给 AI agent 的提示词");
    private RichTextBox agentText;
    private readonly ActionButton copyAgent = new ActionButton("复制提示词", ButtonKind.Secondary);
    private readonly Banner banner = new Banner();
    private readonly ActionButton check = new ActionButton("检查环境", ButtonKind.Secondary);
    private readonly ActionButton close = new ActionButton("关闭", ButtonKind.Primary);
    private readonly List<System.Windows.Forms.Timer> timers = new List<System.Windows.Forms.Timer>();
    private LinkLabel releaseLink;

    /* 供回归检查：链接确实可点、滚轮能带动外层滚动区。 */
    internal string ReleaseLinkText { get { return releaseLink == null ? string.Empty : releaseLink.Text; } }

    internal Control CommandBoxForTest
    {
        get
        {
            foreach (Step step in steps)
            {
                if (step.Boxes.Count > 0) { return step.Boxes[0]; }
            }
            return null;
        }
    }

    internal ScrollableControl ScrollHostForTest { get { return scroll; } }
    internal LinkLabel ReleaseLinkForTest { get { return releaseLink; } }

    internal WslGuideForm(string distribution, Action<string> openUrl = null)
    {
        this.openUrl = openUrl ?? delegate(string url)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        };
        distro = string.IsNullOrEmpty(distribution) ? Wsl.DefaultDistribution : distribution;
        agentPrompt = GetAgentPrompt(distro);
        Text = "WSL 环境准备";
        BackColor = Theme.Body;
        Font = Theme.Body14;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        int width = Theme.S(760);
        int height = Theme.S(700);
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        if (height > work.Height - Theme.S(80)) { height = work.Height - Theme.S(80); }
        if (width > work.Width - Theme.S(40)) { width = work.Width - Theme.S(40); }
        ClientSize = new Size(width, height);
        MinimumSize = new Size(Theme.S(620), Theme.S(480));
        DoubleBuffered = true;
        try
        {
            // 弹窗也要用应用图标，否则标题栏是系统默认的那个。
            using (System.IO.Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("registration-tool.ico"))
            {
                if (stream != null) { Icon = new Icon(stream); }
            }
        }
        catch (Exception)
        {
        }

        scroll.AutoScroll = true;
        scroll.BackColor = Theme.Body;
        Controls.AddRange(new Control[] { scroll, banner, check, close });

        AddStep("1. 安装 WSL 2 与虚拟机平台",
            "以管理员身份打开 PowerShell，先执行第一条，完成后重启电脑；第二条把默认版本设为 2。"
            + "Windows 10 需要 2004 以上，且 BIOS 里要开启 CPU 虚拟化。",
            new[] { "wsl --install --no-distribution", "wsl --set-default-version 2" });
        AddStep("2. 安装 Ubuntu 发行版",
            "已经装过 Ubuntu 可以跳过。发行版名要和编译页使用的一致（默认 Ubuntu）；"
            + "换名字时改 %LOCALAPPDATA%\\QJAR\\build.ini 里的 distro。WSL 1 跑不了 32 位程序，必须是 WSL 2。",
            new[] { "wsl --install -d Ubuntu" });
        AddStep("3. 下载与解压环境包",
            "公开 Release，无需 GitHub 登录，下载约 318 MiB。",
            new[]
            {
                "$dir = Join-Path $env:USERPROFILE 'Downloads\\qj-wsl-env'; New-Item -ItemType Directory -Force -Path $dir | Out-Null; curl.exe --fail --location --output \"$dir\\qj-wsl-env.7z\" '" + DownloadUrl + "'",
                "if ((Get-FileHash -LiteralPath \"$dir\\qj-wsl-env.7z\" -Algorithm SHA256).Hash.ToLowerInvariant() -ne '" + ArchiveSha256 + "') { throw 'SHA-256 mismatch' }",
                "& \"$env:ProgramFiles\\7-Zip\\7z.exe\" x \"$dir\\qj-wsl-env.7z\" \"-o$dir\""
            },
            ReleaseUrl);
        AddStep("4. 恢复环境与安装构建入口",
            "恢复脚本自动使用解压目录，不需要改路径；校验包、编译并运行 32 位 C++ 程序通过后才安装。"
            + "已有环境会拒绝覆盖，先检查并复用。包内自带构建入口，不用另找工程仓库。每条命令返回非 0 时停止。",
            new[]
            {
                "$linuxDir = (wsl.exe -d " + distro + " --exec wslpath -u $dir).Trim(); wsl.exe -d " + distro + " -u root --exec bash \"$linuxDir/prepare-rootfs.sh\"",
                "wsl.exe -d " + distro + " -u root --exec install -m 755 \"$linuxDir/build-controller.sh\" /opt/qjar-build/build-controller.sh"
            });
        AddStep("5. 验证",
            "下面命令应返回 0，也可以点“检查环境”。第一次启动 WSL 可能需要十几秒。",
            new[]
            {
                "wsl.exe -d " + distro + " -u root --exec test -x /opt/qjar-build/build-controller.sh",
                "wsl.exe -d " + distro + " -u root --exec test -f /opt/qjar-build/ubuntu12-i686/lib/modules/3.14.17-xenomai/build/Module.symvers",
                "wsl.exe -d " + distro + " -u root --exec setarch linux32 chroot /opt/qjar-build/ubuntu12-i686 /usr/bin/env -i PATH=/usr/bin:/bin /usr/bin/gcc-4.6 --version"
            });

        check.Click += delegate { Check(); };
        close.Click += delegate { Close(); };

        agentCard.Title = "给 AI agent 的提示词";
        agentText = new GuideTextBox
        {
            Text = agentPrompt,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Body,
            ForeColor = Theme.TextPrimary,
            Font = Theme.LogFont,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            TabStop = false,
            WheelHost = scroll
        };
        agentCard.Controls.AddRange(new Control[] { agentText, copyAgent });
        copyAgent.Click += delegate { Copy(agentPrompt, copyAgent); };
        scroll.Controls.Add(agentCard);
        HideBanner();
    }

    private void AddStep(string title, string note, string[] commands, string link = null)
    {
        var step = new Step();
        step.Card = new Card(title);
        step.Note = Caption(note);
        step.Note.TextAlign = ContentAlignment.TopLeft;
        step.Card.Controls.Add(step.Note);
        if (!string.IsNullOrEmpty(link))
        {
            // 链接本身就是 URL，点一下直接开浏览器，不用手抄。
            step.Link = new LinkLabel
            {
                Text = link,
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = Theme.Caption12,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Accent,
                VisitedLinkColor = Theme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                TextAlign = ContentAlignment.MiddleLeft
            };
            string url = link;
            step.Link.LinkClicked += delegate { OpenLink(url); };
            releaseLink = step.Link;
            step.Card.Controls.Add(step.Link);
        }
        foreach (string command in commands)
        {
            // 命令可能很长：用只读多行框，按宽度折行，避免单行输入框把后半截藏掉。
            var box = new GuideTextBox
            {
                Text = command,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Body,
                ForeColor = Theme.TextPrimary,
                Font = Theme.Code(Theme.FontSm, FontStyle.Regular),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.None,
                TabStop = false,
                DetectUrls = false,
                WheelHost = scroll
            };
            var copy = new ActionButton("复制", ButtonKind.Quiet);
            string value = command;
            copy.Click += delegate { Copy(value, copy); };
            step.Card.Controls.AddRange(new Control[] { box, copy });
            step.Boxes.Add(box);
            step.Copies.Add(copy);
        }
        steps.Add(step);
        scroll.Controls.Add(step.Card);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        banner.Tone = Tone.Neutral;
        LayoutGuide();
    }

    /* 打开时把焦点从命令框移开，避免整行命令被选中看起来像被改过。 */
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        close.Focus();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutGuide();
    }

    private void LayoutGuide()
    {
        // 构造期间设置 ClientSize 会触发 Resize，此时控件还没建齐。
        if (agentText == null) { return; }
        int pad = Theme.S(Theme.Space6);
        int footerHeight = Theme.S(120);
        scroll.SetBounds(pad, pad, ClientSize.Width - pad * 2, ClientSize.Height - pad * 2 - footerHeight);
        banner.SetBounds(pad, scroll.Bottom + Theme.S(Theme.Space3), ClientSize.Width - pad * 2, Theme.S(34));
        close.SetBounds(ClientSize.Width - pad - Theme.S(88), banner.Bottom + Theme.S(Theme.Space3),
            Theme.S(88), Theme.S(Theme.SizeLg));
        check.SetBounds(close.Left - Theme.S(Theme.Space2) - Theme.S(96), close.Top,
            Theme.S(96), Theme.S(Theme.SizeLg));

        int width = Math.Max(Theme.S(320), scroll.ClientSize.Width - Theme.S(Theme.Space3));
        int cardPad = Theme.S(Theme.Space5);
        int y = 0;
        foreach (Step step in steps)
        {
            int noteWidth = width - cardPad * 2;
            Size noteSize = TextRenderer.MeasureText(step.Note.Text, Theme.Caption12,
                new Size(noteWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int noteHeight = Math.Max(Theme.S(18), noteSize.Height + Theme.S(4));
            int boxWidth = width - cardPad * 2 - Theme.S(72) - Theme.S(Theme.Space2);
            var boxes = new int[step.Boxes.Count];
            int commandsHeight = 0;
            for (int i = 0; i < step.Boxes.Count; i++)
            {
                Size size = TextRenderer.MeasureText(step.Boxes[i].Text, step.Boxes[i].Font,
                    new Size(boxWidth - Theme.S(8), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                boxes[i] = Math.Max(Theme.S(24), size.Height + Theme.S(6));
                commandsHeight += boxes[i] + Theme.S(Theme.Space1);
            }
            int linkHeight = step.Link == null ? 0 : Theme.S(18);
            int height = step.Card.ContentTop + noteHeight + linkHeight + Theme.S(Theme.Space2) +
                commandsHeight + Theme.S(Theme.Space3);
            step.Card.SetBounds(0, y, width, height);
            step.Note.SetBounds(cardPad, step.Card.ContentTop, noteWidth, noteHeight);
            int top = step.Note.Bottom + Theme.S(Theme.Space1);
            if (step.Link != null)
            {
                step.Link.SetBounds(cardPad, top, noteWidth, linkHeight);
                top = step.Link.Bottom + Theme.S(Theme.Space1);
            }
            for (int i = 0; i < step.Boxes.Count; i++)
            {
                RichTextBox box = step.Boxes[i];
                ActionButton copy = step.Copies[i];
                box.SetBounds(cardPad, top, boxWidth, boxes[i]);
                copy.SetBounds(width - cardPad - Theme.S(64),
                    top + (boxes[i] - Theme.S(Theme.SizeSm)) / 2, Theme.S(64), Theme.S(Theme.SizeSm));
                top += boxes[i] + Theme.S(Theme.Space1);
            }
            y += height + Theme.S(Theme.Space4);
        }

        int agentHeight = Theme.S(360);
        agentCard.SetBounds(0, y, width, agentHeight);
        copyAgent.SetBounds(width - cardPad - Theme.S(112), Theme.S(Theme.Space5) - Theme.S(2),
            Theme.S(112), Theme.S(Theme.SizeSm));
        int textHeight = agentHeight - agentCard.ContentTop - Theme.S(Theme.Space5);
        agentText.SetBounds(cardPad, agentCard.ContentTop, width - cardPad * 2, Math.Max(Theme.S(80), textHeight));
        y += agentHeight + Theme.S(Theme.Space4);
        scroll.AutoScrollMinSize = new Size(0, y);
    }

    private void OpenLink(string url)
    {
        try
        {
            openUrl(url);
        }
        catch (Exception exception)
        {
            ShowBanner(Tone.Error, "打不开浏览器：" + exception.Message);
        }
    }

    private void Copy(string text, ActionButton button)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            ShowBanner(Tone.Error, "复制失败：剪贴板被占用。");
            return;
        }
        string original = button.Text;
        button.Text = "已复制";
        var timer = new System.Windows.Forms.Timer { Interval = 1400 };
        timer.Tick += delegate
        {
            timer.Stop();
            button.Text = original;
            timers.Remove(timer);
            timer.Dispose();
        };
        timers.Add(timer);
        timer.Start();
    }

    private void Check()
    {
        check.Enabled = false;
        check.Text = "检查中…";
        string target = distro;
        var thread = new Thread(delegate()
        {
            string result;
            try
            {
                result = Wsl.CheckEnvironment(target);
            }
            catch (Exception error)
            {
                result = "检查异常：" + error.Message;
            }
            try
            {
                BeginInvoke((Action)delegate
                {
                    check.Enabled = true;
                    check.Text = "检查环境";
                    if (result == null)
                    {
                        ShowBanner(Tone.Success, "环境就绪：发行版 " + target + " 的编译环境可用。");
                    }
                    else
                    {
                        ShowBanner(Tone.Error, result);
                    }
                });
            }
            catch (Exception)
            {
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private void ShowBanner(Tone tone, string text)
    {
        banner.Tone = tone;
        banner.Text = text;
        banner.Visible = true;
        LayoutGuide();
    }

    private void HideBanner()
    {
        banner.Visible = false;
        LayoutGuide();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (System.Windows.Forms.Timer timer in timers) { timer.Dispose(); }
            timers.Clear();
        }
        base.Dispose(disposing);
    }
}