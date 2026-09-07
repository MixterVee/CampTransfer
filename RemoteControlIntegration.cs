using System.Security.Cryptography;
using System.Text.Json;

namespace CampTransfer;

internal sealed record RemoteControlResult(bool Ok, string Message);

internal static class RemoteControlIntegration
{
    public static RemoteControlHub? Current { get; private set; }

    public static void Attach(MainForm form)
    {
        var toolbar = FindControls<FlowLayoutPanel>(form)
            .FirstOrDefault(p => p.Controls.OfType<Button>().Any(b => b.Text == "Start"));
        if (toolbar is null) return;

        var startButton = toolbar.Controls.OfType<Button>().FirstOrDefault(b => b.Text == "Start");
        var pauseButton = toolbar.Controls.OfType<Button>().FirstOrDefault(b => b.Text is "Pause" or "Resume");
        var cancelButton = toolbar.Controls.OfType<Button>().FirstOrDefault(b => b.Text == "Cancel Current");
        var pauseAfter = toolbar.Controls.OfType<CheckBox>().FirstOrDefault(c => c.Text == "Pause after current");
        var speedBox = toolbar.Controls.OfType<ComboBox>().FirstOrDefault(c =>
            c.Items.Cast<object>().Any(i => string.Equals(i?.ToString(), "Unlimited", StringComparison.OrdinalIgnoreCase)) &&
            c.Items.Cast<object>().Any(i => string.Equals(i?.ToString(), "0.25 Mbps", StringComparison.OrdinalIgnoreCase)));

        if (startButton is null || pauseButton is null || cancelButton is null || pauseAfter is null || speedBox is null)
            return;

        var security = RemoteControlSecurity.LoadOrCreate();
        Current = new RemoteControlHub(form, startButton, pauseButton, cancelButton, pauseAfter, speedBox, security);

        var pairButton = new Button
        {
            Text = "Pair Remote...",
            AutoSize = true,
            Margin = new Padding(8, 3, 2, 0)
        };
        pairButton.Click += (_, _) =>
        {
            using var dialog = new RemotePairingDialog(security);
            dialog.ShowDialog(form);
        };
        toolbar.Controls.Add(pairButton);
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var nested in FindControls<T>(child)) yield return nested;
        }
    }
}

internal sealed class RemoteControlHub
{
    private readonly MainForm _form;
    private readonly Button _startButton;
    private readonly Button _pauseButton;
    private readonly Button _cancelButton;
    private readonly CheckBox _pauseAfter;
    private readonly ComboBox _speedBox;

    public RemoteControlSecurity Security { get; }
    public string Token => Security.Token;
    public string PairingCode => Security.PairingCode;

    public RemoteControlHub(
        MainForm form,
        Button startButton,
        Button pauseButton,
        Button cancelButton,
        CheckBox pauseAfter,
        ComboBox speedBox,
        RemoteControlSecurity security)
    {
        _form = form;
        _startButton = startButton;
        _pauseButton = pauseButton;
        _cancelButton = cancelButton;
        _pauseAfter = pauseAfter;
        _speedBox = speedBox;
        Security = security;
    }

