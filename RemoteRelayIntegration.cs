using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CampTransfer;

internal static class RemoteRelayIntegration
{
    private const int RelayPort = 45829;

    public static void Attach(MainForm form)
    {
        var grid = FindControls<DataGridView>(form).FirstOrDefault();
        if (grid?.DataSource is not BindingSource bindingSource ||
            bindingSource.DataSource is not BindingList<TransferItem> queue)
        {
            return;
        }

        var toolbar = FindControls<FlowLayoutPanel>(form)
            .FirstOrDefault(p => p.Controls.OfType<Button>().Any(b => b.Text == "Start"));
        var statusStrip = FindControls<StatusStrip>(form).FirstOrDefault();
        var whenFinishedBox = FindControls<ComboBox>(form)
            .FirstOrDefault(c => c.Items.Cast<object>().Any(i => string.Equals(i?.ToString(), "Shut down", StringComparison.Ordinal)));
        var speedLimitBox = FindControls<ComboBox>(form)
            .FirstOrDefault(c =>
                c.Items.Cast<object>().Any(i => string.Equals(i?.ToString(), "Unlimited", StringComparison.OrdinalIgnoreCase)) &&
                c.Items.Cast<object>().Any(i => string.Equals(i?.ToString(), "0.25 Mbps", StringComparison.OrdinalIgnoreCase)));

        var settings = RemoteRelayPreferences.Load();
        using var publisher = new RemoteRelayPublisher(settings);

        var relayButton = new Button
        {
            Text = "Relay...",
            AutoSize = true,
            Margin = new Padding(8, 3, 2, 0)
        };
        toolbar?.Controls.Add(relayButton);

        var relayLabel = new ToolStripStatusLabel("Relay: Off")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            BorderStyle = Border3DStyle.Etched
        };
        statusStrip?.Items.Add(relayLabel);

        string CurrentUploadLimit() => string.IsNullOrWhiteSpace(speedLimitBox?.Text)
            ? "Unknown"
            : speedLimitBox.Text.Trim();

        void UpdateRelayLabel()
        {
            var current = publisher.Settings;
            if (!current.Enabled)
            {
                relayLabel.Text = "Relay: Off";
                return;
            }

            if (string.IsNullOrWhiteSpace(current.Host))
            {
                relayLabel.Text = "Relay: Setup needed";
                return;
            }

            if (publisher.LastSuccessUtc.HasValue &&
                DateTimeOffset.UtcNow - publisher.LastSuccessUtc.Value < TimeSpan.FromSeconds(5))
            {
                relayLabel.Text = $"Relay: On • {current.Host}:{RelayPort}";
                return;
            }

            relayLabel.Text = string.IsNullOrWhiteSpace(publisher.LastError)
                ? $"Relay: Connecting • {current.Host}"
                : $"Relay: Error — {publisher.LastError}";
        }

        relayButton.Click += async (_, _) =>
        {
            using var dialog = new RelaySettingsDialog(publisher.Settings);
            if (dialog.ShowDialog(form) != DialogResult.OK) return;

            var updated = dialog.Settings;
            RemoteRelayPreferences.Save(updated);
            publisher.Configure(updated);
            UpdateRelayLabel();

            if (updated.Enabled && !string.IsNullOrWhiteSpace(updated.Host))
            {
                var snapshot = BuildSnapshot(queue, whenFinishedBox?.Text ?? "Do nothing", CurrentUploadLimit());
                await publisher.PublishAsync(snapshot);
                UpdateRelayLabel();
            }
        };

        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += async (_, _) =>
        {
            if (publisher.IsBusy) return;
            var current = publisher.Settings;
            if (current.Enabled && !string.IsNullOrWhiteSpace(current.Host))
            {
                var snapshot = BuildSnapshot(queue, whenFinishedBox?.Text ?? "Do nothing", CurrentUploadLimit());
                await publisher.PublishAsync(snapshot);
            }
            UpdateRelayLabel();
        };
        timer.Start();
        UpdateRelayLabel();

        form.FormClosed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
        };
    }

    private static string BuildSnapshot(BindingList<TransferItem> queue, string whenFinished, string uploadLimit)
    {
        var items = queue.ToList();
        var active = items.FirstOrDefault(i =>
            i.Status is "Transferring" or "Paused" or "Resuming" or "Deleting source" ||
            i.Status.StartsWith("Retrying", StringComparison.Ordinal) ||
            i.Status.StartsWith("Source cleanup pending", StringComparison.Ordinal));

        var remaining = items.Where(i => !i.Completed).ToList();
        var remainingBytes = remaining.Sum(i =>
        {
            if (i.SourceCleanupPending) return 0d;
            var fractionRemaining = 1.0 - Math.Clamp(i.ProgressPercent / 100.0, 0, 1);
            return i.SizeBytes * fractionRemaining;
        });

        var state = active?.Status ?? (items.Count == 0 ? "Ready" : remaining.Count == 0 ? "Queue complete" : "Queued");
        var payload = new
        {
            apiVersion = 1,
            pcName = Environment.MachineName,
            state,
            whenFinished,
            uploadLimit,
            updatedUtc = DateTimeOffset.UtcNow,
            filesLeft = remaining.Count,
            remainingBytes = (long)Math.Max(0, remainingBytes),
            active = active is null ? null : ToRemoteItem(active),
            queue = items.Select(ToRemoteItem).ToArray()
        };
        return JsonSerializer.Serialize(payload);
    }

    private static object ToRemoteItem(TransferItem item) => new
    {
        id = item.Id,
        fileName = item.FileName,
        operation = item.Operation,
        destination = item.DestinationDisplay,
        sizeBytes = item.SizeBytes,
        progressPercent = Math.Round(item.ProgressPercent, 1),
        speed = item.Speed,
        eta = item.Eta,
        status = item.Status,
        completed = item.Completed,
        sourceCleanupPending = item.SourceCleanupPending
    };

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var nested in FindControls<T>(child)) yield return nested;
        }
    }
}

