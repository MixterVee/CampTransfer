using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CampTransfer;

internal static class RemoteMonitorIntegration
{
    private const int HttpPort = 45827;
    private const int DiscoveryPort = 45828;

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

        var service = new RemoteMonitorService(HttpPort, DiscoveryPort);
        var remoteLabel = new ToolStripStatusLabel("Remote: Off")
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            BorderStyle = Border3DStyle.Etched
        };
        statusStrip?.Items.Add(remoteLabel);

        var remoteCheckBox = new CheckBox
        {
            Text = "Remote monitor",
            AutoSize = true,
            Checked = RemoteMonitorPreferences.LoadEnabled(),
            Margin = new Padding(10, 7, 2, 0)
        };
        toolbar?.Controls.Add(remoteCheckBox);

        string CurrentUploadLimit() => string.IsNullOrWhiteSpace(speedLimitBox?.Text)
            ? "Unknown"
            : speedLimitBox.Text.Trim();

        var refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        refreshTimer.Tick += (_, _) =>
        {
            service.UpdateSnapshot(BuildSnapshot(
                queue,
                whenFinishedBox?.Text ?? "Do nothing",
                CurrentUploadLimit()));
            UpdateRemoteLabel(remoteLabel, service);
        };

        void SetEnabled(bool enabled)
        {
            RemoteMonitorPreferences.SaveEnabled(enabled);
            if (enabled)
                service.Start();
            else
                service.Stop();

            UpdateRemoteLabel(remoteLabel, service);
        }

        remoteCheckBox.CheckedChanged += (_, _) => SetEnabled(remoteCheckBox.Checked);

        if (remoteCheckBox.Checked)
            service.Start();

        service.UpdateSnapshot(BuildSnapshot(
            queue,
            whenFinishedBox?.Text ?? "Do nothing",
            CurrentUploadLimit()));
        UpdateRemoteLabel(remoteLabel, service);
        refreshTimer.Start();

        form.FormClosed += (_, _) =>
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            service.Dispose();
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

    private static void UpdateRemoteLabel(ToolStripStatusLabel label, RemoteMonitorService service)
    {
        if (!service.IsRunning)
        {
            label.Text = string.IsNullOrWhiteSpace(service.LastError)
                ? "Remote: Off"
                : $"Remote: Error — {service.LastError}";
            return;
        }

        label.Text = $"Remote: On • {service.DisplayAddress}:{HttpPort}";
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
                yield return match;

            foreach (var nested in FindControls<T>(child))
                yield return nested;
        }
    }
}

internal sealed class RemoteMonitorService : IDisposable
{
    private readonly int _httpPort;
    private readonly int _discoveryPort;
    private readonly object _snapshotLock = new();
    private string _snapshotJson = "{}";
    private CancellationTokenSource? _cts;
    private TcpListener? _tcpListener;
    private UdpClient? _udpClient;
    private Task? _httpTask;
    private Task? _discoveryTask;

    public bool IsRunning { get; private set; }
    public string LastError { get; private set; } = "";
    public string DisplayAddress => GetPreferredLocalIPv4() ?? Environment.MachineName;

    public RemoteMonitorService(int httpPort, int discoveryPort)
    {
        _httpPort = httpPort;
        _discoveryPort = discoveryPort;
    }

    public void UpdateSnapshot(string json)
    {
        lock (_snapshotLock)
            _snapshotJson = json;
    }

    public void Start()
    {
        if (IsRunning) return;

        LastError = "";
        try
        {
            _cts = new CancellationTokenSource();
            _tcpListener = new TcpListener(IPAddress.Any, _httpPort);
            _tcpListener.Start();

            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _udpClient.EnableBroadcast = true;

            IsRunning = true;
            _httpTask = Task.Run(() => HttpLoopAsync(_cts.Token));
            _discoveryTask = Task.Run(() => DiscoveryLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
        }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _tcpListener?.Stop(); } catch { }
        try { _udpClient?.Close(); } catch { }
        _tcpListener = null;
        _udpClient = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task HttpLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _tcpListener is not null)
            {
                var client = await _tcpListener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                string? header;
                do
                {
                    header = await reader.ReadLineAsync(token);
                } while (!string.IsNullOrEmpty(header));

                if (requestLine.StartsWith("GET /api/status ", StringComparison.OrdinalIgnoreCase))
                {
                    string json;
                    lock (_snapshotLock) json = _snapshotJson;
                    await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, token);
                }
                else if (requestLine.StartsWith("GET /api/ping ", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        service = "CampTransfer",
                        apiVersion = 1,
                        pcName = Environment.MachineName
                    });
                    await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, token);
                }
                else
                {
                    await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found", token);
                }
            }
            catch { }
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        if (_udpClient is null) return;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync(token);
                var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (!string.Equals(text, "CAMPTRANSFER_DISCOVER", StringComparison.Ordinal))
                    continue;

                var response = JsonSerializer.Serialize(new
                {
                    service = "CampTransfer",
                    apiVersion = 1,
                    pcName = Environment.MachineName,
                    port = _httpPort
                });
                var bytes = Encoding.UTF8.GetBytes(response);
                await _udpClient.SendAsync(bytes, result.RemoteEndPoint, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, string body, CancellationToken token)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, token);
        await stream.WriteAsync(bodyBytes, token);
        await stream.FlushAsync(token);
    }

    private static string? GetPreferredLocalIPv4()
    {
        var candidates = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                        continue;
                    if (address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        continue;
                    candidates.Add(address);
                }
            }
        }
        catch { }

        return candidates.FirstOrDefault(IsPrivateIPv4)?.ToString() ?? candidates.FirstOrDefault()?.ToString();
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    public void Dispose() => Stop();
}

internal static class RemoteMonitorPreferences
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CampTransfer",
        "remote-monitor.json");

    public static bool LoadEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return !doc.RootElement.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean();
        }
        catch
        {
            return true;
        }
    }

    public static void SaveEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { enabled }));
        }
        catch { }
    }
}
