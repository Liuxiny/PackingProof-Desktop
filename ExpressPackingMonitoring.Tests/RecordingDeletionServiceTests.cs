using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingDeletionServiceTests
{
    [Fact]
    public async Task DeleteAsync_DeletesOnlyTheSelectedAggregateWhenNamesMatch()
    {
        string root = CreateTempDirectory();
        try
        {
            string first = Path.Combine(root, "first");
            string second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            AggregatePaths firstPaths = CreateAggregate(first, "same-name");
            AggregatePaths secondPaths = CreateAggregate(second, "same-name");

            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            long firstId = InsertAggregate(database, firstPaths);
            _ = InsertAggregate(database, secondPaths);
            var service = new RecordingDeletionService(database, _ => false);

            RecordingDeletionResult result = await service.DeleteAsync(
                firstId,
                TestContext.Current.CancellationToken);

            Assert.True(result.Completed);
            Assert.All(firstPaths.All, path => Assert.False(File.Exists(path)));
            Assert.All(secondPaths.All, path => Assert.True(File.Exists(path)));
            Assert.True(GetIncludingDeleted(database, firstId).IsDeleted);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task DeleteAsync_CurrentRecordingCreatesNoIntentAndMutatesNothing()
    {
        string root = CreateTempDirectory();
        try
        {
            AggregatePaths paths = CreateAggregate(root, "current");
            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            long id = InsertAggregate(database, paths);
            var service = new RecordingDeletionService(database, recordId => recordId == id);

            RecordingDeletionResult result = await service.DeleteAsync(
                id,
                TestContext.Current.CancellationToken);

            Assert.False(result.Completed);
            Assert.Empty(database.GetPendingRecordingDeleteJobs());
            Assert.All(paths.All, path => Assert.True(File.Exists(path)));
            Assert.False(database.GetVideoById(id)!.IsDeleted);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task DeleteAsync_OfflineNetworkPersistsAnIdempotentRetry()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "videos.db");
        long id;
        try
        {
            AggregatePaths paths = CreateAggregate(root, "offline");
            string networkRoot = $@"\\127.0.0.1\packing-proof-missing-{Guid.NewGuid():N}";
            using (var database = new VideoDatabase(databasePath))
            {
                id = database.InsertVideoRecord("ORDER", "发货", "h264", "libx264", paths.Video, DateTime.Now);
                database.UpdateRecordingPhoto(id, paths.Photo, DateTime.Now, 1920, 1080,
                    new FileInfo(paths.Photo).Length, "photo-hash", RecordingPhotoStatus.Ready);
                database.ConfigureNetworkArchive(
                    id,
                    paths.Video,
                    Path.Combine(networkRoot, "offline.mp4"),
                    paths.Proof,
                    ready: true,
                    localPhotoPath: paths.Photo,
                    networkPhotoPath: Path.Combine(networkRoot, "offline_面单.jpg"));

                var service = new RecordingDeletionService(database, _ => false);
                RecordingDeletionResult first = await service.DeleteAsync(id, TestContext.Current.CancellationToken);
                Assert.True(first.WaitingForNetwork);
                Assert.False(first.Completed);
                Assert.All(paths.All, path => Assert.False(File.Exists(path)));
                RecordingDeleteJob job = Assert.Single(database.GetPendingRecordingDeleteJobs());
                Assert.Equal(RecordingDeleteJobState.WaitingForNetwork, job.State);
                Assert.True(job.LocalVideoDeleted);
                Assert.False(job.NetworkVideoDeleted);
            }

            using (var reopened = new VideoDatabase(databasePath))
            {
                var recoveredService = new RecordingDeletionService(reopened, _ => false);
                RecordingDeletionResult retried = await recoveredService.DeleteAsync(id, TestContext.Current.CancellationToken);
                Assert.True(retried.WaitingForNetwork);
                Assert.Single(reopened.GetPendingRecordingDeleteJobs());
                Assert.False(reopened.GetVideoById(id)!.IsDeleted);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task DeleteAsync_RepeatedRequestsRemainIdempotent()
    {
        string root = CreateTempDirectory();
        try
        {
            AggregatePaths paths = CreateAggregate(root, "repeat");
            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            long id = InsertAggregate(database, paths);
            var service = new RecordingDeletionService(database, _ => false);

            RecordingDeletionResult[] results = await Task.WhenAll(
                service.DeleteAsync(id, TestContext.Current.CancellationToken),
                service.DeleteAsync(id, TestContext.Current.CancellationToken));

            Assert.All(results, result => Assert.True(result.Completed));
            Assert.True(GetIncludingDeleted(database, id).IsDeleted);
            Assert.Empty(database.GetPendingRecordingDeleteJobs());
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static long InsertAggregate(VideoDatabase database, AggregatePaths paths)
    {
        long id = database.InsertVideoRecord("ORDER", "发货", "h264", "libx264", paths.Video, DateTime.Now);
        database.UpdateRecordingPhoto(id, paths.Photo, DateTime.Now, 1920, 1080,
            new FileInfo(paths.Photo).Length, "photo-hash", RecordingPhotoStatus.Ready);
        database.UpdateRecordingProof(id, paths.Proof, "video-hash");
        return id;
    }

    private static VideoRecord GetIncludingDeleted(VideoDatabase database, long id)
    {
        return Assert.Single(database.QueryVideosPaged(
            startDate: null,
            endDate: null,
            keyword: null,
            page: 1,
            pageSize: 100,
            includeDeleted: true).Records,
            record => record.Id == id);
    }

    private static AggregatePaths CreateAggregate(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, name + ".mp4");
        string photo = Path.Combine(directory, name + "_面单.jpg");
        string proof = Path.Combine(directory, name + ".proof.json");
        File.WriteAllBytes(video, [1, 2, 3]);
        File.WriteAllBytes(photo, [4, 5, 6]);
        File.WriteAllText(proof, "{}");
        return new AggregatePaths(video, photo, proof);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"PackingProofDeleteTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed record AggregatePaths(string Video, string Photo, string Proof)
    {
        public IReadOnlyList<string> All => [Video, Photo, Proof];
    }
}
