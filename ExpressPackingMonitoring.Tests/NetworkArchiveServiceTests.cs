using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkArchiveServiceTests
{
    [Fact]
    public async Task Archive_PublishesAndVerifiesWithoutDeletingLocalOriginal()
    {
        using var fixture = new ArchiveFixture();
        long id = fixture.AddCompleted("ORDER-A", "payload-a", "archive-a.mkv");
        using var service = new NetworkArchiveService(fixture.Database);

        int completed = await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        VideoRecord record = fixture.Database.GetVideoById(id)!;

        Assert.Equal(1, completed);
        Assert.Equal(VideoArchiveStatus.Verified, record.ArchiveStatus);
        Assert.True(File.Exists(record.LocalFilePath));
        Assert.Equal("payload-a", File.ReadAllText(record.NetworkFilePath));
        Assert.False(string.IsNullOrWhiteSpace(record.ContentSha256));
    }

    [Fact]
    public async Task CompetingArchiveFailure_PreservesPublishedTarget()
    {
        using var fixture = new ArchiveFixture();
        long firstId = fixture.AddCompleted("ORDER-A", "valid-output", "shared.mkv");
        using var service = new NetworkArchiveService(fixture.Database);
        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        long secondId = fixture.AddCompleted("ORDER-B", "different-output", "shared.mkv");
        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("valid-output", File.ReadAllText(Path.Combine(fixture.NetworkDirectory, "shared.mkv")));
        Assert.Equal(VideoArchiveStatus.Verified, fixture.Database.GetVideoById(firstId)!.ArchiveStatus);
        Assert.Equal(VideoArchiveStatus.Conflict, fixture.Database.GetVideoById(secondId)!.ArchiveStatus);
        Assert.True(File.Exists(fixture.Database.GetVideoById(secondId)!.LocalFilePath));
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string _root;
        public ArchiveFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"PackingProofArchiveTests-{Guid.NewGuid():N}");
            LocalDirectory = Path.Combine(_root, "local");
            NetworkDirectory = Path.Combine(_root, "network");
            Directory.CreateDirectory(LocalDirectory);
            Directory.CreateDirectory(NetworkDirectory);
            Database = new VideoDatabase(Path.Combine(_root, "videos.db"));
        }

        public string LocalDirectory { get; }
        public string NetworkDirectory { get; }
        public VideoDatabase Database { get; }

        public long AddCompleted(string order, string content, string destinationName)
        {
            string localPath = Path.Combine(LocalDirectory, $"{order}.mkv");
            File.WriteAllText(localPath, content);
            DateTime started = DateTime.Now.AddSeconds(-20);
            long id = Database.InsertVideoRecord(order, "发货", "h264", "h264_qsv", localPath, started, null, "", Environment.MachineName);
            Database.UpdateVideoRecordOnStop(id, DateTime.Now, 20, new FileInfo(localPath).Length, "手动", "h264", "h264_qsv");
            Database.ConfigureNetworkArchive(id, localPath, Path.Combine(NetworkDirectory, destinationName));
            return id;
        }

        public void Dispose()
        {
            Database.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
