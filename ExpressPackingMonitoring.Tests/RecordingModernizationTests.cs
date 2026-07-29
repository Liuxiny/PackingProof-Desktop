using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingModernizationTests
{
    [Fact]
    public void Retention_UsesInclusiveNaturalDaysAndHasNoMaximum()
    {
        DateTime today = new(2026, 7, 29, 12, 0, 0);
        Assert.Equal(new DateTime(2026, 7, 15), StorageRetentionPolicy.GetGlobalCutoffDate(today, 15));
        Assert.Equal(50_000, StorageRetentionPolicy.NormalizeRetentionDays(50_000));
    }

    [Fact]
    public void RetentionEstimate_IncludesCalendarDaysWithoutRecordings()
    {
        var stats = new[]
        {
            new DailyStat { Date = "2026-07-01", TotalBytes = 100 },
            new DailyStat { Date = "2026-07-10", TotalBytes = 100 }
        };

        StorageRetentionEstimate estimate = StorageRetentionPolicy.Estimate(stats, 2_000, 120);

        Assert.True(estimate.HasEnoughHistory);
        Assert.Equal(20, estimate.AverageBytesPerDay);
        Assert.Equal(100, estimate.SuggestedDays);
        Assert.True(estimate.CannotMeetConfiguredDays);
    }

    [Theory]
    [InlineData(270, 20, 15, true)]
    [InlineData(269, 20, 15, false)]
    [InlineData(300, 20, 15, true)]
    public void FirstRecordingVerification_AllowsTenPercentError(
        long frames,
        double seconds,
        int targetFps,
        bool expected)
    {
        RecordingProfileVerificationResult result = RecordingProfileVerifier.Evaluate(
            targetFps,
            frames,
            seconds,
            outputExists: true);
        Assert.Equal(expected, result.Passed);
    }

    [Fact]
    public void ProfileKey_ChangesWithResolutionFpsOrEncoder()
    {
        var config = new AppConfig { CameraMonikerString = "camera", FrameWidth = 1280, FrameHeight = 720, Fps = 15, VideoEncoder = "h264_qsv" };
        string original = RecordingProfileVerifier.BuildProfileKey(config);
        config.Fps = 30;
        Assert.NotEqual(original, RecordingProfileVerifier.BuildProfileKey(config));
    }

    [Fact]
    public async Task RecordingProof_DetectsVideoTampering()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "proof-test.mkv");
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllBytesAsync(video, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray(), cancellationToken);
            var metadata = new RecordingProofMetadata(
                1, "ORDER123", "发货", DateTimeOffset.UtcNow.AddSeconds(-20), DateTimeOffset.UtcNow,
                1280, 720, 15, "h264_qsv", "ABCDEF123456");
            var service = new RecordingIntegrityService();

            RecordingProofResult proof = await service.CreateProofAsync(video, metadata, cancellationToken);
            Assert.True(await RecordingIntegrityService.VerifyProofAsync(video, proof.ProofFilePath, cancellationToken));

            await File.AppendAllTextAsync(video, "tampered", cancellationToken);
            Assert.False(await RecordingIntegrityService.VerifyProofAsync(video, proof.ProofFilePath, cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordingProofV2_BindsFinalMp4AndOriginalPhoto()
    {
        string directory = CreateTempDirectory();
        try
        {
            string video = Path.Combine(directory, "proof-test.mp4");
            string photo = Path.Combine(directory, "proof-test_面单.jpg");
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllBytesAsync(video, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray(), cancellationToken);
            await File.WriteAllBytesAsync(photo, Enumerable.Range(0, 1024).Select(i => (byte)(i % 239)).ToArray(), cancellationToken);
            var metadata = new RecordingProofMetadata(
                2, "ORDER456", "发货", DateTimeOffset.UtcNow.AddSeconds(-20), DateTimeOffset.UtcNow,
                1920, 1080, 30, "h264_qsv", "");

            RecordingProofResult proof = await new RecordingIntegrityService()
                .CreateProofAsync(video, metadata, cancellationToken, photo);
            Assert.NotEmpty(proof.PhotoSha256);
            Assert.True(await RecordingIntegrityService.VerifyProofAsync(video, proof.ProofFilePath, cancellationToken, photo));

            await File.AppendAllTextAsync(photo, "tampered", cancellationToken);
            Assert.False(await RecordingIntegrityService.VerifyProofAsync(video, proof.ProofFilePath, cancellationToken, photo));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WatermarkProofCode_ChangesEachSecond()
    {
        var session = new RecordingIntegritySession();
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddMilliseconds(100);
        string first = session.GetCode(now, "ORDER123");
        Assert.Equal(first, session.GetCode(now.AddMilliseconds(500), "ORDER123"));
        Assert.NotEqual(first, session.GetCode(now.AddSeconds(1), "ORDER123"));
        Assert.Equal(12, first.Length);
    }

    [Fact]
    public void LegacyDatabaseMigration_IsIdempotentAndPopulatesStorageFields()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "legacy.db");
        string videoPath = Path.Combine(directory, "legacy.mkv");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE VideoRecords (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        OrderId TEXT DEFAULT '', Mode TEXT DEFAULT '', FilePath TEXT DEFAULT '',
                        FileSizeBytes INTEGER DEFAULT 0, StartTime TEXT, EndTime TEXT,
                        DurationSeconds REAL DEFAULT 0, StopReason TEXT DEFAULT '',
                        IsDeleted INTEGER DEFAULT 0, DeletedAt TEXT, DeleteReason TEXT DEFAULT ''
                    );
                    INSERT INTO VideoRecords (OrderId, Mode, FilePath, StartTime)
                    VALUES ('LEGACY-1', '发货', $path, '2026-07-20 10:00:00');";
                command.Parameters.AddWithValue("$path", videoPath);
                command.ExecuteNonQuery();
            }

            using (var firstOpen = new VideoDatabase(databasePath))
            {
                VideoRecord migrated = firstOpen.GetVideoById(1)!;
                Assert.Equal(videoPath, migrated.LocalFilePath);
                Assert.Equal(VideoArchiveStatus.LocalOnly, migrated.ArchiveStatus);
            }
            using (var secondOpen = new VideoDatabase(databasePath))
            {
                VideoRecord migratedAgain = secondOpen.GetVideoById(1)!;
                Assert.Equal(videoPath, migratedAgain.LocalFilePath);
                Assert.Equal(0, migratedAgain.ArchiveRetryCount);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"PackingProofTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