    public bool IsAuthorized(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token),
            System.Text.Encoding.UTF8.GetBytes(Token));

    public Task<RemoteControlResult> ExecuteAsync(string action, string? value)
    {
        var tcs = new TaskCompletionSource<RemoteControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void ExecuteOnUi()
        {
            try
            {
                RemoteControlResult result = action switch
                {
                    "start" => Start(),
                    "pause" => Pause(),
                    "resume" => Resume(),
                    "cancelCurrent" => CancelCurrent(),
                    "pauseAfterCurrent" => SetPauseAfter(value),
                    "setUploadLimit" => SetUploadLimit(value),
                    _ => new(false, "Unknown remote command.")
                };
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new RemoteControlResult(false, ex.Message));
            }
        }

        if (_form.IsDisposed)
        {
            tcs.SetResult(new RemoteControlResult(false, "CampTransfer is closing."));
        }
        else if (_form.InvokeRequired)
        {
            try { _form.BeginInvoke((Action)ExecuteOnUi); }
            catch (Exception ex) { tcs.SetResult(new RemoteControlResult(false, ex.Message)); }
        }
        else
        {
            ExecuteOnUi();
        }

        return tcs.Task;
    }

    private RemoteControlResult Start()
    {
        if (!_startButton.Enabled)
            return new(false, "The queue is already running or cannot be started yet.");
        _startButton.PerformClick();
        return new(true, "Queue started.");
    }

    private RemoteControlResult Pause()
    {
        if (!_pauseButton.Enabled)
            return new(false, "There is no active transfer to pause.");
        if (string.Equals(_pauseButton.Text, "Resume", StringComparison.OrdinalIgnoreCase))
            return new(true, "Transfer is already paused.");
        _pauseButton.PerformClick();
        return new(true, "Transfer paused.");
    }

    private RemoteControlResult Resume()
    {
        if (!_pauseButton.Enabled)
            return new(false, "There is no paused transfer to resume.");
        if (!string.Equals(_pauseButton.Text, "Resume", StringComparison.OrdinalIgnoreCase))
            return new(true, "Transfer is already running.");
        _pauseButton.PerformClick();
        return new(true, "Transfer resumed.");
    }

    private RemoteControlResult CancelCurrent()
    {
        if (!_cancelButton.Enabled)
            return new(false, "There is no active transfer to cancel.");
        _cancelButton.PerformClick();
        return new(true, "Current transfer cancelled; the queue will continue.");
    }

    private RemoteControlResult SetPauseAfter(string? value)
    {
        if (!bool.TryParse(value, out var enabled))
            return new(false, "Invalid Pause After Current value.");
        _pauseAfter.Checked = enabled;
        return new(true, enabled ? "Pause After Current enabled." : "Pause After Current disabled.");
    }

    private RemoteControlResult SetUploadLimit(string? value)
    {
        value = value?.Trim() ?? "";
        var match = _speedBox.Items.Cast<object>()
            .Select(i => i?.ToString() ?? "")
            .FirstOrDefault(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(match))
            return new(false, "That upload limit is not available in CampTransfer.");

        _speedBox.Text = match;
        return new(true, $"Upload limit changed to {match}.");
    }
}

internal sealed class RemoteControlSecurity
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CampTransfer",
        "remote-control.json");

    public string Token { get; set; } = "";
    public string PairingCode { get; set; } = "";

    public static RemoteControlSecurity LoadOrCreate()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<RemoteControlSecurity>(File.ReadAllText(SettingsPath));
                if (loaded is not null && loaded.Token.Length >= 32 && loaded.PairingCode.Length == 6)
                    return loaded;
            }
        }
        catch { }

        var created = new RemoteControlSecurity();
        created.Regenerate(save: false);
        created.Save();
        return created;
    }

    public void Regenerate(bool save = true)
    {
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        PairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        if (save) Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

internal sealed class RemotePairingDialog : Form
{
    public RemotePairingDialog(RemoteControlSecurity security)
    {
        Text = "Pair CampTransfer Remote";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 6,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(500, 0)
        };

        var title = new Label
        {
            Text = "Remote control pairing",
            AutoSize = true,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        layout.Controls.Add(title);

        layout.Controls.Add(new Label
        {
            Text = "Enter this one-time code in CampTransfer Remote. After pairing, the phone stores a private control token and you do not need to enter the code again.",
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Margin = new Padding(0, 0, 0, 12)
        });

        var codeLabel = new Label
        {
            Text = security.PairingCode,
            AutoSize = true,
            Font = new Font("Consolas", 24f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(codeLabel);

        var regenerate = new Button { Text = "Generate New Code", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        regenerate.Click += (_, _) =>
        {
            security.Regenerate();
            codeLabel.Text = security.PairingCode;
        };
        layout.Controls.Add(regenerate);

        layout.Controls.Add(new Label
        {
            Text = "Generating a new code also invalidates previously paired phones.",
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Margin = new Padding(0, 0, 0, 12)
        });

        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Right };
        layout.Controls.Add(close);
        Controls.Add(layout);
        AcceptButton = close;
    }
}
