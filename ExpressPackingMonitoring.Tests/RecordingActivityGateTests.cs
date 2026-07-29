using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingActivityGateTests
{
    [Fact]
    public void NewRecordingActivity_PreemptsExistingIdleWorkAndResetsIdleWindow()
    {
        using var gate = new RecordingActivityGate();
        CancellationToken workToken = gate.GetPreemptionToken();
        DateTimeOffset before = gate.LastActivityAt;

        gate.SignalActivity();

        Assert.True(workToken.IsCancellationRequested);
        Assert.False(gate.IsContinuouslyIdleFor(TimeSpan.FromSeconds(60), gate.LastActivityAt.AddSeconds(59)));
        Assert.True(gate.IsContinuouslyIdleFor(TimeSpan.FromSeconds(60), gate.LastActivityAt.AddSeconds(60)));
        Assert.True(gate.LastActivityAt >= before);
    }

    [Fact]
    public void SnapshotOrModeSwitchLease_BlocksIdleWorkerUntilOperationEnds()
    {
        using var gate = new RecordingActivityGate();
        IDisposable activity = gate.BeginActivity();
        Assert.True(gate.HasActiveOperation);
        Assert.False(gate.IsContinuouslyIdleFor(TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(1)));

        activity.Dispose();

        Assert.False(gate.HasActiveOperation);
        Assert.True(gate.IsContinuouslyIdleFor(TimeSpan.Zero, DateTimeOffset.UtcNow.AddMinutes(1)));
    }
}
