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
        private StatusDot statusDot;
        private Label versionLabel;
        private Label entryLabel;
        private Label unknownLabel;
        private Label scanLabel;

        private ModernButton applyButton;
        private ModernButton restartButton;
        private ModernButton openDataButton;
        private ModernButton exitButton;
        private CheckBox monitorCheckBox;
        private CheckBox adaptCheckBox;
        private CheckBox packUpdateCheckBox;
        private CheckBox startupCheckBox;

        private bool busy;
        private bool isCurrentlyLocalized;
        private bool lastRunningState;
        private string detectedClaudeVersion = "未检测";
        private static int translatedCount = 32606;
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
            ClientSize = new Size(740, 615);
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = LoadAssistantIcon();
            BackColor = Color.FromArgb(248, 249, 251);

            // 1. Header Panel
            HeaderPanel header = new HeaderPanel();
            header.Location = new Point(0, 0);
            header.Size = new Size(740, 84);
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(header);

            PictureBox logo = new PictureBox();
            logo.Image = LoadAssistantBitmap();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Location = new Point(28, 20);
            logo.Size = new Size(44, 44);
            header.Controls.Add(logo);

            Label title = new Label();
            title.Text = "Claude Code 桌面版中文助手";
            title.Font = new Font("Microsoft YaHei UI", 14.5F, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(31, 35, 40);
            title.BackColor = Color.Transparent;
            title.Location = new Point(86, 17);
            title.AutoSize = true;
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "轻量原生 · 离线优先 · 一键汉化 · 无损还原";
            subtitle.Font = new Font("Microsoft YaHei UI", 9F);
            subtitle.ForeColor = Color.FromArgb(101, 109, 118);
            subtitle.BackColor = Color.Transparent;
            subtitle.Location = new Point(88, 47);
            subtitle.AutoSize = true;
            header.Controls.Add(subtitle);

            PillBadge versionBadge = new PillBadge();
            versionBadge.Text = "v1.2.0";
            versionBadge.Location = new Point(740 - 28 - 72, 30);
            versionBadge.Size = new Size(72, 24);
            versionBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(versionBadge);

            // 2. Status Card Panel
            CardPanel statusPanel = new CardPanel();
            statusPanel.Location = new Point(28, 100);
            statusPanel.Size = new Size(684, 126);
            statusPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            statusPanel.Paint += delegate(object sender, PaintEventArgs pe)
            {
                using (Pen dividerPen = new Pen(Color.FromArgb(240, 242, 245), 1F))
                {
                    pe.Graphics.DrawLine(dividerPen, 24, 76, statusPanel.Width - 24, 76);
                }
            };
            Controls.Add(statusPanel);

            statusDot = new StatusDot();
            statusDot.Location = new Point(24, 22);
            statusPanel.Controls.Add(statusDot);

            statusLabel = new Label();
            statusLabel.Text = "准备就绪";
            statusLabel.Font = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold);
            statusLabel.ForeColor = Color.FromArgb(31, 35, 40);
            statusLabel.Location = new Point(42, 16);
            statusLabel.AutoSize = true;
            statusPanel.Controls.Add(statusLabel);

            detailLabel = new Label();
            detailLabel.Text = "检测到系统已安装 Claude Desktop。点击下方“立即检测并应用”一键部署汉化。";
            detailLabel.Font = new Font("Microsoft YaHei UI", 9F);
            detailLabel.ForeColor = Color.FromArgb(101, 109, 118);
            detailLabel.Location = new Point(42, 44);
            detailLabel.Size = new Size(618, 22);
            statusPanel.Controls.Add(detailLabel);

            versionLabel = CreateMetricLabel(statusPanel, "Claude：检测中...", 24);
            entryLabel = CreateMetricLabel(statusPanel, "已汉化：32,606 条", 190);
            unknownLabel = CreateMetricLabel(statusPanel, "待适配：0 条", 360);
            scanLabel = CreateMetricLabel(statusPanel, "状态：就绪", 520);

            versionLabel.Top = entryLabel.Top = unknownLabel.Top = scanLabel.Top = 90;

            // 3. Automation Settings Panel
            CardPanel settingsPanel = new CardPanel();
            settingsPanel.Location = new Point(28, 242);
            settingsPanel.Size = new Size(684, 280);
            settingsPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(settingsPanel);

            Label settingsTitle = new Label();
            settingsTitle.Text = "自动化与首选项";
            settingsTitle.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            settingsTitle.ForeColor = Color.FromArgb(31, 35, 40);
            settingsTitle.Location = new Point(24, 16);
            settingsTitle.AutoSize = true;
            settingsPanel.Controls.Add(settingsTitle);

            monitorCheckBox = CreateSettingRow(settingsPanel, "后台持续守护", "在后台监控 Claude 运行状态，确保汉化持久生效", 46, true);
            adaptCheckBox = CreateSettingRow(settingsPanel, "版本更新自动适配", "Claude 升级后自动增量合并并部署最新汉化词条", 94, true);
            packUpdateCheckBox = CreateSettingRow(settingsPanel, "汉化词库云端同步", "优先读取云端更新的汉化词典包，无需重复下载助手", 142, true);
            startupCheckBox = CreateSettingRow(settingsPanel, "随 Windows 开机启动", "开机自启动并默认最小化至系统托盘静默运行", 190, IsAutoStartEnabled());

            startupCheckBox.CheckedChanged += delegate
            {
                SetAutoStart(startupCheckBox.Checked);
                SaveSettings();
            };
            monitorCheckBox.CheckedChanged += delegate { SaveSettings(); };
            adaptCheckBox.CheckedChanged += delegate { SaveSettings(); };
            packUpdateCheckBox.CheckedChanged += delegate { SaveSettings(); };

            Panel securityBar = new Panel();
            securityBar.Location = new Point(24, 240);
            securityBar.Size = new Size(settingsPanel.Width - 48, 26);
            securityBar.BackColor = Color.FromArgb(246, 248, 250);
            securityBar.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            securityBar.Paint += delegate(object s, PaintEventArgs pe)
            {
                using (Pen borderPen = new Pen(Color.FromArgb(235, 238, 242), 1F))
                {
                    pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath p = RoundedRectangle(new Rectangle(0, 0, securityBar.Width - 1, securityBar.Height - 1), 6))
                    {
                        pe.Graphics.DrawPath(borderPen, p);
                    }
                }
            };
            Label securityText = new Label();
            securityText.Text = "安全承诺：仅适配客户端界面显示文本，绝不收集、上传或修改您的对话、代码及工程文件。";
            securityText.Font = new Font("Microsoft YaHei UI", 8.5F);
            securityText.ForeColor = Color.FromArgb(101, 109, 118);
            securityText.Location = new Point(12, 5);
            securityText.AutoSize = true;
            securityBar.Controls.Add(securityText);
            settingsPanel.Controls.Add(securityBar);

            // 4. Bottom Action Bar
            exitButton = new ModernButton();
            exitButton.Text = "退出助手";
            exitButton.BackColor = Color.White;
            exitButton.HoverBackColor = Color.FromArgb(243, 244, 246);
            exitButton.PressedBackColor = Color.FromArgb(229, 231, 235);
            exitButton.BorderColor = Color.FromArgb(208, 215, 222);
            exitButton.ForeColor = Color.FromArgb(101, 109, 118);
            exitButton.Font = new Font("Microsoft YaHei UI", 9.5F);
            exitButton.Click += delegate
            {
                trayIcon.Visible = false;
                Application.Exit();
            };
            Controls.Add(exitButton);

            openDataButton = new ModernButton();
            openDataButton.Text = "配置目录";
            openDataButton.BackColor = Color.White;
            openDataButton.HoverBackColor = Color.FromArgb(243, 244, 246);
            openDataButton.PressedBackColor = Color.FromArgb(229, 231, 235);
            openDataButton.BorderColor = Color.FromArgb(208, 215, 222);
            openDataButton.ForeColor = Color.FromArgb(36, 41, 47);
            openDataButton.Font = new Font("Microsoft YaHei UI", 9.5F);
            openDataButton.Click += delegate
            {
                try { Process.Start("explorer.exe", DataDirectory); } catch { }
            };
            Controls.Add(openDataButton);

            restartButton = new ModernButton();
            restartButton.Text = "重启 Claude";
            restartButton.BackColor = Color.FromArgb(238, 244, 254);
            restartButton.HoverBackColor = Color.FromArgb(224, 235, 252);
            restartButton.PressedBackColor = Color.FromArgb(210, 225, 250);
            restartButton.BorderColor = Color.FromArgb(198, 218, 248);
            restartButton.ForeColor = Color.FromArgb(26, 108, 231);
            restartButton.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            restartButton.Click += delegate { RestartClaude(); };
            Controls.Add(restartButton);

            applyButton = new ModernButton();
            applyButton.Text = "立即检测并应用";
            applyButton.BackColor = Color.FromArgb(217, 119, 87);
            applyButton.HoverBackColor = Color.FromArgb(196, 98, 68);
            applyButton.PressedBackColor = Color.FromArgb(175, 80, 52);
            applyButton.ForeColor = Color.White;
            applyButton.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            applyButton.Click += async delegate { await OnApplyButtonClickedAsync(); };
            Controls.Add(applyButton);

            Action updateButtonLayout = delegate
            {
                int btnHeight = 42;
                int btnY = ClientSize.Height - btnHeight - 20;

                // Left Utilities
                exitButton.Size = new Size(88, btnHeight);
                exitButton.Location = new Point(28, btnY);

                openDataButton.Size = new Size(100, btnHeight);
                openDataButton.Location = new Point(exitButton.Right + 10, btnY);

                // Right Actions
                applyButton.Size = new Size(168, btnHeight);
                applyButton.Location = new Point(ClientSize.Width - 28 - applyButton.Width, btnY);

                restartButton.Size = new Size(128, btnHeight);
                restartButton.Location = new Point(applyButton.Left - 12 - restartButton.Width, btnY);
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
            trayMenu.MenuItems.Add(new MenuItem("重启 Claude", delegate { RestartClaude(); }));
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
                if (restartButton != null) restartButton.Enabled = false;
                versionLabel.Text = "Claude：未安装";
                return;
            }

            detectedClaudeVersion = env.Version ?? "已安装";
            versionLabel.Text = "Claude：v" + detectedClaudeVersion;

            isCurrentlyLocalized = CheckIfCurrentlyLocalized(env);
            lastRunningState = env.IsRunning;

            if (isCurrentlyLocalized)
            {
                statusDot.BackColor = Color.FromArgb(30, 142, 62); // Green
                statusLabel.Text = "汉化已部署";
                if (env.IsRunning)
                {
                    detailLabel.Text = "汉化包已部署完成。若当前窗口未刷新，请点击下方“重启 Claude”生效。";
                }
                else
                {
                    detailLabel.Text = "Claude 桌面版已成功配置为中文环境，启动即可直接使用全中文界面。";
                }
                applyButton.Enabled = true;
                applyButton.Text = "恢复官方原版";
                applyButton.BackColor = Color.FromArgb(55, 65, 81);
                applyButton.HoverBackColor = Color.FromArgb(40, 48, 60);
                applyButton.PressedBackColor = Color.FromArgb(25, 30, 40);
                applyButton.Invalidate();
                scanLabel.Text = "词库状态：已部署";
            }
            else
            {
                statusDot.BackColor = Color.FromArgb(26, 115, 232); // Blue
                statusLabel.Text = "准备就绪";
                detailLabel.Text = "检测到系统已安装 Claude Desktop。点击下方“立即检测并应用”一键部署汉化。";
                applyButton.Enabled = true;
                applyButton.Text = "立即检测并应用";
                applyButton.BackColor = Color.FromArgb(217, 119, 87);
                applyButton.HoverBackColor = Color.FromArgb(196, 98, 68);
                applyButton.PressedBackColor = Color.FromArgb(175, 80, 52);
                applyButton.Invalidate();
                scanLabel.Text = "词库状态：就绪";
            }

            if (restartButton != null)
            {
                restartButton.Enabled = env.IsInstalled;
                restartButton.Text = env.IsRunning ? "重启 Claude" : "启动 Claude";
                restartButton.Invalidate();
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
            if (localized != isCurrentlyLocalized || env.IsRunning != lastRunningState)
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
                            "中文汉化已部署完成！\n\n检测到 Claude 正在运行。需要重启 Claude 才能加载生效。\n\n是否立即帮您重启 Claude？",
                            "汉化完成",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);

                        if (restartPrompt == DialogResult.Yes)
                        {
                            RestartClaude();
                        }
                    }
                    else
                    {
                        DialogResult launchPrompt = MessageBox.Show(
                            "中文汉化已成功部署！\n\n是否立即启动 Claude Desktop 查看全中文界面？",
                            "汉化完成",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information);

                        if (launchPrompt == DialogResult.Yes)
                        {
                            RestartClaude();
                        }
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
                            "已成功恢复官方原版！\n\n检测到 Claude 正在运行。需要重启 Claude 才能完全恢复为官方界面。\n\n是否立即帮您重启 Claude？",
                            "恢复完成",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);

                        if (restartPrompt == DialogResult.Yes)
                        {
                            RestartClaude();
                        }
                    }
                    else
                    {
                        MessageBox.Show("已成功恢复为官方英文原版。", "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

                if (processes.Length > 0)
                {
                    Thread.Sleep(1000);
                }

                // 1. Official execution alias (most reliable for MSIX WindowsApps on Windows 10/11)
                string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "claude-desktop.exe");
                if (File.Exists(alias))
                {
                    Process.Start(new ProcessStartInfo(alias) { UseShellExecute = true });
                    return;
                }

                // 2. Local unpackaged executable if installed in LocalAppData\Programs
                string localExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Claude", "Claude.exe");
                if (File.Exists(localExe))
                {
                    Process.Start(new ProcessStartInfo(localExe) { UseShellExecute = true });
                    return;
                }

                // 3. Start Menu Shortcut fallback
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs", "Claude.lnk");
                if (File.Exists(lnk))
                {
                    Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                    return;
                }

                // 4. Explorer shell AUMID fallback
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
            label.ForeColor = Color.FromArgb(87, 96, 106);
            label.Location = new Point(left, 90);
            label.AutoSize = true;
            label.BackColor = Color.Transparent;
            parent.Controls.Add(label);
            return label;
        }

        private static CheckBox CreateSettingRow(Control parent, string title, string description, int top, bool isChecked)
        {
            CheckBox cb = new CheckBox();
            cb.Text = title;
            cb.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            cb.ForeColor = Color.FromArgb(31, 35, 40);
            cb.Location = new Point(24, top);
            cb.AutoSize = true;
            cb.BackColor = Color.Transparent;
            cb.Checked = isChecked;
            parent.Controls.Add(cb);

            Label descLabel = new Label();
            descLabel.Text = description;
            descLabel.Font = new Font("Microsoft YaHei UI", 9F);
            descLabel.ForeColor = Color.FromArgb(101, 109, 118);
            descLabel.Location = new Point(44, top + 22);
            descLabel.AutoSize = true;
            descLabel.BackColor = Color.Transparent;
            descLabel.Cursor = Cursors.Hand;
            descLabel.Click += delegate { cb.Checked = !cb.Checked; };
            parent.Controls.Add(descLabel);

            return cb;
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

    internal sealed class ModernButton : Button
    {
        public int CornerRadius { get; set; }
        public Color BorderColor { get; set; }
        public Color HoverBackColor { get; set; }
        public Color PressedBackColor { get; set; }

        private bool isHovered;
        private bool isPressed;

        public ModernButton()
        {
            DoubleBuffered = true;
            CornerRadius = 8;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BorderColor = Color.Empty;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            isHovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            isHovered = false;
            isPressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                isPressed = true;
                Invalidate();
            }
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            isPressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color bg = BackColor;
            if (!Enabled)
            {
                bg = Color.FromArgb(235, 238, 242);
            }
            else if (isPressed && PressedBackColor != Color.Empty)
            {
                bg = PressedBackColor;
            }
            else if (isHovered && HoverBackColor != Color.Empty)
            {
                bg = HoverBackColor;
            }

            if (Parent != null)
            {
                using (SolidBrush parentBrush = new SolidBrush(Parent.BackColor))
                {
                    g.FillRectangle(parentBrush, ClientRectangle);
                }
            }

            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MainForm.RoundedRectangle(rect, CornerRadius))
            {
                using (SolidBrush brush = new SolidBrush(bg))
                {
                    g.FillPath(brush, path);
                }

                if (BorderColor != Color.Empty && Enabled)
                {
                    using (Pen pen = new Pen(BorderColor, 1F))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            Color textColor = Enabled ? ForeColor : Color.FromArgb(160, 164, 170);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    internal sealed class StatusDot : Control
    {
        public StatusDot()
        {
            DoubleBuffered = true;
            Size = new Size(10, 10);
            BackColor = Color.FromArgb(26, 115, 232);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Parent != null)
            {
                using (SolidBrush parentBrush = new SolidBrush(Parent.BackColor))
                {
                    g.FillRectangle(parentBrush, ClientRectangle);
                }
            }
            using (SolidBrush brush = new SolidBrush(BackColor))
            {
                g.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    internal sealed class PillBadge : Control
    {
        public Color BadgeColor { get; set; }
        public Color TextColor { get; set; }

        public PillBadge()
        {
            DoubleBuffered = true;
            BadgeColor = Color.FromArgb(254, 242, 238);
            TextColor = Color.FromArgb(217, 119, 87);
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (Parent != null)
            {
                using (SolidBrush parentBrush = new SolidBrush(Parent.BackColor))
                {
                    g.FillRectangle(parentBrush, ClientRectangle);
                }
            }
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = MainForm.RoundedRectangle(rect, Height / 2))
            {
                using (SolidBrush brush = new SolidBrush(BadgeColor))
                {
                    g.FillPath(brush, path);
                }
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    internal sealed class HeaderPanel : Panel
    {
        public HeaderPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Color.FromArgb(235, 238, 242), 1F))
            {
                e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
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
            BorderColor = Color.FromArgb(228, 232, 238);
            CornerRadius = 12;
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
