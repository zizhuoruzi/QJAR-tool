using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

/* 编译页：读取/修改工程 Makefile，按实体轴或虚拟轴调用 WSL 通用入口编译，
   成功后由构建脚本打包出 control_update.tar.gz。 */
internal sealed class BuildPage : Panel
{
    private const string PackageName = SshUpgrade.ControllerPackage;
    private const string BuildGroupPrefix = "QJAR build group: ";
    private static readonly Regex OutputLine = new Regex(@"^Build output:\s*(?<path>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class PendingLine
    {
        internal string Text;
        internal bool Error;
    }

    private readonly MainForm shell;
    private readonly Card projectCard = new Card("工程");
    private readonly TextField projectField = new TextField("控制器工程目录（含 src/Makefile 与 lib）");
    private readonly ActionButton choose = new ActionButton("选择目录", ButtonKind.Secondary);
    private readonly ActionButton guide = new ActionButton("环境准备", ButtonKind.Secondary);

    private readonly Card settingCard = new Card("编译设置");
    private readonly Label versionCaption = Caption("版本号");
    private readonly TextField versionField = new TextField("例如 V4.0.5_dev186");
    private readonly ActionButton saveVersion = new ActionButton("保存", ButtonKind.Secondary);
    private readonly Label targetCaption = Caption("编译目标");
    private readonly ActionButton ethercat = new ActionButton("实体轴", ButtonKind.Secondary);
    private readonly ActionButton virtualServo = new ActionButton("虚拟轴", ButtonKind.Secondary);
    private readonly ActionButton build = new ActionButton("开始构建", ButtonKind.Primary);
    private readonly ActionButton cancel = new ActionButton("取消", ButtonKind.Quiet);
    private readonly ActionButton packageToggle = new ActionButton("打包：开", ButtonKind.Secondary);
    private readonly ActionButton upgrade = new ActionButton("升级控制器", ButtonKind.Secondary);
    private readonly StatusPill statePill = new StatusPill();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly Label phaseText = Caption("等待开始");
    private readonly Label elapsedText = Caption("");

    private readonly Card logCard = new Card("构建日志");
    private readonly LogView log = new LogView("暂无记录。选择工程后点“开始构建”。");
    private readonly ActionButton clearLog = new ActionButton("清空", ButtonKind.Quiet);
    private readonly ActionButton exportLog = new ActionButton("导出日志", ButtonKind.Quiet);
    private readonly ActionButton openDir = new ActionButton("打开产物目录", ButtonKind.Quiet);

    private readonly System.Windows.Forms.Timer flushTimer = new System.Windows.Forms.Timer { Interval = 120 };
    private readonly System.Windows.Forms.Timer elapsedTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    private readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
    private readonly object gate = new object();
    private readonly List<PendingLine> pending = new List<PendingLine>();

    private ProjectMakefile model;
    private string projectDir = string.Empty;
    private string distro = Wsl.DefaultDistribution;
    private string outputDir;
    private string workName;
    private string lastLine;
    private bool package = true;
    private bool admin;
    private bool running;
    private bool shellBusy;
    private volatile bool cancelled;
    private volatile int processGroup;
    private int phase;              // 0 准备 / 1 编译 / 2 校验产物 / 3 打包 / 4 打包完成

    internal BuildPage(MainForm shell)
    {
        this.shell = shell;
        BackColor = Theme.Body;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        choose.Click += delegate { ChooseProject(); };
        guide.Click += delegate
        {
            using (var dialog = new WslGuideForm(distro)) { dialog.ShowDialog(this); }
        };
        saveVersion.Click += delegate { SaveVersion(); };
        versionField.Accepted += delegate { SaveVersion(); };
        ethercat.Click += delegate { SetTarget(ProjectMakefile.DriverEthercat); };
        virtualServo.Click += delegate { SetTarget(ProjectMakefile.DriverVirtual); };
        build.Click += delegate { StartBuild(); };
        cancel.Click += delegate { CancelBuild(); };
        packageToggle.Click += delegate { TogglePackage(); };
        upgrade.Click += delegate
        {
            if (!string.IsNullOrEmpty(outputDir))
            {
                shell.UpgradeBuildPackage(Path.Combine(outputDir, PackageName));
            }
        };
        clearLog.Click += delegate { log.Clear(); };
        exportLog.Click += delegate { ExportLog(); };
        openDir.Click += delegate { OpenOutputDir(); };
        versionField.Edit.TextChanged += delegate { versionField.Invalid = false; };
        flushTimer.Tick += delegate { FlushLog(); };
        elapsedTimer.Tick += delegate { OnElapsedTick(); };

        Controls.AddRange(new Control[] { projectCard, settingCard, logCard });
        projectCard.Controls.AddRange(new Control[] { projectField, choose, guide });
        settingCard.Controls.AddRange(new Control[]
        {
            versionCaption, versionField, saveVersion, targetCaption, ethercat, virtualServo,
            build, cancel, packageToggle, upgrade, statePill, progress, phaseText, elapsedText
        });
        logCard.Controls.AddRange(new Control[] { log, clearLog, exportLog, openDir });

        openDir.Enabled = false;
        projectField.IsReadOnly = true;
        elapsedText.TextAlign = ContentAlignment.MiddleRight;
        string savedProject;
        bool savedPackage;
        BuildSettings.Load(out savedProject, out distro, out savedPackage);
        package = savedPackage;
        packageToggle.Text = package ? "打包：开" : "打包：关";
        if (distro.Length == 0) { distro = Wsl.DefaultDistribution; }
        if (!string.IsNullOrEmpty(savedProject) && Directory.Exists(savedProject))
        {
            LoadProject(savedProject, false);
        }
        else
        {
            projectDir = savedProject ?? string.Empty;
            projectField.Text = projectDir;
            SetState(projectDir.Length == 0 ? "等待选择工程" : "工程目录不存在", Tone.Neutral);
            UpdateEnabled();
        }
    }

    internal void FocusFirst() { choose.Focus(); }

    internal bool Running { get { return running; } }

    internal string LogText { get { return log.AllText; } }

    internal string VersionText { get { return versionField.Text; } }

    internal string ProjectText { get { return projectField.Text; } }

    internal string PillText { get { return statePill.Caption; } }

    /* 测试钩子：走与界面相同的读写路径。 */
    internal void LoadProjectForTest(string path) { LoadProject(path, false); }

    internal void SaveVersionForTest(string version)
    {
        versionField.Text = version;
        SaveVersion();
    }

    internal TextBox VersionEditForTest { get { return versionField.Edit; } }

    internal void SetTargetForTest(string driver) { SetTarget(driver); }

    internal void BuildForTest() { StartBuild(); }

    internal void CancelForTest() { CancelBuild(); }

    /* 关窗时通知构建线程停止 WSL 进程组 */
    internal void StopWork()
    {
        if (running)
        {
            cancelled = true;
        }
    }

    internal string OutputDirForTest { get { return outputDir; } }

    internal void SetAdmin(bool value)
    {
        admin = value;
        UpdateEnabled();
    }

    internal void SetBusy(bool busy)
    {
        shellBusy = busy;
        UpdateEnabled();
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
        int width = Math.Max(Theme.S(360), Width - pad * 2);
        int cardPad = Theme.S(Theme.Space5);
        int bar = Theme.S(Theme.SizeLg);
        int field = Theme.S(Theme.SizeMd);

        projectCard.SetBounds(pad, pad, width, Theme.S(98));
        int top = projectCard.ContentTop;
        choose.SetBounds(projectCard.Width - cardPad - Theme.S(96), top, Theme.S(96), field);
        guide.SetBounds(choose.Left - Theme.S(Theme.Space2) - Theme.S(88), top, Theme.S(88), field);
        projectField.SetBounds(cardPad, top, guide.Left - Theme.S(Theme.Space2) - cardPad, field);

        settingCard.SetBounds(pad, projectCard.Bottom + gap, width, Theme.S(225));
        int y = settingCard.ContentTop;
        versionCaption.SetBounds(cardPad, y + Theme.S(7), Theme.S(56), Theme.S(18));
        saveVersion.SetBounds(settingCard.Width - cardPad - Theme.S(64), y, Theme.S(64), field);
        int versionLeft = cardPad + Theme.S(56) + Theme.S(Theme.Space2);
        versionField.SetBounds(versionLeft, y, saveVersion.Left - Theme.S(Theme.Space2) - versionLeft, field);

        int row2 = y + field + Theme.S(Theme.Space3);
        targetCaption.SetBounds(cardPad, row2 + Theme.S(7), Theme.S(56), Theme.S(18));
        ethercat.SetBounds(versionLeft, row2, Theme.S(96), field);
        virtualServo.SetBounds(ethercat.Right + Theme.S(Theme.Space2), row2, Theme.S(96), field);

        int row3 = row2 + field + Theme.S(Theme.Space3);
        build.SetBounds(cardPad, row3, Theme.S(108), bar);
        cancel.SetBounds(build.Right + Theme.S(Theme.Space2), row3, Theme.S(72), bar);
        packageToggle.SetBounds(cancel.Right + Theme.S(Theme.Space2), row3, Theme.S(96), bar);
        upgrade.SetBounds(settingCard.Width - cardPad - Theme.S(104), row2, Theme.S(104), field);
        statePill.SetBounds(settingCard.Width - cardPad - statePill.Width,
            row3 + (bar - statePill.Height) / 2, statePill.Width, statePill.Height);

        int row4 = row3 + bar + Theme.S(Theme.Space3);
        int settingInner = settingCard.Width - cardPad * 2;
        progress.SetBounds(cardPad, row4, settingInner, Theme.S(6));
        int captionTop = progress.Bottom + Theme.S(Theme.Space1);
        phaseText.SetBounds(cardPad, captionTop, settingInner - Theme.S(160), Theme.S(18));
        elapsedText.SetBounds(settingCard.Width - cardPad - Theme.S(160), captionTop, Theme.S(160), Theme.S(18));

        int logTop = settingCard.Bottom + gap;
        logCard.SetBounds(pad, logTop, width, Math.Max(Theme.S(96), Height - pad - logTop));
        int toolbarY = Theme.S(Theme.Space5) - Theme.S(2);
        clearLog.SetBounds(logCard.Width - cardPad - Theme.S(64), toolbarY, Theme.S(64), Theme.S(Theme.SizeSm));
        exportLog.SetBounds(clearLog.Left - Theme.S(Theme.Space2) - Theme.S(88), toolbarY, Theme.S(88), Theme.S(Theme.SizeSm));
        openDir.SetBounds(exportLog.Left - Theme.S(Theme.Space2) - Theme.S(112), toolbarY, Theme.S(112), Theme.S(Theme.SizeSm));
        int logY = Theme.S(Theme.Space5) + Theme.S(Theme.FontLg) + Theme.S(Theme.Space3);
        log.SetBounds(cardPad, logY, logCard.Width - cardPad * 2,
            Math.Max(Theme.S(60), logCard.Height - logY - cardPad));
    }

    // ---- 工程与 Makefile ----------------------------------------------------
    private void ChooseProject()
    {
        if (running) { return; }
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = "选择控制器工程或它的上级目录";
            dialog.ShowNewFolderButton = false;
            dialog.RootFolder = Environment.SpecialFolder.MyComputer;
            string previous = projectDir;
            while (!string.IsNullOrEmpty(previous) && !Directory.Exists(previous))
            {
                previous = Path.GetDirectoryName(previous);
            }
            if (!string.IsNullOrEmpty(previous)) { dialog.SelectedPath = previous; }
            if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
            string note;
            string resolved = ResolveProject(dialog.SelectedPath, out note);
            if (resolved != null) { LoadProject(resolved, true); }
            if (note != null) { Append(note, resolved == null ? Theme.Warning : Theme.TextSecondary); }
        }
    }

