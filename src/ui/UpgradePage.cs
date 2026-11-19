using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class UpgradePage : Panel
{
    private readonly MainForm shell;
    private readonly Card controllerCard = new Card("控制器升级");
    private readonly Card pendantCard = new Card("示教器升级");
    private readonly Label controllerCaption = Caption("");
    private readonly Label pendantCaption = Caption(SshUpgrade.DefaultPendantIp);
    private readonly TextField controllerFile = new TextField("选择或拖入控制器升级包");
    private readonly TextField pendantFile = new TextField("选择或拖入示教器升级包");
    private readonly ActionButton chooseController = new ActionButton("选包", ButtonKind.Secondary);
    private readonly ActionButton choosePendant = new ActionButton("选包", ButtonKind.Secondary);
    private readonly ActionButton uploadController = new ActionButton("升级控制器", ButtonKind.Primary);
    private readonly ActionButton uploadPendant = new ActionButton("升级示教器", ButtonKind.Primary);

    private readonly Card logCard = new Card("升级日志");
    private readonly LogView log = new LogView("");
    private readonly ActionButton exportLog = new ActionButton("导出日志", ButtonKind.Quiet);
    private readonly ActionButton clearLog = new ActionButton("清空", ButtonKind.Quiet);
    private readonly StatusPill state = new StatusPill();
    private readonly ProgressBar progress = new ProgressBar();
    private CancellationTokenSource upload;
    private bool busy;

    internal UpgradePage(MainForm shell)
    {
        this.shell = shell;
        BackColor = Theme.Body;
        controllerFile.IsReadOnly = true;
        pendantFile.IsReadOnly = true;

        controllerCard.Controls.AddRange(new Control[]
        {
            controllerCaption, controllerFile, chooseController, uploadController
        });
        pendantCard.Controls.AddRange(new Control[]
        {
            pendantCaption, pendantFile, choosePendant, uploadPendant
        });
        logCard.Controls.AddRange(new Control[] { log, exportLog, clearLog, state, progress });
        Controls.AddRange(new Control[] { controllerCard, pendantCard, logCard });

        chooseController.Click += delegate { ChoosePackage(false); };
        choosePendant.Click += delegate { ChoosePackage(true); };
        uploadController.Click += delegate { StartUpload(false); };
        uploadPendant.Click += delegate { StartUpload(true); };
        clearLog.Click += delegate { log.Clear(); };
        exportLog.Click += delegate { log.SaveTo(this, "QJAR-升级日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"); };

        EnableDrop(this, null);
        EnableDrop(controllerCard, false);
        EnableDrop(pendantCard, true);
        EnableDrop(controllerFile, false);
        EnableDrop(controllerFile.Edit, false);
        EnableDrop(pendantFile, true);
        EnableDrop(pendantFile.Edit, true);
        SetState("等待选择升级包", Tone.Neutral);
        UpdateEnabled();
    }

    internal void FocusFirst() { chooseController.Focus(); }

    internal void RefreshAddress() { controllerCaption.Text = shell.ControllerIp; }

    internal void SetBusy(bool value)
    {
        busy = value;
        UpdateEnabled();
    }

    internal void RefreshAdmin() { UpdateEnabled(); }

    internal void StopWork()
    {
        if (upload != null)
        {
            upload.Cancel();
        }
    }

    internal void UpgradeBuildPackage(string path)
    {
        if (SetPackage(false, path))
        {
            StartUpload(false);
        }
    }

    private void ChoosePackage(bool pendant)
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = pendant ? "选择示教器升级包" : "选择控制器升级包";
            dialog.Filter = "升级包 (*.tar.gz)|*.tar.gz";
            dialog.CheckFileExists = true;
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                SetPackage(pendant, dialog.FileName);
            }
        }
    }

    private void EnableDrop(Control control, bool? pendant)
    {
        control.AllowDrop = true;
        control.DragEnter += delegate(object sender, DragEventArgs e)
        {
            e.Effect = !busy && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        control.DragDrop += delegate(object sender, DragEventArgs e)
        {
            if (!busy && e.Data != null)
            {
                DropPackages(e.Data.GetData(DataFormats.FileDrop) as string[], pendant);
            }
        };
    }

    private void DropPackages(string[] paths, bool? row)
    {
        if (paths == null)
        {
            return;
        }
        bool loaded = false;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                string controller = Path.Combine(path, SshUpgrade.ControllerPackage);
                string pendant = Path.Combine(path, SshUpgrade.PendantPackage);
                if (row != true && File.Exists(controller))
                {
                    loaded |= SetPackage(false, controller);
                }
                if (row != false && File.Exists(pendant))
                {
                    loaded |= SetPackage(true, pendant);
                }
            }
            else if (row.HasValue)
            {
                loaded |= SetPackage(row.Value, path);
            }
            else if (HasName(path, SshUpgrade.ControllerPackage))
            {
                loaded |= SetPackage(false, path);
            }
            else if (HasName(path, SshUpgrade.PendantPackage))
            {
                loaded |= SetPackage(true, path);
            }
        }
        if (!loaded)
        {
            log.Append("未找到对应的升级包，请拖入升级包或包含标准文件名的文件夹。", Theme.Warning);
        }
    }

    private bool SetPackage(bool pendant, string path)
    {
        string other = pendant ? SshUpgrade.ControllerPackage : SshUpgrade.PendantPackage;
        if (!File.Exists(path) || !path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || HasName(path, other))
        {
            log.Append("请选择与目标设备对应的 .tar.gz 升级包。", Theme.Warning);
            return false;
        }
        (pendant ? pendantFile : controllerFile).Text = path;
        SetState("升级包已选择", Tone.Info);
        UpdateEnabled();
        return true;
    }

    private static bool HasName(string path, string name)
    {
        return string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);
    }

    private async void StartUpload(bool pendant)
    {
        if (!shell.IsAdmin || busy || upload != null)
        {
            return;
        }
        string path = (pendant ? pendantFile : controllerFile).Text;
        if (!File.Exists(path))
        {
            SetState("升级包不存在", Tone.Error);
            log.Append("请重新选择升级包。", Theme.Error);
            return;
        }
        string ip;
        if (!SshUpgrade.TryGetIp(pendant ? SshUpgrade.DefaultPendantIp : shell.ControllerIp, out ip))
        {
            SetState("设备 IP 无效", Tone.Error);
            return;
        }
        string device = pendant ? "示教器" : "控制器";
        string target = pendant ? SshUpgrade.PendantPath : SshUpgrade.ControllerPath;
        if (MessageBox.Show(this, "目标：" + device + " " + ip + "\n远端文件：" + target +
            "\n本地包：" + path + "\n\n上传后会替换同名升级包。", "确认上传升级包",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }

        var current = new CancellationTokenSource();
        upload = current;
        shell.SetBusy(true);
        progress.Value = 0;
        progress.Tone = Tone.Info;
        SetState("正在上传", Tone.Info);
        log.Append(device + " " + ip + " ← " + path, Theme.TextPrimary);
        var report = new Progress<int>(value =>
        {
            if (upload == current && !IsDisposed)
            {
                progress.Value = value;
            }
        });
        try
        {
            await Task.Run(() => SshUpgrade.Upload(ip, path, pendant, current.Token, ((IProgress<int>)report).Report));
            if (!IsDisposed)
            {
                progress.Value = 100;
                progress.Tone = Tone.Success;
                SetState("上传完成", Tone.Success);
                log.Append("升级包已写入并校验：" + target, Theme.Success);
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                progress.Tone = Tone.Error;
                SetState("上传未确认", Tone.Error);
                log.Append(error.Message, Theme.Error);
            }
        }
        finally
        {
            upload = null;
            current.Dispose();
            if (!shell.IsDisposed && !shell.Disposing)
            {
                shell.SetBusy(false);
            }
        }
    }

    private void UpdateEnabled()
    {
        chooseController.Enabled = !busy;
        choosePendant.Enabled = !busy;
        uploadController.Enabled = shell.IsAdmin && !busy && controllerFile.Text.Length > 0;
        uploadPendant.Enabled = shell.IsAdmin && !busy && pendantFile.Text.Length > 0;
    }

    private void SetState(string caption, Tone tone)
    {
        state.Caption = caption;
        state.Tone = tone;
        LayoutPage();
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
        int inset = Theme.S(Theme.Space5);
        int width = Math.Max(Theme.S(360), Width - pad * 2);
        int height = Theme.S(174);
        int leftWidth = (width - gap) / 2;
        controllerCard.SetBounds(pad, pad, leftWidth, height);
        pendantCard.SetBounds(controllerCard.Right + gap, pad, width - leftWidth - gap, height);
        LayoutTarget(controllerCard, controllerCaption, controllerFile, chooseController, uploadController);
        LayoutTarget(pendantCard, pendantCaption, pendantFile, choosePendant, uploadPendant);

        int top = pendantCard.Bottom + gap;
        logCard.SetBounds(pad, top, width, Math.Max(Theme.S(100), Height - pad - top));
        int toolbar = Theme.S(Theme.Space5) - Theme.S(2);
        clearLog.SetBounds(width - inset - Theme.S(64), toolbar, Theme.S(64), Theme.S(Theme.SizeSm));
        exportLog.SetBounds(clearLog.Left - Theme.S(96), toolbar, Theme.S(88), Theme.S(Theme.SizeSm));
        state.Location = new Point(inset + Theme.S(92), toolbar + (clearLog.Height - state.Height) / 2);
        progress.SetBounds(inset, logCard.ContentTop, width - inset * 2, Theme.S(5));
        int logY = progress.Bottom + Theme.S(Theme.Space2);
        log.SetBounds(inset, logY, width - inset * 2, Math.Max(Theme.S(35), logCard.Height - logY - inset));
    }

    private static void LayoutTarget(Card card, Label caption, TextField path,
        ActionButton choose, ActionButton upload)
    {
        int pad = Theme.S(Theme.Space5);
        int gap = Theme.S(Theme.Space2);
        int height = Theme.S(Theme.SizeMd);
        int top = card.ContentTop;
        caption.SetBounds(pad, top, Theme.S(180), height);
        int right = card.Width - pad;
        choose.SetBounds(right - Theme.S(56), top + height, Theme.S(56), height);
        path.SetBounds(pad, choose.Top, Math.Max(0, choose.Left - gap - pad), height);
        upload.SetBounds(right - Theme.S(106), path.Bottom + gap, Theme.S(106), height);
    }

    private static Label Caption(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = Theme.TextSecondary,
            BackColor = Color.Transparent,
            Font = Theme.Caption12,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }
}
