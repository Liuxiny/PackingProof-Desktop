using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingAudioPipelineTests
{
    [Fact]
    public void RecordingArguments_EncodePcmPipeAsAacLcInsideRecoverableMkv()
    {
        string args = MainViewModel.BuildFFmpegArgs(
            1920,
            1080,
            30,
            @"D:\recording.mkv",
            "h264_qsv",
            withAudio: true,
            videoCqp: 25,
            audioPipeName: "test-audio-pipe");

        Assert.Contains("-f s16le -ar 48000 -ac 1", args);
        Assert.Contains(@"\\.\pipe\test-audio-pipe", args);
        Assert.Contains("-map 0:v:0 -map 1:a:0", args);
        Assert.Contains("-c:a aac -profile:a aac_low -b:a 128k", args);
        Assert.DoesNotContain(".wav", args, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("recording.mkv\"", args);
    }

    [Fact]
    public void SilentRecordingArguments_DoNotDeclareAudioInput()
    {
        string args = MainViewModel.BuildFFmpegArgs(
            1280,
            720,
            25,
            @"D:\silent.mkv",
            "libx264",
            withAudio: false,
            videoCqp: 23);

        Assert.DoesNotContain("s16le", args);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void FinalMp4_StreamCopiesBothEmbeddedTracks()
    {
        string args = MainViewModel.BuildMkvToMp4Args(
            @"D:\source.mkv",
            null,
            @"D:\target.mp4",
            0);

        Assert.Contains("-map 0:v:0 -map 0:a? -c copy", args);
        Assert.DoesNotContain("-c:a aac", args);
    }
}