internal sealed class RemoteRelayPublisher : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };
    private bool _busy;

    public RemoteRelaySettings Settings { get; private set; }
    public bool IsBusy => _busy;
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string LastError { get; private set; } = "";

    public RemoteRelayPublisher(RemoteRelaySettings settings)
    {
        Settings = settings;
    }

    public void Configure(RemoteRelaySettings settings)
    {
        Settings = settings;
        LastSuccessUtc = null;
        LastError = "";
    }

    public async Task PublishAsync(string snapshotJson)
    {
        if (_busy || !Settings.Enabled || string.IsNullOrWhiteSpace(Settings.Host)) return;
        _busy = true;
        try
        {
            var host = NormalizeHost(Settings.Host);
            using var content = new StringContent(snapshotJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"http://{host}:{RemoteRelayIntegrationPort.Value}/api/status", content);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

            LastSuccessUtc = DateTimeOffset.UtcNow;
            LastError = "";
        }
        catch (Exception ex)
        {
            LastError = FriendlyError(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    public static async Task<string> TestAsync(string host)
    {
        host = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(host)) return "Enter the home relay server address first.";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await client.GetAsync($"http://{host}:{RemoteRelayIntegrationPort.Value}/api/ping");
            if (!response.IsSuccessStatusCode) return $"Relay returned HTTP {(int)response.StatusCode}.";
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var service = doc.RootElement.TryGetProperty("service", out var serviceElement)
                ? serviceElement.GetString()
                : "";
            return string.Equals(service, "CampTransferRelay", StringComparison.Ordinal)
                ? "Connected to CampTransfer Relay."
                : "A server answered, but it was not CampTransfer Relay.";
        }
        catch (Exception ex)
        {
            return "Could not reach relay: " + FriendlyError(ex);
        }
    }

    public static string NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var host = value.Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host[7..];
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host[8..];
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];
        var colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out _)) host = host[..colon];
        return host.Trim();
    }

    private static string FriendlyError(Exception ex)
    {
        var text = ex.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(text)) return "Connection failed";
        if (text.Length > 80) text = text[..80] + "…";
        return text;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal static class RemoteRelayIntegrationPort
{
    public const int Value = 45829;
}

internal sealed class RemoteRelaySettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
}

internal static class RemoteRelayPreferences
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CampTransfer",
        "relay-monitor.json");

    public static RemoteRelaySettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new RemoteRelaySettings();
            return JsonSerializer.Deserialize<RemoteRelaySettings>(File.ReadAllText(SettingsPath)) ?? new RemoteRelaySettings();
        }
        catch
        {
            return new RemoteRelaySettings();
        }
    }

    public static void Save(RemoteRelaySettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch { }
    }
}

internal sealed class RelaySettingsDialog : Form
{
    private readonly CheckBox _enabled;
    private readonly TextBox _host;
    private readonly Label _testResult;

    public RemoteRelaySettings Settings => new()
    {
        Enabled = _enabled.Checked,
        Host = RemoteRelayPublisher.NormalizeHost(_host.Text)
    };

    public RelaySettingsDialog(RemoteRelaySettings current)
    {
        Text = "CampTransfer Relay";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 245);
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = "Home relay server",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 16)
        };
        Controls.Add(title);

        var help = new Label
        {
            Text = "CampTransfer will send its monitor status to CampTransfer Relay over your existing network. No transferred file data is relayed.",
            AutoSize = false,
            Location = new Point(18, 46),
            Size = new Size(460, 42)
        };
        Controls.Add(help);

        _enabled = new CheckBox
        {
            Text = "Enable home relay",
            Checked = current.Enabled,
            AutoSize = true,
            Location = new Point(18, 92)
        };
        Controls.Add(_enabled);

        var hostLabel = new Label { Text = "Server IP / hostname:", AutoSize = true, Location = new Point(18, 126) };
        Controls.Add(hostLabel);

        _host = new TextBox
        {
            Text = current.Host,
            Location = new Point(145, 122),
            Size = new Size(220, 25)
        };
        Controls.Add(_host);

        var port = new Label { Text = $"Port {RemoteRelayIntegrationPort.Value}", AutoSize = true, Location = new Point(374, 126) };
        Controls.Add(port);

        var testButton = new Button { Text = "Test", Location = new Point(145, 157), Size = new Size(80, 30) };
        _testResult = new Label { Text = "", AutoSize = false, Location = new Point(235, 163), Size = new Size(245, 42) };
        testButton.Click += async (_, _) =>
        {
            testButton.Enabled = false;
            _testResult.Text = "Testing...";
            _testResult.Text = await RemoteRelayPublisher.TestAsync(_host.Text);
            testButton.Enabled = true;
        };
        Controls.Add(testButton);
        Controls.Add(_testResult);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(314, 207), Size = new Size(80, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(402, 207), Size = new Size(80, 30) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && _enabled.Checked && string.IsNullOrWhiteSpace(RemoteRelayPublisher.NormalizeHost(_host.Text)))
            {
                MessageBox.Show(this, "Enter the IP address or hostname of the home server running CampTransfer Relay.",
                    "CampTransfer Relay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
            }
        };
    }
}
