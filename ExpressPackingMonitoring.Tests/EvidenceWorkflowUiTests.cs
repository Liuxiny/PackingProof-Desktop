using ExpressPackingMonitoring.Services;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class EvidenceWorkflowUiTests
{
    [Fact]
    public void WebPhotoRoutesRemainProtectedAndReadOnly()
    {
        string webServer = ReadRepositoryFile("ExpressPackingMonitoring", "Services", "WebServer.cs");
        string index = ReadRepositoryFile("ExpressPackingMonitoring", "Web", "index.html");

        Assert.True(WebServer.RequiresAccessKey("/api/videos/123/photo"));
        Assert.True(WebServer.RequiresAccessKey("/api/videos/123/photo-thumbnail"));
        Assert.Contains("HandleRecordingPhoto", webServer, StringComparison.Ordinal);
        Assert.Contains("photoThumbnailUrl", webServer, StringComparison.Ordinal);
        Assert.Contains("v.photoThumbnailUrl", index, StringComparison.Ordinal);
        Assert.Contains("v.photoUrl", index, StringComparison.Ordinal);
        Assert.DoesNotContain("method == \"DELETE\"", webServer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("method:'DELETE'", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("method: 'DELETE'", index, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaybackRowsExposePhotoViewerAndIndependentConfirmedDeleteAction()
    {
        XDocument document = XDocument.Parse(ReadRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement photoButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "BtnShowPhoto_Click");
        XElement deleteButton = Assert.Single(
            document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute("Click") == "BtnDeleteVideo_Click");

        Assert.Contains("HasPhoto", (string?)photoButton.Attribute("IsEnabled") ?? "", StringComparison.Ordinal);
        Assert.Contains("CanDelete", (string?)deleteButton.Attribute("IsEnabled") ?? "", StringComparison.Ordinal);
        string codeBehind = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs");
        Assert.Contains("二次确认删除", codeBehind, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", codeBehind, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
