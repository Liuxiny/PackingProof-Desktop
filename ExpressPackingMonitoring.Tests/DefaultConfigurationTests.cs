using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Helpers;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DefaultConfigurationTests
{
    [Fact]
    public void AppConfig_EnablesAutoStartForNewConfiguration()
    {
        Assert.True(new AppConfig().AutoStartOnBoot);
        Assert.True(JsonSerializer.Deserialize<AppConfig>("{}")!.AutoStartOnBoot);
    }

    [Fact]
    public void AppConfig_PreservesExplicitlyDisabledAutoStart()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{\"AutoStartOnBoot\":false}")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.AutoStartOnBoot);
    }

    [Fact]
    public void AppConfig_UsesDateRetentionAndUnlimitedCapacityByDefault()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{}")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(90, config.MaxRetentionDays);
        Assert.False(config.EnableMaxStorageUsage);
        Assert.Equal(0, config.MaxStorageUsageGB);
        Assert.Equal(60, config.MaxDurationSeconds);
        Assert.Equal("auto", config.VideoEncoder);
    }

    [Fact]
    public void NormalizeAfterLoad_ClampsRetentionMinimumWithoutMaximum()
    {
        var tooShort = new AppConfig { MaxRetentionDays = 1 };
        var veryLong = new AppConfig { MaxRetentionDays = 50000 };

        AppConfig.NormalizeAfterLoad(tooShort);
        AppConfig.NormalizeAfterLoad(veryLong);

        Assert.Equal(15, tooShort.MaxRetentionDays);
        Assert.Equal(50000, veryLong.MaxRetentionDays);
    }

    [Fact]
    public void NormalizeAfterLoad_MigratesEnabledLegacyDurationAndEncoder()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>(
            "{\"EnableMaxDuration\":true,\"MaxDurationMinutes\":5,\"GpuEncoder\":\"intel\",\"VideoCodec\":\"h265\"}")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(300, config.MaxDurationSeconds);
        Assert.Equal("hevc_qsv", config.VideoEncoder);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(60, 60)]
    [InlineData(601, 600)]
    public void NormalizeAfterLoad_ClampsMaximumDurationSeconds(int requested, int expected)
    {
        var config = new AppConfig { MaxDurationSeconds = requested };
        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal(expected, config.MaxDurationSeconds);
    }

    [Fact]
    public void FixedEncoderNeverAllowsAutomaticFallback()
    {
        Assert.True(EncodingHelper.AllowsAutomaticFallback(new AppConfig { VideoEncoder = "auto" }));
        Assert.False(EncodingHelper.AllowsAutomaticFallback(new AppConfig { VideoEncoder = "h264_qsv" }));
        Assert.False(EncodingHelper.AllowsAutomaticFallback(new AppConfig { VideoEncoder = "libx265" }));
    }

    [Fact]
    public void AppConfig_HidesAdvancedSettingsForLegacyConfiguration()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>(
            "{\"VideoCqp\":19,\"ScannerAutoSubmitQuietMs\":345}")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.ShowAdvancedSettings);
        Assert.Equal(19, config.VideoCqp);
        Assert.Equal(345, config.ScannerAutoSubmitQuietMs);
    }

    [Fact]
    public void AppConfig_PreservesAdvancedSettingsVisibilityAndValuesDuringRoundTrip()
    {
        var original = new AppConfig
        {
            ShowAdvancedSettings = true,
            VideoCqp = 22,
            ScannerAutoSubmitQuietMs = 310
        };

        AppConfig restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(original))!;

        Assert.True(restored.ShowAdvancedSettings);
        Assert.Equal(22, restored.VideoCqp);
        Assert.Equal(310, restored.ScannerAutoSubmitQuietMs);
    }

    [Fact]
    public void CreateDefaultStorageLocations_UsesEveryReadyNonSystemFixedDrive()
    {
        var drives = new[]
        {
            new StorageDriveCandidate(@"E:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"C:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"D:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"F:\", true, DriveType.Removable),
            new StorageDriveCandidate(@"G:\", false, DriveType.Fixed)
        };

        List<StorageLocation> locations = AppConfig.CreateDefaultStorageLocations(drives);

        Assert.Equal([@"D:\快递打包视频", @"E:\快递打包视频"], locations.Select(location => location.Path));
        Assert.Equal([0, 1], locations.Select(location => location.Priority));
    }

    [Fact]
    public void CreateDefaultStorageLocations_FallsBackToSystemDrive()
    {
        var drives = new[]
        {
            new StorageDriveCandidate(@"C:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"D:\", false, DriveType.Fixed)
        };

        StorageLocation location = Assert.Single(AppConfig.CreateDefaultStorageLocations(drives));

        Assert.Equal(@"C:\快递打包视频", location.Path);
        Assert.Equal(0, location.Priority);
    }

    [Fact]
    public void NormalizeAfterLoad_PreservesExistingStorageLocations()
    {
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation { Path = @"Z:\自定义录像", ReserveGB = 25, Priority = 0 }
            ]
        };

        AppConfig.NormalizeAfterLoad(config);

        StorageLocation location = Assert.Single(config.StorageLocations);
        Assert.Equal(@"Z:\自定义录像", location.Path);
    }

    [Fact]
    public void ResolveStartupExecutable_PrefersRootLauncherForCleanPackage()
    {
        string processPath = @"D:\Package\app\ExpressPackingMonitoring.exe";
        string launcherPath = @"D:\Package\ExpressPackingMonitoring.exe";

        string result = AutoStartService.ResolveStartupExecutable(
            processPath,
            @"D:\Package\app\",
            path => string.Equals(path, launcherPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(launcherPath, result);
    }

    [Fact]
    public void ResolveStartupExecutable_FallsBackToCurrentProcess()
    {
        string processPath = @"D:\Source\bin\ExpressPackingMonitoring.exe";

        string result = AutoStartService.ResolveStartupExecutable(
            processPath,
            @"D:\Source\bin\",
            path => string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(processPath, result);
    }
}
