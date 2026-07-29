using ExpressPackingMonitoring.Data;
using System.Collections.Concurrent;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal static class VideoFileResolver
{
    private static readonly ConcurrentDictionary<string, AvailabilityState> Availability =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    public static string Resolve(VideoRecord record)
    {
        if (record == null) return "";

        string networkPath = record.NetworkFilePath?.Trim() ?? "";
        bool networkPublished = record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted;
        if (networkPublished && networkPath.Length > 0)
        {
            if (!StorageLocationResolver.IsNetworkPath(networkPath))
            {
                if (File.Exists(networkPath)) return networkPath;
            }
            else if (TryUseCachedNetworkPath(networkPath))
            {
                return networkPath;
            }
        }

        string localPath = record.LocalFilePath?.Trim() ?? "";
        if (localPath.Length > 0 && File.Exists(localPath))
            return localPath;

        if (networkPublished && networkPath.Length > 0 && !StorageLocationResolver.IsNetworkPath(networkPath))
            return networkPath;

        return record.FilePath?.Trim() ?? "";
    }

    public static void MarkNetworkPathAvailable(string path)
    {
        string root = GetAvailabilityKey(path);
        if (root.Length > 0)
            Availability[root] = new AvailabilityState(true, DateTime.UtcNow);
    }

    private static bool TryUseCachedNetworkPath(string path)
    {
        string key = GetAvailabilityKey(path);
        if (key.Length == 0) return false;

        if (Availability.TryGetValue(key, out AvailabilityState state)
            && DateTime.UtcNow - state.CheckedAtUtc <= CacheLifetime)
        {
            return state.IsAvailable;
        }

        _ = ProbeAsync(key, path);
        return state.IsAvailable && DateTime.UtcNow - state.CheckedAtUtc <= TimeSpan.FromMinutes(1);
    }

    private static async Task ProbeAsync(string key, string path)
    {
        try
        {
            bool exists = await Task.Run(() => File.Exists(path)).ConfigureAwait(false);
            Availability[key] = new AvailabilityState(exists, DateTime.UtcNow);
        }
        catch
        {
            Availability[key] = new AvailabilityState(false, DateTime.UtcNow);
        }
    }

    private static string GetAvailabilityKey(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : path;
            }
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        }
        catch
        {
            return path?.Trim() ?? "";
        }
    }

    private readonly record struct AvailabilityState(bool IsAvailable, DateTime CheckedAtUtc);
}
