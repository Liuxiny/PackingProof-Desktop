using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal sealed record RecordingDeletionResult(
    bool Completed,
    bool WaitingForNetwork,
    string Message);

internal sealed class RecordingDeletionService
{
    private readonly VideoDatabase _database;
    private readonly Func<long, bool> _isCurrentRecording;
    private readonly Action? _onDeleteRequested;

    public RecordingDeletionService(
        VideoDatabase database,
        Func<long, bool> isCurrentRecording,
        Action? onDeleteRequested = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _isCurrentRecording = isCurrentRecording ?? throw new ArgumentNullException(nameof(isCurrentRecording));
        _onDeleteRequested = onDeleteRequested;
    }

    public async Task<RecordingDeletionResult> DeleteAsync(
        long recordId,
        CancellationToken cancellationToken,
        bool signalActivity = true)
    {
        if (recordId <= 0)
            return new RecordingDeletionResult(false, false, "录像记录无效");
        if (_isCurrentRecording(recordId))
            return new RecordingDeletionResult(false, false, "当前正在录制，不能删除");

        if (signalActivity)
            _onDeleteRequested?.Invoke();

        RecordingDeleteJob job = _database.GetPendingRecordingDeleteJobs(1000)
            .FirstOrDefault(item => item.RecordId == recordId)
            ?? new RecordingDeleteJob
            {
                RecordId = recordId,
                RequestedAt = DateTime.Now,
                State = RecordingDeleteJobState.Pending
            };
        _database.UpsertRecordingDeleteJob(job);

        using IDisposable ownership = await VideoLifecycleCoordinator.EnterAsync(recordId, cancellationToken);
        if (_isCurrentRecording(recordId))
            return new RecordingDeletionResult(false, false, "当前正在录制，不能删除");

        VideoRecord? record = _database.GetVideoById(recordId);
        if (record == null || record.IsDeleted)
        {
            job.State = RecordingDeleteJobState.Completed;
            job.LocalVideoDeleted = true;
            job.NetworkVideoDeleted = true;
            job.LocalPhotoDeleted = true;
            job.NetworkPhotoDeleted = true;
            job.ProofDeleted = true;
            _database.UpsertRecordingDeleteJob(job);
            return new RecordingDeletionResult(true, false, "录像已删除");
        }

        bool waitingForNetwork = false;
        var errors = new List<string>();
        job.LocalVideoDeleted = DeleteLocal(record.LocalFilePath, errors);
        job.LocalPhotoDeleted = DeleteLocal(record.LocalPhotoPath, errors);

        job.NetworkVideoDeleted = DeleteNetwork(record.NetworkFilePath, errors, ref waitingForNetwork);
        job.NetworkPhotoDeleted = DeleteNetwork(record.NetworkPhotoPath, errors, ref waitingForNetwork);

        bool localProofDeleted = DeleteLocal(record.ProofFilePath, errors);
        string networkProofPath = string.IsNullOrWhiteSpace(record.NetworkFilePath)
            ? ""
            : Path.ChangeExtension(record.NetworkFilePath, ".proof.json");
        bool networkProofDeleted = DeleteNetwork(networkProofPath, errors, ref waitingForNetwork);
        job.ProofDeleted = localProofDeleted && networkProofDeleted;

        DeleteOwnedTemporaryFiles(record, errors);
        DeletePhotoThumbnailCache(recordId);
        bool complete = job.LocalVideoDeleted
            && job.NetworkVideoDeleted
            && job.LocalPhotoDeleted
            && job.NetworkPhotoDeleted
            && job.ProofDeleted;
        job.State = complete
            ? RecordingDeleteJobState.Completed
            : waitingForNetwork
                ? RecordingDeleteJobState.WaitingForNetwork
                : RecordingDeleteJobState.Failed;
        job.LastError = errors.Count == 0
            ? (waitingForNetwork ? "网络路径不可用" : "")
            : string.Join("；", errors.Distinct());
        _database.UpsertRecordingDeleteJob(job);

        if (complete)
        {
            _database.MarkVideoDeletedById(recordId, "用户手动删除");
            RuntimeLog.Info("Delete", $"Recording aggregate deleted id={recordId}");
            return new RecordingDeletionResult(true, false, "录像及关联照片和证明已删除");
        }

        RuntimeLog.Warn("Delete", $"Recording aggregate deletion pending id={recordId}, state={job.State}, error={job.LastError}");
        return new RecordingDeletionResult(false, waitingForNetwork, waitingForNetwork
            ? "本地副本已处理，等待网络恢复后继续删除"
            : $"部分文件删除失败：{job.LastError}");
    }

    public async Task<int> ProcessPendingOnceAsync(CancellationToken cancellationToken)
    {
        int completed = 0;
        foreach (RecordingDeleteJob job in _database.GetPendingRecordingDeleteJobs())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordingDeletionResult result = await DeleteAsync(job.RecordId, cancellationToken, signalActivity: false);
            if (result.Completed) completed++;
        }
        return completed;
    }

    private static bool DeleteLocal(string? path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (!Path.IsPathRooted(path))
        {
            errors.Add($"拒绝删除非绝对路径：{path}");
            return false;
        }
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex)
        {
            errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
            return false;
        }
    }

    private static bool DeleteNetwork(string? path, List<string> errors, ref bool waitingForNetwork)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (StorageLocationResolver.IsNetworkPath(path)
                && (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)))
            {
                waitingForNetwork = true;
                return false;
            }
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException ex) when (StorageLocationResolver.IsNetworkPath(path))
        {
            waitingForNetwork = true;
            errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
            return false;
        }
    }

    private static void DeleteOwnedTemporaryFiles(VideoRecord record, List<string> errors)
    {
        var paths = new List<string>();
        AddTemporaryPath(paths, record.LocalFilePath, path => path + ".writing");
        AddTemporaryPath(paths, record.LocalFilePath, path => Path.ChangeExtension(path, ".mp4") + ".writing");
        AddTemporaryPath(paths, record.NetworkFilePath, path => path + $".{record.Id}.uploading");
        AddTemporaryPath(paths, record.LocalPhotoPath, path => path + ".writing");
        AddTemporaryPath(paths, record.NetworkPhotoPath, path => path + $".{record.Id}.uploading");
        AddTemporaryPath(paths, record.ProofFilePath, path => path + ".writing");
        AddTemporaryPath(paths, record.NetworkFilePath, path => Path.ChangeExtension(path, ".proof.json") + $".{record.Id}.uploading");

        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            DeleteLocal(path, errors);
    }

    private static void AddTemporaryPath(List<string> paths, string? ownerPath, Func<string, string> factory)
    {
        if (!string.IsNullOrWhiteSpace(ownerPath) && Path.IsPathRooted(ownerPath))
            paths.Add(factory(ownerPath!));
    }

    private static void DeletePhotoThumbnailCache(long recordId)
    {
        try
        {
            if (!Directory.Exists(AppPaths.PhotoThumbnailDir)) return;
            foreach (string path in Directory.EnumerateFiles(
                         AppPaths.PhotoThumbnailDir,
                         $"{recordId}_*.jpg",
                         SearchOption.TopDirectoryOnly))
            {
                if (Path.IsPathRooted(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Delete", $"Failed to clear photo thumbnail cache id={recordId}: {ex.Message}");
        }
    }
}
