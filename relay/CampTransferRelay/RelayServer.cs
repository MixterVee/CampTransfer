using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampTransferRelay;

internal sealed class RelayServer : IDisposable
{
    public const int Port = 45829;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(12);

    private readonly object _snapshotLock = new();
    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _listenTask;
    private string? _snapshotJson;
    private DateTimeOffset? _lastReceivedUtc;
    private string _lastSource = "";

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
                client.ReceiveTimeout = 4000;
                client.SendTimeout = 4000;
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, token);
                if (request is null) return;

                if (request.RequestLine.StartsWith("POST /api/status ", StringComparison.OrdinalIgnoreCase))
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
                    StatusChanged?.Invoke();
                    await WriteResponseAsync(stream, "204 No Content", "text/plain", "", token);
                    return;
                }

                if (request.RequestLine.StartsWith("GET /api/status ", StringComparison.OrdinalIgnoreCase))
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
                    return;
                }

                if (request.RequestLine.StartsWith("GET /api/ping ", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        service = "CampTransferRelay",
                        apiVersion = 1,
                        relayName = Environment.MachineName,
                        port = Port,
                        ready = GetStatus().HasSnapshot
                    });
                    await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, token);
                    return;
                }

                await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found", token);
            }
            catch (JsonException)
            {
                try
                {
                    await using var stream = client.GetStream();
                    await WriteResponseAsync(stream, "400 Bad Request", "application/json; charset=utf-8",
                        "{\"error\":\"Invalid JSON\"}", token);
                }
                catch { }
            }
            catch { }
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

        var contentLength = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
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

        return new HttpRequest(lines[0], body);
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

    private sealed record HttpRequest(string RequestLine, byte[] Body);
}

internal sealed record RelayStatus(
    bool IsRunning,
    string LastError,
    DateTimeOffset? LastReceivedUtc,
    string LastSource,
    TimeSpan? Age,
    bool HasSnapshot);
