using System.Diagnostics;
using System.Text.Json;

namespace CampTransfer;

public sealed class TransferEngine
{
    private const int MaxRetryAttempts = 60;
    private const int SourceCleanupRetryAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SourceCleanupRetryDelay = TimeSpan.FromSeconds(2);

    private readonly object _pauseLock = new();
    private readonly object _destinationChangeLock = new();
    private readonly SemaphoreSlim _pauseSignal = new(0);
    private CancellationTokenSource? _currentCts;
    private DestinationChangeRequest? _pendingDestinationChange;
    private TransferItem? _currentItem;

    public Func<double> GetSpeedLimitBytesPerSecond { get; set; } = () => 0;
    public Func<bool> ShouldStopAfterCurrent { get; set; } = () => false;
    public bool IsPaused { get; private set; }
    public bool IsRunning { get; private set; }
    public bool StoppedAfterCurrent { get; private set; }

    public void Pause()
    {
        lock (_pauseLock)
        {
            if (IsPaused) return;
            while (_pauseSignal.Wait(0)) { }
            IsPaused = true;
        }
    }

    public void Resume()
    {
        var shouldWake = false;
        lock (_pauseLock)
        {
            if (!IsPaused) return;
            IsPaused = false;
            shouldWake = true;
        }

        if (shouldWake)
            _pauseSignal.Release();
    }

    public void CancelCurrent() => _currentCts?.Cancel();

    public async Task<DestinationChangeResult> ChangePausedDestinationAsync(TransferItem item, string destinationRoot)
    {
        destinationRoot = PathHelpers.NormalizeDestinationPath(destinationRoot);
        if (string.IsNullOrWhiteSpace(destinationRoot))
            return new DestinationChangeResult(false, "The new destination is empty.");

        lock (_pauseLock)
        {
            if (!IsRunning || !IsPaused || !ReferenceEquals(_currentItem, item))
                return new DestinationChangeResult(false, "Pause the active transfer before changing its destination.");
        }

        var completion = new TaskCompletionSource<DestinationChangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_destinationChangeLock)
        {
            if (_pendingDestinationChange is not null)
                return new DestinationChangeResult(false, "A destination change is already in progress.");

            _pendingDestinationChange = new DestinationChangeRequest(item, destinationRoot, completion);
        }

