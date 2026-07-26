using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppDialogTests
{
    [Fact]
    public void AppDialog_ExposesThemedMessageAndConfirmationEntryPoints()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "AppDialog.cs");
        string xaml = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "ConfirmDialog.xaml");

        Assert.Contains("public static void ShowMessage(", source, StringComparison.Ordinal);
        Assert.Contains("public static bool Confirm(", source, StringComparison.Ordinal);
        Assert.Contains("dispatcher.Invoke(action)", source, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation.CenterScreen", source, StringComparison.Ordinal);
        Assert.Contains("Fluent", ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ConfirmDialog.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"14\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"430\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonStyle", ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ConfirmDialog.xaml.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppDialogSeverity.Information)]
    [InlineData(AppDialogSeverity.Warning)]
    [InlineData(AppDialogSeverity.Error)]
    public void AppDialogSeverity_DefinesSupportedVisualLevels(AppDialogSeverity severity)
    {
        Assert.True(Enum.IsDefined(severity));
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
