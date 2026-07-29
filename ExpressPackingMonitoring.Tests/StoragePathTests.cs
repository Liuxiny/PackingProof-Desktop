using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StoragePathTests
{
    [Theory]
    [InlineData(@"D:\录像", @"D:\录像")]
    [InlineData(@"D:\录像\", @"D:\录像")]
    [InlineData(@"\\server\share\录像", @"\\server\share\录像")]
    [InlineData(@"  \\server\share\录像\  ", @"\\server\share\录像")]
    public void StoragePathSelection_NormalizesLocalAndUncPaths(string input, string expected)
    {
        bool success = StoragePathSelectionDialog.TryNormalizePath(
            input,
            out string normalizedPath,
            out string errorMessage);

        Assert.True(success, errorMessage);
        Assert.Equal(expected, normalizedPath, ignoreCase: true);
    }

    [Theory]
    [InlineData("recordings")]
    [InlineData("")]
    public void StoragePathSelection_RejectsNonAbsolutePaths(string input)
    {
        Assert.False(StoragePathSelectionDialog.TryNormalizePath(input, out _, out _));
    }

    [Fact]
    public void StoragePathComparison_IgnoresCaseAndTrailingSeparator()
    {
        Assert.True(SettingsWindow.AreSameStoragePath(@"D:\Recordings\", @"d:\recordings"));
        Assert.True(SettingsWindow.AreSameStoragePath(
            @"\\server\share\Recordings\",
            @"\\SERVER\SHARE\recordings"));
        Assert.False(SettingsWindow.AreSameStoragePath(@"D:\one", @"D:\two"));
    }

    [Fact]
    public void StorageVolumeInfo_ReadsCapacityForLocalFolder()
    {
        Assert.True(StorageVolumeInfo.TryGet(Path.GetTempPath(), out StorageVolumeInfo volume));
        Assert.True(volume.TotalSize > 0);
        Assert.True(volume.AvailableFreeSpace >= 0);
    }
}
