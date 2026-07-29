using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Config;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private static string QueryFFmpegEncoders(string ffmpegPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return "";
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                if (!WaitForEncoderProbeExit(proc, 5000))
                {
                    RuntimeLog.Warn("EncoderDetect", "ffmpeg -encoders timed out");
                    return "";
                }

                string output = ReadProbeOutput(stdoutTask);
                string stderr = ReadProbeOutput(stderrTask);
                if (proc.ExitCode != 0)
                    RuntimeLog.Warn("EncoderDetect", $"ffmpeg -encoders failed exit={proc.ExitCode}, stderr={stderr}");
                return output;
            }
            catch { return ""; }
        }

        public void ResetEncoderDetect()
        {
            if (_isEncoderDetectRunning)
            {
                ShowToast("处理中：检测已在运行中...");
                return;
            }

            Task.Run(() =>
            {
                _isEncoderDetectRunning = true;
                try
                {
                    ShowToast("处理中：正在重新检测 GPU 编码器，请稍候...");

                    var (options, validated) = DetectAvailableEncodersSync();
                    CachedEncoderOptions = options;
                    ValidatedEncoders = validated;

                    Config.EncoderOptionsCache = options;
                    Config.ValidatedEncodersCache = validated.ToList();
                    Config.IsEncoderDetected = true;
                    SaveConfig();
                    ShowToast("成功：编码器重新检测完成");
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("EncoderDetect", "Manual encoder detection failed", ex);
                    ShowToast("编码器检测失败，已保留现有设置");
                }
                finally
                {
                    _isEncoderDetectRunning = false;
                }
            });
        }

        private static (bool ok, string stderr) TestEncoder(string ffmpegPath, string encoder)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f lavfi -i color=black:s=256x256:d=0.1 -frames:v 2 -an -pix_fmt yuv420p -c:v {encoder} -f null -",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Process.Start returned null");
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                bool exited = WaitForEncoderProbeExit(proc, 15000);
                string stderr = ReadProbeOutput(stderrTask);
                int exitCode = exited ? proc.ExitCode : -999;
                return (exited && exitCode == 0, $"exit={exitCode} stderr={stderr}");
            }
            catch (Exception ex) { return (false, $"exception: {ex.Message}"); }
        }

        private static bool WaitForEncoderProbeExit(Process process, int timeoutMs)
        {
            if (process.WaitForExit(timeoutMs))
                return true;

            try { process.Kill(entireProcessTree: true); }
            catch { }
            try { process.WaitForExit(3000); }
            catch { }
            return false;
        }

        private static string ReadProbeOutput(Task<string> outputTask)
        {
            try
            {
                return outputTask.Wait(TimeSpan.FromSeconds(3)) ? outputTask.Result ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        public static (List<GpuEncoderOption> options, HashSet<string> validated) DetectAvailableEncodersSync()
        {
            var log = new StringBuilder();
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === GPU 编码器检测开始 ===");

            var list = new List<GpuEncoderOption>
            {
                new GpuEncoderOption { Value = "auto", DisplayName = "自动选择（根据本机实测）" }
            };
            var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string ffmpegPath = AppPaths.FindFFmpeg();
            log.AppendLine($"FFmpeg 路径: {ffmpegPath}");
            log.AppendLine($"FFmpeg 存在: {!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath)}");

            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                log.AppendLine("FFmpeg 不存在，跳过检测");
                WriteEncoderLog(log);
                return (list, validated);
            }

            string output = QueryFFmpegEncoders(ffmpegPath);
            log.AppendLine($"ffmpeg -encoders 输出长度: {output.Length}");

            string[] candidates =
            {
                "h264_qsv", "hevc_qsv", "av1_qsv",
                "h264_nvenc", "hevc_nvenc", "av1_nvenc",
                "h264_amf", "hevc_amf", "av1_amf",
                "libx264", "libx265", "libsvtav1"
            };

            foreach (string encoder in candidates)
            {
                log.AppendLine($"\n=== {EncodingHelper.GetEncoderLabel(encoder)} ===");
                bool inList = output.Contains(encoder, StringComparison.OrdinalIgnoreCase);
                log.AppendLine($"  ffmpeg -encoders 包含: {inList}");
                if (!inList)
                {
                    log.AppendLine("  跳过试编码（不在编码器列表中）");
                    continue;
                }

                var (testOk, testDetail) = TestEncoder(ffmpegPath, encoder);
                log.AppendLine($"  试编码结果: {(testOk ? "✓ 通过" : "✗ 失败")}");
                log.AppendLine($"  详情: {testDetail}");
                if (testOk)
                {
                    validated.Add(encoder);
                    list.Add(new GpuEncoderOption
                    {
                        Value = encoder,
                        DisplayName = EncodingHelper.GetEncoderLabel(encoder)
                    });
                }
            }

            log.AppendLine($"\n编码器选项: {string.Join(", ", list.Select(e => e.Value))}");
            log.AppendLine($"已验证编码器: {string.Join(", ", validated)}");
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 检测结束 ===");
            WriteEncoderLog(log);
            return (list, validated);
        }

        private static void WriteEncoderLog(StringBuilder log)
        {
            try
            {
                string logPath = AppPaths.EncoderDetectLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