    /* 只定位包含 src/Makefile 与 lib 的工程；多个候选不默选，防止编错工程。
       ponytail: 本地最多三层、400 目录，网络/大仓库应让用户缩小选择范围。 */
    private static string ResolveProject(string path, out string note)
    {
        note = null;
        if (IsProject(path)) { return path; }
        var queue = new Queue<KeyValuePair<string, int>>();
        var matches = new List<string>();
        queue.Enqueue(new KeyValuePair<string, int>(path, 0));
        int remaining = 400;
        while (queue.Count > 0)
        {
            var entry = queue.Dequeue();
            if (entry.Value >= 3) { continue; }
            try
            {
                foreach (string child in Directory.EnumerateDirectories(entry.Key))
                {
                    if (--remaining < 0)
                    {
                        note = "目录范围太大，请选择更接近工程的文件夹。";
                        return null;
                    }
                    if (SkipSearchDirectory(child)) { continue; }
                    if (IsProject(child))
                    {
                        matches.Add(child);
                        if (matches.Count > 1)
                        {
                            note = "找到多个工程，请选择具体工程目录：" + Environment.NewLine + string.Join(Environment.NewLine, matches);
                            return null;
                        }
                    }
                    else { queue.Enqueue(new KeyValuePair<string, int>(child, entry.Value + 1)); }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        if (matches.Count == 1)
        {
            note = "已自动定位到工程：" + matches[0];
            return matches[0];
        }
        note = "当前目录及下三级未找到含 src/Makefile 与 lib 的工程，请选择更接近工程的文件夹。";
        return null;
    }

    internal static string ResolveProjectForTest(string path, out string note)
    {
        return ResolveProject(path, out note);
    }

    private static bool IsProject(string path)
    {
        return !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "src", "Makefile")) &&
            Directory.Exists(Path.Combine(path, "lib"));
    }

    private static bool SkipSearchDirectory(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith(".", StringComparison.Ordinal) || name.Equals("build-wsl", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("build", StringComparison.OrdinalIgnoreCase) || name.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { return true; }
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    private void LoadProject(string path, bool announce)
    {
        if (!string.Equals(projectDir, path ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            outputDir = null;
            openDir.Enabled = false;
        }
        projectDir = path ?? string.Empty;
        projectField.Text = projectDir;
        try
        {
            model = ProjectMakefile.Load(projectDir);
        }
        catch (Exception exception)
        {
            model = null;
            versionField.Text = string.Empty;
            SetState("工程不可用", Tone.Error);
            Append("! " + exception.Message, Theme.Error);
            UpdateEnabled();
            return;
        }
        versionField.Text = model.Version;
        UpdateTargetButtons();
        SetState("就绪", Tone.Neutral);
        if (announce) { Append("已读取 " + model.FilePath, Theme.TextSecondary); }
        BuildSettings.Save(projectDir, distro, package);
        UpdateEnabled();
    }

    private void SaveVersion()
    {
        if (!admin || running || model == null)
        {
            if (!admin) { SetState("请先登录管理员", Tone.Error); }
            return;
        }
        string error;
        string version = (versionField.Text ?? string.Empty).Trim();
        if (!ProjectMakefile.IsValidVersion(version, out error))
        {
            versionField.Invalid = true;
            Append("! " + error, Theme.Error);
            return;
        }
        if (version == model.Version)
        {
            Append("版本号没有变化。", Theme.TextSecondary);
            return;
        }
        if (!model.TrySetVersion(version, out error))
        {
            Append("! " + error, Theme.Error);
            return;
        }
        try
        {
            model.Save();
        }
        catch (Exception exception)
        {
            Append("! " + exception.Message, Theme.Error);
            return;
        }
        model = ProjectMakefile.Load(projectDir);
        versionField.Text = model.Version;
        Append("版本号已写回 src/Makefile：" + model.Version, Theme.TextPrimary);
        SetState("版本号已保存", Tone.Success);
    }

    private void SetTarget(string driver)
    {
        if (!admin || running || model == null)
        {
            if (!admin) { SetState("请先登录管理员", Tone.Error); }
            return;
        }
        if (model.Driver == driver) { return; }
        string error;
        if (!model.TrySetDriver(driver, out error))
        {
            Append("! " + error, Theme.Error);
            return;
        }
        try
        {
            model.Save();
        }
        catch (Exception exception)
        {
            Append("! " + exception.Message, Theme.Error);
            return;
        }
        Append("编译目标已写回 src/Makefile：" + driver, Theme.TextPrimary);
        LoadProject(projectDir, false);
        SetState("编译目标已切换", Tone.Success);
    }

    private void UpdateTargetButtons()
    {
        bool ethercatActive = model != null && model.Driver == ProjectMakefile.DriverEthercat;
        bool virtualActive = model != null && model.Driver == ProjectMakefile.DriverVirtual;
        ethercat.Kind = ethercatActive ? ButtonKind.Primary : ButtonKind.Secondary;
        virtualServo.Kind = virtualActive ? ButtonKind.Primary : ButtonKind.Secondary;
        ethercat.Invalidate();
        virtualServo.Invalidate();
    }

    private void TogglePackage()
    {
        if (running || !admin) { return; }
        package = !package;
        packageToggle.Text = package ? "打包：开" : "打包：关";
        BuildSettings.Save(projectDir, distro, package);
    }

    // ---- 构建 ---------------------------------------------------------------
    private void StartBuild()
    {
        if (running) { return; }
        if (!admin)
        {
            SetState("请先登录管理员", Tone.Error);
            Append("请先登录管理员后再构建。", Theme.Error);
            return;
        }
        if (model == null)
        {
            SetState("请先选择工程", Tone.Error);
            return;
        }
        string targetDistro = distro;
        string dir = projectDir;
        bool doPackage = package;
        running = true;
        cancelled = false;
        processGroup = 0;
        outputDir = null;
        workName = null;
        lastLine = null;
        phase = 0;
        progress.Value = 3;
        progress.Tone = Tone.Info;
        phaseText.Text = "准备中";
        clock.Reset();
        clock.Start();
        elapsedText.Text = "已用时 00:00";
        lock (gate) { pending.Clear(); }
        log.Clear();
        openDir.Enabled = false;
        SetState("正在构建", Tone.Info);
        shell.SetBusy(true);
        UpdateEnabled();
        flushTimer.Start();
        elapsedTimer.Start();
        Append("工程 " + dir, Theme.TextSecondary);
        Append("WSL " + distro + " · 目标 " + model.Driver + " · 版本 " + model.Version +
            (doPackage ? " · 编译后打包" : " · 仅编译"), Theme.TextSecondary);

        // 关窗后仍让构建线程完成 WSL 进程组的停止操作
        var thread = new Thread(delegate() { RunBuild(targetDistro, dir, doPackage); });
        thread.Start();
    }

    private void RunBuild(string targetDistro, string dir, bool doPackage)
    {
        string error = null;
        int exit = -1;
        bool environment = false;
        try
        {
            Post(delegate { Append("检查 WSL 环境…", Theme.TextSecondary); });
            string listError;
            string[] distributions = Wsl.ListDistributions(out listError);
            if (listError != null)
            {
                error = listError;
                environment = true;
            }
            else if (Array.IndexOf(distributions, targetDistro) < 0)
            {
                error = "找不到 WSL 发行版 " + targetDistro + "（现有：" +
                    (distributions.Length == 0 ? "无" : string.Join("、", distributions)) + "）。";
                environment = true;
            }
            else if (!cancelled)
            {
                string check = Wsl.CheckEnvironment(targetDistro);
                if (check != null)
                {
                    error = check;
                    environment = true;
                }
                else if (!cancelled)
                {
                    Post(delegate { Append("启动 WSL 构建…", Theme.TextSecondary); });
                    string buildId = "qjar-build-" + Guid.NewGuid().ToString("N");
                    using (var handle = new WslRunner())
                    {
                        handle.Line += OnLine;
                        if (!handle.Start(Wsl.BuildArguments(targetDistro, dir, doPackage, buildId)))
                        {
                            error = "无法启动 wsl.exe。";
                        }
                        else
                        {
                            bool stopAttempted = false;
                            while (!handle.WaitForExit(100))
                            {
                                int group = processGroup;
                                if (cancelled && !stopAttempted && group > 1)
                                {
                                    stopAttempted = true;
                                    string failure = Wsl.KillBuild(targetDistro, group, buildId);
                                    if (failure != null)
                                    {
                                        Post(delegate { Append("! 取消构建失败：" + failure, Theme.Error); });
                                    }
                                }
                            }
                            exit = handle.ExitCode;
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            error = "构建异常：" + exception.Message;
        }
        bool isEnvironment = environment;
        Post(delegate { Finish(exit, error, dir, isEnvironment); });
    }

    private void CancelBuild()
    {
        if (!running || cancelled) { return; }
        cancelled = true;
        SetState("正在取消", Tone.Warning);
        phaseText.Text = "正在取消";
        UpdateEnabled();
        Append("正在停止构建…", Theme.Warning);
    }

    private void Finish(int exit, string error, string dir, bool environment)
    {
        FlushLog();
        flushTimer.Stop();
        elapsedTimer.Stop();
        clock.Stop();
        elapsedText.Text = "已用时 " + FormatClock(clock.Elapsed);
        running = false;
        shell.SetBusy(false);
        if (cancelled && (exit == 9 || exit == 15 || exit == 137 || exit == 143 ||
            (exit == -1 && error == null)))
        {
            progress.Tone = Tone.Warning;
            phaseText.Text = "已取消";
            SetState("已取消", Tone.Warning);
            Append("构建已取消，产物可能不完整。", Theme.Warning);
            UpdateEnabled();
            return;
        }
        if (error != null)
        {
            progress.Value = 0;
            progress.Tone = Tone.Error;
            phaseText.Text = environment ? "环境不可用" : "失败";
            SetState(environment ? "环境不可用" : "构建失败", Tone.Error);
            Append("! " + error, Theme.Error);
            if (environment) { Append("没有启动编译；环境修好后重新点“开始构建”即可。", Theme.TextSecondary); }
            UpdateEnabled();
            return;
        }
        if (exit != 0)
        {
            progress.Value = 0;
            progress.Tone = Tone.Error;
            phaseText.Text = "失败";
            SetState("构建失败", Tone.Error);
            Append("构建失败，退出码 " + exit + "。", Theme.Error);
            if (!string.IsNullOrEmpty(lastLine)) { Append("最后一行：" + lastLine, Theme.TextSecondary); }
            if (string.IsNullOrEmpty(workName))
            {
                Append("WSL 没有返回构建目录，请检查发行版是否可用。", Theme.TextSecondary);
            }
            UpdateEnabled();
            return;
        }

        FinishSuccess(dir);
        UpdateEnabled();
    }

    private void FinishSuccess(string dir)
    {
        string name = workName;
        if (string.IsNullOrEmpty(name))
        {
            name = NewestBuildName(dir);
        }
        if (string.IsNullOrEmpty(name))
        {
            SetState("构建成功但找不到产物", Tone.Warning);
            Append("构建返回成功，但没有找到 build-wsl 产物目录。", Theme.Warning);
            return;
        }
        outputDir = Path.Combine(Path.Combine(dir, "build-wsl"), name);
        string version = model != null ? model.Version : string.Empty;
        string driver = model != null ? model.Driver : string.Empty;
        string resultPath = Path.Combine(outputDir, "result.json");
        if (File.Exists(resultPath))
        {
            try
            {
                string json = File.ReadAllText(resultPath);
                version = JsonString(json, "version") ?? version;
                driver = JsonString(json, "driver") ?? driver;
            }
            catch (IOException)
            {
            }
        }
        Append("构建成功：" + version + " · " + driver, Theme.Success);
        Append("产物 bin " + CountFiles(outputDir, "bin") + " 个 · rtlib " + CountFiles(outputDir, "rtlib") +
            " 个 · lib " + CountFiles(outputDir, "lib") + " 个。", Theme.TextSecondary);

        string packagePath = Path.Combine(outputDir, PackageName);
        if (File.Exists(packagePath))
        {
            var info = new FileInfo(packagePath);
            Append("已打包 " + PackageName + "（" + FormatSize(info.Length) + "），SHA256 " +
                Sha256(packagePath), Theme.TextPrimary);
            Append("包位置：" + packagePath, Theme.TextSecondary);
        }
        else if (package)
        {
            Append("! 没有生成 " + PackageName + "，请检查构建日志。", Theme.Error);
        }
        progress.Value = 100;
        progress.Tone = Tone.Success;
        phaseText.Text = "完成";
        SetState("构建成功", Tone.Success);
        openDir.Enabled = true;
    }

    /* 阶段只由构建日志里的标记推进；每秒再让进度条往前挪一点，避免长时间没有变化。 */
    private void OnElapsedTick()
    {
        if (!running) { elapsedTimer.Stop(); return; }
        elapsedText.Text = "已用时 " + FormatClock(clock.Elapsed);
        if (cancelled)
        {
            return;
        }
        int cap = phase <= 0 ? 8 : phase == 1 ? 70 : phase == 2 ? 85 : phase == 3 ? 92 : 98;
        if (progress.Value < cap) { progress.Value = progress.Value + 1; }
        phaseText.Text = phase <= 0 ? "准备中"
            : phase == 1 ? "编译中"
            : phase == 2 ? "校验产物"
            : phase == 3 ? "打包中"
            : "打包完成";
    }

    private static string FormatClock(TimeSpan elapsed)
    {
        int minutes = (int)elapsed.TotalMinutes;
        return minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00");
    }

    private void OnLine(string line, bool isError)
    {
        int group;
        if (!isError && line.StartsWith(BuildGroupPrefix, StringComparison.Ordinal) &&
            int.TryParse(line.Substring(BuildGroupPrefix.Length), out group) && group > 1)
        {
            processGroup = group;
            return;
        }

        lock (gate)
        {
            pending.Add(new PendingLine { Text = line, Error = isError });
            Match match = OutputLine.Match(line);
            if (match.Success)
            {
                workName = Path.GetFileName(match.Groups["path"].Value.TrimEnd('/'));
            }
            if (line.StartsWith("编译：make modules", StringComparison.Ordinal)) { phase = 1; }
            else if (line.StartsWith("检查产物", StringComparison.Ordinal)) { phase = 2; }
            else if (line.StartsWith("打包：make tar", StringComparison.Ordinal)) { phase = 3; }
            else if (line.StartsWith("打包完成", StringComparison.Ordinal)) { phase = 4; }
            lastLine = line;
        }
    }

    private void FlushLog()
    {
        List<PendingLine> lines;
        lock (gate)
        {
            if (pending.Count == 0) { return; }
            lines = new List<PendingLine>(pending);
            pending.Clear();
        }
        var text = new StringBuilder();
        bool error = lines[0].Error;
        foreach (PendingLine line in lines)
        {
            if (line.Error != error)
            {
                log.Append(text.ToString(), error ? Theme.Error : Theme.TextPrimary);
                text.Length = 0;
                error = line.Error;
            }
            if (text.Length > 0) { text.Append(Environment.NewLine); }
            text.Append(line.Text);
        }
        if (text.Length > 0) { log.Append(text.ToString(), error ? Theme.Error : Theme.TextPrimary); }
    }

    // ---- 结果与导出 ---------------------------------------------------------
    private void OpenOutputDir()
    {
        if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir)) { return; }
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", "\"" + outputDir + "\"");
        }
        catch (Exception exception)
        {
            Append("! 无法打开目录：" + exception.Message, Theme.Error);
        }
    }

    private void ExportLog()
    {
        log.SaveTo(this, "QJAR-构建日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
    }

    private void Append(string line, Color color)
    {
        if (string.IsNullOrEmpty(line)) { return; }
        log.Append(line, color);
    }

    private void SetState(string caption, Tone tone)
    {
        statePill.Caption = caption;
        statePill.Tone = tone;
        LayoutPage();
    }

    private void UpdateEnabled()
    {
        bool editable = admin && !running && !shellBusy && model != null;
        versionField.Edit.Enabled = editable;
        saveVersion.Enabled = editable;
        ethercat.Enabled = editable;
        virtualServo.Enabled = editable;
        build.Enabled = editable;
        cancel.Enabled = running && !cancelled;
        packageToggle.Enabled = admin && !running;
        upgrade.Enabled = admin && !running && !shellBusy && !string.IsNullOrEmpty(outputDir) &&
            File.Exists(Path.Combine(outputDir, PackageName));
        choose.Enabled = !running && !shellBusy;
        guide.Enabled = !running && !shellBusy;
    }

    private void Post(Action action)
    {
        try
        {
            if (IsHandleCreated) { BeginInvoke(action); }
        }
        catch (Exception)
        {
        }
    }

    private static int CountFiles(string dir, string name)
    {
        try
        {
            string path = Path.Combine(dir, name);
            return Directory.Exists(path) ? Directory.GetFiles(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string NewestBuildName(string dir)
    {
        string root = Path.Combine(dir, "build-wsl");
        try
        {
            if (!Directory.Exists(root)) { return null; }
            string newest = null;
            DateTime stamp = DateTime.MinValue;
            foreach (string path in Directory.GetDirectories(root, "controller.*"))
            {
                DateTime time = Directory.GetLastWriteTime(path);
                if (time > stamp) { stamp = time; newest = Path.GetFileName(path); }
            }
            return newest;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Sha256(string path)
    {
        try
        {
            using (var stream = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) { text.Append(value.ToString("x2")); }
                return text.ToString();
            }
        }
        catch (Exception)
        {
            return "—";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L) { return (bytes / 1048576.0).ToString("0.0") + " MB"; }
        if (bytes >= 1024L) { return (bytes / 1024.0).ToString("0.0") + " KB"; }
        return bytes + " B";
    }

    private static string JsonString(string json, string key)
    {
        Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
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
            StopWork();
            flushTimer.Stop();
            flushTimer.Dispose();
            elapsedTimer.Stop();
            elapsedTimer.Dispose();
            clock.Stop();
        }
        base.Dispose(disposing);
    }
}
