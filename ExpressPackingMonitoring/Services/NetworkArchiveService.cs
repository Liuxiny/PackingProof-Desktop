using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Security.Cryptography;

namespace ExpressPackingMonitoring.Services;

internal sealed class NetworkArchiveService : IDisposable
{
    private readonly VideoDatabase _database;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Task _worker;

    public NetworkArchiveService(VideoDatabase database, CancellationToken cancellationToken = default)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Wake()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
                _wakeSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task<int> ProcessPendingOnceAsync(CancellationToken cancellationToken)
    {
        int completed = 0;
        foreach (VideoRecord candidate in _database.GetPendingNetworkArchives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryArchiveAsync(candidate.Id, cancellationToken).ConfigureAwait(false))
                completed++;
        }
        return completed;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Archive", $"Archive worker iteration failed: {ex.Message}");
            }

            try
            {
                await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool> TryArchiveAsync(long recordId, CancellationToken cancellationToken)
    {
        using IDisposable ownership = await VideoLifecycleCoordinator.EnterAsync(recordId, cancellationToken);
        VideoRecord record = _database.GetVideoById(recordId);
        if (record == null || record.IsDeleted || string.IsNullOrWhiteSpace(record.NetworkFilePath))
            return false;
        if (record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted)
            return true;

        string localPath = record.LocalFilePath;
        string networkPath = record.NetworkFilePath;
        DateTime attemptedAt = DateTime.Now;
        if (!File.Exists(localPath))
        {
            _database.UpdateArchiveState(
                recordId,
                VideoArchiveStatus.Failed,
                error: "本地录像文件不存在，无法归档",
                attemptedAt: attemptedAt,
                incrementRetry: true);
            return false;
        }

        _database.UpdateArchiveState(recordId, VideoArchiveStatus.Copying, attemptedAt: attemptedAt);
        try
        {
            string videoSha256 = await PublishFileAsync(localPath, networkPath, recordId, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(record.ProofFilePath))
            {
                if (!File.Exists(record.ProofFilePath))
                    throw new IOException("录像证明文件不存在");
                string networkProofPath = Path.ChangeExtension(networkPath, ".proof.json");
                await PublishFileAsync(record.ProofFilePath, networkProofPath, recordId, cancellationToken)
                    .ConfigureAwait(false);
            }

            _database.UpdateArchiveState(
                recordId,
                VideoArchiveStatus.Verified,
                contentSha256: videoSha256,
                attemptedAt: attemptedAt,
                completedAt: DateTime.Now);
            VideoFileResolver.MarkNetworkPathAvailable(networkPath);
            RuntimeLog.Info("Archive", $"Archive verified id={recordId}, target={networkPath}");
            return true;
        }
        catch (ArchiveConflictException ex)
        {
            _database.UpdateArchiveState(
                recordId,
                VideoArchiveStatus.Conflict,
                error: ex.Message,
                attemptedAt: attemptedAt,
                incrementRetry: true);
            RuntimeLog.Warn("Archive", $"Archive conflict id={recordId}, target={networkPath}, error={ex.Message}");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _database.UpdateArchiveState(
                recordId,
                VideoArchiveStatus.Pending,
                error: "归档已暂停，等待下次继续",
                attemptedAt: attemptedAt);
            throw;
        }
        catch (Exception ex)
        {
            _database.UpdateArchiveState(
                recordId,
                VideoArchiveStatus.Failed,
                error: ex.Message,
                attemptedAt: attemptedAt,
                incrementRetry: true);
            RuntimeLog.Warn("Archive", $"Archive failed id={recordId}, target={networkPath}, error={ex.Message}");
            return false;
        }
    }

    private static async Task<string> PublishFileAsync(
        string sourcePath,
        string destinationPath,
        long recordId,
        CancellationToken cancellationToken)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new IOException("网络归档目标目录无效");
        Directory.CreateDirectory(destinationDirectory);

        string sourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (File.Exists(destinationPath))
        {
            string existingHash = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(sourceHash, existingHash, StringComparison.OrdinalIgnoreCase))
                return sourceHash;
            throw new ArchiveConflictException("网络目标已存在同名但内容不同的文件，已禁止覆盖");
        }

        string temporaryPath = destinationPath + $".{recordId}.uploading";
        // This operation owns this unique temporary name. It may remove an earlier incomplete copy
        // only while the complete local source is still present under the same lifecycle lock.
        if (File.Exists(temporaryPath) && File.Exists(sourcePath))
            File.Delete(temporaryPath);

        try
        {
            await CopyFileAsync(sourcePath, temporaryPath, cancellationToken).ConfigureAwait(false);
            string temporaryHash = await ComputeSha256Async(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sourceHash, temporaryHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("网络临时文件 SHA-256 校验失败");

            if (File.Exists(destinationPath))
            {
                string concurrentHash = await ComputeSha256Async(destinationPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(sourceHash, concurrentHash, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveConflictException("发布时发现同名文件冲突，已禁止覆盖");
                File.Delete(temporaryPath);
                return sourceHash;
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            return sourceHash;
        }
        catch
        {
            if (File.Exists(sourcePath) && File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
            throw;
        }
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, bufferSize, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        if (source.Length != destination.Length)
            throw new IOException("网络临时文件长度校验失败");
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public void Dispose()
    {
        _cts.Cancel();
        Wake();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _wakeSignal.Dispose();
        _cts.Dispose();
    }

    private sealed class ArchiveConflictException(string message) : IOException(message);
}
