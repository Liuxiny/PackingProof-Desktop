using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services;

public sealed record CameraRecordingMode(int Width, int Height, int Fps);

public sealed record RecordingPerformanceRequest(
    string CameraMoniker,
    int Width,
    int Height,
    int Fps,
    string Encoder,
    IReadOnlyList<CameraRecordingMode> AvailableModes);

public sealed record RecordingPerformanceResult(
    bool Success,
    bool MeetsTarget,
    double InputFps,
    double EncodedFps,
    double DropPercent,
    double CpuPercent,
    int RecommendedWidth,
    int RecommendedHeight,
    int RecommendedFps,
    string RecommendedEncoder,
    string Summary,
    string Error);

internal sealed class RecordingPerformanceService
{
    private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(5);

    public async Task<RecordingPerformanceResult> RunAsync(
        RecordingPerformanceRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CameraMoniker))
            return Failure(request, "未选择摄像头");
        if (request.Width <= 0 || request.Height <= 0 || request.Fps <= 0)
            return Failure(request, "分辨率或帧率无效");
        if (string.IsNullOrWhiteSpace(request.Encoder) || request.Encoder == "auto")
            return Failure(request, "性能检测需要一个已解析的实际编码器");

        string ffmpegPath = AppPaths.FindFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            return Failure(request, "未找到 FFmpeg");

        Directory.CreateDirectory(AppPaths.CacheDir);
        string outputPath = Path.Combine(AppPaths.CacheDir, $"performance-{Guid.NewGuid():N}.mkv");
        var queue = new BlockingCollection<byte[]>(boundedCapacity: 3);
        VideoCaptureDevice? camera = null;
        Process? process = null;
        Task<string>? stderrTask = null;
        Task? writerTask = null;
        long capturedFrames = 0;
        long queuedFrames = 0;
        long droppedFrames = 0;
        DateTime startedAt = DateTime.UtcNow;
        TimeSpan cpuStarted = Process.GetCurrentProcess().TotalProcessorTime;

        try
        {
            process = StartEncoder(ffmpegPath, request, outputPath);
            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            writerTask = Task.Run(async () =>
            {
                await using Stream stdin = process.StandardInput.BaseStream;
                foreach (byte[] frame in queue.GetConsumingEnumerable(cancellationToken))
                    await stdin.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            camera = new VideoCaptureDevice(request.CameraMoniker);
            VideoCapabilities? capability = camera.VideoCapabilities.FirstOrDefault(item =>
                item.FrameSize.Width == request.Width
                && item.FrameSize.Height == request.Height
                && NormalizeFps(item.AverageFrameRate) == request.Fps);
            if (capability == null)
                return Failure(request, "摄像头驱动不支持当前分辨率和帧率组合");

            camera.VideoResolution = capability;
            camera.NewFrame += (_, args) =>
            {
                Interlocked.Increment(ref capturedFrames);
                try
                {
                    byte[] bytes = ConvertToBgr24(args.Frame, request.Width, request.Height);
                    if (queue.TryAdd(bytes))
                        Interlocked.Increment(ref queuedFrames);
                    else
                        Interlocked.Increment(ref droppedFrames);
                }
                catch
                {
                    Interlocked.Increment(ref droppedFrames);
                }
            };

            startedAt = DateTime.UtcNow;
            cpuStarted = Process.GetCurrentProcess().TotalProcessorTime;
            camera.Start();
            await Task.Delay(TestDuration, cancellationToken).ConfigureAwait(false);
            camera.SignalToStop();
            camera.WaitForStop();
            queue.CompleteAdding();
            if (writerTask != null)
                await writerTask.ConfigureAwait(false);

            using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exitCts.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            string stderr = stderrTask == null ? "" : await stderrTask.ConfigureAwait(false);

            double elapsedSeconds = Math.Max(0.1, (DateTime.UtcNow - startedAt).TotalSeconds);
            double inputFps = capturedFrames / elapsedSeconds;
            double encodedFps = queuedFrames / elapsedSeconds;
            double dropPercent = capturedFrames <= 0 ? 100 : droppedFrames * 100.0 / capturedFrames;
            TimeSpan cpuElapsed = Process.GetCurrentProcess().TotalProcessorTime - cpuStarted;
            double cpuPercent = cpuElapsed.TotalSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100.0;
            bool outputOk = process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;

            return Evaluate(
                request,
                inputFps,
                encodedFps,
                dropPercent,
                cpuPercent,
                outputOk,
                outputOk ? "" : TrimError(stderr));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request, "性能检测已取消");
        }
        catch (Exception ex)
        {
            return Failure(request, ex.Message);
        }
        finally
        {
            try
            {
                if (camera?.IsRunning == true)
                {
                    camera.SignalToStop();
                    camera.WaitForStop();
                }
            }
            catch { }
            try { queue.CompleteAdding(); } catch { }
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            process?.Dispose();
            queue.Dispose();
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    internal static RecordingPerformanceResult Evaluate(
        RecordingPerformanceRequest request,
        double inputFps,
        double encodedFps,
        double dropPercent,
        double cpuPercent,
        bool outputOk,
        string error)
    {
        bool captureMeets = inputFps >= request.Fps * 0.9;
        bool encodeMeets = encodedFps >= request.Fps * 0.9;
        bool dropsMeet = dropPercent <= 10.0;
        bool meets = outputOk && captureMeets && encodeMeets && dropsMeet;

        int recommendedWidth = request.Width;
        int recommendedHeight = request.Height;
        int recommendedFps = request.Fps;
        if (!captureMeets)
        {
            int measuredLimit = Math.Max(1, (int)Math.Floor(inputFps));
            CameraRecordingMode? sameResolution = request.AvailableModes
                .Where(mode => mode.Width == request.Width && mode.Height == request.Height && mode.Fps <= measuredLimit)
                .OrderByDescending(mode => mode.Fps)
                .FirstOrDefault();
            if (sameResolution != null)
                recommendedFps = sameResolution.Fps;
        }

        if ((!encodeMeets || cpuPercent >= 90) && request.AvailableModes.Count > 0)
        {
            CameraRecordingMode? lowerMode = request.AvailableModes
                .Where(mode => mode.Width * (long)mode.Height < request.Width * (long)request.Height
                    && mode.Fps <= recommendedFps)
                .OrderByDescending(mode => mode.Width * (long)mode.Height)
                .ThenByDescending(mode => mode.Fps)
                .FirstOrDefault();
            if (lowerMode != null)
            {
                recommendedWidth = lowerMode.Width;
                recommendedHeight = lowerMode.Height;
                recommendedFps = lowerMode.Fps;
            }
        }

        string bottleneck = !captureMeets
            ? "摄像头输入不足，请检查光线、自动曝光、驱动或 USB 带宽"
            : !encodeMeets || !dropsMeet
                ? "编码吞吐不足，建议降低分辨率或帧率"
                : "当前配置达到目标";
        string summary = $"输入 {inputFps:F1} FPS，编码 {encodedFps:F1} FPS，丢帧 {dropPercent:F1}%，CPU {cpuPercent:F0}%：{bottleneck}";
        return new RecordingPerformanceResult(
            outputOk,
            meets,
            inputFps,
            encodedFps,
            dropPercent,
            cpuPercent,
            recommendedWidth,
            recommendedHeight,
            Math.Max(1, recommendedFps),
            request.Encoder,
            summary,
            error);
    }

    private static Process StartEncoder(string ffmpegPath, RecordingPerformanceRequest request, string outputPath)
    {
        string encoderOptions = request.Encoder switch
        {
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => "-preset p4 -cq 30",
            "h264_qsv" or "hevc_qsv" or "av1_qsv" => "-global_quality 30",
            "h264_amf" or "hevc_amf" or "av1_amf" => "-quality balanced -qp_i 30 -qp_p 30",
            "libx264" or "libx265" => "-preset ultrafast -crf 30",
            "libsvtav1" => "-preset 12 -crf 35",
            _ => ""
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -f rawvideo -video_size {request.Width}x{request.Height} -pixel_format bgr24 -framerate {request.Fps} -i pipe:0 -an -c:v {request.Encoder} {encoderOptions} -pix_fmt yuv420p \"{outputPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        return Process.Start(startInfo) ?? throw new IOException("无法启动 FFmpeg 性能检测进程");
    }

    private static byte[] ConvertToBgr24(Bitmap source, int width, int height)
    {
        using var converted = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(converted))
            graphics.DrawImage(source, 0, 0, width, height);

        Rectangle rect = new(0, 0, width, height);
        BitmapData data = converted.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = checked(width * 3);
            byte[] result = new byte[checked(rowBytes * height)];
            for (int y = 0; y < height; y++)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(row, result, y * rowBytes, rowBytes);
            }
            return result;
        }
        finally
        {
            converted.UnlockBits(data);
        }
    }

    private static int NormalizeFps(int fps) => fps <= 0 ? 1 : fps;

    private static string TrimError(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "FFmpeg 性能检测失败";
        value = value.Trim();
        return value.Length <= 500 ? value : value[^500..];
    }

    private static RecordingPerformanceResult Failure(RecordingPerformanceRequest request, string error) =>
        new(false, false, 0, 0, 100, 0, request.Width, request.Height, request.Fps, request.Encoder, "性能检测失败", error);
}
