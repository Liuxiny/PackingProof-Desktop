using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DeploymentStartupTests
{
    [Fact]
    public void UserscriptGuideNavigationAlwaysBuildsHostedGuideUrl()
    {
        string url = UserscriptGuideNavigation.BuildUrl("http://192.168.1.20:5280/");

        Assert.StartsWith(
            "http://192.168.1.20:5280/kuaidizs-install-guide?refresh=",
            url,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/?refresh=", url, StringComparison.Ordinal);
    }

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
    public void ViewerClientUsesSingleDynamicUserscriptButton()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");

        Assert.Equal(1, xaml.Split("Click=\"InstallUserscript_Click\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("x:Name=\"UserscriptButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UserscriptStatusText\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("重新生成快递助手脚本", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerClientExposesPurposeSwitchAndUsesSharedRestartFlow()
    {
        string viewerXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string mobileBackupXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml");
        string recordingXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        Assert.Contains("Content=\"切换用途\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SwitchPurpose_Click\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"切换用途\"", mobileBackupXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"切换用途\"", recordingXaml, StringComparison.Ordinal);
        Assert.Contains("new WorkstationSelectionWindow { Owner = this }", source, StringComparison.Ordinal);
        Assert.Contains("WorkstationNetwork.AskRestart(this)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileBackupPurposeUsesPhoneIcon()
    {
        string selector = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "WorkstationSelectionWindow.xaml");
        string settings = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml");
        string icons = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Themes",
            "FluentIcons.xaml");

        Assert.Contains("Data=\"{StaticResource FluentPhoneIcon}\"", selector, StringComparison.Ordinal);
        Assert.Contains("Data=\"{StaticResource FluentPhoneIcon}\"", settings, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"FluentPhoneIcon\"", icons, StringComparison.Ordinal);
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
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains("AppConfig.ShouldRunDeploymentSetup(config)", appSource, StringComparison.Ordinal);
        Assert.Contains("AppConfig.ShouldRunRecordingSetup(config)", appSource, StringComparison.Ordinal);
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
            "AppConfig.MarkDeploymentSetupCompleted(config)",
            StringComparison.Ordinal);

        Assert.True(validation >= 0);
        Assert.True(completion > validation);
    }

    [Fact]
    public void SwitchingNonRecordingComputerToRecordingDefersSetupUntilRestart()
    {
        string viewerSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");
        string mobileBackupSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");
        string settingsSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml.cs");

        Assert.DoesNotContain(
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            viewerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            mobileBackupSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            settingsSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingSetupCopyMakesCameraRecognitionPrimaryAndScannerOptional()
    {
        string selector = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "WorkstationSelectionWindow.xaml");
        string wizard = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml");
        string wizardSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        Assert.Contains("优先由摄像头识别面单条码，也可选用扫码枪", selector, StringComparison.Ordinal);
        Assert.Contains("优先使用摄像头识别面单条码", wizard, StringComparison.Ordinal);
        Assert.Contains("没有扫码枪可直接进入下一步", wizard, StringComparison.Ordinal);
        Assert.Contains("可选扫码枪仍可随时作为后备方案", wizardSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingHostWindowExposesNodeAndUserscriptStatus()
    {
        string xaml = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "MainWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.cs");

        Assert.Contains("x:Name=\"BtnInstallUserscript\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Config.NodeName", source, StringComparison.Ordinal);
        Assert.Contains(
            "WorkstationPrintStatusText = $\"{Config.NodeName} · {verifiedAddress}\";",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("· 录像设备 {recorderCount}", source, StringComparison.Ordinal);
        Assert.Contains("public void OpenUserscriptGuide()", source, StringComparison.Ordinal);
        Assert.Contains("UserscriptGuideNavigation.TryOpen", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding MobileBackupDeviceStatuses}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"连接手机\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UserscriptButtonText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UserscriptSetupStatusText", source, StringComparison.Ordinal);
        Assert.Contains("OrderIntegrationStatusText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingHostSettingsOpenHostedUserscriptGuide()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml.cs");

        Assert.Contains("Context.OpenUserscriptGuide?.Invoke();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PrintToolInstallGuide.CreateLocalGuide(address)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileBackupHostSettingsOpenHostedUserscriptGuide()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");

        Assert.Contains("OpenUserscriptGuide = OpenUserscriptGuide", source, StringComparison.Ordinal);
        Assert.Contains("UserscriptGuideNavigation.TryOpen", source, StringComparison.Ordinal);
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
