using ExpressPackingMonitoring.Config;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

public sealed record RecordingProfileVerificationResult(
    bool Passed,
    double ActualFps,
    double RequiredFps,
    double DroppedFrameRatio,
    string Message);

internal readonly record struct RecordingMediaProbeResult(bool Parseable, int Width, int Height, string Error);

internal static class RecordingProfileVerifier
{
    internal const double MinimumAchievementRatio = 0.90;

    internal static string BuildProfileKey(AppConfig config)
    {
        string camera = config.CameraMonikerString?.Trim() ?? "";
        string encoder = AppConfig.NormalizeVideoEncoder(config.VideoEncoder);
        return $"{camera}|{config.FrameWidth}x{config.FrameHeight}|{config.Fps}|{encoder}";
    }

    internal static RecordingProfileVerificationResult Evaluate(
        int targetFps,
        long encodedFrameCount,
        double durationSeconds,
        bool outputExists,
        int targetWidth = 0,
        int targetHeight = 0,
        RecordingMediaProbeResult? mediaProbe = null)
    {
        double safeDuration = Math.Max(0.001, durationSeconds);
        double actualFps = encodedFrameCount / safeDuration;
        double requiredFps = Math.Max(1, targetFps) * MinimumAchievementRatio;
        double expectedFrames = Math.Max(1, targetFps) * safeDuration;
        double droppedFrameRatio = Math.Clamp((expectedFrames - encodedFrameCount) / expectedFrames, 0, 1);
        bool mediaValid = mediaProbe == null
            || mediaProbe.Value.Parseable
               && (targetWidth <= 0 || mediaProbe.Value.Width == targetWidth)
               && (targetHeight <= 0 || mediaProbe.Value.Height == targetHeight);
        bool passed = outputExists
            && encodedFrameCount > 0
            && actualFps >= requiredFps
            && droppedFrameRatio <= 1 - MinimumAchievementRatio + 0.0001
            && mediaValid;
        string message = passed
            ? $"首次录像复检通过：实际 {actualFps:F1} FPS，目标 {targetFps} FPS，丢帧 {droppedFrameRatio:P1}"
            : BuildFailureMessage(actualFps, targetFps, droppedFrameRatio, targetWidth, targetHeight, mediaProbe);
        return new RecordingProfileVerificationResult(passed, actualFps, requiredFps, droppedFrameRatio, message);
    }

    internal static RecordingMediaProbeResult Probe(string ffmpegPath, string videoPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath) || !File.Exists(videoPath))
            return new RecordingMediaProbeResult(false, 0, 0, "录像文件或 FFmpeg 不存在");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string argument in new[] { "-hide_banner", "-i", videoPath, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "NUL" })
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process == null)
                return new RecordingMediaProbeResult(false, 0, 0, "无法启动 FFmpeg 校验");
            string error = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(); } catch { }
                return new RecordingMediaProbeResult(false, 0, 0, "FFmpeg 校验超时");
            }

            Match streamLine = Regex.Match(
                error,
                @"Stream[^\r\n]*Video:[^\r\n]*?\b(?<w>\d{2,5})x(?<h>\d{2,5})\b",
                RegexOptions.IgnoreCase);
            int width = 0;
            int height = 0;
            bool dimensionsFound = streamLine.Success
                && int.TryParse(streamLine.Groups["w"].Value, out width)
                && int.TryParse(streamLine.Groups["h"].Value, out height);
            bool parseable = process.ExitCode == 0 && dimensionsFound;
            return new RecordingMediaProbeResult(
                parseable,
                dimensionsFound ? width : 0,
                dimensionsFound ? height : 0,
                parseable ? "" : "录像无法解析或未找到视频流");
        }
        catch (Exception ex)
        {
            return new RecordingMediaProbeResult(false, 0, 0, ex.Message);
        }
    }

    private static string BuildFailureMessage(
        double actualFps,
        int targetFps,
        double droppedFrameRatio,
        int targetWidth,
        int targetHeight,
        RecordingMediaProbeResult? mediaProbe)
    {
        string mediaText = mediaProbe == null
            ? ""
            : mediaProbe.Value.Parseable
                ? $"，实际分辨率 {mediaProbe.Value.Width}x{mediaProbe.Value.Height}，目标 {targetWidth}x{targetHeight}"
                : $"，文件校验失败：{mediaProbe.Value.Error}";
        return $"当前配置未达到要求：实际 {actualFps:F1} FPS，目标 {targetFps} FPS，丢帧 {droppedFrameRatio:P1}{mediaText}（允许 10% 误差）";
    }
}
