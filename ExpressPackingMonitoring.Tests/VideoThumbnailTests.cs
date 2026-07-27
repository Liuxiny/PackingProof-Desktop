using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoThumbnailTests
{
    [Theory]
    [InlineData(10, 8)]
    [InlineData(60, 48)]
    [InlineData(1, 0.8)]
    public void WebThumbnailUsesFrameAtEightyPercent(double duration, double expected)
    {
        Assert.Equal(expected, VideoClipService.CalculateThumbnailSecond(duration), precision: 1);
    }
}
