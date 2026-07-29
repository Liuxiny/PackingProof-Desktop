using ExpressPackingMonitoring.UI;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackWindowTests
{
    [Fact]
    public void PhotoThumbnailCache_IsBoundedAndSupportsCancellation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"PackingProofThumbTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var cache = new PhotoThumbnailCache(10);
            for (int i = 0; i < 18; i++)
            {
                string path = Path.Combine(directory, $"{i}.jpg");
                using (var bitmap = new Bitmap(32, 24))
                    bitmap.Save(path, ImageFormat.Jpeg);
                Assert.NotNull(cache.Get(path, TestContext.Current.CancellationToken));
            }
            Assert.InRange(cache.Count, 1, 10);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => cache.Get(Path.Combine(directory, "0.jpg"), cancellation.Token));
            cache.Clear();
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(3, 3, false, true)]
    [InlineData(2, 3, false, false)]
    [InlineData(3, 3, true, false)]
    public void IsCurrentLoadRequest_AcceptsOnlyLatestOpenWindowRequest(
        int requestVersion,
        int currentVersion,
        bool isClosing,
        bool expected)
    {
        Assert.Equal(expected, PlaybackWindow.IsCurrentLoadRequest(requestVersion, currentVersion, isClosing));
    }

    [Fact]
    public void GetOrderDisplayName_PrefersTrackingNumber()
    {
        string result = PlaybackWindow.GetOrderDisplayName(
            "YT123456789012",
            "ORDER-OLD",
            "FILE-NAME_20260723_发货.mp4");

        Assert.Equal("YT123456789012", result);
    }

    [Fact]
    public void GetOrderDisplayName_FallsBackToOrderId()
    {
        string result = PlaybackWindow.GetOrderDisplayName(
            "",
            "SF123456789012",
            "FILE-NAME_20260723_发货.mp4");

        Assert.Equal("SF123456789012", result);
    }

    [Theory]
    [InlineData("JD123456789012_20260723_120000_发货.mp4", "JD123456789012")]
    [InlineData("YT123456789012.mkv", "YT123456789012")]
    [InlineData("", "未识别面单")]
    public void GetOrderDisplayName_ExtractsFileSystemFallback(string fileName, string expected)
    {
        Assert.Equal(expected, PlaybackWindow.GetOrderDisplayName("", "", fileName));
    }

    [Theory]
    [InlineData("external", "android-1234567890a1b2c3", "手机1", "来源：手机1")]
    [InlineData("EXTERNAL", "", "", "来源：手机设备")]
    [InlineData("external", "", "一号打包手机", "来源：一号打包手机")]
    [InlineData("pc", "pc-1", "一号电脑", "来源：电脑")]
    [InlineData("", "", "", "来源：电脑")]
    public void GetSourceDisplay_UsesBackupDeviceIdentity(
        string sourceType,
        string sourceDeviceId,
        string sourceDeviceName,
        string expected)
    {
        Assert.Equal(
            expected,
            PlaybackWindow.GetSourceDisplay(sourceType, sourceDeviceId, sourceDeviceName));
    }

    [Theory]
    [InlineData("external", "APP 备份", "")]
    [InlineData("external", "APP备份", "")]
    [InlineData("external", "上传完成", "上传完成")]
    [InlineData("pc", "扫码枪停止", "扫码枪停止")]
    public void GetStopReasonDisplay_HidesDuplicatedBackupLabel(
        string sourceType,
        string stopReason,
        string expected)
    {
        Assert.Equal(expected, PlaybackWindow.GetStopReasonDisplay(sourceType, stopReason));
    }
}
