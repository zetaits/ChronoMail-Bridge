using ChronoMailBridge.Core;
using ChronoMailBridge.Infrastructure;

namespace ChronoMailBridge.Tests;

public sealed class EndToEndSimulationTests
{
    [Fact]
    public async Task OneHundredSyntheticMessagesMigrateAndResumeWithoutRepeating()
    {
        using var directory = new TemporaryDirectory();
        Guid jobId = directory.JobId;
        FakeImapSource imap = FakeImapSource.Create(jobId, 100, withDuplicates: true);
        var gmail = new FakeGmailDestination();
        await using var store = new SqliteMigrationStore(Path.Combine(directory.Path, "state", "chronomail.db"));
        var archive = new FileArchiveStore(directory.Path);
        var coordinator = new MigrationCoordinator(
            imap,
            gmail,
            store,
            archive,
            new DefaultErrorClassifier(),
            new FakeClock(),
            new FakeDelay(),
            new FixedRandom(0.5),
            new NoOpPowerManagement(),
            new NullTechnicalLog());
        var job = new MigrationJob(
            jobId,
            "integration",
            directory.Path,
            new(),
            new(),
            TransferInterval: TimeSpan.Zero);

        await coordinator.ScanAsync(job, "", [], CancellationToken.None);
        await coordinator.RunAsync(job, "", CancellationToken.None);
        int firstImportCount = gmail.ImportCount;
        int firstDownloadCount = imap.OpenCount;
        await coordinator.RunAsync(job, "", CancellationToken.None);

        Assert.Equal(90, firstImportCount);
        Assert.Equal(firstImportCount, gmail.ImportCount);
        Assert.Equal(100, firstDownloadCount);
        Assert.Equal(firstDownloadCount, imap.OpenCount);
        Assert.All(gmail.ImportedLabels, labels =>
            Assert.Contains(labels, label => label.Contains("Imported from Turbify", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MessageWithoutMessageIdDefaultsToNeedsReview()
    {
        using var directory = new TemporaryDirectory();
        Guid jobId = directory.JobId;
        byte[] mime = "Date: Tue, 01 Jan 2019 10:00:00 +0000\r\n\r\nbody"u8.ToArray();
        var message = new SourceMessage(
            0, jobId, "Inbox", 1, 1,
            new(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
            null, mime.Length, new HashSet<string>());
        var imap = new FakeImapSource(
            [(new SourceFolder("Inbox", '/', FolderKind.Inbox), 1, [message])],
            new Dictionary<(string, uint), byte[]> { [("Inbox", 1)] = mime });
        var gmail = new FakeGmailDestination();
        await using var store = new SqliteMigrationStore(Path.Combine(directory.Path, "state.db"));
        var coordinator = new MigrationCoordinator(
            imap, gmail, store, new FileArchiveStore(directory.Path),
            new DefaultErrorClassifier(), new FakeClock(), new FakeDelay(), new FixedRandom(0.5),
            new NoOpPowerManagement(), new NullTechnicalLog());
        var job = new MigrationJob(jobId, "test", directory.Path, new(), new(), TransferInterval: TimeSpan.Zero);

        await coordinator.ScanAsync(job, "", [], CancellationToken.None);
        await coordinator.RunAsync(job, "", CancellationToken.None);

        MigrationSnapshot snapshot = await store.GetSnapshotAsync(jobId, CancellationToken.None);
        Assert.Equal(0, gmail.ImportCount);
        Assert.Contains(snapshot.ReviewItems, item => item.ErrorCode == "message_id_missing");
    }
}
