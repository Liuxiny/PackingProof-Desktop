namespace ExpressPackingMonitoring.Data
{
    public sealed class MkvConversionFailureState
    {
        public string FilePath { get; init; } = "";
        public DateTime? FirstFailedAt { get; init; }
        public DateTime? LastAttemptAt { get; init; }
        public int FailureCount { get; init; }
        public string LastError { get; init; } = "";
        public DateTime? LastNotifiedAt { get; init; }
    }

    public enum MkvAutomaticRetryDecision
    {
        Retry,
        Deferred,
        Suppressed
    }

    public static class MkvConversionRetryPolicy
    {
        public static readonly TimeSpan AggressiveRetryPeriod = TimeSpan.FromHours(24);
        public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(24);
        public static readonly TimeSpan AutomaticRetryLifetime = TimeSpan.FromDays(7);

        public static MkvAutomaticRetryDecision GetAutomaticRetryDecision(
            MkvConversionFailureState? state,
            DateTime now)
        {
            if (state?.FirstFailedAt == null)
                return MkvAutomaticRetryDecision.Retry;

            TimeSpan failureAge = now - state.FirstFailedAt.Value;
            if (failureAge > AutomaticRetryLifetime)
                return MkvAutomaticRetryDecision.Suppressed;

            if (failureAge <= AggressiveRetryPeriod || state.LastAttemptAt == null)
                return MkvAutomaticRetryDecision.Retry;

            return now - state.LastAttemptAt.Value >= RetryInterval
                ? MkvAutomaticRetryDecision.Retry
                : MkvAutomaticRetryDecision.Deferred;
        }

        public static bool ShouldNotify(MkvConversionFailureState? state, DateTime now)
        {
            if (state?.FirstFailedAt == null
                || now - state.FirstFailedAt.Value > AutomaticRetryLifetime)
            {
                return false;
            }

            return state.LastNotifiedAt == null
                || state.LastNotifiedAt.Value.Date < now.Date;
        }
    }

    public sealed class MkvBatchConversionResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public int SkippedCount { get; set; }
        public int DeferredCount { get; set; }
        public int SuppressedCount { get; set; }
        public int NotificationCount { get; set; }
        public bool ShouldNotify => NotificationCount > 0;
    }
}
