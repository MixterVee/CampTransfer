using Microsoft.Win32;

namespace CampTransferRelay;

internal sealed class RelayForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CampTransferRelay";

    private readonly RelayServer _server = new();
    private readonly Label _statusValue;
    private readonly Label _addressValue;
    private readonly Label _sourceValue;
    private readonly Label _lastUpdateValue;
    private readonly CheckBox _startWithWindows;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _trayIcon;
    private bool _exitRequested;

    public RelayForm()
    {
        Text = "CampTransfer Relay";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 330);
        Size = new Size(620, 390);
        Font = new Font("Segoe UI", 10f);

        _trayIcon = BuildTrayIcon();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(22),
            ColumnCount = 2,
            RowCount = 8,
            AutoSize = false
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var title = new Label
        {
            Text = "CampTransfer Relay",
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        root.Controls.Add(title, 0, 0);
        root.SetColumnSpan(title, 2);

        AddRow(root, 1, "Relay status", out _statusValue);
        AddRow(root, 2, "Address", out _addressValue);
        AddRow(root, 3, "CampTransfer", out _sourceValue);
        AddRow(root, 4, "Last update", out _lastUpdateValue);

        _startWithWindows = new CheckBox
        {
            Text = "Start CampTransfer Relay with Windows",
            AutoSize = true,
            Checked = IsStartupEnabled(),
            Anchor = AnchorStyles.Left
        };
        _startWithWindows.CheckedChanged += (_, _) => SetStartupEnabled(_startWithWindows.Checked);
        root.Controls.Add(_startWithWindows, 1, 5);

        var help = new Label
        {
            Text = "CampTransfer sends only its small monitor-status snapshot here; no transferred file data passes through the relay. You can close this window with X — the relay will keep running in the system tray. Use the tray icon menu to exit completely.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 8, 0, 0)
        };
        root.Controls.Add(help, 0, 6);
        root.SetColumnSpan(help, 2);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false
        };
        var copyButton = new Button { Text = "Copy relay address", AutoSize = true };
        copyButton.Click += (_, _) =>
        {
            try { Clipboard.SetText($"{_server.DisplayAddress}:{RelayServer.Port}"); } catch { }
        };
        buttons.Controls.Add(copyButton);
        root.Controls.Add(buttons, 0, 7);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);

        _server.StatusChanged += OnServerStatusChanged;
        _server.Start();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();

        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _server.Dispose();
        };
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open CampTransfer Relay");
        openItem.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit CampTransfer Relay");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        var icon = new NotifyIcon
        {
            Text = "CampTransfer Relay",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitRequested || e.CloseReason == CloseReason.WindowsShutDown)
            return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        _trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        if (IsDisposed) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _trayIcon.Visible = false;
        Close();
    }

    private static void AddRow(TableLayoutPanel root, int row, string label, out Label value)
    {
        var name = new Label
        {
            Text = label,
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };
        value = new Label
        {
            Text = "—",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        root.Controls.Add(name, 0, row);
        root.Controls.Add(value, 1, row);
    }

    private void OnServerStatusChanged()
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(RefreshStatus);
            else RefreshStatus();
        }
        catch { }
    }

    private void RefreshStatus()
    {
        if (IsDisposed) return;
        var status = _server.GetStatus();

        if (!status.IsRunning)
        {
            _statusValue.Text = string.IsNullOrWhiteSpace(status.LastError)
                ? "Stopped"
                : "Error — " + status.LastError;
            _statusValue.ForeColor = Color.Firebrick;
        }
        else
        {
            _statusValue.Text = "Listening";
            _statusValue.ForeColor = Color.SeaGreen;
        }

        _addressValue.Text = $"{_server.DisplayAddress}:{RelayServer.Port}";
        _sourceValue.Text = status.HasSnapshot
            ? (string.IsNullOrWhiteSpace(status.LastSource) ? "Live" : "Live • " + status.LastSource)
            : "Waiting for CampTransfer";

        if (!status.LastReceivedUtc.HasValue || !status.Age.HasValue)
        {
            _lastUpdateValue.Text = "—";
            _lastUpdateValue.ForeColor = SystemColors.ControlText;
        }
        else
        {
            var seconds = Math.Max(0, status.Age.Value.TotalSeconds);
            _lastUpdateValue.Text = seconds < 2
                ? "Just now"
                : seconds < 60
                    ? $"{Math.Round(seconds):0} seconds ago"
                    : $"{Math.Floor(seconds / 60):0}m {Math.Round(seconds % 60):0}s ago";
            _lastUpdateValue.ForeColor = seconds > 12 ? Color.DarkOrange : SystemColors.ControlText;
        }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var value = key?.GetValue(RunValueName)?.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (enabled)
            {
                var exe = Application.ExecutablePath;
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            // Startup is a convenience only; relay operation itself is unaffected.
        }
    }
}
