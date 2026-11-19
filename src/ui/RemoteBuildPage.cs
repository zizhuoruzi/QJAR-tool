using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class RemoteBuildPage : Panel
{
    private readonly MainForm shell;
    private readonly Card connectionCard = new Card("远程工程");
    private const string DefaultSource = @"E:\QJAR_WORK\项目\";
    private const string SshUser = "qjrobot";
    private const int SshPort = 22;
    private readonly TextField source = new TextField("Windows cx-v3.0 源码目录");
    private readonly ToolTip directoryTip = new ToolTip();
    private readonly ActionButton browse = new ActionButton("浏览…", ButtonKind.Secondary);
    private readonly ComboBox driver = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox projects = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, MaxDropDownItems = 12 };
    private readonly Label projectLabel = new Label { Text = "Ubuntu 目标目录", AutoSize = true };
    private readonly ActionButton refresh = new ActionButton("刷新目录", ButtonKind.Secondary);
    private readonly ActionButton createProject = new ActionButton("新建文件夹", ButtonKind.Secondary);
    private readonly ActionButton compile = new ActionButton("编译", ButtonKind.Primary);
    private readonly Label versionCaption = new Label { Text = "版本号", AutoSize = true };
    private readonly TextField version = new TextField("选择源码目录后可修改版本号");
    private readonly Label installedVersion = new Label { Text = "已部署版本：请选择项目", AutoEllipsis = true };
    private readonly CheckBox packageAfter = new CheckBox { Text = "编译后打包", AutoSize = true };
    private readonly CheckBox packageOnly = new CheckBox { Text = "仅打包", AutoSize = true };
    private readonly CheckBox startAfter = new CheckBox { Text = "编译后启动", AutoSize = true };
    private readonly ActionButton startExisting = new ActionButton("启动已有虚拟版", ButtonKind.Secondary);
    private readonly Label status = new Label();
    private readonly Card logCard = new Card("编译日志");
    private readonly LogView log = new LogView("暂无编译记录。");
    private readonly ActionButton export = new ActionButton("导出日志", ButtonKind.Quiet);
    private readonly Card outputCard = new Card("构建产物");
    private readonly Label packageName = new Label { AutoEllipsis = true, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ActionButton browsePackage = new ActionButton("打开目录", ButtonKind.Quiet);
    private readonly ActionButton upgradePackage = new ActionButton("升级控制器", ButtonKind.Secondary);
    private readonly ActionButton stopConsole = new ActionButton("停止跟随", ButtonKind.Quiet);
    private readonly ActionButton logLayout = new ActionButton("放大日志", ButtonKind.Quiet);
    private readonly System.Windows.Forms.Timer consoleTimer = new System.Windows.Forms.Timer { Interval = 100 };
    private readonly Queue<string> consoleLines = new Queue<string>();
    private CancellationTokenSource consoleCancellation;
    private bool logExpanded;
    private bool admin;
    private bool running;
    private string host;
    private string sourceRoot;
    private string sourceVersion;
    private string packageDirectory;
    private ProjectChoice selectedProject;
    private bool projectSelectedManually;
    private string matchedProject;
    private bool projectsLoaded;
    private string savedPackage;

    private sealed class ProjectChoice
    {
        internal readonly string Name;
        internal ProjectChoice(string name) { Name = name; }
        public override string ToString() { return ProjectDisplayName(Name); }
    }

    internal RemoteBuildPage(MainForm shell)
    {
        this.shell = shell;
        BackColor = Theme.Body;
        source.Text = DefaultSource;
        source.IsReadOnly = true;
        source.Edit.Font = Theme.Code(Theme.FontBase, FontStyle.Regular);
        source.TextChanged += delegate { directoryTip.SetToolTip(source.Edit, source.Text); };
        version.Edit.Font = Theme.Code(Theme.FontBase, FontStyle.Regular);
        driver.AccessibleName = "轴类型";
        driver.Items.AddRange(new object[] { "实体轴（EtherCAT）", "虚拟轴（Virtual Servo）" });
        driver.SelectedIndex = 0;
        driver.SelectedIndexChanged += delegate { UpdateButtons(); };
        projects.AccessibleName = projectLabel.Text;
        projects.BackColor = Theme.Surface;
        projects.SelectionChangeCommitted += delegate { SelectProject((ProjectChoice)projects.SelectedItem, true); };

        refresh.Click += async delegate { await RefreshProjects(); };
        createProject.Click += async delegate { await CreateMatchedProject(); };
        compile.Click += async delegate
        {
            if (packageOnly.Checked) { await PackageExisting(); }
            else { await Compile(); }
        };
        packageOnly.CheckedChanged += delegate
        {
            source.Text = packageOnly.Checked ? (packageDirectory ?? sourceRoot ?? DefaultSource) : SourceDisplayPath;
            source.Invalid = false;
            SetStatus(packageOnly.Checked ? "打包所选 Ubuntu 项目的已有版本，路径框为 Windows 保存目录。"
                : "请选择源码目录、Ubuntu 项目和轴类型。");
            UpdateButtons();
        };
        startExisting.Click += async delegate { await StartExisting(); };
        version.Edit.TextChanged += delegate { version.Invalid = false; };
        browse.Click += delegate { Browse(); };
        export.Click += delegate { log.SaveTo(this, "QJAR-编译日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"); };
        packageName.Text = "暂无升级包";
        browsePackage.Enabled = upgradePackage.Enabled = false;
        browsePackage.Click += delegate { BrowseCompletedPackage(); };
        upgradePackage.Click += delegate
        {
            if (File.Exists(savedPackage)) { shell.UpgradeBuildPackage(savedPackage); }
            else { SetStatus("升级包已移动或删除，请重新打包。", true); }
        };
        stopConsole.Visible = false;
        stopConsole.Click += delegate { StopConsole(); };
        logLayout.Click += delegate { SetLogExpanded(!logExpanded); };
        consoleTimer.Tick += delegate { FlushConsole(); };
        status.BackColor = Color.Transparent;
        status.AutoEllipsis = true;

        Controls.AddRange(new Control[] { connectionCard, logCard, outputCard });
        connectionCard.Controls.AddRange(new Control[] { source, browse, versionCaption, version, driver, packageAfter, packageOnly, startAfter,
            projectLabel, installedVersion, projects, refresh, createProject, compile, startExisting, status });
        logCard.Controls.AddRange(new Control[] { log, export, stopConsole, logLayout });
        outputCard.Controls.AddRange(new Control[] { packageName, browsePackage, upgradePackage });
        string rememberedSource = AddressSettings.LoadBuildSource();
        if (rememberedSource != null) { SetSourcePath(rememberedSource); }
        SetAdmin(false);
    }

    internal void FocusFirst() { if (logExpanded) { logLayout.Focus(); } else { browse.Focus(); } }

    internal bool Running { get { return running; } }
    internal void StopWork() { StopConsole(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width > 0 && Height > 0) { LayoutPage(); }
    }

    private string SourceDisplayPath
    {
        get { return sourceRoot == null ? DefaultSource : Path.Combine(sourceRoot, "cx-v3.0"); }
    }

    internal void SetHost(string value)
    {
        if (host == value) { return; }
        StopConsole();
        host = value;
        ResetProjects();
    }

    internal void SetAdmin(bool value)
    {
        admin = value;
        UpdateButtons();
    }

    internal void LayoutPage()
    {
        int pad = Theme.S(Theme.Space6);
        int cardPad = Theme.S(Theme.Space5);
        int gap = Theme.S(Theme.Space2);
        int field = Theme.S(Theme.SizeMd);
        int bar = Theme.S(Theme.SizeLg);
        int width = Math.Max(Theme.S(360), Width - pad * 2);
        int actionWidth = Theme.S(104);
        int browseWidth = Theme.S(64);
        int inner = width - cardPad * 2;

        source.SetBounds(cardPad, connectionCard.ContentTop, inner - browseWidth - gap, field);
        browse.SetBounds(source.Right + gap, source.Top, browseWidth, field);
        versionCaption.Font = Theme.Caption12;
        versionCaption.Location = new Point(cardPad, source.Bottom + gap + Theme.S(8));
        version.SetBounds(versionCaption.Right + gap, source.Bottom + gap, cardPad + inner - versionCaption.Right - gap, field);

        driver.Font = Theme.Body14;
        int driverWidth = Theme.S(180);
        driver.SetBounds(cardPad, version.Bottom + gap, driverWidth, field);
        int optionGap = Theme.S(Theme.Space1);
        packageAfter.Font = packageOnly.Font = startAfter.Font = Theme.Body14;
        packageAfter.Location = new Point(driver.Right + optionGap, driver.Top + Theme.S(4));
        packageOnly.Location = new Point(packageAfter.Right + optionGap, packageAfter.Top);
        startAfter.Location = new Point(packageOnly.Right + optionGap, packageAfter.Top);
        refresh.SetBounds(browse.Right - actionWidth, projects.Top, actionWidth, field);
        projectLabel.Font = Theme.Caption12;
        projectLabel.Location = new Point(cardPad, driver.Bottom + gap);
        installedVersion.Font = Theme.Caption12;
        installedVersion.SetBounds(projectLabel.Right + Theme.S(18), projectLabel.Top,
            cardPad + inner - projectLabel.Right - Theme.S(18), Theme.S(18));
        projects.Font = Theme.Body14;
        projects.SetBounds(cardPad, projectLabel.Bottom + gap, Math.Min(Theme.S(240), inner - Theme.S(224)), field);
        refresh.Top = projects.Top;
        createProject.SetBounds(projects.Right + gap, projects.Top, Theme.S(104), field);

        compile.SetBounds(cardPad, projects.Bottom + gap, Theme.S(96), bar);
        startExisting.SetBounds(compile.Right + gap, compile.Top, Theme.S(144), bar);
        LayoutStatus();
        connectionCard.SetBounds(pad, pad, width, compile.Bottom + cardPad);

        int logTop = logExpanded ? pad : connectionCard.Bottom + Theme.S(Theme.Space4);
        int outputHeight = Theme.S(58);
        logCard.SetBounds(pad, logTop, width, Math.Max(Theme.S(96), Height - pad - logTop - outputHeight - gap));
        export.SetBounds(width - cardPad - Theme.S(88), Theme.S(Theme.Space5) - Theme.S(2), Theme.S(88), Theme.S(Theme.SizeSm));
        stopConsole.SetBounds(export.Left - gap - export.Width, export.Top, export.Width, export.Height);
        int otherActionsLeft = consoleCancellation == null ? export.Left : stopConsole.Left;
        logLayout.SetBounds(otherActionsLeft - gap - export.Width, export.Top, export.Width, export.Height);
        log.SetBounds(cardPad, logCard.ContentTop, inner, Math.Max(Theme.S(48), logCard.Height - logCard.ContentTop - cardPad));
        outputCard.SetBounds(pad, logCard.Bottom + gap, width, outputHeight);
        int outputY = (outputHeight - bar) / 2;
        upgradePackage.SetBounds(width - cardPad - Theme.S(104), outputY, Theme.S(104), bar);
        browsePackage.SetBounds(upgradePackage.Left - gap - Theme.S(88), outputY, Theme.S(88), bar);
        int fileLeft = Theme.S(112);
        packageName.SetBounds(fileLeft, outputY, Math.Max(0, browsePackage.Left - fileLeft - gap), bar);
    }

    private void SetLogExpanded(bool expanded)
    {
        logExpanded = expanded;
        connectionCard.Visible = !expanded;
        logLayout.Text = expanded ? "还原布局" : "放大日志";
        LayoutPage();
    }

    private void LayoutStatus()
    {
        int gap = Theme.S(Theme.Space2);
        int left = (driver.SelectedIndex == 1 ? startExisting.Right : compile.Right) + gap;
        status.Font = Theme.Caption12;
        status.SetBounds(left, compile.Top + Theme.S(2), Math.Max(0, browse.Right - left), compile.Height);
    }

    private void ResetProjects()
    {
        projectsLoaded = false;
        ClearProjectSelection();
        projects.Items.Clear();
    }

    private void ClearProjectSelection()
    {
        projects.SelectedIndex = -1;
        directoryTip.SetToolTip(projects, null);
        selectedProject = null;
        matchedProject = null;
        projectSelectedManually = false;
        installedVersion.Text = "已部署版本：请选择项目";
        UpdateButtons();
    }

    private void SelectProject(ProjectChoice choice, bool manual)
    {
        if (choice == null) { return; }
        projects.SelectedItem = choice;
        directoryTip.SetToolTip(projects, choice.Name);
        selectedProject = choice;
        projectSelectedManually = manual;
        UpdateButtons();
        string message = (manual ? "已选择 " : "已匹配 ") + choice;
        SetStatus(admin ? message : message + "，请登录管理员后编译。");
        LoadInstalledVersion(choice);
    }

    private static string ProjectDisplayName(string name)
    {
        string label = Regex.Replace(name, @"^[0-9]+[\s、._-]*", "").Trim();
        return label.Length == 0 ? name : label;
    }

    private static string MatchUbuntuProject(string windowsRoot, IEnumerable<string> projectNames)
    {
        if (windowsRoot == null) { return null; }
        var sourceDirectory = new DirectoryInfo(windowsRoot);
        var names = projectNames.ToArray();
        for (DirectoryInfo directory = sourceDirectory; directory != null && directory.Parent != null; directory = directory.Parent)
        {
            if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar),
                DefaultSource.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) { break; }
            string key = ProjectDisplayName(directory.Name);
            if (key == "代码" || key == "项目") { continue; }
            string[] exact = names.Where(name => string.Equals(ProjectDisplayName(name), key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length > 0) { return exact.Length == 1 ? exact[0] : null; }
        }

        int separator = sourceDirectory.Name.LastIndexOfAny(new[] { '_', '-', ' ' });
        if (separator < 0) { return null; }
        string suffix = sourceDirectory.Name.Substring(separator + 1);
        var matches = names.Where(name => string.Equals(ProjectDisplayName(name), suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void SelectMatchedProject(string manualSelection = null)
    {
        var choices = projects.Items.Cast<ProjectChoice>().ToArray();
        matchedProject = MatchUbuntuProject(sourceRoot, choices.Select(choice => choice.Name));
        if (matchedProject == null) { UpdateButtons(); return; }
        var manual = choices.FirstOrDefault(choice => choice.Name == manualSelection);
        if (manual != null) { SelectProject(manual, true); return; }
        var matched = choices.FirstOrDefault(choice => choice.Name == matchedProject);
        if (matched != null) { SelectProject(matched, false); }
    }

    private string GetNewProjectName(out string number)
    {
        number = "";
        if (sourceRoot == null) { return null; }
        var sourceDirectory = new DirectoryInfo(sourceRoot);
        for (DirectoryInfo directory = sourceDirectory; directory != null && directory.Parent != null; directory = directory.Parent)
        {
            Match match = Regex.Match(directory.Name, @"^([0-9]+)[\s、._-]+(.+)$");
            if (!match.Success) { continue; }
            number = match.Groups[1].Value;
            return ProjectDisplayName(directory.Name);
        }
        return ProjectDisplayName(sourceDirectory.Name);
    }

    private async Task CreateMatchedProject()
    {
        if (!createProject.Enabled) { return; }
        string name = GetNewProjectName(out string number);
        if (projects.Items.Cast<ProjectChoice>().Any(choice =>
            string.Equals(ProjectDisplayName(choice.Name), name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Ubuntu 上已有多个同名项目，请先整理同名目录。", true);
            return;
        }

        string requestedHost = host;
        ControllerBuild connection = GetConnection();
        bool created = false;
        await RunOperation("正在新建 Ubuntu 项目目录…", async delegate
        {
            await Task.Run(() => connection.CreateProject(name, number));
            created = true;
        });
        if (created || host != requestedHost) { await RefreshProjects(); }
    }

    private void UpdateButtons()
    {
        refresh.Enabled = !running && !string.IsNullOrEmpty(host);
        upgradePackage.Enabled = admin && !running && File.Exists(savedPackage);
        bool onlyPackage = packageOnly.Checked;
        compile.Text = onlyPackage ? "打包" : "编译";
        compile.Enabled = admin && !running && (onlyPackage || sourceRoot != null) && selectedProject != null;
        bool virtualAxis = driver.SelectedIndex == 1;
        startExisting.Visible = startAfter.Visible = virtualAxis;
        startExisting.Enabled = virtualAxis && admin && !running && selectedProject != null;
        startAfter.Enabled = virtualAxis && !running && !onlyPackage;
        packageAfter.Enabled = !running && !onlyPackage;
        packageOnly.Enabled = !running;
        LayoutStatus();
        version.Edit.Enabled = !running && !onlyPackage && sourceRoot != null;
        projects.Enabled = !running && matchedProject != null;
        createProject.Enabled = admin && !running && projectsLoaded && sourceRoot != null && matchedProject == null;
        driver.Enabled = browse.Enabled = !running;
        source.Edit.Enabled = !running;
    }

    private void Browse()
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = packageOnly.Checked ? "选择更新包的 Windows 保存目录" : "选择 cx-v3.0 源码目录";
            dialog.ShowNewFolderButton = false;
            if (Directory.Exists(source.Text)) { dialog.SelectedPath = source.Text; }
            if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
            if (packageOnly.Checked)
            {
                packageDirectory = dialog.SelectedPath;
                source.Text = packageDirectory;
                source.Invalid = false;
                SetStatus("已选择更新包保存目录。");
            }
            else { TrySelectSource(dialog.SelectedPath); }
        }
    }

    private bool TrySelectSource(string path)
    {
        string root = AddressSettings.GetBuildSourceRoot(path);
        if (root == null)
        {
            SetStatus("请选择 cx-v3.0 源码目录。", true);
            return false;
        }
        SetSourcePath(root);
        ClearProjectSelection();
        SelectMatchedProject();
        try
        {
            AddressSettings.SaveBuildSource(SourceDisplayPath);
            if (selectedProject == null) { SetStatus("未匹配 Ubuntu 目录，可在列表加载后新建。"); }
        }
        catch (IOException) { SetStatus("已选择源码目录，本次路径保存失败。", true); }
        catch (UnauthorizedAccessException) { SetStatus("已选择源码目录，本次路径保存失败。", true); }
        return true;
    }

    private void SetSourcePath(string path)
    {
        sourceRoot = path;
        source.Text = SourceDisplayPath;
        source.Invalid = false;
        UpdateSourceVersion(path);
        UpdateButtons();
    }

    private void UpdateSourceVersion(string path)
    {
        sourceVersion = "";
        try
        {
            foreach (string line in File.ReadLines(Path.Combine(path, "cx-v3.0", "src", "Makefile")))
            {
                Match match = Regex.Match(line, @"^\s*BUILD_MSG\s*[:?]?=\s*([^#]*)");
                if (!match.Success) { continue; }
                string value = match.Groups[1].Value.Trim(' ', '\t', '"', '\'', '\\');
                sourceVersion = value;
                break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        version.Text = sourceVersion;
    }

    private async void LoadInstalledVersion(ProjectChoice selection)
    {
        string currentHost = host;
        string project = selection.Name;
        ControllerBuild connection = GetConnection();
        installedVersion.Text = "已部署版本：正在读取…";
        string value;
        try { value = await Task.Run(() => connection.ReadInstalledVersion(project)); }
        catch (IOException) { value = null; }
        if (IsDisposed || selectedProject != selection || host != currentHost) { return; }
        installedVersion.Text = "已部署版本：" + (string.IsNullOrEmpty(value) ? "未读取到" : value);
    }

    private ControllerBuild GetConnection()
    {
        IProgress<string> progress = new Progress<string>(AppendLog);
        return new ControllerBuild(host, SshUser, SshPort, progress.Report);
    }

    private async void LoadVisibleProjects()
    {
        if (Visible && !IsDisposed && IsHandleCreated && AddressSettings.OverridePath == null && !projectsLoaded) { await RefreshProjects(); }
    }

    private async Task RefreshProjects()
    {
        if (!refresh.Enabled) { return; }
        string requestedHost = host;
        ControllerBuild connection = GetConnection();
        string previousSelection = projectSelectedManually ? selectedProject.Name : null;
        ResetProjects();
        await RunOperation("正在读取 Ubuntu 项目目录…", async delegate
        {
            string[] projects = await Task.Run(() => connection.ListProjects());
            if (IsDisposed || host != requestedHost) { return; }
            this.projects.Items.AddRange(projects.Select(name => new ProjectChoice(name)).ToArray());
            projectsLoaded = true;
            SelectMatchedProject(previousSelection);
            if (selectedProject == null)
            {
                SetStatus(sourceRoot == null ? "请先选择 Windows 源码目录。"
                    : "未匹配 Ubuntu 目录，请点「新建文件夹」。");
            }
        });
        if (host != requestedHost) { LoadVisibleProjects(); }
    }

    private async Task Compile()
    {
        if (!compile.Enabled) { return; }
        ControllerBuild connection = GetConnection();
        if (!File.Exists(Path.Combine(sourceRoot, "cx-v3.0", "src", "Makefile")) ||
            !File.Exists(Path.Combine(sourceRoot, "cx-v3.0", "bin", "control")))
        {
            ShowInvalid(source, "所选目录内需要有 cx-v3.0 源码。");
            return;
        }
        string buildSource = sourceRoot;
        string buildHost = host;
        ProjectChoice selection = selectedProject;
        string projectName = selection.Name;
        string driverName = driver.SelectedIndex == 0 ? "ETHERCAT_X86" : "VIRTUAL_SERVO";
        bool launch = driver.SelectedIndex == 1 && startAfter.Checked;
        bool package = packageAfter.Checked;
        string versionOverride = version.Text.Trim();
        if (versionOverride == sourceVersion) { versionOverride = null; }
        else if (!ControllerBuild.IsValidVersion(versionOverride))
        {
            ShowInvalid(version, "版本号限 1–96 位，以字母或数字开头，可用 . _ + -。");
            return;
        }
        log.Clear();
        await RunOperation("正在打包、上传并编译…", async delegate
        {
            if (versionOverride != null)
            {
                await Task.Run(() => ControllerBuild.SaveSourceVersion(buildSource, versionOverride));
                sourceVersion = versionOverride;
                version.Text = versionOverride;
                AppendLog("已保存 Windows 源码版本：" + versionOverride);
            }
            await Task.Run(() => connection.Build(buildSource, projectName, driverName, launch, package,
                PasswordPrompt(buildHost, projectName), RetryOccupiedPackage, ConsoleReady(connection),
                path => Invoke(new Action(() => ShowCompletedPackage(path)))));
            string result = package ? "编译成功，更新包已保存到 Windows。" : "编译成功。";
            SetStatus(launch ? result + "虚拟控制器已启动。" : result);
        });
        if (host != buildHost) { LoadVisibleProjects(); }
        else if (selectedProject == selection) { LoadInstalledVersion(selection); }
    }

    private async Task PackageExisting()
    {
        if (!compile.Enabled) { return; }
        string directory = source.Text;
        if (!Directory.Exists(directory))
        {
            ShowInvalid(source, "请选择存在的 Windows 保存目录。");
            return;
        }
        string packageHost = host;
        string project = selectedProject.Name;
        string driverName = driver.SelectedIndex == 0 ? "ETHERCAT_X86" : "VIRTUAL_SERVO";
        ControllerBuild connection = GetConnection();
        log.Clear();
        await RunOperation("正在打包 Ubuntu 已部署版本…", async delegate
        {
            string path = await Task.Run(() => connection.PackageExisting(project, driverName, directory, RetryOccupiedPackage));
            ShowCompletedPackage(path);
            SetStatus("打包成功，更新包已保存到 Windows。");
        });
        if (host != packageHost) { LoadVisibleProjects(); }
    }

    private void ShowCompletedPackage(string path)
    {
        savedPackage = path;
        packageName.Text = Path.GetFileName(path);
        browsePackage.Enabled = true;
        UpdateButtons();
        directoryTip.SetToolTip(browsePackage, path);
    }

    private void BrowseCompletedPackage()
    {
        if (!File.Exists(savedPackage))
        {
            SetStatus("安装包已移动或删除，请重新打包。", true);
            browsePackage.Enabled = upgradePackage.Enabled = false;
            return;
        }
        try
        {
            using (Process.Start("explorer.exe", "/select,\"" + savedPackage + "\"")) { }
        }
        catch (System.ComponentModel.Win32Exception error) { SetStatus(error.Message, true); }
    }

    private bool RetryOccupiedPackage(string path)
    {
        return (bool)Invoke(new Func<bool>(() =>
            MessageBox.Show(this, "以下旧包正在被其他程序使用。关闭占用它的程序后，点击「重试」继续下载。\n\n" + path,
                "更新包文件被占用", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                == DialogResult.Retry));
    }

    private async Task StartExisting()
    {
        if (!startExisting.Enabled) { return; }
        string buildHost = host;
        string project = selectedProject.Name;
        ControllerBuild connection = GetConnection();
        if (MessageBox.Show(this, "启动 " + buildHost + " / " + project + " 的已有虚拟控制器？",
            "启动虚拟控制器", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes) { return; }
        log.Clear();
        await RunOperation("正在启动虚拟控制器…", async delegate
        {
            await Task.Run(() => connection.StartExisting(project, PasswordPrompt(buildHost, project), ConsoleReady(connection)));
            SetStatus("虚拟控制器已启动。");
        });
        if (host != buildHost) { LoadVisibleProjects(); }
    }

    private Func<string> PasswordPrompt(string buildHost, string project)
    {
        return () => (string)Invoke(new Func<string>(() => RequestSudoPassword(buildHost, project)));
    }

    private Action<string> ConsoleReady(ControllerBuild connection)
    {
        return directory => Invoke(new Action(() => OpenConsole(connection, directory)));
    }

    private async void OpenConsole(ControllerBuild connection, string directory)
    {
        StopConsole();
        var session = new CancellationTokenSource();
        consoleCancellation = session;
        stopConsole.Visible = true;
        SetLogExpanded(true);
        consoleTimer.Start();
        AppendLog("正在跟随虚拟控制器的实时输出…");
        try
        {
            await Task.Run(() => connection.FollowConsole(directory, line =>
            {
                lock (consoleLines)
                {
                    if (session.IsCancellationRequested) { return; }
                    if (consoleLines.Count == 2048) { consoleLines.Dequeue(); }
                    consoleLines.Enqueue(line);
                }
            }, session.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            if (consoleCancellation == session) { AppendLog(error.Message); }
        }
        finally
        {
            if (consoleCancellation == session)
            {
                FlushConsole();
                StopConsole();
            }
            session.Dispose();
        }
    }

    private void FlushConsole()
    {
        var lines = new StringBuilder();
        lock (consoleLines)
        {
            while (consoleLines.Count > 0)
            {
                if (lines.Length > 0) { lines.AppendLine(); }
                lines.Append(consoleLines.Dequeue());
            }
        }
        if (lines.Length == 0 || IsDisposed) { return; }
        log.TrimToRecentText(240000);
        AppendLog(lines.ToString());
    }

    private void StopConsole()
    {
        if (consoleCancellation == null) { return; }
        consoleCancellation.Cancel();
        consoleCancellation = null;
        consoleTimer.Stop();
        stopConsole.Visible = false;
        if (!Disposing) { LayoutPage(); }
        lock (consoleLines) { consoleLines.Clear(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopConsole();
            consoleTimer.Dispose();
            directoryTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private string RequestSudoPassword(string buildHost, string projectName)
    {
        using (var dialog = new Form())
        {
            dialog.Text = "需要 Ubuntu sudo 密码";
            dialog.Font = Theme.Body14;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MinimizeBox = dialog.MaximizeBox = dialog.ShowInTaskbar = false;
            dialog.ClientSize = new Size(Theme.S(452), Theme.S(132));
            var target = new Label { Text = buildHost + "  /  " + projectName, AutoEllipsis = true };
            var password = new TextBox { AccessibleName = "Ubuntu sudo 密码", UseSystemPasswordChar = true };
            var confirm = new Button { Text = "继续", DialogResult = DialogResult.OK, Enabled = false };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            target.SetBounds(Theme.S(16), Theme.S(14), Theme.S(420), Theme.S(24));
            password.SetBounds(Theme.S(16), Theme.S(42), Theme.S(420), Theme.S(28));
            confirm.SetBounds(Theme.S(228), Theme.S(84), Theme.S(100), Theme.S(32));
            cancel.SetBounds(Theme.S(336), Theme.S(84), Theme.S(100), Theme.S(32));
            password.TextChanged += delegate { confirm.Enabled = password.TextLength > 0; };
            dialog.Controls.AddRange(new Control[] { target, password, confirm, cancel });
            dialog.AcceptButton = confirm;
            dialog.CancelButton = cancel;
            dialog.ActiveControl = password;
            return dialog.ShowDialog(this) == DialogResult.OK ? password.Text : null;
        }
    }

    private async Task RunOperation(string message, Func<Task> operation)
    {
        if (running || shell.IsBusy) { return; }
        running = true;
        shell.SetBusy(true);
        UpdateButtons();
        SetStatus(message);
        try { await operation(); }
        catch (Exception error)
        {
            AppendLog(error.Message);
            SetStatus(error.Message, true);
        }
        finally
        {
            running = false;
            if (!shell.IsDisposed && !shell.Disposing) { shell.SetBusy(false); }
            UpdateButtons();
        }
    }

    private void AppendLog(string line)
    {
        if (!IsDisposed && !string.IsNullOrEmpty(line)) { log.Append(line, Theme.TextPrimary); }
    }

    private void ShowInvalid(TextField field, string message)
    {
        field.Invalid = true;
        field.Edit.Focus();
        SetStatus(message, true);
    }

    private void SetStatus(string text, bool error = false)
    {
        status.ForeColor = error ? Theme.Error : Theme.TextSecondary;
        status.Text = text;
    }
}
