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
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

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

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

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

        private float uiScale = 1F;
        private StatusPillButton statusPill;
        private Label infoLabel;
        private OnboardingInfoButton restartButton;
        private OnboardingInfoButton openDataButton;
        private SoftCheckBox adaptCheckBox;
        private SoftCheckBox startupCheckBox;
        private CheckBox monitorCheckBox;
        private CheckBox packUpdateCheckBox;
        private CardPanel onboardingCard;
        private ToolTip toolTip;

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
            updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd("CCZhAssistant/1.2.0");

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

            using (Graphics dpiGraphics = CreateGraphics())
            {
                uiScale = Math.Max(1F, dpiGraphics.DpiX / 96F);
            }

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = true;
            ClientSize = new Size(U(500), U(550));
            MinimumSize = MaximumSize = SizeFromClientSize(ClientSize);
            BackColor = Color.FromArgb(234, 236, 240);
            DoubleBuffered = true;
            Icon = LoadAssistantIcon();

            ApplyRoundedRegion(this, U(8));
            Paint += DrawOnboardingBackground;

            // Top-right window buttons
            WindowGlyphButton minimizeButton = new WindowGlyphButton("—");
            minimizeButton.SetBounds(U(409), 0, U(45), U(32));
            minimizeButton.TabStop = false;
            minimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(minimizeButton);

            WindowGlyphButton closeButton = new WindowGlyphButton("✕");
            closeButton.SetBounds(U(454), 0, U(46), U(32));
            closeButton.IsCloseButton = true;
            closeButton.TabStop = false;
            closeButton.Click += delegate { Close(); };
            Controls.Add(closeButton);

            // Centered Brand Logo
            PictureBox brand = new PictureBox();
            brand.Image = LoadAssistantBitmap();
            brand.SizeMode = PictureBoxSizeMode.Zoom;
            brand.BackColor = Color.Transparent;
            brand.SetBounds(U(218), U(90), U(64), U(64));
            Controls.Add(brand);

            // Welcome Title
            Label assistantTitle = FixedPixelLabel("Welcome to Claude", 24F, FontStyle.Regular, Color.FromArgb(76, 79, 105));
            assistantTitle.Font = new Font("Segoe UI Variable Display Semib", 24F * uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            assistantTitle.SetBounds(U(78), U(174), U(344), U(36));
            assistantTitle.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(assistantTitle);

            // Centered Onboarding Card
            onboardingCard = new CardPanel();
            onboardingCard.SetBounds(U(78), U(238), U(344), U(175));
            onboardingCard.BackColor = Color.FromArgb(236, 239, 242);
            onboardingCard.BorderColor = Color.FromArgb(215, 217, 222);
            onboardingCard.CornerRadius = U(12);
            Controls.Add(onboardingCard);

            // Card Header - Version text
            Label assistantVersion = FixedPixelLabel("Claude Code 汉化助手 v1.2.0", 13.5F, FontStyle.Bold, Color.FromArgb(76, 79, 105));
            assistantVersion.SetBounds(U(24), U(18), U(296), U(22));
            assistantVersion.TextAlign = ContentAlignment.MiddleCenter;
            onboardingCard.Controls.Add(assistantVersion);

            // Status Pill Button
            statusPill = new StatusPillButton();
            statusPill.Text = "立即应用汉化";
            statusPill.Font = new Font("Microsoft YaHei UI", 14F * uiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            statusPill.SetBounds(U(28), U(56), U(288), U(44));
            statusPill.Click += async delegate { await ToggleLocalizationAsync(); };
            onboardingCard.Controls.Add(statusPill);

            // Settings checkboxes inside card
            adaptCheckBox = new SoftCheckBox();
            adaptCheckBox.Text = "自动适配";
            adaptCheckBox.Font = new Font("Microsoft YaHei UI", 12F * uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            adaptCheckBox.Checked = true;
            adaptCheckBox.SetBounds(U(76), U(124), U(90), U(24));
            adaptCheckBox.CheckedChanged += delegate { SaveSettings(); };
            onboardingCard.Controls.Add(adaptCheckBox);

            startupCheckBox = new SoftCheckBox();
            startupCheckBox.Text = "开机启动";
            startupCheckBox.Font = new Font("Microsoft YaHei UI", 12F * uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            startupCheckBox.Checked = IsAutoStartEnabled();
            startupCheckBox.SetBounds(U(180), U(124), U(90), U(24));
            startupCheckBox.CheckedChanged += delegate
            {
                SetAutoStart(startupCheckBox.Checked);
                SaveSettings();
            };
            onboardingCard.Controls.Add(startupCheckBox);

            // Headless / background setting holders
            monitorCheckBox = new CheckBox { Checked = true, Visible = false };
            packUpdateCheckBox = new CheckBox { Checked = true, Visible = false };

            // Status Info text below Card
            infoLabel = FixedPixelLabel(BuildInfoText(), 12F, FontStyle.Regular, Color.FromArgb(103, 107, 129));
            infoLabel.SetBounds(U(50), U(432), U(400), U(24));
            infoLabel.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(infoLabel);

            // Action Buttons below infoLabel
            restartButton = new OnboardingInfoButton();
            restartButton.Text = "重启 Claude";
            restartButton.Font = new Font("Microsoft YaHei UI", 12F * uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            restartButton.SetBounds(U(125), U(472), U(118), U(34));
            restartButton.Click += delegate { RestartClaude(); };
            Controls.Add(restartButton);

            openDataButton = new OnboardingInfoButton();
            openDataButton.Text = "配置目录";
            openDataButton.Font = new Font("Microsoft YaHei UI", 12F * uiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            openDataButton.SetBounds(U(257), U(472), U(118), U(34));
            openDataButton.Click += delegate
            {
                try { Process.Start("explorer.exe", DataDirectory); } catch { }
            };
            Controls.Add(openDataButton);

            // ToolTips for smooth UX
            toolTip = new ToolTip();
            toolTip.InitialDelay = 350;
            toolTip.ReshowDelay = 150;
            toolTip.SetToolTip(restartButton, "关闭并重新启动 Claude Desktop 客户端以刷新界面");
            toolTip.SetToolTip(openDataButton, "打开汉化包与配置文件所在的数据目录");

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

        private int U(int value)
        {
            return Math.Max(1, (int)Math.Round(value * uiScale));
        }

        private Label FixedPixelLabel(string text, float pixelSize, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei UI", pixelSize * uiScale, style, GraphicsUnit.Pixel);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.AutoEllipsis = true;
            return label;
        }

        private string BuildInfoText()
        {
            string versionStr = detectedClaudeVersion != null && detectedClaudeVersion != "未检测" ? detectedClaudeVersion : "已就绪";
            if (versionStr.StartsWith("v", StringComparison.OrdinalIgnoreCase)) versionStr = versionStr.Substring(1);
            return "Claude v" + versionStr + "  ·  已汉化 " + translatedCount.ToString("N0") + " 条  ·  " + (lastRunningState ? "运行中" : "未启动");
        }

        private void DrawOnboardingBackground(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            GraphicsState state = e.Graphics.Save();
            e.Graphics.ScaleTransform(uiScale, uiScale);

            // Ambient pastel glow behind Claude icon (Terracotta, Gold, Coral, Lavender)
            DrawSoftGlow(e.Graphics, new Rectangle(142, 57, 148, 158), Color.FromArgb(66, 217, 119, 87));
            DrawSoftGlow(e.Graphics, new Rectangle(198, 43, 148, 148), Color.FromArgb(54, 255, 197, 120));
            DrawSoftGlow(e.Graphics, new Rectangle(230, 61, 134, 144), Color.FromArgb(48, 234, 153, 115));
            DrawSoftGlow(e.Graphics, new Rectangle(183, 101, 151, 150), Color.FromArgb(48, 140, 150, 220));

            // Top-right window controls bar background
            using (SolidBrush titleButtons = new SolidBrush(Color.FromArgb(228, 230, 234)))
                e.Graphics.FillRectangle(titleButtons, 409, 0, 91, 32);

            // Card drop shadow
            using (GraphicsPath shadow = RoundedRectangle(new Rectangle(78, 240, 344, 175), 12))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(22, 0, 0, 0)))
                e.Graphics.FillPath(shadowBrush, shadow);

            e.Graphics.Restore(state);

            // Window border
            using (GraphicsPath border = RoundedRectangle(
                new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), U(8)))
            using (Pen pen = new Pen(Color.FromArgb(205, 208, 215)))
                e.Graphics.DrawPath(pen, border);
        }

        private static void DrawSoftGlow(Graphics graphics, Rectangle bounds, Color centerColor)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(bounds);
                using (PathGradientBrush brush = new PathGradientBrush(path))
                {
                    brush.CenterColor = centerColor;
                    brush.SurroundColors = new[] { Color.FromArgb(0, centerColor.R, centerColor.G, centerColor.B) };
                    brush.FocusScales = new PointF(0.08F, 0.08F);
                    graphics.FillEllipse(brush, bounds);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < U(36))
            {
                ReleaseCapture();
                SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
            }
            base.OnMouseDown(e);
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
                isCurrentlyLocalized = false;
                lastRunningState = false;
                if (statusPill != null)
                {
                    statusPill.IsLocalized = false;
                    statusPill.Text = "未检测到 Claude";
                    statusPill.Enabled = false;
                }
                if (restartButton != null) restartButton.Enabled = false;
                if (infoLabel != null) infoLabel.Text = "未检测到 Claude Desktop 安装";
                return;
            }

            detectedClaudeVersion = env.Version ?? "已安装";
            isCurrentlyLocalized = CheckIfCurrentlyLocalized(env);
            lastRunningState = env.IsRunning;

            if (statusPill != null)
            {
                statusPill.Enabled = true;
                statusPill.IsLocalized = isCurrentlyLocalized;
                statusPill.Text = isCurrentlyLocalized ? "汉化已生效" : "立即应用汉化";
                if (toolTip != null)
                {
                    toolTip.SetToolTip(statusPill, isCurrentlyLocalized
                        ? "当前汉化已生效。点击可恢复为官方原版英文界面"
                        : "点击立即部署全中文语言包");
                }
            }

            if (restartButton != null)
            {
                restartButton.Enabled = true;
                restartButton.Text = env.IsRunning ? "重启 Claude" : "启动 Claude";
            }

            if (infoLabel != null)
            {
                infoLabel.Text = BuildInfoText();
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

        private async Task ToggleLocalizationAsync()
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
            if (statusPill != null)
            {
                statusPill.Enabled = false;
                statusPill.Text = "正在应用汉化...";
            }
            if (infoLabel != null)
            {
                infoLabel.Text = "正在写入中文语言包，请稍候...";
            }

            try
            {
                ClaudeEnvironment env = DetectClaudeEnvironment();
                if (!env.IsInstalled)
                {
                    if (showFeedback) MessageBox.Show("未找到 Claude Desktop 安装路径。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

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
            if (statusPill != null)
            {
                statusPill.Enabled = false;
                statusPill.Text = "正在恢复原版...";
            }
            if (infoLabel != null)
            {
                infoLabel.Text = "正在恢复官方原版文件与配置，请稍候...";
            }

            try
            {
                ClaudeEnvironment env = DetectClaudeEnvironment();
                if (!env.IsInstalled) return;

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

    internal sealed class WindowGlyphButton : Button
    {
        private bool hovering;
        public bool IsCloseButton { get; set; }

        public WindowGlyphButton(string glyph)
        {
            Text = glyph;
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel);
            ForeColor = Color.FromArgb(76, 79, 105);
            BackColor = Color.Transparent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;

            if (hovering)
            {
                Color hover = IsCloseButton ? Color.FromArgb(196, 43, 28) : Color.FromArgb(215, 218, 224);
                Rectangle hoverBounds = new Rectangle(0, 0, Width, Height);
                using (GraphicsPath hoverPath = MainForm.RoundedRectangle(hoverBounds, (int)Math.Round(4 * scale)))
                using (SolidBrush brush = new SolidBrush(hover))
                    e.Graphics.FillPath(brush, hoverPath);
            }

            int cx = Width / 2;
            int cy = Height / 2;
            Color iconColor = (hovering && IsCloseButton) ? Color.White : ForeColor;

            using (Pen pen = new Pen(iconColor, Math.Max(1.2F, 1.4F * scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                if (IsCloseButton)
                {
                    int sz = (int)Math.Round(4.5F * scale);
                    e.Graphics.DrawLine(pen, cx - sz, cy - sz, cx + sz, cy + sz);
                    e.Graphics.DrawLine(pen, cx + sz, cy - sz, cx - sz, cy + sz);
                }
                else
                {
                    int sz = (int)Math.Round(5F * scale);
                    e.Graphics.DrawLine(pen, cx - sz, cy, cx + sz, cy);
                }
            }
        }
    }

    internal sealed class StatusPillButton : Button
    {
        private bool hovering;
        private bool isLocalized;

        public bool IsLocalized
        {
            get { return isLocalized; }
            set
            {
                if (isLocalized == value) return;
                isLocalized = value;
                Invalidate();
            }
        }

        public StatusPillButton()
        {
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Pixel);
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            int radius = (int)Math.Round(8 * scale);

            // Subtle drop shadow
            Rectangle shadowBounds = new Rectangle(1, 2, Width - 2, Height - 3);
            using (GraphicsPath shadow = MainForm.RoundedRectangle(shadowBounds, radius))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(24, 0, 0, 0)))
                e.Graphics.FillPath(shadowBrush, shadow);

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 3);
            using (GraphicsPath path = MainForm.RoundedRectangle(bounds, radius))
            {
                Color fillColor = IsLocalized
                    ? (hovering && Enabled ? Color.FromArgb(196, 98, 68) : Color.FromArgb(217, 119, 87))
                    : (hovering && Enabled ? Color.FromArgb(222, 224, 230) : Color.FromArgb(230, 232, 236));
                using (SolidBrush fill = new SolidBrush(fillColor))
                    e.Graphics.FillPath(fill, path);
            }

            Color textColor = IsLocalized ? Color.FromArgb(250, 250, 252) : Color.FromArgb(76, 79, 105);

            if (IsLocalized)
            {
                Size textSize = TextRenderer.MeasureText(Text, Font, Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int checkW = (int)Math.Round(14 * scale);
                int gap = (int)Math.Round(8 * scale);
                int totalWidth = checkW + gap + textSize.Width;
                int left = (Width - totalWidth) / 2;
                int midY = bounds.Height / 2;

                // Draw crisp vector checkmark
                using (Pen checkPen = new Pen(Color.White, 2F * scale))
                {
                    checkPen.StartCap = LineCap.Round;
                    checkPen.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(checkPen, new[]
                    {
                        new Point(left, midY),
                        new Point(left + (int)Math.Round(4 * scale), midY + (int)Math.Round(4 * scale)),
                        new Point(left + checkW, midY - (int)Math.Round(5 * scale))
                    });
                }

                Rectangle textBounds = new Rectangle(left + checkW + gap, 0, textSize.Width, bounds.Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, textColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, bounds, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 0 || Height <= 0) return;
            float scale;
            using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96F;
            using (GraphicsPath path = MainForm.RoundedRectangle(new Rectangle(0, 0, Width, Height), (int)Math.Round(8 * scale)))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null) oldRegion.Dispose();
            }
        }
    }

    internal sealed class SoftCheckBox : CheckBox
    {
        public SoftCheckBox()
        {
            AutoSize = false;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel);
            ForeColor = Color.FromArgb(120, 125, 145);
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            int boxSize = (int)Math.Round(14 * scale);
            Rectangle box = new Rectangle(0, (Height - boxSize) / 2, boxSize, boxSize);

            Color themeColor = Color.FromArgb(217, 119, 87);
            using (GraphicsPath path = MainForm.RoundedRectangle(box, (int)Math.Round(4 * scale)))
            using (SolidBrush fill = new SolidBrush(Checked ? themeColor : Color.FromArgb(230, 232, 236)))
            using (Pen border = new Pen(Checked ? themeColor : Color.FromArgb(203, 205, 212)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            if (Checked)
            {
                using (Pen check = new Pen(Color.White, 1.5F * scale))
                {
                    check.StartCap = LineCap.Round;
                    check.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(check, new[]
                    {
                        new Point(box.Left + (int)Math.Round(3 * scale), box.Top + (int)Math.Round(7 * scale)),
                        new Point(box.Left + (int)Math.Round(6 * scale), box.Top + (int)Math.Round(10 * scale)),
                        new Point(box.Left + (int)Math.Round(11 * scale), box.Top + (int)Math.Round(4 * scale))
                    });
                }
            }

            int textGap = (int)Math.Round(6 * scale);
            Rectangle textBounds = new Rectangle(box.Right + textGap, 0, Width - box.Right - textGap, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class OnboardingInfoButton : Button
    {
        private bool hovering;
        private bool pressed;

        public OnboardingInfoButton()
        {
            TabStop = false;
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel);
            BackColor = Color.Transparent;
            ForeColor = Color.FromArgb(76, 79, 105);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { pressed = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            int radius = (int)Math.Round(8 * scale);

            Rectangle shadowBounds = new Rectangle(1, 2, Width - 2, Height - 3);
            using (GraphicsPath shadow = MainForm.RoundedRectangle(shadowBounds, radius))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(18, 0, 0, 0)))
                e.Graphics.FillPath(shadowBrush, shadow);

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 3);
            Color fill = pressed ? Color.FromArgb(220, 222, 228) : hovering ? Color.FromArgb(238, 240, 244) : Color.FromArgb(230, 232, 236);
            using (GraphicsPath path = MainForm.RoundedRectangle(bounds, radius))
            using (SolidBrush fillBrush = new SolidBrush(fill))
            using (Pen border = new Pen(Color.FromArgb(213, 215, 220)))
            {
                e.Graphics.FillPath(fillBrush, path);
                e.Graphics.DrawPath(border, path);
            }

            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class CardPanel : Panel
    {
        public Color BorderColor { get; set; }
        public int CornerRadius { get; set; }

        public CardPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(236, 239, 242);
            BorderColor = Color.FromArgb(215, 217, 222);
            CornerRadius = 12;
            Padding = Padding.Empty;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width <= 2 || Height <= 2) return;
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
