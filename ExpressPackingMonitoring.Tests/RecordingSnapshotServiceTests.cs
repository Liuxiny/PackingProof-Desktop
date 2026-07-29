using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingSnapshotServiceTests
{
    [Fact]
    public void StableFrameSelector_SelectsFullUncroppedFrameAfterThreeStableComparisons()
    {
        using Mat frame = CreateDetailedFrame(1920, 1080);
        using var selector = new StableFrameSelector();

        Assert.False(selector.TryAdd(frame, out _));
        Assert.False(selector.TryAdd(frame, out _));
        Assert.False(selector.TryAdd(frame, out _));
        Assert.True(selector.TryAdd(frame, out StableSnapshotResult? result));
        using (result)
        {
            Assert.NotNull(result);
            Assert.Equal(1920, result.Frame.Width);
            Assert.Equal(1080, result.Frame.Height);
            Assert.True(result.Sharpness >= 12);
        }
    }

    [Fact]
    public void StableFrameSelector_ResetsWhenSceneMoves()
    {
        using Mat first = CreateDetailedFrame(1280, 720);
        using Mat moved = first.Clone();
        Cv2.Rectangle(moved, new Rect(0, 0, 500, 500), Scalar.White, -1);
        using var selector = new StableFrameSelector();

        selector.TryAdd(first, out _);
        selector.TryAdd(first, out _);
        Assert.False(selector.TryAdd(moved, out _));
        Assert.False(selector.TryAdd(moved, out _));
        Assert.False(selector.TryAdd(moved, out _));
        Assert.True(selector.TryAdd(moved, out StableSnapshotResult? result));
        result?.Dispose();
    }

    [Fact]
    public async Task AtomicPhotoPublish_FailedCompetingTaskPreservesPublishedTarget()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"RecordingSnapshotTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "TRACK_面单.jpg");
            using Mat first = CreateDetailedFrame(640, 480);
            SavedRecordingSnapshot published = await RecordingSnapshotService.SaveAtomicJpegAsync(
                first, path, DateTime.Now, TestContext.Current.CancellationToken);
            byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            using Mat competing = new(480, 640, MatType.CV_8UC3, Scalar.Black);
            await Assert.ThrowsAsync<IOException>(() => RecordingSnapshotService.SaveAtomicJpegAsync(
                competing, path, DateTime.Now, TestContext.Current.CancellationToken));

            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(original.LongLength, published.FileSizeBytes);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.writing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Mat CreateDetailedFrame(int width, int height)
    {
        var frame = new Mat(height, width, MatType.CV_8UC3, new Scalar(120, 120, 120));
        for (int x = 0; x < width; x += 24)
            Cv2.Line(frame, new Point(x, 0), new Point(x, height - 1), Scalar.Black, 2);
        for (int y = 0; y < height; y += 24)
            Cv2.Line(frame, new Point(0, y), new Point(width - 1, y), Scalar.White, 2);
        return frame;
    }
}
