using System.Diagnostics;
using System.Text.Json;

namespace CampTransfer;

public sealed class TransferEngine
{
    private readonly object _pauseLock = new();
    private TaskCompletionSource<bool>? _resumeTcs;
    private CancellationTokenSource? _currentCts;

    public Func<double> GetSpeedLimitBytesPerSecond { get; set; } = () => 0;
    public bool IsPaused { get; private set; }
    public bool IsRunning { get; private set; }

    public void Pause()
    {
        lock (_pauseLock)
        {
            if (IsPaused) return;
            IsPaused = true;
            _resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? resumeTcs;
        lock (_pauseLock)
        {
            if (!IsPaused) return;
            IsPaused = false;
            resumeTcs = _resumeTcs;
            _resumeTcs = null;
        }

        resumeTcs?.TrySetResult(true);
    }

    public void CancelCurrent() => _currentCts?.Cancel();

    public async Task RunQueueAsync(IReadOnlyList<TransferItem> items, Action<TransferItem> itemChanged, CancellationToken stopQueueToken)
    {
        if (IsRunning) return;
        IsRunning = true;
        Resume();

        try
        {
            foreach (var item in items)
            {
                stopQueueToken.ThrowIfCancellationRequested();
                if (item.Status == "Completed") continue;

                _currentCts?.Dispose();
                _currentCts = CancellationTokenSource.CreateLinkedTokenSource(stopQueueToken);

                try
                {
                    await CopyOneAsync(item, itemChanged, _currentCts.Token);
                }
                catch (OperationCanceledException) when (!stopQueueToken.IsCancellationRequested)
                {
                    item.Status = "Cancelled";
                    item.Speed = "";
                    item.Eta = "";
                    itemChanged(item);
                }
                catch (Exception ex)
                {
                    item.Status = $"Error: {ex.Message}";
                    item.Speed = "";
                    item.Eta = "";
                    itemChanged(item);
                }
            }
        }
        finally
        {
            _currentCts?.Dispose();
            _currentCts = null;
            IsRunning = false;
            Resume();
        }
    }

    private async Task CopyOneAsync(TransferItem item, Action<TransferItem> itemChanged, CancellationToken token)
    {
        if (!File.Exists(item.SourcePath))
            throw new FileNotFoundException("Source file not found", item.SourcePath);
        if (string.IsNullOrWhiteSpace(item.DestinationRoot))
            throw new InvalidOperationException("Destination is not set");

        var finalPath = Path.Combine(item.DestinationRoot, item.RelativePath);
        var finalDirectory = Path.GetDirectoryName(finalPath) ?? item.DestinationRoot;
        Directory.CreateDirectory(finalDirectory);

        var tempPath = finalPath + ".camptransfer.part";
        var metaPath = tempPath + ".json";
        var sourceInfo = new FileInfo(item.SourcePath);
        item.SizeBytes = sourceInfo.Length;
        item.Completed = false;

        long resumeAt = 0;
        if (File.Exists(tempPath))
        {
            var metadataMatches = false;
            try
            {
                if (File.Exists(metaPath))
                {
                    var metadata = JsonSerializer.Deserialize<TransferMetadata>(await File.ReadAllTextAsync(metaPath, token));
                    metadataMatches = metadata is not null &&
                        metadata.SourceLength == sourceInfo.Length &&
                        metadata.SourceLastWriteUtcTicks == sourceInfo.LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                metadataMatches = false;
            }

            var tempLength = new FileInfo(tempPath).Length;
            if (metadataMatches && tempLength <= sourceInfo.Length)
            {
                resumeAt = tempLength;
            }
            else
            {
                File.Delete(tempPath);
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
        }

        var transferMetadata = new TransferMetadata(sourceInfo.Length, sourceInfo.LastWriteTimeUtc.Ticks);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(transferMetadata), token);

        item.Status = resumeAt > 0 ? "Resuming" : "Transferring";
        item.ProgressPercent = sourceInfo.Length == 0 ? 100 : (double)resumeAt / sourceInfo.Length * 100;
        itemChanged(item);

        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];

        await using var source = new FileStream(
            item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        source.Position = resumeAt;
        destination.Position = resumeAt;
        destination.SetLength(resumeAt);

        long transferred = resumeAt;
        long sampleBytes = 0;
        var sampleWatch = Stopwatch.StartNew();
        var uiWatch = Stopwatch.StartNew();

        // Cumulative pacing prevents normal Task.Delay timer overshoot from being
        // added permanently on every chunk. If one delay runs long, later chunks
        // automatically catch up while the long-term average stays at the limit.
        double pacedLimit = -1;
        long pacedBytes = 0;
        var paceWatch = Stopwatch.StartNew();

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var pauseWait = Stopwatch.StartNew();
            await WaitWhilePausedAsync(token);
            if (pauseWait.ElapsedMilliseconds >= 50)
            {
                sampleBytes = 0;
                sampleWatch.Restart();
                uiWatch.Restart();
                pacedLimit = -1;
                pacedBytes = 0;
                paceWatch.Restart();
            }

            // Aim for about 20 paced writes per second. This avoids large SMB/TCP
            // bursts on slow links while still keeping higher limits efficient.
            var limit = GetSpeedLimitBytesPerSecond();
            if (limit <= 0)
            {
                pacedLimit = 0;
                pacedBytes = 0;
                paceWatch.Restart();
            }
            else if (Math.Abs(limit - pacedLimit) > 0.5)
            {
                // A live speed-limit change starts a fresh pacing window so the new
                // selection takes effect immediately without carrying old timing debt.
                pacedLimit = limit;
                pacedBytes = 0;
                paceWatch.Restart();
            }

            var chunkSize = limit > 0
                ? (int)Math.Clamp(limit / 20.0, 4 * 1024, buffer.Length)
                : buffer.Length;

            var read = await source.ReadAsync(buffer.AsMemory(0, chunkSize), token);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            transferred += read;
            sampleBytes += read;

            if (limit > 0)
            {
                pacedBytes += read;
                var targetSeconds = pacedBytes / limit;
                var remaining = targetSeconds - paceWatch.Elapsed.TotalSeconds;
                if (remaining > 0)
                    await Task.Delay(TimeSpan.FromSeconds(remaining), token);
            }

            if (uiWatch.ElapsedMilliseconds >= 250)
            {
                var seconds = Math.Max(sampleWatch.Elapsed.TotalSeconds, 0.001);
                var actualBytesPerSecond = sampleBytes / seconds;
                item.ProgressPercent = sourceInfo.Length == 0 ? 100 : (double)transferred / sourceInfo.Length * 100;
                item.Speed = actualBytesPerSecond > 0 ? $"{TransferItem.FormatBytes(actualBytesPerSecond)}/s" : "";

                if (actualBytesPerSecond > 1 && transferred < sourceInfo.Length)
                {
                    var etaSeconds = (sourceInfo.Length - transferred) / actualBytesPerSecond;
                    item.Eta = FormatEta(TimeSpan.FromSeconds(etaSeconds));
                }
                else
                {
                    item.Eta = "";
                }

                item.Status = IsPaused ? "Paused" : "Transferring";
                itemChanged(item);

                sampleBytes = 0;
                sampleWatch.Restart();
                uiWatch.Restart();
            }
        }

        await destination.FlushAsync(token);
        destination.Close();
        source.Close();

        File.Move(tempPath, finalPath, overwrite: true);
        if (File.Exists(metaPath)) File.Delete(metaPath);
        File.SetLastWriteTimeUtc(finalPath, sourceInfo.LastWriteTimeUtc);

        item.Completed = true;
        item.ProgressPercent = 100;
        item.Speed = "";
        item.Eta = "";
        item.Status = "Completed";
        itemChanged(item);
    }

    private async Task WaitWhilePausedAsync(CancellationToken token)
    {
        Task? waitTask = null;
        lock (_pauseLock)
        {
            if (IsPaused)
                waitTask = _resumeTcs?.Task;
        }

        if (waitTask is not null)
            await waitTask.WaitAsync(token);
    }

    private sealed record TransferMetadata(long SourceLength, long SourceLastWriteUtcTicks);

    private static string FormatEta(TimeSpan time)
    {
        if (time.TotalHours >= 1) return $"{(int)time.TotalHours}h {time.Minutes}m";
        if (time.TotalMinutes >= 1) return $"{time.Minutes}m {time.Seconds}s";
        return $"{Math.Max(0, time.Seconds)}s";
    }
}
