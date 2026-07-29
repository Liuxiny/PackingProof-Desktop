using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ReleasePackagingPolicyTests
{
    [Fact]
    public void DesktopRuntime_ContainsNoAutomaticUpdateImplementation()
    {
        string root = FindRepositoryRoot();
        string launcher = File.ReadAllText(
            Path.Combine(root, "ExpressPackingMonitoring.Launcher", "Program.cs"),
            Encoding.UTF8);
        string project = File.ReadAllText(
            Path.Combine(root, "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"),
            Encoding.UTF8);
        string settings = File.ReadAllText(
            Path.Combine(root, "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml"),
            Encoding.UTF8);

        Assert.DoesNotContain("HttpClient", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppPatch", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UpdateCheckService", project, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableAutoCheckUpdate", settings, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "ExpressPackingMonitoring", "Services", "UpdateCheckService.cs")));
    }

    [Fact]
    public void Packaging_ProducesOnlyUserInitiatedCompletePackages()
    {
        string root = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(root, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("Build-Installer.ps1", publishScript);
        Assert.Contains("Compress-Archive", publishScript);
        Assert.Contains("-t7z", publishScript);
        Assert.DoesNotContain("update_$releaseTag.json", publishScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-AppPatchPackage", publishScript, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "Tools", "Stage-AppPatch.ps1")));
        Assert.False(File.Exists(Path.Combine(root, "Tools", "Install-AppPatch.cmd")));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found");
    }
}
