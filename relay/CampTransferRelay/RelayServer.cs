using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampTransferRelay;

internal sealed class RelayServer : IDisposable
{
    public const int Port = 80;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CommandExpireAfter = TimeSpan.FromSeconds(60);

    private readonly object _snapshotLock = new();
    private readonly object _controlLock = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _listenTask;
    private string? _snapshotJson;
    private DateTimeOffset? _lastReceivedUtc;
    private string _lastSource = "";
    private string _controlToken = "";
    private string _pairingCode = "";
    private string _pcName = "";
    private readonly List<PendingCommand> _pendingCommands = [];

    public bool IsRunning { get; private set; }
    public string LastError { get; private set; } = "";
    public string DisplayAddress => GetPreferredLocalIPv4() ?? Environment.MachineName;
    public event Action? StatusChanged;

    public RelayStatus GetStatus()
    {
        lock (_snapshotLock)
        {
            var age = _lastReceivedUtc.HasValue
                ? DateTimeOffset.UtcNow - _lastReceivedUtc.Value
                : (TimeSpan?)null;
            return new RelayStatus(
                IsRunning,
                LastError,
                _lastReceivedUtc,
                _lastSource,
                age,
                _snapshotJson is not null);
        }
    }

    public void Start()
    {
        if (IsRunning) return;

        LastError = "";
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            IsRunning = true;
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
            StatusChanged?.Invoke();
        }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        lock (_controlLock)
        {
            foreach (var command in _pendingCommands)
                command.Completion.TrySetResult(new CommandResult(false, "Relay stopped before CampTransfer acknowledged the command."));
            _pendingCommands.Clear();
        }
        StatusChanged?.Invoke();
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _listener is not null)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
            StatusChanged?.Invoke();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 7000;
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, token);
                if (request is null) return;

                if (request.RequestLine.StartsWith("POST /api/status ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleStatusPostAsync(stream, request, client, token);
                    return;
                }

                if (request.RequestLine.StartsWith("GET /api/status ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleStatusGetAsync(stream, token);
                    return;
                }

                if (request.RequestLine.StartsWith("GET /api/ping ", StringComparison.OrdinalIgnoreCase))
                {
                    bool controlAvailable;
                    lock (_controlLock) controlAvailable = !string.IsNullOrWhiteSpace(_controlToken);
                    var json = JsonSerializer.Serialize(new
                    {
                        service = "CampTransferRelay",
                        apiVersion = 2,
                        relayName = Environment.MachineName,
                        port = Port,
                        ready = GetStatus().HasSnapshot,
                        remoteControlAvailable = controlAvailable
                    });
                    await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, token);
                    return;
                }

                if (request.RequestLine.StartsWith("POST /api/pair ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePairAsync(stream, request, token);
                    return;
                }

                if (request.RequestLine.StartsWith("POST /api/control ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleRemoteControlAsync(stream, request, token);
                    return;
                }

                if (request.RequestLine.StartsWith("GET /api/commands ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleGetCommandsAsync(stream, request, token);
                    return;
                }

                if (request.RequestLine.StartsWith("POST /api/command-result ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCommandResultAsync(stream, request, token);
                    return;
                }

                await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found", token);
            }
            catch (JsonException)
            {
                try
                {
                    await WriteResponseAsync(client.GetStream(), "400 Bad Request", "application/json; charset=utf-8",
                        "{\"ok\":false,\"message\":\"Invalid JSON\"}", token);
                }
                catch { }
            }
            catch { }
        }
    }

    private async Task HandleStatusPostAsync(NetworkStream stream, HttpRequest request, TcpClient client, CancellationToken token)
    {
        if (request.Body.Length == 0)
        {
            await WriteResponseAsync(stream, "400 Bad Request", "application/json; charset=utf-8",
                "{\"error\":\"Missing status body\"}", token);
            return;
        }

        var json = Encoding.UTF8.GetString(request.Body);
        using (JsonDocument.Parse(json)) { }

        lock (_snapshotLock)
        {
            _snapshotJson = json;
            _lastReceivedUtc = DateTimeOffset.UtcNow;
            _lastSource = client.Client.RemoteEndPoint?.ToString() ?? "CampTransfer";
        }

        request.Headers.TryGetValue("X-CampTransfer-Control-Token", out var newToken);
        request.Headers.TryGetValue("X-CampTransfer-Pairing-Code", out var newCode);
        request.Headers.TryGetValue("X-CampTransfer-PC", out var newPcName);
        if (!string.IsNullOrWhiteSpace(newToken) && !string.IsNullOrWhiteSpace(newCode))
        {
            lock (_controlLock)
            {
                if (!string.Equals(_controlToken, newToken, StringComparison.Ordinal) && _pendingCommands.Count > 0)
                {
                    foreach (var pending in _pendingCommands)
                        pending.Completion.TrySetResult(new CommandResult(false, "Remote pairing changed before this command completed."));
                    _pendingCommands.Clear();
                }
                _controlToken = newToken.Trim();
                _pairingCode = newCode.Trim();
                _pcName = string.IsNullOrWhiteSpace(newPcName) ? "CampTransfer" : newPcName.Trim();
                ExpireCommandsLocked();
            }
        }

        StatusChanged?.Invoke();
        await WriteResponseAsync(stream, "204 No Content", "text/plain", "", token);
    }

    private async Task HandleStatusGetAsync(NetworkStream stream, CancellationToken token)
    {
        string? snapshot;
        DateTimeOffset? received;
        lock (_snapshotLock)
        {
            snapshot = _snapshotJson;
            received = _lastReceivedUtc;
        }

        if (snapshot is null || received is null)
        {
            var waiting = JsonSerializer.Serialize(new
            {
                service = "CampTransferRelay",
                ready = false,
                relayName = Environment.MachineName,
                message = "Waiting for CampTransfer"
            });
            await WriteResponseAsync(stream, "503 Service Unavailable", "application/json; charset=utf-8", waiting, token);
            return;
        }

        var age = DateTimeOffset.UtcNow - received.Value;
        var node = JsonNode.Parse(snapshot) as JsonObject ?? new JsonObject();
        node["viaRelay"] = true;
        node["relay"] = new JsonObject
        {
            ["serverName"] = Environment.MachineName,
            ["receivedUtc"] = received.Value.ToString("O"),
            ["ageSeconds"] = Math.Round(Math.Max(0, age.TotalSeconds), 1),
            ["stale"] = age > StaleAfter
        };

        await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", node.ToJsonString(), token);
    }

    private async Task HandlePairAsync(NetworkStream stream, HttpRequest request, CancellationToken token)
    {
        string expectedCode;
        string controlToken;
        string pcName;
        lock (_controlLock)
        {
            expectedCode = _pairingCode;
            controlToken = _controlToken;
            pcName = _pcName;
        }

        if (string.IsNullOrWhiteSpace(controlToken) || string.IsNullOrWhiteSpace(expectedCode))
        {
            await WriteResponseAsync(stream, "503 Service Unavailable", "application/json; charset=utf-8",
                "{\"ok\":false,\"message\":\"CampTransfer has not supplied remote-control pairing information yet\"}", token);
            return;
        }

        using var doc = JsonDocument.Parse(request.Body);
        var code = doc.RootElement.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? "" : "";
        if (!string.Equals(code.Trim(), expectedCode, StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, "403 Forbidden", "application/json; charset=utf-8",
                "{\"ok\":false,\"message\":\"Pairing code was not accepted\"}", token);
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            ok = true,
            token = controlToken,
            pcName,
            message = "CampTransfer Remote paired successfully through the relay."
        });
        await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, token);
    }

    private async Task HandleRemoteControlAsync(NetworkStream stream, HttpRequest request, CancellationToken token)
    {
        if (!IsAuthorized(request.Headers))
        {
            await WriteResponseAsync(stream, "401 Unauthorized", "application/json; charset=utf-8",
                "{\"ok\":false,\"message\":\"Pair CampTransfer Remote first\"}", token);
            return;
        }

        using var doc = JsonDocument.Parse(request.Body);
        var action = doc.RootElement.TryGetProperty("action", out var actionElement) ? actionElement.GetString() ?? "" : "";
        string? value = null;
        if (doc.RootElement.TryGetProperty("value", out var valueElement))
        {
            value = valueElement.ValueKind == JsonValueKind.String
                ? valueElement.GetString()
                : valueElement.GetRawText();
        }

        if (!IsAllowedAction(action))
        {
            await WriteResponseAsync(stream, "400 Bad Request", "application/json; charset=utf-8",
                "{\"ok\":false,\"message\":\"Unknown remote command\"}", token);
            return;
        }

        var pending = new PendingCommand(Guid.NewGuid().ToString("N"), action, value);
        lock (_controlLock)
        {
            ExpireCommandsLocked();
            _pendingCommands.Add(pending);
        }

        var finished = await Task.WhenAny(pending.Completion.Task, Task.Delay(TimeSpan.FromSeconds(6), token));
        if (finished == pending.Completion.Task)
        {
            var result = await pending.Completion.Task;
            var json = JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message, acknowledged = true });
            await WriteResponseAsync(stream, result.Ok ? "200 OK" : "409 Conflict", "application/json; charset=utf-8", json, token);
            return;
        }

        var queued = JsonSerializer.Serialize(new
        {
            ok = true,
            queued = true,
            acknowledged = false,
            message = "Command queued; waiting for CampTransfer acknowledgement."
        });
        await WriteResponseAsync(stream, "202 Accepted", "application/json; charset=utf-8", queued, token);
    }

    private async Task HandleGetCommandsAsync(NetworkStream stream, HttpRequest request, CancellationToken token)
    {
        if (!IsAuthorized(request.Headers))
        {
            await WriteResponseAsync(stream, "401 Unauthorized", "application/json; charset=utf-8",
                "{\"ok\":false,\"message\":\"Unauthorized\"}", token);
            return;
        }

        List<object> ready;
        lock (_controlLock)
        {
            ExpireCommandsLocked();
            var now = DateTimeOffset.UtcNow;
            var commands = _pendingCommands
                .Where(c => !c.LastDeliveredUtc.HasValue || now - c.LastDeliveredUtc.Value > TimeSpan.FromSeconds(3))
                .Take(8)
                .ToList();
            foreach (var command in commands)
                command.LastDeliveredUtc = now;
            ready = commands.Select(c => (object)new { c.Id, c.Action, c.Value }).ToList();
        }

        if (ready.Count == 0)
        {
            await WriteResponseAsync(stream, "204 No Content", "application/json; charset=utf-8", "", token);
            return;
        }

        await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", JsonSerializer.Serialize(ready), token);
    }

    private async Task HandleCommandResultAsync(NetworkStream stream, HttpRequest request, CancellationToken token)
    {
        if (!IsAuthorized(request.Headers))
        {
            await WriteResponseAsync(stream, "401 Unauthorized", "application/json; charset=utf-8",
                "{\"ok\":false,\"message\":\"Unauthorized\"}", token);
            return;
        }

        using var doc = JsonDocument.Parse(request.Body);
        var id = doc.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
        var ok = doc.RootElement.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        var message = doc.RootElement.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "" : "";

        PendingCommand? matched = null;
        lock (_controlLock)
        {
            matched = _pendingCommands.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
            if (matched is not null)
                _pendingCommands.Remove(matched);
        }

        matched?.Completion.TrySetResult(new CommandResult(ok, message));
        await WriteResponseAsync(stream, "204 No Content", "text/plain", "", token);
    }

    private bool IsAuthorized(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Authorization", out var auth)) return false;
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = auth[prefix.Length..].Trim();
        lock (_controlLock)
            return !string.IsNullOrWhiteSpace(_controlToken) && string.Equals(presented, _controlToken, StringComparison.Ordinal);
    }

    private static bool IsAllowedAction(string action) => action is
        "start" or "pause" or "resume" or "cancelCurrent" or "pauseAfterCurrent" or "setUploadLimit";

    private void ExpireCommandsLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - CommandExpireAfter;
        foreach (var expired in _pendingCommands.Where(c => c.CreatedUtc < cutoff).ToList())
        {
            _pendingCommands.Remove(expired);
            expired.Completion.TrySetResult(new CommandResult(false, "Remote command expired before CampTransfer acknowledged it."));
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        const int maxHeaderBytes = 32768;
        using var captured = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;

        while (captured.Length < maxHeaderBytes && headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read <= 0) return null;
            captured.Write(buffer, 0, read);
            headerEnd = FindHeaderEnd(captured.GetBuffer(), (int)captured.Length);
        }

        if (headerEnd < 0) throw new InvalidDataException("HTTP header too large");

        var all = captured.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var contentLength = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = value;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out contentLength);
        }

        if (contentLength < 0 || contentLength > 2_000_000)
            throw new InvalidDataException("Invalid Content-Length");

        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        var already = Math.Min(contentLength, Math.Max(0, all.Length - bodyStart));
        if (already > 0)
            Buffer.BlockCopy(all, bodyStart, body, 0, already);

        var offset = already;
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), token);
            if (read <= 0) throw new EndOfStreamException("Unexpected end of request body");
            offset += read;
        }

        return new HttpRequest(lines[0], headers, body);
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        }
        return -1;
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
        if (bodyBytes.Length > 0)
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
                    var text = address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
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

    private sealed record HttpRequest(string RequestLine, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed class PendingCommand
    {
        public string Id { get; }
        public string Action { get; }
        public string? Value { get; }
        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastDeliveredUtc { get; set; }
        public TaskCompletionSource<CommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingCommand(string id, string action, string? value)
        {
            Id = id;
            Action = action;
            Value = value;
        }
    }

    private sealed record CommandResult(bool Ok, string Message);
}

internal sealed record RelayStatus(
    bool IsRunning,
    string LastError,
    DateTimeOffset? LastReceivedUtc,
    string LastSource,
    TimeSpan? Age,
    bool HasSnapshot);
