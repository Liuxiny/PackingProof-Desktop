using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private void ForceCheckDiskAndCleanup()
        {
            _ = Task.Run(() => RunDiskCleanupCore(forceFullScan: true));
        }

        private async Task CheckDiskAndCleanup()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                RunDiskCleanupCore(forceFullScan: false);
                int interval = IsRecording ? 10000 : 60000;
                try { await Task.Delay(interval, _cts.Token); } catch { break; }
            }
        }

        private int _diskCleanupRunning;
        private DateTime _lastFullDiskCleanup = DateTime.MinValue;
        private long _lastKnownDiskTotalBytes;
        private long _lastKnownDiskCapacityBytes;

        private void RunDiskCleanupCore(bool forceFullScan)
        {
            if (Interlocked.Exchange(ref _diskCleanupRunning, 1) == 1) return;
            try
            {
                if (Config.StorageLocations == null || Config.StorageLocations.Count == 0 || _db == null) return;

                bool fullScan = forceFullScan
                    || _lastFullDiskCleanup == DateTime.MinValue
                    || (DateTime.Now - _lastFullDiskCleanup).TotalSeconds >= (IsRecording ? 60 : 180);

                long totalCurrentBytes = _lastKnownDiskTotalBytes;
                long totalCapacityBytes = _lastKnownDiskCapacityBytes;
                if (fullScan)
                {
                    (totalCurrentBytes, totalCapacityBytes) = ScanLocalRecordingStorage();
                    long releasedBytes = ApplyRetentionPolicies(totalCurrentBytes);
                    totalCurrentBytes = Math.Max(0, totalCurrentBytes - releasedBytes);

                    _lastFullDiskCleanup = DateTime.Now;
                    _lastKnownDiskTotalBytes = totalCurrentBytes;
                    _lastKnownDiskCapacityBytes = totalCapacityBytes;
                }

                if (IsRecording && !string.IsNullOrEmpty(_currentVideoFilePath))
                {
                    try
                    {
                        if (File.Exists(_currentVideoFilePath))
                            totalCurrentBytes += new FileInfo(_currentVideoFilePath).Length;
                    }
                    catch { }
                }

                UpdateDiskUsageText(totalCurrentBytes, totalCapacityBytes);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Storage", $"Retention scan failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _diskCleanupRunning, 0);
            }
        }

        private (long UsedBytes, long CapacityBytes) ScanLocalRecordingStorage()
        {
            var paths = new List<(string Path, StorageLocation Policy)>();
            foreach (StorageLocation location in Config.StorageLocations)
            {
                if (string.IsNullOrWhiteSpace(location.Path)
                    || StorageLocationResolver.IsNetworkPath(location.Path))
                    continue;
                paths.Add((NormalizeStoragePath(location.Path), location));
            }

            if (Config.StorageLocations.Any(location =>
                    !string.IsNullOrWhiteSpace(location.Path)
                    && StorageLocationResolver.IsNetworkPath(location.Path))
                && !string.IsNullOrWhiteSpace(Config.LocalRecordingBufferPath))
            {
                string bufferPath = NormalizeStoragePath(Config.LocalRecordingBufferPath);
                paths.Add((bufferPath, new StorageLocation { Path = bufferPath, ReserveGB = 0 }));
            }

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long usedBytes = 0;
            long capacityBytes = 0;
            foreach ((string path, StorageLocation policy) in paths)
            {
                if (!Directory.Exists(path)) continue;
                foreach (FileInfo file in EnumerateVideoFiles(path))
                {
                    string fullName = file.FullName;
                    if (seenFiles.Add(fullName))
                        usedBytes += file.Length;
                }

                if (StorageVolumeInfo.TryGet(path, out StorageVolumeInfo volume)
                    && seenVolumes.Add(volume.RootPath))
                {
                    long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(policy, volume);
                    capacityBytes += Math.Max(0, volume.AvailableFreeSpace - reserveBytes);
                }
            }

            return (usedBytes, usedBytes + capacityBytes);
        }

        private long ApplyRetentionPolicies(long totalCurrentBytes)
        {
            if (IsRecording || _recordingActivityGate.HasActiveOperation)
            {
                RuntimeLog.Info("Storage", "Retention cleanup deferred for recording or snapshot activity");
                return 0;
            }

            IReadOnlyList<VideoRecord> candidates = _db.GetRetentionCandidates();
            long releasedBytes = 0;
            int deletedRecords = 0;
            int deletedLocalCopies = 0;
            DateTime today = DateTime.Today;

            foreach (VideoRecord record in candidates)
            {
                if (IsCurrentRecording(record)) continue;
                if (StorageRetentionPolicy.IsExpiredByDate(record, today, Config.MaxRetentionDays))
                {
                    long released = TryDeleteExpiredRecord(record, "达到最长保留天数");
                    if (released > 0)
                    {
                        releasedBytes += released;
                        deletedRecords++;
                    }
                }
            }

            foreach (VideoRecord record in candidates)
            {
                if (IsCurrentRecording(record)) continue;
                if (StorageRetentionPolicy.IsArchivedLocalCopyExpired(record, today))
                {
                    long released = TryDeleteVerifiedLocalCopy(record);
                    if (released > 0)
                    {
                        releasedBytes += released;
                        deletedLocalCopies++;
                    }
                }
            }

            if (Config.EnableMaxStorageUsage && Config.MaxStorageUsageGB > 0)
            {
                long maxBytes = (long)Math.Ceiling(Config.MaxStorageUsageGB * StorageSpacePolicy.BytesPerGiB);
                long currentBytes = Math.Max(0, totalCurrentBytes - releasedBytes);
                foreach (VideoRecord record in candidates)
                {
                    if (currentBytes <= maxBytes) break;
                    if (IsCurrentRecording(record)) continue;

                    long released = record.ArchiveStatus == VideoArchiveStatus.Verified
                        ? TryDeleteVerifiedLocalCopy(record)
                        : TryDeleteExpiredRecord(record, "达到录像最大占用空间");
                    if (released <= 0) continue;

                    releasedBytes += released;
                    currentBytes = Math.Max(0, currentBytes - released);
                    if (record.ArchiveStatus == VideoArchiveStatus.Verified)
                        deletedLocalCopies++;
                    else
                        deletedRecords++;
                }
            }

            if (deletedRecords > 0 || deletedLocalCopies > 0)
            {
                RuntimeLog.Info(
                    "Storage",
                    $"Retention cleanup records={deletedRecords}, localCopies={deletedLocalCopies}, released={releasedBytes}");
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    ShowToast($"存储清理：删除 {deletedRecords} 条过期录像，释放 {releasedBytes / 1024d / 1024d / 1024d:F1} GB");
                    RefreshTodayStats();
                });
            }

            return releasedBytes;
        }

        private bool IsCurrentRecording(VideoRecord record) =>
            record.Id > 0 && record.Id == _currentRecordId;

        private long TryDeleteVerifiedLocalCopy(VideoRecord candidate)
        {
            try
            {
                using IDisposable ownership = VideoLifecycleCoordinator
                    .EnterAsync(candidate.Id, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                VideoRecord record = _db.GetVideoById(candidate.Id);
                if (record == null
                    || _db.IsRecordingAggregateBusy(record.Id)
                    || record.ArchiveStatus != VideoArchiveStatus.Verified
                    || string.IsNullOrWhiteSpace(record.LocalFilePath)
                    || string.IsNullOrWhiteSpace(record.NetworkFilePath)
                    || !File.Exists(record.LocalFilePath)
                    || !File.Exists(record.NetworkFilePath))
                {
                    return 0;
                }

                if (!RemoteArchiveMatches(record))
                    return 0;

                long released = new FileInfo(record.LocalFilePath).Length;
                File.Delete(record.LocalFilePath);
                if (!string.IsNullOrWhiteSpace(record.LocalPhotoPath) && File.Exists(record.LocalPhotoPath))
                {
                    released += new FileInfo(record.LocalPhotoPath).Length;
                    File.Delete(record.LocalPhotoPath);
                }
                DeleteOwnedSidecar(record.ProofFilePath, record.LocalFilePath);
                _db.MarkLocalCopyDeleted(record.Id, DateTime.Now, "网络已校验，本地仅保留昨天和今天");
                return released;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Storage", $"Local archive copy cleanup skipped id={candidate.Id}: {ex.Message}");
                return 0;
            }
        }

        private bool RemoteArchiveMatches(VideoRecord record)
        {
            try
            {
                var local = new FileInfo(record.LocalFilePath);
                var remote = new FileInfo(record.NetworkFilePath);
                if (local.Length != remote.Length) return false;
                if (string.IsNullOrWhiteSpace(record.ContentSha256)) return true;
                string remoteHash = NetworkArchiveService
                    .ComputeSha256Async(record.NetworkFilePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (!string.Equals(remoteHash, record.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (string.IsNullOrWhiteSpace(record.LocalPhotoPath))
                    return true;
                if (string.IsNullOrWhiteSpace(record.NetworkPhotoPath)
                    || !File.Exists(record.LocalPhotoPath)
                    || !File.Exists(record.NetworkPhotoPath))
                    return false;
                var localPhoto = new FileInfo(record.LocalPhotoPath);
                var remotePhoto = new FileInfo(record.NetworkPhotoPath);
                if (localPhoto.Length != remotePhoto.Length) return false;
                if (string.IsNullOrWhiteSpace(record.PhotoSha256)) return true;
                string remotePhotoHash = NetworkArchiveService
                    .ComputeSha256Async(record.NetworkPhotoPath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                return string.Equals(remotePhotoHash, record.PhotoSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private long TryDeleteExpiredRecord(VideoRecord candidate, string reason)
        {
            try
            {
                using IDisposable ownership = VideoLifecycleCoordinator
                    .EnterAsync(candidate.Id, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                VideoRecord record = _db.GetVideoById(candidate.Id);
                if (record == null || IsCurrentRecording(record) || _db.IsRecordingAggregateBusy(record.Id)) return 0;

                bool hasPublishedNetworkFile = record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted;
                if (hasPublishedNetworkFile
                    && !string.IsNullOrWhiteSpace(record.NetworkFilePath)
                    && !File.Exists(record.NetworkFilePath))
                {
                    RuntimeLog.Warn("Storage", $"Expired network record deferred because target is unavailable id={record.Id}, target={record.NetworkFilePath}");
                    return 0;
                }

                long released = 0;
                if (!string.IsNullOrWhiteSpace(record.LocalFilePath) && File.Exists(record.LocalFilePath))
                {
                    released += new FileInfo(record.LocalFilePath).Length;
                    File.Delete(record.LocalFilePath);
                }
                if (!string.IsNullOrWhiteSpace(record.LocalPhotoPath) && File.Exists(record.LocalPhotoPath))
                {
                    released += new FileInfo(record.LocalPhotoPath).Length;
                    File.Delete(record.LocalPhotoPath);
                }
                if (hasPublishedNetworkFile
                    && !string.IsNullOrWhiteSpace(record.NetworkFilePath)
                    && File.Exists(record.NetworkFilePath))
                {
                    File.Delete(record.NetworkFilePath);
                    DeleteSidecar(Path.ChangeExtension(record.NetworkFilePath, ".proof.json"));
                }
                if (hasPublishedNetworkFile
                    && !string.IsNullOrWhiteSpace(record.NetworkPhotoPath)
                    && File.Exists(record.NetworkPhotoPath))
                {
                    File.Delete(record.NetworkPhotoPath);
                }
                DeleteOwnedSidecar(record.ProofFilePath, record.LocalFilePath);
                _db.MarkVideoDeletedById(
                    record.Id,
                    hasPublishedNetworkFile ? reason : $"{reason}（未完成网络归档）");
                RuntimeLog.Info("Storage", $"Deleted expired recording id={record.Id}, reason={reason}");
                return released > 0 ? released : Math.Max(1, record.FileSizeBytes);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Storage", $"Expired record cleanup skipped id={candidate.Id}: {ex.Message}");
                return 0;
            }
        }

        private static void DeleteOwnedSidecar(string proofPath, string localVideoPath)
        {
            if (string.IsNullOrWhiteSpace(proofPath) || string.IsNullOrWhiteSpace(localVideoPath)) return;
            string? proofDirectory = Path.GetDirectoryName(Path.GetFullPath(proofPath));
            string? videoDirectory = Path.GetDirectoryName(Path.GetFullPath(localVideoPath));
            if (string.Equals(proofDirectory, videoDirectory, StringComparison.OrdinalIgnoreCase))
                DeleteSidecar(proofPath);
        }

        private static void DeleteSidecar(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private void UpdateDiskUsageText(long totalCurrentBytes, long totalCapacityBytes)
        {
            double usedGB = totalCurrentBytes / (double)StorageSpacePolicy.BytesPerGiB;
            double capacityGB = totalCapacityBytes / (double)StorageSpacePolicy.BytesPerGiB;
            long effectiveCapacity = totalCapacityBytes;
            if (Config.EnableMaxStorageUsage && Config.MaxStorageUsageGB > 0)
            {
                effectiveCapacity = Math.Min(
                    effectiveCapacity,
                    (long)Math.Ceiling(Config.MaxStorageUsageGB * StorageSpacePolicy.BytesPerGiB));
            }

            StorageRetentionEstimate estimate = StorageRetentionPolicy.Estimate(
                _db.GetRangeStats(DateTime.Today.AddDays(-29), DateTime.Today),
                effectiveCapacity,
                Config.MaxRetentionDays);
            (int pendingCount, long pendingBytes, string archiveError) = _db.GetNetworkArchiveOverview();

            string estimateText = estimate.HasEnoughHistory
                ? $"，预计可保存 {estimate.EstimatedDays:F0} 天"
                : "，历史数据不足，暂无法估算";
            string warning = estimate.CannotMeetConfiguredDays
                ? $"当前目录可能无法达到预期存储时间，预计约 {estimate.EstimatedDays:F0} 天，当前设置 {Config.MaxRetentionDays} 天"
                : "";
            string archiveText = pendingCount > 0
                ? $"；待上传 {pendingCount} 条（{pendingBytes / 1024d / 1024d / 1024d:F1} GB）"
                : "";
            if (!string.IsNullOrWhiteSpace(archiveError))
                archiveText += $"；最近错误：{archiveError}";

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed) return;
                DiskUsagePercent = capacityGB > 0 ? Math.Min(100.0, usedGB / capacityGB * 100.0) : 0;
                DiskUsageText = $"{usedGB:F1} / {capacityGB:F1} GB{estimateText}{archiveText}";
                SuggestedRetentionDays = estimate.SuggestedDays;
                StorageRetentionWarningText = warning;
            });
        }

        private static readonly string[] _videoExtensions = [".mkv", ".mp4"];

        private static IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            if (!dir.Exists) yield break;
            foreach (FileInfo file in dir.EnumerateFiles("*.*", SearchOption.AllDirectories))
            {
                if (_videoExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                    yield return file;
                else if (file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                         && file.Name.EndsWith("_面单.jpg", StringComparison.OrdinalIgnoreCase))
                    yield return file;
                else if (file.Name.EndsWith(".proof.json", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }
}
