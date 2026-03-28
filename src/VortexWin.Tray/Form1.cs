namespace VortexWin.Tray;

using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.Threading;

public partial class Form1 : Form
{
    private TextBox txtLogs = null!;
    private Button btnRefresh = null!;
    private System.Windows.Forms.Timer refreshTimer = null!;
    private NotifyIcon trayIcon = null!;
    private bool _isFirstLoad = true;
    private IpcTrayServer? _ipcServer;
    private CancellationTokenSource? _cts;

    public Form1()
    {
        InitializeComponent();
        SetupUI();
    }

    private void SetupUI()
    {
        this.Text = "Vortex Win - Diagnostics Log Viewer";
        this.Size = new Size(800, 600);
        this.StartPosition = FormStartPosition.CenterScreen;

        // Tray Icon Setup
        trayIcon = new NotifyIcon();
        trayIcon.Text = "Vortex Win Tray";
        trayIcon.Icon = SystemIcons.Shield; // default shield icon
        trayIcon.Visible = true;
        
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("View Diagnostics Log", null, (s, e) => this.Show());
        contextMenu.Items.Add("Exit", null, (s, e) => {
            trayIcon.Visible = false;
            Application.Exit();
        });
        trayIcon.ContextMenuStrip = contextMenu;

        trayIcon.DoubleClick += (s, e) => this.Show();

        // Log UI Components
        txtLogs = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.LightGreen,
            Font = new Font("Consolas", 10F)
        };

        btnRefresh = new Button
        {
            Text = "Refresh Logs",
            Dock = DockStyle.Bottom,
            Height = 40,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnRefresh.Click += (s, e) => LoadLogs();

        this.Controls.Add(txtLogs);
        this.Controls.Add(btnRefresh);

        // Form settings to hide on start
        this.ShowInTaskbar = false;
        this.WindowState = FormWindowState.Minimized;
        this.Load += (s, e) => {
            if (_isFirstLoad) 
            {
                this.Hide(); 
                _isFirstLoad = false;
            }
            LoadLogs();
        };

        // Refresh Timer for active viewing
        refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        refreshTimer.Tick += (s, e) => { if (this.Visible) LoadLogs(); };
        refreshTimer.Start();

        // Start IPC Server
        _ipcServer = new IpcTrayServer(this);
        _cts = new CancellationTokenSource();
        _ = _ipcServer.StartAsync(_cts.Token);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            this.Hide();
        }
        else
        {
            _ipcServer?.Stop();
            _cts?.Cancel();
        }
    }

    public void ShowAlert(string payloadJson)
    {
        try 
        {
            var alertData = System.Text.Json.JsonSerializer.Deserialize<VortexWin.Core.Ipc.AlertData>(payloadJson);
            if (alertData == null) return;

            string title = alertData.Title;
            string text = alertData.Body;
            
            // PRD: Toast/Modal UI alert implementation
            if (alertData.PercentRemaining == 75 || alertData.PercentRemaining == 50)
            {
                // Simple Toast (Tooltip bubble)
                trayIcon.ShowBalloonTip(alertData.AutoDismissSeconds > 0 ? alertData.AutoDismissSeconds * 1000 : 5000, title, text, ToolTipIcon.Warning);
                if (alertData.PercentRemaining == 50) 
                    System.Media.SystemSounds.Exclamation.Play(); // Soft sound
            }
            else if (alertData.PercentRemaining == 25 || alertData.IsExpiry)
            {
                // Alert Modal + beep setup
                System.Media.SystemSounds.Hand.Play(); // Loud beep
                if (alertData.IsExpiry) 
                {
                    MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                }
                else
                {
                    // 25% modal
                    MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, MessageBoxOptions.RightAlign);
                }
            }
            else if (alertData.IsSecured)
            {
                trayIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
            }
        }
        catch { }
    }

    private void LoadLogs()
    {
        string serviceLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VortexWin", "logs", "service.log");

        var logDir = Path.GetDirectoryName(serviceLog);
        if (Directory.Exists(logDir))
        {
            var logFiles = Directory.GetFiles(logDir, "service*.log").OrderByDescending(f => f).ToList();
            if (logFiles.Any())
            {
                serviceLog = logFiles.First();
            }
        }

        if (!File.Exists(serviceLog))
        {
            txtLogs.Text = "No service logs found. Service might not be running or log directory is empty."; 
            return;
        }

        try
        {
            using var fs = new FileStream(serviceLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var logs = sr.ReadToEnd();

            var lines = logs.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                            .Reverse().Take(150).Reverse().ToList();
            var analysis = new System.Text.StringBuilder();

            foreach(var line in lines)
            {
                analysis.AppendLine(line);
                if (line.Contains("[ERR]") || line.Contains("[FTL]") || line.Contains("Exception") || line.Contains("Fail"))
                {
                    analysis.AppendLine("    *** DIAGNOSTIC SOLUTION ***");
                    if (line.Contains("IPC server error"))
                        analysis.AppendLine("    -> Try restarting the VortexWin service. Ensure Named Pipes permission is granted.");
                    else if (line.Contains("Failed to start challenge"))
                        analysis.AppendLine("    -> Check config files or system privileges.");
                    else if (line.Contains("FileSystemWatcher error"))
                        analysis.AppendLine("    -> Sentinel Desktop folder might be in use or permissions restricted.");
                    else if (line.Contains("Invalid master password"))
                        analysis.AppendLine("    -> Master password does not match. Reset to fallback if needed.");
                    else
                        analysis.AppendLine("    -> Unknown error. Please review the trace above or contact support.");
                    analysis.AppendLine("    ***************************");
                }
            }
            
            if (txtLogs.Text != analysis.ToString()) 
            {
                txtLogs.Text = analysis.ToString();
                txtLogs.SelectionStart = txtLogs.Text.Length;
                txtLogs.ScrollToCaret();
            }
        }
        catch { }
    }
}
