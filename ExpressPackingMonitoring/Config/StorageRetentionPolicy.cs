using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Config;

internal static class StorageRetentionPolicy
{
    public const int MinimumRetentionDays = 15;
    public const int DefaultRetentionDays = 90;

    public static int NormalizeRetentionDays(int days) => Math.Max(MinimumRetentionDays, days);

    public static DateTime GetGlobalCutoffDate(DateTime today, int retentionDays) =>
        today.Date.AddDays(-(NormalizeRetentionDays(retentionDays) - 1));

    public static DateTime GetArchivedLocalCopyCutoffDate(DateTime today) =>
        today.Date.AddDays(-1);

    public static bool IsExpiredByDate(VideoRecord record, DateTime today, int retentionDays) =>
        record.StartTime.Date < GetGlobalCutoffDate(today, retentionDays);

    public static bool IsArchivedLocalCopyExpired(VideoRecord record, DateTime today) =>
        record.ArchiveStatus == VideoArchiveStatus.Verified
        && record.StartTime.Date < GetArchivedLocalCopyCutoffDate(today);

    public static StorageRetentionEstimate Estimate(
        IEnumerable<DailyStat> dailyStats,
        long usableCapacityBytes,
        int configuredDays)
    {
        List<DailyStat> stats = dailyStats?
            .Where(item => item.TotalBytes > 0 && DateTime.TryParse(item.Date, out _))
            .OrderBy(item => item.Date, StringComparer.Ordinal)
            .ToList() ?? [];

        if (stats.Count == 0 || usableCapacityBytes <= 0)
            return new StorageRetentionEstimate(false, 0, 0, Math.Max(MinimumRetentionDays, configuredDays), false);

        DateTime first = DateTime.Parse(stats[0].Date).Date;
        DateTime last = DateTime.Parse(stats[^1].Date).Date;
        int calendarDays = Math.Max(1, (last - first).Days + 1);
        long totalBytes = stats.Sum(item => Math.Max(0, item.TotalBytes));
        double dailyBytes = totalBytes / (double)calendarDays;
        if (dailyBytes <= 0)
            return new StorageRetentionEstimate(false, 0, 0, Math.Max(MinimumRetentionDays, configuredDays), false);

        double estimatedDays = usableCapacityBytes / dailyBytes;
        int suggestedDays = Math.Max(MinimumRetentionDays, (int)Math.Floor(estimatedDays));
        return new StorageRetentionEstimate(
            true,
            dailyBytes,
            estimatedDays,
            suggestedDays,
            estimatedDays + 0.001 < configuredDays);
    }
}

internal readonly record struct StorageRetentionEstimate(
    bool HasEnoughHistory,
    double AverageBytesPerDay,
    double EstimatedDays,
    int SuggestedDays,
    bool CannotMeetConfiguredDays);
