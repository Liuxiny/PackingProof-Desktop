using OpenCvSharp;
using System.IO;
using System.Security.Cryptography;

namespace ExpressPackingMonitoring.Services;

internal sealed record StableSnapshotResult(
    Mat Frame,
    double Motion,
    double Sharpness,
    double Exposure) : IDisposable
{
    public void Dispose() => Frame.Dispose();
}

internal sealed record SavedRecordingSnapshot(
    string FilePath,
    DateTime CapturedAt,
    int Width,
    int Height,
    long FileSizeBytes,
    string Sha256);

internal sealed class StableFrameSelector : IDisposable
{
    private const int SampleWidth = 320;
    private const int SampleHeight = 180;
    private readonly Mat _sample = new();
    private readonly Mat _gray = new();
    private readonly Mat _previousGray = new();
    private readonly Mat _difference = new();
    private readonly Mat _laplacian = new();
    private bool _hasPrevious;
    private int _stableCount;
    private Mat? _bestFrame;
    private double _bestSharpness;

    public bool TryAdd(Mat frame, out StableSnapshotResult? result)
    {
        result = null;
        if (frame == null || frame.IsDisposed || frame.Empty())
            return false;

        Cv2.Resize(frame, _sample, new OpenCvSharp.Size(SampleWidth, SampleHeight), interpolation: InterpolationFlags.Area);
        if (_sample.Channels() == 1)
            _sample.CopyTo(_gray);
        else
            Cv2.CvtColor(_sample, _gray, _sample.Channels() == 4
                ? ColorConversionCodes.BGRA2GRAY
                : ColorConversionCodes.BGR2GRAY);

        double exposure = Cv2.Mean(_gray).Val0;
        Cv2.Laplacian(_gray, _laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(_laplacian, out _, out Scalar deviation);
        double sharpness = deviation.Val0 * deviation.Val0;
        double motion = double.MaxValue;
        if (_hasPrevious)
        {
            Cv2.Absdiff(_gray, _previousGray, _difference);
            motion = Cv2.Mean(_difference).Val0 / 255d;
        }
        _gray.CopyTo(_previousGray);
        _hasPrevious = true;

        bool exposureUsable = exposure >= 25 && exposure <= 235;
        bool stable = motion <= 0.018 && exposureUsable && sharpness >= 12;
        _stableCount = stable ? _stableCount + 1 : 0;
        if (stable && sharpness >= _bestSharpness)
        {
            _bestFrame?.Dispose();
            _bestFrame = frame.Clone();
            _bestSharpness = sharpness;
        }

        if (_stableCount < 3 || _bestFrame == null)
            return false;

        Mat selected = _bestFrame;
        _bestFrame = null;
        result = new StableSnapshotResult(selected, motion, sharpness, exposure);
        return true;
    }

    public void Dispose()
    {
        _bestFrame?.Dispose();
        _sample.Dispose();
        _gray.Dispose();
        _previousGray.Dispose();
        _difference.Dispose();
        _laplacian.Dispose();
    }
}

internal static class RecordingSnapshotService
{
    public static async Task<StableSnapshotResult> WaitForStableFrameAsync(
        Func<Mat?> frameProvider,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frameProvider);
        using var selector = new StableFrameSelector();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();
                using Mat? frame = frameProvider();
                if (frame != null && selector.TryAdd(frame, out StableSnapshotResult? result) && result != null)
                    return result;
                await Task.Delay(80, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("画面在限定时间内未达到稳定和清晰要求");
        }
    }

    public static async Task<SavedRecordingSnapshot> SaveAtomicJpegAsync(
        Mat frame,
        string destinationPath,
        DateTime capturedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        string? directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("照片保存目录无效");
        Directory.CreateDirectory(directory);

        string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.writing";
        try
        {
            Cv2.ImEncode(".jpg", frame, out byte[] jpeg, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 95)]);
            if (jpeg.Length == 0)
                throw new IOException("照片 JPEG 编码失败");
            await File.WriteAllBytesAsync(temporaryPath, jpeg, cancellationToken).ConfigureAwait(false);
            byte[] digest;
            await using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            {
                digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, destinationPath, overwrite: false);
            return new SavedRecordingSnapshot(
                destinationPath,
                capturedAt,
                frame.Width,
                frame.Height,
                jpeg.LongLength,
                Convert.ToHexString(digest).ToLowerInvariant());
        }
        catch
        {
            // The GUID-scoped temporary file is owned by this invocation. Never touch the
            // published destination when another invocation won the race.
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            throw;
        }
    }
}
