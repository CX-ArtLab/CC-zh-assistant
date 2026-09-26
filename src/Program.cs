using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Claude Code 桌面版中文助手")]
[assembly: AssemblyDescription("Claude Desktop / Claude Code 离线界面汉化伴侣")]
[assembly: AssemblyCompany("CX-ArtLab")]
[assembly: AssemblyProduct("Claude Code 桌面版中文助手")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CCZhAssistant
{
    internal static class Program
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        private static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            bool isApply = Array.Exists(args, delegate(string a) { return string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase); });
            bool isRestore = Array.Exists(args, delegate(string a) { return string.Equals(a, "--restore", StringComparison.OrdinalIgnoreCase); });

            if (isApply || isRestore)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                MainForm.ClaudeEnvironment cliEnv = MainForm.DetectClaudeEnvironment();
                if (!cliEnv.IsInstalled)
                {
                    Console.WriteLine("[CCZhAssistant] 未检测到 Claude Desktop 安装。");
                    return;
                }

                if (isApply)
                {
                    MainForm.PerformApplyLocalization(cliEnv);
                    Console.WriteLine("[CCZhAssistant] Claude Desktop 中文汉化包已成功应用！");
                }
                else
                {
                    MainForm.PerformRestoreOfficial(cliEnv);
                    Console.WriteLine("[CCZhAssistant] Claude Desktop 已成功恢复官方英文原版！");
                }
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\CCZhAssistant.Singleton", out created))
            using (EventWaitHandle activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CCZhAssistant.Activate"))
            {
                if (!created)
                {
                    activationEvent.Set();
                    IntPtr existing = FindWindow(null, "Claude Code 桌面版中文助手");
                    if (existing != IntPtr.Zero)
                    {
                        ShowWindow(existing, 9); // SW_RESTORE
                        SetForegroundWindow(existing);
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool startupMode = Array.Exists(args, delegate(string arg)
                {
                    return string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase);
                });

                try
                {
                    Application.Run(new MainForm(startupMode, activationEvent));
                }
                catch (Exception ex)
                {
                    try
                    {
                        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude Code 中文助手", "startup_error.log");
                        File.WriteAllText(log, ex.ToString(), Encoding.UTF8);
                    }
                    catch { }
                    MessageBox.Show("启动遇到错误：" + ex.Message + "\n\n" + ex.StackTrace, "CCZhAssistant 启动异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    internal sealed class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        private const string AppName = "Claude Code 桌面版中文助手";
        private const string RunValueName = "CCZhAssistant";
        private const string PackManifestUrl = "https://raw.githubusercontent.com/CX-ArtLab/CC-zh-assistant/main/translation/manifest.json";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly bool startupMode;
        private readonly EventWaitHandle activationEvent;
        private readonly Thread activationThread;
        private readonly HttpClient updateHttp;
        private readonly System.Windows.Forms.Timer monitorTimer;
        private NotifyIcon trayIcon;

        private Label statusLabel;
        private Label detailLabel;
        private Panel statusDot;
        private Label versionLabel;
        private Label entryLabel;
        private Label unknownLabel;
        private Label scanLabel;

        private Button applyButton;
        private Button hideButton;
        private CheckBox monitorCheckBox;
        private CheckBox adaptCheckBox;
        private CheckBox packUpdateCheckBox;
        private CheckBox startupCheckBox;

        private bool busy;
        private bool isCurrentlyLocalized;
        private string detectedClaudeVersion = "未检测";
        private static int translatedCount = 32597;
        private static int pendingCount = 0;
        private DateTime lastAppliedTime = DateTime.MinValue;

        private static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude Code 中文助手"); }
        }

        private static string BackupDirectory
        {
            get { return Path.Combine(DataDirectory, "backups"); }
        }

        private static string PackPath
        {
            get { return Path.Combine(DataDirectory, "translation-pack.json"); }
        }

        private static string UnknownReportPath
        {
            get { return Path.Combine(DataDirectory, "待适配词条.json"); }
        }

        private static string SettingsFilePath
        {
            get { return Path.Combine(DataDirectory, "settings.json"); }
        }

        public MainForm(bool startupMode, EventWaitHandle activationEvent)
        {
            this.startupMode = startupMode;
            this.activationEvent = activationEvent;

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(BackupDirectory);

            updateHttp = new HttpClient();
            updateHttp.Timeout = TimeSpan.FromSeconds(8);
            updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CCZhAssistant/1.0.0");

            InitializeComponents();

            // Set up activation listening thread
            activationThread = new Thread(ListenForActivation) { IsBackground = true };
            activationThread.Start();

            // Background monitor timer
            monitorTimer = new System.Windows.Forms.Timer();
            monitorTimer.Interval = 3000;
            monitorTimer.Tick += async delegate { await OnMonitorTickAsync(); };
            monitorTimer.Start();

            // Initial state evaluation
            LoadSettings();
            EvaluateState();

            if (startupMode)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }

            Shown += async delegate
            {
                if (startupMode)
                {
                    Hide();
                }
                await CheckPackUpdateAsync(false);
            };
        }

        private void InitializeComponents()
        {
            Text = AppName;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(760, 620);
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = LoadAssistantIcon();
            BackColor = Color.FromArgb(248, 250, 253);

            // Header Gradient Panel
            GradientPanel header = new GradientPanel();
            header.Location = new Point(0, 0);
            header.Size = new Size(760, 105);
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            header.Color1 = Color.FromArgb(254, 250, 248);
            header.Color2 = Color.FromArgb(248, 250, 253);
            Controls.Add(header);

            PictureBox logo = new PictureBox();
            logo.Image = LoadAssistantBitmap();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Location = new Point(32, 22);
            logo.Size = new Size(54, 54);
            header.Controls.Add(logo);

            Label title = new Label();
            title.Text = "Claude Code 桌面版中文助手";
            title.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(31, 31, 31);
            title.BackColor = Color.Transparent;
            title.Location = new Point(100, 18);
            title.AutoSize = true;
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "外挂伴侣 · 离线优先 · 一键应用 · 无损还原";
            subtitle.ForeColor = Color.FromArgb(100, 104, 110);
            subtitle.BackColor = Color.Transparent;
            subtitle.Location = new Point(102, 58);
            subtitle.Size = new Size(420, 24);
            header.Controls.Add(subtitle);

            Label versionBadge = new Label();
            versionBadge.Text = "v1.0.0";
            versionBadge.TextAlign = ContentAlignment.MiddleCenter;
            versionBadge.ForeColor = Color.FromArgb(195, 75, 45);
            versionBadge.BackColor = Color.FromArgb(255, 237, 232);
            versionBadge.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            versionBadge.Location = new Point(640, 32);
            versionBadge.Size = new Size(84, 30);
            versionBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            ApplyRoundedRegion(versionBadge, 15);
            header.Controls.Add(versionBadge);

            // Status Panel
            CardPanel statusPanel = new CardPanel();
            statusPanel.Location = new Point(32, 115);
            statusPanel.Size = new Size(696, 136);
            statusPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(statusPanel);

            statusDot = new Panel();
            statusDot.BackColor = Color.FromArgb(26, 115, 232);
            statusDot.Location = new Point(24, 25);
            statusDot.Size = new Size(12, 12);
            ApplyRoundedRegion(statusDot, 6);
            statusPanel.Controls.Add(statusDot);

            statusLabel = new Label();
            statusLabel.Text = "准备就绪";
            statusLabel.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            statusLabel.ForeColor = Color.FromArgb(31, 31, 31);
            statusLabel.Location = new Point(46, 18);
            statusLabel.AutoSize = true;
            statusPanel.Controls.Add(statusLabel);

            detailLabel = new Label();
            detailLabel.Text = "检测到系统已安装 Claude Desktop。点击下方按钮即可一键应用汉化。";
            detailLabel.ForeColor = Color.FromArgb(80, 84, 90);
            detailLabel.Location = new Point(46, 48);
            detailLabel.Size = new Size(630, 24);
            statusPanel.Controls.Add(detailLabel);

            versionLabel = CreateMetricLabel(statusPanel, "Claude：检测中...", 24);
            entryLabel = CreateMetricLabel(statusPanel, "已汉化：13,062 条", 200);
            unknownLabel = CreateMetricLabel(statusPanel, "待适配：0 条", 370);
            scanLabel = CreateMetricLabel(statusPanel, "状态：就绪", 530);

            versionLabel.Top = entryLabel.Top = unknownLabel.Top = scanLabel.Top = 94;

            // Automation Settings Panel
            CardPanel settingsPanel = new CardPanel();
            settingsPanel.Location = new Point(32, 265);
            settingsPanel.Size = new Size(696, 265);
            settingsPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(settingsPanel);

            Label settingsTitle = new Label();
            settingsTitle.Text = "自动化与配置";
            settingsTitle.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            settingsTitle.Location = new Point(22, 16);
            settingsTitle.AutoSize = true;
            settingsPanel.Controls.Add(settingsTitle);

            monitorCheckBox = CreateSettingCheckBox("后台监控 Claude", 24, 48, true);
            adaptCheckBox = CreateSettingCheckBox("Claude 更新后自动适配", 24, 92, true);
            packUpdateCheckBox = CreateSettingCheckBox("自动检查最新汉化包", 24, 136, true);
            startupCheckBox = CreateSettingCheckBox("随 Windows 启动", 24, 180, IsAutoStartEnabled());

            settingsPanel.Controls.Add(monitorCheckBox);
            settingsPanel.Controls.Add(adaptCheckBox);
            settingsPanel.Controls.Add(packUpdateCheckBox);
            settingsPanel.Controls.Add(startupCheckBox);

            settingsPanel.Controls.Add(CreateSettingDescription("运行期间自动检测 Claude 启动与状态。", 310, 50));
            settingsPanel.Controls.Add(CreateSettingDescription("客户端版本升级后自动增量适配并重写配置。", 310, 94));
            settingsPanel.Controls.Add(CreateSettingDescription("优先读取最新词典包，无需重新下载助手。", 310, 138));
            settingsPanel.Controls.Add(CreateSettingDescription("开机自启并默认最小化至系统托盘。", 310, 182));

            startupCheckBox.CheckedChanged += delegate
            {
                SetAutoStart(startupCheckBox.Checked);
                SaveSettings();
            };
            monitorCheckBox.CheckedChanged += delegate { SaveSettings(); };
            adaptCheckBox.CheckedChanged += delegate { SaveSettings(); };
            packUpdateCheckBox.CheckedChanged += delegate { SaveSettings(); };

            Label privacyLabel = new Label();
            privacyLabel.Text = "安全保障：仅处理系统界面元素，不修改对话内容、代码或项目文件；随时可一键完全恢复。";
            privacyLabel.ForeColor = Color.FromArgb(120, 124, 130);
            privacyLabel.Location = new Point(24, 228);
            privacyLabel.Size = new Size(648, 22);
            privacyLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            settingsPanel.Controls.Add(privacyLabel);

            Button exitButton = new Button();
            exitButton.Text = "退出助手";
            exitButton.Size = new Size(95, 44);
            exitButton.BackColor = Color.FromArgb(240, 242, 246);
            exitButton.ForeColor = Color.FromArgb(90, 94, 100);
            exitButton.Font = new Font("Microsoft YaHei UI", 9.5F);
            exitButton.FlatStyle = FlatStyle.Flat;
            exitButton.FlatAppearance.BorderSize = 0;
            exitButton.Click += delegate
            {
                trayIcon.Visible = false;
                Application.Exit();
            };
            Controls.Add(exitButton);

            Button openDataButton = new Button();
            openDataButton.Text = "打开配置目录";
            openDataButton.Size = new Size(115, 44);
            openDataButton.BackColor = Color.FromArgb(240, 242, 246);
            openDataButton.ForeColor = Color.FromArgb(90, 94, 100);
            openDataButton.Font = new Font("Microsoft YaHei UI", 9.5F);
            openDataButton.FlatStyle = FlatStyle.Flat;
            openDataButton.FlatAppearance.BorderSize = 0;
            openDataButton.Click += delegate
            {
                try { Process.Start("explorer.exe", DataDirectory); } catch { }
            };
            Controls.Add(openDataButton);

            applyButton = new Button();
            applyButton.Text = "立即检测并应用";
            applyButton.Size = new Size(190, 44);
            applyButton.BackColor = Color.FromArgb(217, 119, 87);
            applyButton.ForeColor = Color.White;
            applyButton.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            applyButton.FlatStyle = FlatStyle.Flat;
            applyButton.FlatAppearance.BorderSize = 0;
            applyButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(193, 95, 60);
            applyButton.Click += async delegate { await OnApplyButtonClickedAsync(); };
            Controls.Add(applyButton);

            hideButton = new Button();
            hideButton.Text = "最小化到托盘";
            hideButton.Size = new Size(130, 44);
            hideButton.BackColor = Color.FromArgb(240, 242, 246);
            hideButton.ForeColor = Color.FromArgb(60, 64, 70);
            hideButton.Font = new Font("Microsoft YaHei UI", 9.5F);
            hideButton.FlatStyle = FlatStyle.Flat;
            hideButton.FlatAppearance.BorderSize = 0;
            hideButton.Click += delegate { HideToTray(); };
            Controls.Add(hideButton);

            Action updateButtonLayout = delegate
            {
                int btnHeight = 44;
                int btnY = ClientSize.Height - btnHeight - 22;
                exitButton.Location = new Point(32, btnY);
                openDataButton.Location = new Point(exitButton.Right + 12, btnY);
                applyButton.Location = new Point(ClientSize.Width - 32 - applyButton.Width, btnY);
                hideButton.Location = new Point(applyButton.Left - 14 - hideButton.Width, btnY);
                ApplyRoundedRegion(exitButton, 22);
                ApplyRoundedRegion(openDataButton, 22);
                ApplyRoundedRegion(applyButton, 22);
                ApplyRoundedRegion(hideButton, 22);
            };

            updateButtonLayout();
            Resize += delegate { updateButtonLayout(); };

            // System Tray
            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon;
            trayIcon.Text = AppName;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };

            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add(new MenuItem("显示主界面", delegate { RestoreFromTray(); }));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add(new MenuItem("立即应用汉化", async delegate { await ApplyLocalizationAsync(true); }));
            trayMenu.MenuItems.Add(new MenuItem("恢复官方原版", async delegate { await RestoreOfficialAsync(true); }));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add(new MenuItem("随 Windows 启动", delegate
            {
                bool newState = !IsAutoStartEnabled();
                SetAutoStart(newState);
                startupCheckBox.Checked = newState;
                SaveSettings();
            }));
            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add(new MenuItem("退出", delegate
            {
                trayIcon.Visible = false;
                Application.Exit();
            }));
            trayIcon.ContextMenu = trayMenu;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            trayIcon.Visible = false;
            base.OnFormClosing(e);
        }

        private void HideToTray()
        {
            Hide();
            trayIcon.ShowBalloonTip(1500, AppName, "助手已最小化到系统托盘，后台持续守护汉化状态。", ToolTipIcon.Info);
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            SetForegroundWindow(Handle);
        }

        private void ListenForActivation()
        {
            while (true)
            {
                activationEvent.WaitOne();
                BeginInvoke(new Action(RestoreFromTray));
            }
        }

        // ==========================================
        // Claude Detection & Environment
        // ==========================================

        internal sealed class ClaudeEnvironment
        {
            public bool IsInstalled { get; set; }
            public string AppPath { get; set; }
            public string ResourcesPath { get; set; }
            public string Version { get; set; }
            public bool IsRunning { get; set; }
        }

        internal static ClaudeEnvironment DetectClaudeEnvironment()
        {
            ClaudeEnvironment env = new ClaudeEnvironment();

            // 1. Check running processes first (most reliable when Claude is running)
            try
            {
                Process[] processes = Process.GetProcessesByName("claude");
                if (processes.Length > 0)
                {
                    env.IsRunning = true;
                    foreach (Process p in processes)
                    {
                        try
                        {
                            string fn = p.MainModule.FileName;
                            if (string.IsNullOrEmpty(fn) || !fn.EndsWith("claude.exe", StringComparison.OrdinalIgnoreCase)) continue;

                            string appDir = Path.GetDirectoryName(fn);
                            string res = Path.Combine(appDir, "resources");
                            if (Directory.Exists(res))
                            {
                                env.IsInstalled = true;
                                env.AppPath = appDir;
                                env.ResourcesPath = res;

                                // Try to extract version from parent folder name e.g. Claude_2.9939.2.0_...
                                string parentName = Path.GetFileName(Path.GetDirectoryName(appDir));
                                if (parentName != null && parentName.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase))
                                {
                                    string[] parts = parentName.Split('_');
                                    if (parts.Length > 1) env.Version = parts[1];
                                }
                                if (string.IsNullOrEmpty(env.Version))
                                {
                                    FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(fn);
                                    env.Version = fvi.ProductVersion ?? fvi.FileVersion ?? "2.9939.2";
                                }
                                return env;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 2. Check HKCU AppModel Repository (standard for MSIX / Store apps, always accessible without admin)
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (key != null)
                    {
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            if (sub.StartsWith("Claude", StringComparison.OrdinalIgnoreCase))
                            {
                                using (RegistryKey subKey = key.OpenSubKey(sub))
                                {
                                    if (subKey != null)
                                    {
                                        object rootObj = subKey.GetValue("PackageRootFolder");
                                        if (rootObj != null)
                                        {
                                            string root = rootObj.ToString();
                                            string res = Path.Combine(root, "app", "resources");
                                            if (Directory.Exists(res))
                                            {
                                                env.IsInstalled = true;
                                                env.AppPath = Path.Combine(root, "app");
                                                env.ResourcesPath = res;
                                                string[] parts = sub.Split('_');
                                                if (parts.Length > 1) env.Version = parts[1];
                                                else env.Version = "2.9939.2";
                                                return env;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Search common unpackaged / local directories
            string localProg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude", "resources");
            if (Directory.Exists(localProg))
            {
                env.IsInstalled = true;
                env.AppPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude");
                env.ResourcesPath = localProg;
                env.Version = "本地版";
                return env;
            }

            // 4. Fallback search WindowsApps packages with direct pattern if accessible
            string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(windowsApps))
            {
                try
                {
                    string[] dirs = Directory.GetDirectories(windowsApps, "Claude_*");
                    Array.Sort(dirs);
                    if (dirs.Length > 0)
                    {
                        string latestDir = dirs[dirs.Length - 1];
                        string res = Path.Combine(latestDir, "app", "resources");
                        if (Directory.Exists(res))
                        {
                            env.IsInstalled = true;
                            env.AppPath = Path.Combine(latestDir, "app");
                            env.ResourcesPath = res;
                            string dirName = Path.GetFileName(latestDir);
                            string[] parts = dirName.Split('_');
                            if (parts.Length > 1) env.Version = parts[1];
                            else env.Version = "2.9939.2";
                            return env;
                        }
                    }
                }
                catch { }
            }

            return env;
        }

        private bool CheckIfCurrentlyLocalized(ClaudeEnvironment env)
        {
            if (env == null || !env.IsInstalled || string.IsNullOrEmpty(env.ResourcesPath))
            {
                return false;
            }

            string zhJson = Path.Combine(env.ResourcesPath, "ion-dist", "i18n", "zh-CN.json");
            return File.Exists(zhJson);
        }

        private void EvaluateState()
        {
            ClaudeEnvironment env = DetectClaudeEnvironment();
            if (!env.IsInstalled)
            {
                detectedClaudeVersion = "未安装";
                statusDot.BackColor = Color.FromArgb(217, 48, 37); // Red
                statusLabel.Text = "未检测到 Claude Desktop";
                detailLabel.Text = "请先下载并安装 Claude Desktop（桌面版客户端）。";
                applyButton.Enabled = false;
                applyButton.Text = "未找到目标";
                versionLabel.Text = "Claude：未安装";
                return;
            }

            detectedClaudeVersion = env.Version ?? "已安装";
            versionLabel.Text = "Claude：v" + detectedClaudeVersion;

            isCurrentlyLocalized = CheckIfCurrentlyLocalized(env);

            if (isCurrentlyLocalized)
            {
                statusDot.BackColor = Color.FromArgb(30, 142, 62); // Green
                statusLabel.Text = "汉化已生效";
                detailLabel.Text = "Claude 桌面版已成功配置为中文环境，支持所有对话与设置。";
                applyButton.Enabled = true;
                applyButton.Text = "恢复官方原版";
                applyButton.BackColor = Color.FromArgb(60, 64, 70);
                scanLabel.Text = "状态：已生效";
            }
            else
            {
                statusDot.BackColor = Color.FromArgb(26, 115, 232); // Blue
                statusLabel.Text = "准备就绪";
                detailLabel.Text = "已就绪。点击下方按钮即可一键部署汉化包。";
                applyButton.Enabled = true;
                applyButton.Text = "立即检测并应用";
                applyButton.BackColor = Color.FromArgb(217, 119, 87);
                scanLabel.Text = "状态：官方原版";
            }

            UpdateStatsDisplay();
        }

        private void UpdateStatsDisplay()
        {
            entryLabel.Text = "已汉化：" + translatedCount.ToString("N0") + " 条";
            unknownLabel.Text = "待适配：" + pendingCount.ToString("N0") + " 条";
            if (lastAppliedTime != DateTime.MinValue)
            {
                scanLabel.Text = "上次应用：" + lastAppliedTime.ToString("HH:mm:ss");
            }
        }

        private async Task OnMonitorTickAsync()
        {
            if (busy) return;
            if (!monitorCheckBox.Checked) return;

            ClaudeEnvironment env = DetectClaudeEnvironment();
            if (!env.IsInstalled) return;

            bool localized = CheckIfCurrentlyLocalized(env);
            if (localized != isCurrentlyLocalized)
            {
                EvaluateState();
            }

            // Auto adapt if enabled and version changed
            if (adaptCheckBox.Checked && isCurrentlyLocalized && env.IsRunning)
            {
                // Verify files exist
                string zhJson = Path.Combine(env.ResourcesPath, "ion-dist", "i18n", "zh-CN.json");
                if (!File.Exists(zhJson))
                {
                    await ApplyLocalizationAsync(false);
                }
            }
        }

        // ==========================================
        // Apply & Restore Operations
        // ==========================================

        private async Task OnApplyButtonClickedAsync()
        {
            if (busy) return;

            if (isCurrentlyLocalized)
            {
                DialogResult dr = MessageBox.Show(
                    "确定要恢复官方原版英文界面吗？\n助手将完整还原所有修改过的配置与资源。",
                    "恢复官方语言确认",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (dr == DialogResult.Yes)
                {
                    await RestoreOfficialAsync(true);
                }
            }
            else
            {
                await ApplyLocalizationAsync(true);
            }
        }

        private async Task ApplyLocalizationAsync(bool showFeedback)
        {
            if (busy) return;
            busy = true;
            applyButton.Enabled = false;

            try
            {
                ClaudeEnvironment env = DetectClaudeEnvironment();
                if (!env.IsInstalled)
                {
                    if (showFeedback) MessageBox.Show("未找到 Claude Desktop 安装路径。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                statusDot.BackColor = Color.FromArgb(232, 113, 10);
                statusLabel.Text = "正在应用汉化...";
                detailLabel.Text = "正在备份原始配置并写入中文语言包，请稍候...";

                await Task.Run(() =>
                {
                    PerformApplyLocalization(env);
                });

                lastAppliedTime = DateTime.Now;
                EvaluateState();

                if (showFeedback)
                {
                    if (env.IsRunning)
                    {
                        DialogResult restartPrompt = MessageBox.Show(
                            "中文汉化已成功部署！\n\n【免重启生效】：可在 Claude 窗口中按 Ctrl+R 刷新当前界面。\n【全量生效（推荐）】：顶层菜单、右键菜单由系统主进程管理，重启后可 100% 完整生效。\n\n是否立即帮您重启 Claude？",
                            "汉化成功",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information);

                        if (restartPrompt == DialogResult.Yes)
                        {
                            RestartClaude();
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            "中文汉化已成功应用！\n启动 Claude Desktop 即可直接呈现全中文界面。",
                            "汉化成功",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                }
            }
            catch (UnauthorizedAccessException uex)
            {
                MessageBox.Show(
                    "写入资源时遭遇权限受限：" + uex.Message + "\n\n建议右键点击本助手，选择“以管理员身份运行”后再试。",
                    "权限不足",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("应用汉化失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                applyButton.Enabled = true;
                EvaluateState();
            }
        }

        internal static void PerformApplyLocalization(ClaudeEnvironment env)
        {
            string resources = env.ResourcesPath;
            string ionDist = Path.Combine(resources, "ion-dist");
            string i18nDir = Path.Combine(ionDist, "i18n");
            string dynamicDir = Path.Combine(i18nDir, "dynamic");
            string statsigDir = Path.Combine(i18nDir, "statsig");
            string assetsDir = Path.Combine(ionDist, "assets", "v1");

            Directory.CreateDirectory(i18nDir);
            Directory.CreateDirectory(dynamicDir);
            Directory.CreateDirectory(statsigDir);

            // Load translation pack (from local update or embedded)
            IDictionary pack = LoadTranslationPack();
            if (pack == null) throw new Exception("无法加载汉化词典包。");

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            // 1. Write frontend zh-CN.json
            object frontendObj = pack["frontend"];
            if (frontendObj != null)
            {
                string frontendJson = serializer.Serialize(frontendObj);
                File.WriteAllText(Path.Combine(i18nDir, "zh-CN.json"), frontendJson, Utf8NoBom);
            }

            // 2. Write dynamic zh-CN.json (models, thinking mode, features)
            object dynamicObj = pack["dynamic"];
            if (dynamicObj != null)
            {
                string dynamicJson = serializer.Serialize(dynamicObj);
                File.WriteAllText(Path.Combine(dynamicDir, "zh-CN.json"), dynamicJson, Utf8NoBom);
            }

            // 3. Write statsig zh-CN.json
            object statsigObj = pack["statsig"];
            if (statsigObj != null)
            {
                string statsigJson = serializer.Serialize(statsigObj);
                File.WriteAllText(Path.Combine(statsigDir, "zh-CN.json"), statsigJson, Utf8NoBom);
            }

            // 4. Write desktop zh-CN.json & patch resources/en-US.json fallback
            object desktopObj = pack["desktop"];
            if (desktopObj != null)
            {
                string desktopJson = serializer.Serialize(desktopObj);
                File.WriteAllText(Path.Combine(resources, "zh-CN.json"), desktopJson, Utf8NoBom);

                string enDesktopPath = Path.Combine(resources, "en-US.json");
                if (File.Exists(enDesktopPath))
                {
                    string enOrigBackup = Path.Combine(BackupDirectory, "en-US.json.orig");
                    if (!File.Exists(enOrigBackup))
                    {
                        File.Copy(enDesktopPath, enOrigBackup, true);
                    }

                    try
                    {
                        string enContent = File.ReadAllText(enDesktopPath, Encoding.UTF8);
                        Dictionary<string, object> enDict = serializer.Deserialize<Dictionary<string, object>>(enContent);
                        IDictionary deskDict = desktopObj as IDictionary;
                        if (enDict != null && deskDict != null)
                        {
                            foreach (DictionaryEntry de in deskDict)
                            {
                                enDict[de.Key.ToString()] = de.Value;
                            }
                            File.WriteAllText(enDesktopPath, serializer.Serialize(enDict), Utf8NoBom);
                        }
                    }
                    catch { }
                }
            }

            // 5. Deploy translator.js and inject into index.html / frame-shell.html
            string translatorScript = LoadTranslatorScript();
            if (!string.IsNullOrEmpty(translatorScript))
            {
                File.WriteAllText(Path.Combine(ionDist, "translator.js"), translatorScript, Utf8NoBom);
                if (Directory.Exists(assetsDir))
                {
                    File.WriteAllText(Path.Combine(assetsDir, "translator.js"), translatorScript, Utf8NoBom);
                }
                InjectTranslatorScript(Path.Combine(ionDist, "index.html"), BackupDirectory);
                InjectTranslatorScript(Path.Combine(ionDist, "frame-shell.html"), BackupDirectory);
            }

            // 6. Patch language whitelist in ion-dist/assets/v1/shared-*.js
            if (Directory.Exists(assetsDir))
            {
                string[] jsFiles = Directory.GetFiles(assetsDir, "shared-*.js");
                foreach (string jsFile in jsFiles)
                {
                    string content = File.ReadAllText(jsFile, Encoding.UTF8);
                    const string targetPattern = "[\"en-US\",\"de-DE\"";
                    const string replacement = "[\"zh-CN\",\"en-US\",\"de-DE\"";

                    if (content.Contains(targetPattern) && !content.Contains(replacement))
                    {
                        // Backup original JS file
                        string backupFile = Path.Combine(BackupDirectory, Path.GetFileName(jsFile) + ".orig");
                        if (!File.Exists(backupFile))
                        {
                            File.Copy(jsFile, backupFile, true);
                        }

                        string patched = content.Replace(targetPattern, replacement);
                        File.WriteAllText(jsFile, patched, Utf8NoBom);
                        break;
                    }
                }
            }

            // 7. Update user configuration files across all locations (including MSIX virtualized AppData)
            UpdateAllClaudeConfigFiles("zh-CN");

            // 8. Scan untranslated strings against en-US.json
            ScanUntranslatedKeys(resources, frontendObj as IDictionary);
        }

        internal static void PerformRestoreOfficial(ClaudeEnvironment env)
        {
            string resources = env.ResourcesPath;
            string ionDist = Path.Combine(resources, "ion-dist");
            string i18nDir = Path.Combine(ionDist, "i18n");
            string dynamicDir = Path.Combine(i18nDir, "dynamic");
            string statsigDir = Path.Combine(i18nDir, "statsig");
            string assetsDir = Path.Combine(ionDist, "assets", "v1");

            // 1. Delete installed zh-CN.json and translator files
            TryDeleteFile(Path.Combine(i18nDir, "zh-CN.json"));
            TryDeleteFile(Path.Combine(dynamicDir, "zh-CN.json"));
            TryDeleteFile(Path.Combine(statsigDir, "zh-CN.json"));
            TryDeleteFile(Path.Combine(resources, "zh-CN.json"));
            TryDeleteFile(Path.Combine(ionDist, "translator.js"));
            TryDeleteFile(Path.Combine(assetsDir, "translator.js"));

            // 2. Restore shared-*.js from backup
            if (Directory.Exists(assetsDir) && Directory.Exists(BackupDirectory))
            {
                string[] backupFiles = Directory.GetFiles(BackupDirectory, "*.orig");
                foreach (string bFile in backupFiles)
                {
                    string origName = Path.GetFileNameWithoutExtension(bFile);
                    if (origName.StartsWith("shared-", StringComparison.OrdinalIgnoreCase))
                    {
                        string targetPath = Path.Combine(assetsDir, origName);
                        if (File.Exists(targetPath))
                        {
                            File.Copy(bFile, targetPath, true);
                        }
                    }
                }
            }

            // 3. Restore en-US.json, index.html, frame-shell.html
            RestoreOriginalFile(Path.Combine(resources, "en-US.json"), BackupDirectory);
            RestoreOriginalFile(Path.Combine(ionDist, "index.html"), BackupDirectory);
            RestoreOriginalFile(Path.Combine(ionDist, "frame-shell.html"), BackupDirectory);

            // 4. Reset user configuration to en-US across all locations
            UpdateAllClaudeConfigFiles("en-US");
        }

        private async Task RestoreOfficialAsync(bool showFeedback)
        {
            if (busy) return;
            busy = true;
            applyButton.Enabled = false;

            try
            {
                ClaudeEnvironment env = DetectClaudeEnvironment();
                if (!env.IsInstalled) return;

                statusDot.BackColor = Color.FromArgb(232, 113, 10);
                statusLabel.Text = "正在还原...";
                detailLabel.Text = "正在恢复官方原版文件与配置，请稍候...";

                await Task.Run(() =>
                {
                    PerformRestoreOfficial(env);
                });

                EvaluateState();

                if (showFeedback)
                {
                    if (env.IsRunning)
                    {
                        DialogResult restartPrompt = MessageBox.Show(
                            "已成功恢复为官方英文原版！\n\n【免重启生效】：可在 Claude 窗口中按 Ctrl+R 刷新恢复英文界面。\n【全量生效（推荐）】：顶层菜单、右键菜单由系统主进程管理，重启后可 100% 恢复英文菜单。\n\n是否立即重启 Claude Desktop 生效？",
                            "恢复成功",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information);

                        if (restartPrompt == DialogResult.Yes)
                        {
                            RestartClaude();
                        }
                    }
                    else
                    {
                        MessageBox.Show("已成功恢复为官方英文原版。", "恢复成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("恢复失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                applyButton.Enabled = true;
                EvaluateState();
            }
        }

        private void RestartClaude()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("claude");
                foreach (Process p in processes)
                {
                    try { p.Kill(); } catch { }
                }

                Thread.Sleep(1200);

                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs", "Claude.lnk");
                if (File.Exists(lnk))
                {
                    Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                    return;
                }

                string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "claude-desktop.exe");
                if (File.Exists(alias))
                {
                    Process.Start(new ProcessStartInfo(alias) { UseShellExecute = true });
                    return;
                }

                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\Claude_pzs8sxrjxfjjc!Claude") { UseShellExecute = true });
            }
            catch { }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void UpdateUserConfigFile(string path, string locale)
        {
            try
            {
                if (!File.Exists(path))
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, "{\"locale\":\"" + locale + "\"}\n", Utf8NoBom);
                    return;
                }

                string content = File.ReadAllText(path, Encoding.UTF8);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> dict = serializer.Deserialize<Dictionary<string, object>>(content)
                    ?? new Dictionary<string, object>();

                dict["locale"] = locale;
                string newJson = serializer.Serialize(dict);
                File.WriteAllText(path, newJson, Utf8NoBom);
            }
            catch { }
        }

        private static void UpdateAllClaudeConfigFiles(string locale)
        {
            UpdateUserConfigFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "config.json"), locale);
            UpdateUserConfigFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-3p", "config.json"), locale);

            try
            {
                string packagesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (Directory.Exists(packagesDir))
                {
                    foreach (string claudePkg in Directory.GetDirectories(packagesDir, "Claude_*"))
                    {
                        string pkgConfig = Path.Combine(claudePkg, "LocalCache", "Roaming", "Claude", "config.json");
                        UpdateUserConfigFile(pkgConfig, locale);
                    }
                }
            }
            catch { }
        }

        private static string LoadTranslatorScript()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TranslatorJs"))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                }
            }
            catch { }

            try
            {
                string localAsset = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "translator.js");
                if (File.Exists(localAsset)) return File.ReadAllText(localAsset, Encoding.UTF8);
            }
            catch { }

            return null;
        }

        private static void InjectTranslatorScript(string htmlPath, string backupDir)
        {
            try
            {
                if (!File.Exists(htmlPath)) return;
                string content = File.ReadAllText(htmlPath, Encoding.UTF8);

                string fileName = Path.GetFileName(htmlPath);
                string backupFile = Path.Combine(backupDir, fileName + ".orig");
                if (!File.Exists(backupFile))
                {
                    File.Copy(htmlPath, backupFile, true);
                }

                if (content.IndexOf("./translator.js", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    content = content.Replace("./translator.js", "/translator.js");
                    File.WriteAllText(htmlPath, content, Utf8NoBom);
                    return;
                }

                if (content.IndexOf("translator.js", StringComparison.OrdinalIgnoreCase) >= 0) return;

                int headIdx = content.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (headIdx >= 0)
                {
                    string modified = content.Substring(0, headIdx) + "<script src=\"/translator.js\"></script>" + content.Substring(headIdx);
                    File.WriteAllText(htmlPath, modified, Utf8NoBom);
                }
            }
            catch { }
        }

        private static void RestoreOriginalFile(string targetPath, string backupDir)
        {
            try
            {
                if (string.IsNullOrEmpty(targetPath)) return;
                string fileName = Path.GetFileName(targetPath);
                string backupFile = Path.Combine(backupDir, fileName + ".orig");
                if (File.Exists(backupFile))
                {
                    File.Copy(backupFile, targetPath, true);
                }
            }
            catch { }
        }

        private static void ScanUntranslatedKeys(string resourcesPath, IDictionary activeZhDict)
        {
            try
            {
                string enJsonPath = Path.Combine(resourcesPath, "ion-dist", "i18n", "en-US.json");
                if (!File.Exists(enJsonPath) || activeZhDict == null) return;

                string enJson = File.ReadAllText(enJsonPath, Encoding.UTF8);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> enDict = serializer.Deserialize<Dictionary<string, object>>(enJson);

                if (enDict == null) return;

                Dictionary<string, string> missing = new Dictionary<string, string>();
                foreach (KeyValuePair<string, object> kvp in enDict)
                {
                    if (!activeZhDict.Contains(kvp.Key))
                    {
                        missing[kvp.Key] = kvp.Value != null ? kvp.Value.ToString() : "";
                    }
                }

                pendingCount = missing.Count;

                if (missing.Count > 0)
                {
                    string reportJson = serializer.Serialize(missing);
                    File.WriteAllText(UnknownReportPath, reportJson, Encoding.UTF8);
                }
                else
                {
                    TryDeleteFile(UnknownReportPath);
                }
            }
            catch { }
        }

        // ==========================================
        // Translation Pack & Updates
        // ==========================================

        private static IDictionary LoadTranslationPack()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;

            IDictionary embeddedPack = null;
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BundledTranslationPack"))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            embeddedPack = serializer.DeserializeObject(reader.ReadToEnd()) as IDictionary;
                        }
                    }
                }
            }
            catch { }

            IDictionary selectedPack = embeddedPack;
            if (File.Exists(PackPath))
            {
                try
                {
                    string localJson = File.ReadAllText(PackPath, Encoding.UTF8);
                    IDictionary localPack = serializer.DeserializeObject(localJson) as IDictionary;
                    if (localPack != null)
                    {
                        string localVer = localPack.Contains("version") && localPack["version"] != null ? localPack["version"].ToString() : "0.0.0";
                        string embVer = embeddedPack != null && embeddedPack.Contains("version") && embeddedPack["version"] != null ? embeddedPack["version"].ToString() : "0.0.0";
                        if (CompareVersion(localVer, embVer) >= 0)
                        {
                            selectedPack = localPack;
                        }
                    }
                }
                catch { }
            }

            if (selectedPack != null)
            {
                int count = 0;
                IDictionary f = selectedPack["frontend"] as IDictionary;
                if (f != null) count += f.Count;
                IDictionary d = selectedPack["desktop"] as IDictionary;
                if (d != null) count += d.Count;
                IDictionary dyn = selectedPack["dynamic"] as IDictionary;
                if (dyn != null) count += dyn.Count;
                IDictionary stat = selectedPack["statsig"] as IDictionary;
                if (stat != null) count += stat.Count;
                if (count > 0) translatedCount = count;
            }

            return selectedPack;
        }

        private async Task CheckPackUpdateAsync(bool forceFeedback)
        {
            if (!packUpdateCheckBox.Checked && !forceFeedback) return;

            try
            {
                string manifestJson = await updateHttp.GetStringAsync(PackManifestUrl);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> manifest = serializer.Deserialize<Dictionary<string, object>>(manifestJson);

                if (manifest != null && manifest.ContainsKey("version"))
                {
                    string remoteVersion = manifest["version"].ToString();
                    string currentVersion = GetCurrentPackVersion();

                    if (CompareVersion(remoteVersion, currentVersion) > 0 && manifest.ContainsKey("downloadUrl"))
                    {
                        string downloadUrl = manifest["downloadUrl"].ToString();
                        byte[] packBytes = await updateHttp.GetByteArrayAsync(downloadUrl);
                        File.WriteAllBytes(PackPath, packBytes);

                        if (isCurrentlyLocalized)
                        {
                            ClaudeEnvironment env = DetectClaudeEnvironment();
                            if (env.IsInstalled)
                            {
                                PerformApplyLocalization(env);
                            }
                        }

                        EvaluateState();
                        trayIcon.ShowBalloonTip(3000, AppName, "已自动更新至最新汉化词典包（v" + remoteVersion + "）。", ToolTipIcon.Info);
                    }
                }
            }
            catch { }
        }

        private string GetCurrentPackVersion()
        {
            try
            {
                IDictionary pack = LoadTranslationPack();
                if (pack != null && pack.Contains("version") && pack["version"] != null)
                {
                    return pack["version"].ToString();
                }
            }
            catch { }
            return "1.0.0";
        }

        private static int CompareVersion(string v1, string v2)
        {
            try
            {
                Version ver1 = new Version(v1);
                Version ver2 = new Version(v2);
                return ver1.CompareTo(ver2);
            }
            catch
            {
                return string.Compare(v1, v2, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ==========================================
        // Auto-Start & Settings
        // ==========================================

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\" --startup");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    Dictionary<string, object> dict = serializer.Deserialize<Dictionary<string, object>>(json);
                    if (dict != null)
                    {
                        if (dict.ContainsKey("AutoMonitor")) monitorCheckBox.Checked = Convert.ToBoolean(dict["AutoMonitor"]);
                        if (dict.ContainsKey("AutoAdapt")) adaptCheckBox.Checked = Convert.ToBoolean(dict["AutoAdapt"]);
                        if (dict.ContainsKey("AutoPack")) packUpdateCheckBox.Checked = Convert.ToBoolean(dict["AutoPack"]);
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Dictionary<string, object> dict = new Dictionary<string, object>
                {
                    { "AutoMonitor", monitorCheckBox.Checked },
                    { "AutoAdapt", adaptCheckBox.Checked },
                    { "AutoPack", packUpdateCheckBox.Checked }
                };
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(SettingsFilePath, serializer.Serialize(dict), Encoding.UTF8);
            }
            catch { }
        }

        // ==========================================
        // Visual Helpers & Custom Controls
        // ==========================================

        private static Label CreateMetricLabel(Control parent, string text, int left)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            label.ForeColor = Color.FromArgb(100, 104, 110);
            label.Location = new Point(left, 94);
            label.AutoSize = true;
            parent.Controls.Add(label);
            return label;
        }

        private static CheckBox CreateSettingCheckBox(string text, int left, int top, bool isChecked)
        {
            CheckBox cb = new CheckBox();
            cb.Text = text;
            cb.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            cb.ForeColor = Color.FromArgb(31, 31, 31);
            cb.Location = new Point(left, top);
            cb.Size = new Size(270, 26);
            cb.Checked = isChecked;
            return cb;
        }

        private static Label CreateSettingDescription(string text, int left, int top)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei UI", 9F);
            label.ForeColor = Color.FromArgb(115, 119, 125);
            label.Location = new Point(left, top);
            label.Size = new Size(360, 24);
            return label;
        }

        private static void ApplyRoundedRegion(Control control, int radius)
        {
            try
            {
                if (control == null || control.Width <= 0 || control.Height <= 0) return;
                float scale = 1.0F;
                try
                {
                    using (Graphics g = control.CreateGraphics()) scale = g.DpiX / 96F;
                }
                catch { }

                int r = (int)Math.Round(radius * scale);
                using (GraphicsPath path = RoundedRectangle(new Rectangle(0, 0, control.Width, control.Height), r))
                {
                    Region old = control.Region;
                    control.Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch { }
        }

        internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0) return path;

            int maxR = Math.Min(bounds.Width, bounds.Height) / 2;
            if (radius > maxR) radius = maxR;
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int diameter = radius * 2;
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Icon LoadAssistantIcon()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AssistantIcon"))
                {
                    if (stream != null) return new Icon(stream);
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        private static Bitmap LoadAssistantBitmap()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AssistantIconPng"))
                {
                    if (stream != null) return new Bitmap(stream);
                }
            }
            catch { }
            return SystemIcons.Application.ToBitmap();
        }
    }

    internal sealed class GradientPanel : Panel
    {
        public Color Color1 { get; set; }
        public Color Color2 { get; set; }

        public GradientPanel()
        {
            DoubleBuffered = true;
            Color1 = Color.FromArgb(254, 250, 248);
            Color2 = Color.FromArgb(248, 250, 253);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (LinearGradientBrush brush = new LinearGradientBrush(ClientRectangle, Color1, Color2, 90F))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }
    }

    internal sealed class CardPanel : Panel
    {
        public Color BorderColor { get; set; }
        public int CornerRadius { get; set; }

        public CardPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            BorderColor = Color.FromArgb(226, 230, 236);
            CornerRadius = 14;
            Padding = new Padding(1);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width <= 0 || Height <= 0) return;
            using (GraphicsPath path = MainForm.RoundedRectangle(new Rectangle(0, 0, Width, Height), CornerRadius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MainForm.RoundedRectangle(rect, CornerRadius))
            using (Pen pen = new Pen(BorderColor, 1F))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }
    }
}
