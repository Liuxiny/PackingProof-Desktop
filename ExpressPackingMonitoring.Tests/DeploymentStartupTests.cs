using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DeploymentStartupTests
{
    [Theory]
    [InlineData("RecordingHost", DeploymentPresets.RecordingHost)]
    [InlineData("CameraMonitor", DeploymentPresets.RecordingHost)]
    [InlineData("monitor", DeploymentPresets.RecordingHost)]
    [InlineData("MobileBackupHost", DeploymentPresets.MobileBackupHost)]
    [InlineData("PrintStation", DeploymentPresets.MobileBackupHost)]
    [InlineData("order", DeploymentPresets.MobileBackupHost)]
    [InlineData("ViewerClient", DeploymentPresets.ViewerClient)]
    [InlineData("viewer", DeploymentPresets.ViewerClient)]
    public void DeploymentCommandNamesMapToCurrentPresets(string input, string expected)
    {
        Assert.Equal(expected, App.NormalizePresetName(input));
    }

    [Theory]
    [InlineData("--monitor", DeploymentPresets.RecordingHost)]
    [InlineData("--print-station", DeploymentPresets.MobileBackupHost)]
    [InlineData("--order-workstation", DeploymentPresets.MobileBackupHost)]
    [InlineData("--viewer", DeploymentPresets.ViewerClient)]
    public void LegacyCommandLineFlagsMapToCurrentPresets(string flag, string expected)
    {
        Assert.Equal(expected, App.ResolveLegacyRequestedPreset([flag]));
    }

    [Fact]
    public void SingleInstanceCoordinatorIsGlobalAcrossDeploymentPresets()
    {
        string? previousScope = Environment.GetEnvironmentVariable("EPM_INSTANCE_SCOPE");
        Environment.SetEnvironmentVariable("EPM_INSTANCE_SCOPE", $"deployment{Guid.NewGuid():N}");
        try
        {
            Assert.True(WorkstationInstanceCoordinator.TryCreate(out WorkstationInstanceCoordinator? first));
            using (first)
            {
                Assert.True(WorkstationInstanceCoordinator.IsRunning());
                Assert.False(WorkstationInstanceCoordinator.TryCreate(out WorkstationInstanceCoordinator? second));
                Assert.Null(second);
            }
            Assert.False(WorkstationInstanceCoordinator.IsRunning());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EPM_INSTANCE_SCOPE", previousScope);
        }
    }

    [Fact]
    public void ViewerClientWindowDoesNotReferenceLocalRecordingOrHostServices()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "Workstations", "ViewerClientWindow.xaml.cs");

        Assert.DoesNotContain("VideoDatabase", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new WebServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NoCameraWorkstationHost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoCaptureDevice", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioProbe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalKeyboardHook", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppMapsEveryPresetToItsDedicatedWindow()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "App.xaml.cs");

        Assert.Contains("DeploymentPresets.ViewerClient => new ViewerClientWindow", source, StringComparison.Ordinal);
        Assert.Contains("DeploymentPresets.MobileBackupHost => new PrintWorkstationWindow", source, StringComparison.Ordinal);
        Assert.Contains("_ => new MainWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--temporary-role", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenOtherRole", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstDeploymentUsesDraftAndRecordingHostRequiresHardwareSetup()
    {
        string appSource = ReadRepositoryFile("ExpressPackingMonitoring", "App.xaml.cs");
        string wizardSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        Assert.Contains("JsonSerializer.Serialize(config)", appSource, StringComparison.Ordinal);
        Assert.Contains(
            "new FirstUseSetupWizardWindow(draft, allowSkip: false)",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains("bool shouldPersistDraft = string.Equals", appSource, StringComparison.Ordinal);
        Assert.Contains("if (shouldPersistDraft", appSource, StringComparison.Ordinal);
        Assert.Contains("SkipButton.Visibility = allowSkip", wizardSource, StringComparison.Ordinal);
        Assert.Contains("录制主机必须先选择可用摄像头", wizardSource, StringComparison.Ordinal);
        Assert.Contains("录制主机必须先选择可用麦克风", wizardSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerClientCompletesFirstUseOnlyAfterBindingAValidatedHost()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        int validation = source.IndexOf(
            "if (!node.IsValidHost)",
            StringComparison.Ordinal);
        int completion = source.IndexOf(
            "config.FirstUseWizardCompleted = true",
            StringComparison.Ordinal);

        Assert.True(validation >= 0);
        Assert.True(completion > validation);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