        _pauseSignal.Release();
        return await completion.Task;
    }

    public async Task RunQueueAsync(IReadOnlyList<TransferItem> items, Action<TransferItem> itemChanged, CancellationToken stopQueueToken)
    {
        if (IsRunning) return;
        IsRunning = true;
        StoppedAfterCurrent = false;
        Resume();

        try
        {
            foreach (var item in items)
            {
                stopQueueToken.ThrowIfCancellationRequested();
                if (item.Completed) continue;

                _currentCts?.Dispose();
                _currentCts = CancellationTokenSource.CreateLinkedTokenSource(stopQueueToken);
                lock (_pauseLock) _currentItem = item;

                try
                {
                    if (item.SourceCleanupPending)
                    {
                        await CompleteMoveSourceCleanupAsync(item, itemChanged, _currentCts.Token);
                    }
                    else
                    {
                        var retryAttempt = 0;
                        while (true)
                        {
                            try
                            {
                                await CopyOneAsync(item, itemChanged, _currentCts.Token);
                                break;
                            }
                            catch (Exception ex) when (IsRetryableTransferException(ex, item) && retryAttempt < MaxRetryAttempts)
                            {
                                retryAttempt++;
                                item.Status = $"Retrying {retryAttempt}/{MaxRetryAttempts}";
                                item.Speed = "";
                                item.Eta = $"{(int)RetryDelay.TotalSeconds}s";
                                item.CurrentBytesPerSecond = 0;
                                itemChanged(item);
                                await Task.Delay(RetryDelay, _currentCts.Token);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!stopQueueToken.IsCancellationRequested)
                {
                    item.Status = "Cancelled";
                    item.Speed = "";
                    item.Eta = "";
                    item.CurrentBytesPerSecond = 0;
                    itemChanged(item);
                }
                catch (Exception ex)
                {
                    item.Status = $"Error: {ex.Message}";
                    item.Speed = "";
                    item.Eta = "";
                    item.CurrentBytesPerSecond = 0;
                    itemChanged(item);
                }
                finally
                {
                    lock (_pauseLock)
                    {
                        if (ReferenceEquals(_currentItem, item))
                            _currentItem = null;
                    }
                    FailPendingDestinationChange(item, "The transfer is no longer active.");
                }

                if (item.Completed && ShouldStopAfterCurrent())
                {
                    StoppedAfterCurrent = true;
                    break;
                }
            }
        }
        finally
        {
            _currentCts?.Dispose();
            _currentCts = null;
            lock (_pauseLock) _currentItem = null;
            FailAnyPendingDestinationChange("The transfer queue stopped.");
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

        await WaitWhilePausedAsync(token);

        var normalizedDestination = PathHelpers.NormalizeDestinationPath(item.DestinationRoot);
        if (!string.Equals(normalizedDestination, item.DestinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            item.DestinationRoot = normalizedDestination;
            item.NotifyDestinationChanged();
            itemChanged(item);
        }

        var finalPath = Path.Combine(normalizedDestination, item.RelativePath);
        if (PathHelpers.PathsEqual(item.SourcePath, finalPath))
            throw new InvalidOperationException("Source and destination are the same file");

        var finalDirectory = Path.GetDirectoryName(finalPath) ?? normalizedDestination;
        Directory.CreateDirectory(finalDirectory);

        var tempPath = finalPath + ".camptransfer.part";
        var metaPath = tempPath + ".json";
        var sourceInfo = new FileInfo(item.SourcePath);
        item.SizeBytes = sourceInfo.Length;
        item.Completed = false;
        item.SourceCleanupPending = false;

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
        item.CurrentBytesPerSecond = 0;
        itemChanged(item);

        const int bufferSize = 64 * 1024;
        var buffer = new byte[bufferSize];
        FileStream? source = null;
        FileStream? destination = null;

        long transferred = resumeAt;
        long speedSampleBytes = 0;
        var speedSampleWatch = Stopwatch.StartNew();
        var uiWatch = Stopwatch.StartNew();

        double pacedLimit = -1;
        long pacedBytes = 0;
        var paceWatch = Stopwatch.StartNew();

        void OpenStreams()
        {
            source = new FileStream(
                item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            destination = new FileStream(
                tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            source.Position = transferred;
            destination.Position = transferred;
            destination.SetLength(transferred);
        }

        OpenStreams();

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (IsPaused)
                {
                    if (destination is not null)
                    {
                        await destination.FlushAsync(token);
                        await destination.DisposeAsync();
                        destination = null;
                    }
                    if (source is not null)
                    {
                        await source.DisposeAsync();
                        source = null;
                    }

                    var pausedPaths = await WaitWhilePausedAndHandleDestinationChangesAsync(
                        item,
                        sourceInfo,
                        transferred,
                        new TransferPaths(normalizedDestination, finalPath, tempPath, metaPath),
                        itemChanged,
                        token);

                    normalizedDestination = pausedPaths.DestinationRoot;
                    finalPath = pausedPaths.FinalPath;
                    tempPath = pausedPaths.TempPath;
                    metaPath = pausedPaths.MetaPath;

                    OpenStreams();
                    speedSampleBytes = 0;
                    speedSampleWatch.Restart();
                    uiWatch.Restart();
                    pacedLimit = -1;
                    pacedBytes = 0;
                    paceWatch.Restart();
                }

                var limit = GetSpeedLimitBytesPerSecond();
                if (limit <= 0)
                {
                    pacedLimit = 0;
                    pacedBytes = 0;
                    paceWatch.Restart();
                }
                else if (Math.Abs(limit - pacedLimit) > 0.5)
                {
                    pacedLimit = limit;
                    pacedBytes = 0;
                    paceWatch.Restart();
                    speedSampleBytes = 0;
                    speedSampleWatch.Restart();
                }

                var chunkSize = limit > 0
                    ? (int)Math.Clamp(limit / 20.0, 4 * 1024, buffer.Length)
                    : buffer.Length;

                var read = await source!.ReadAsync(buffer.AsMemory(0, chunkSize), token);
                if (read == 0) break;

                await destination!.WriteAsync(buffer.AsMemory(0, read), token);
                transferred += read;
                speedSampleBytes += read;

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
                    item.ProgressPercent = sourceInfo.Length == 0 ? 100 : (double)transferred / sourceInfo.Length * 100;

                    if (speedSampleWatch.ElapsedMilliseconds >= 1000)
                    {
                        var seconds = Math.Max(speedSampleWatch.Elapsed.TotalSeconds, 0.001);
                        var actualBytesPerSecond = speedSampleBytes / seconds;
                        item.CurrentBytesPerSecond = actualBytesPerSecond;
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

                        speedSampleBytes = 0;
                        speedSampleWatch.Restart();
                    }

                    item.Status = IsPaused ? "Paused" : "Transferring";
                    itemChanged(item);
                    uiWatch.Restart();
                }
            }

            await destination!.FlushAsync(token);
        }
        finally
        {
            if (destination is not null)
                await destination.DisposeAsync();
            if (source is not null)
                await source.DisposeAsync();
        }

        File.Move(tempPath, finalPath, overwrite: true);
        if (File.Exists(metaPath)) File.Delete(metaPath);
        File.SetLastWriteTimeUtc(finalPath, sourceInfo.LastWriteTimeUtc);

        item.ProgressPercent = 100;
        item.Speed = "";
        item.Eta = "";
        item.CurrentBytesPerSecond = 0;

        if (string.Equals(item.Operation, "Move", StringComparison.OrdinalIgnoreCase))
        {
            item.Completed = false;
            item.SourceCleanupPending = true;
            item.Status = "Deleting source";
            itemChanged(item);
            await CompleteMoveSourceCleanupAsync(item, itemChanged, token);
        }
        else
        {
            item.SourceCleanupPending = false;
            item.Completed = true;
            item.Status = "Completed";
            itemChanged(item);
        }
    }

    private async Task<TransferPaths> WaitWhilePausedAndHandleDestinationChangesAsync(
        TransferItem item,
        FileInfo sourceInfo,
        long transferred,
        TransferPaths paths,
        Action<TransferItem> itemChanged,
        CancellationToken token)
    {
        item.Status = "Paused";
        item.Speed = "";
        item.Eta = "";
        item.CurrentBytesPerSecond = 0;
        itemChanged(item);

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var request = TakePendingDestinationChange(item);
            if (request is not null)
            {
                try
                {
                    paths = RelocatePartialTransfer(item, sourceInfo, transferred, paths, request.DestinationRoot);
                    item.DestinationRoot = paths.DestinationRoot;
                    item.NotifyDestinationChanged();
                    item.Status = "Paused";
                    itemChanged(item);
                    request.Completion.TrySetResult(new DestinationChangeResult(true, "Destination changed. Press Resume to continue."));
                }
                catch (Exception ex)
                {
                    item.Status = "Paused";
                    itemChanged(item);
                    request.Completion.TrySetResult(new DestinationChangeResult(false, ex.Message));
                }
            }

            lock (_pauseLock)
            {
                if (!IsPaused)
                    return paths;
            }

            await _pauseSignal.WaitAsync(token);
        }
    }

    private static TransferPaths RelocatePartialTransfer(
        TransferItem item,
        FileInfo sourceInfo,
        long transferred,
        TransferPaths oldPaths,
        string newDestinationRoot)
    {
        newDestinationRoot = PathHelpers.NormalizeDestinationPath(newDestinationRoot);
        if (string.IsNullOrWhiteSpace(newDestinationRoot))
            throw new InvalidOperationException("The new destination is empty.");

        var newFinalPath = Path.Combine(newDestinationRoot, item.RelativePath);
        if (PathHelpers.PathsEqual(item.SourcePath, newFinalPath))
            throw new InvalidOperationException("Source and destination are the same file.");

        var newDirectory = Path.GetDirectoryName(newFinalPath) ?? newDestinationRoot;
        Directory.CreateDirectory(newDirectory);

        var newTempPath = newFinalPath + ".camptransfer.part";
        var newMetaPath = newTempPath + ".json";

        if (PathHelpers.PathsEqual(oldPaths.TempPath, newTempPath))
            return new TransferPaths(newDestinationRoot, newFinalPath, newTempPath, newMetaPath);

        if (File.Exists(newTempPath) || File.Exists(newMetaPath))
            throw new IOException("The new destination already contains partial transfer data for this file.");

        if (!File.Exists(oldPaths.TempPath))
            throw new FileNotFoundException("The current partial transfer file could not be found.", oldPaths.TempPath);

        var oldLength = new FileInfo(oldPaths.TempPath).Length;
        if (oldLength != transferred)
            throw new IOException("The partial transfer changed unexpectedly. The destination was not changed.");

        var copiedTemp = false;
        var copiedMeta = false;
        try
        {
            File.Copy(oldPaths.TempPath, newTempPath, overwrite: false);
            copiedTemp = true;

            if (File.Exists(oldPaths.MetaPath))
            {
                File.Copy(oldPaths.MetaPath, newMetaPath, overwrite: false);
                copiedMeta = true;
            }
            else
            {
                var metadata = new TransferMetadata(sourceInfo.Length, sourceInfo.LastWriteTimeUtc.Ticks);
                File.WriteAllText(newMetaPath, JsonSerializer.Serialize(metadata));
                copiedMeta = true;
            }

            var newLength = new FileInfo(newTempPath).Length;
            if (newLength != transferred)
                throw new IOException("The partial transfer could not be verified at the new destination.");

            File.Delete(oldPaths.TempPath);
            if (File.Exists(oldPaths.MetaPath)) File.Delete(oldPaths.MetaPath);
        }
        catch
        {
            if (copiedTemp)
            {
                try { if (File.Exists(newTempPath)) File.Delete(newTempPath); } catch { }
            }
            if (copiedMeta)
            {
                try { if (File.Exists(newMetaPath)) File.Delete(newMetaPath); } catch { }
            }
            throw;
        }

        return new TransferPaths(newDestinationRoot, newFinalPath, newTempPath, newMetaPath);
    }

    private DestinationChangeRequest? TakePendingDestinationChange(TransferItem item)
    {
        lock (_destinationChangeLock)
        {
            if (_pendingDestinationChange is null || !ReferenceEquals(_pendingDestinationChange.Item, item))
                return null;

            var request = _pendingDestinationChange;
            _pendingDestinationChange = null;
            return request;
        }
    }

    private void FailPendingDestinationChange(TransferItem item, string message)
    {
        DestinationChangeRequest? request = null;
        lock (_destinationChangeLock)
        {
            if (_pendingDestinationChange is not null && ReferenceEquals(_pendingDestinationChange.Item, item))
            {
                request = _pendingDestinationChange;
                _pendingDestinationChange = null;
            }
        }
        request?.Completion.TrySetResult(new DestinationChangeResult(false, message));
    }

    private void FailAnyPendingDestinationChange(string message)
    {
        DestinationChangeRequest? request;
        lock (_destinationChangeLock)
        {
            request = _pendingDestinationChange;
            _pendingDestinationChange = null;
        }
        request?.Completion.TrySetResult(new DestinationChangeResult(false, message));
    }

    private async Task<bool> CompleteMoveSourceCleanupAsync(TransferItem item, Action<TransferItem> itemChanged, CancellationToken token)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= SourceCleanupRetryAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            if (!File.Exists(item.SourcePath))
            {
                MarkMoveCleanupComplete(item, itemChanged);
                return true;
            }

            item.Status = attempt == 1
                ? "Deleting source"
                : $"Deleting source {attempt}/{SourceCleanupRetryAttempts}";
            item.Speed = "";
            item.Eta = "";
            item.CurrentBytesPerSecond = 0;
            itemChanged(item);

            try
            {
                File.Delete(item.SourcePath);
                if (!File.Exists(item.SourcePath))
                {
                    MarkMoveCleanupComplete(item, itemChanged);
                    return true;
                }

                lastError = new IOException("Source file still exists after delete.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            if (attempt < SourceCleanupRetryAttempts)
                await Task.Delay(SourceCleanupRetryDelay, token);
        }

        item.Completed = false;
        item.SourceCleanupPending = true;
        item.ProgressPercent = 100;
        item.Speed = "";
        item.Eta = "";
        item.CurrentBytesPerSecond = 0;
        item.Status = lastError is null
            ? "Source cleanup pending"
            : $"Source cleanup pending: {lastError.Message}";
        itemChanged(item);
        return false;
    }

    private static void MarkMoveCleanupComplete(TransferItem item, Action<TransferItem> itemChanged)
    {
        item.SourceCleanupPending = false;
        item.Completed = true;
        item.ProgressPercent = 100;
        item.Speed = "";
        item.Eta = "";
        item.CurrentBytesPerSecond = 0;
        item.Status = "Completed";
        itemChanged(item);
    }

    private static bool IsRetryableTransferException(Exception ex, TransferItem item)
    {
        return ex is IOException && !item.Completed && !item.SourceCleanupPending && File.Exists(item.SourcePath);
    }

    private async Task WaitWhilePausedAsync(CancellationToken token)
    {
        while (true)
        {
            lock (_pauseLock)
            {
                if (!IsPaused) return;
            }

            await _pauseSignal.WaitAsync(token);
        }
    }

    public sealed record DestinationChangeResult(bool Ok, string Message);
    private sealed record DestinationChangeRequest(
        TransferItem Item,
        string DestinationRoot,
        TaskCompletionSource<DestinationChangeResult> Completion);
    private sealed record TransferPaths(string DestinationRoot, string FinalPath, string TempPath, string MetaPath);
    private sealed record TransferMetadata(long SourceLength, long SourceLastWriteUtcTicks);

    private static string FormatEta(TimeSpan time)
    {
        if (time.TotalHours >= 1) return $"{(int)time.TotalHours}h {time.Minutes}m";
        if (time.TotalMinutes >= 1) return $"{time.Minutes}m {time.Seconds}s";
        return $"{Math.Max(0, time.Seconds)}s";
    }
}
