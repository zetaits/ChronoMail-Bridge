using System.Text;
using ChronoMailBridge.Core;
using ChronoMailBridge.Infrastructure;

namespace ChronoMailBridge.Tests;

public sealed class ArchiveAndStoreTests
{
    [Fact]
    public async Task AtomicWriteCreatesEmlAndNoPart()
    {
        using var directory = new TemporaryDirectory();
        var archive = new FileArchiveStore(directory.Path);
        SourceMessage message = Message(directory.JobId, 1, Encoding.UTF8.GetByteCount("mime"));
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("mime"));

        ArchiveWriteResult result = await archive.WriteAtomicAsync(message, source, CancellationToken.None);

        Assert.True(File.Exists(result.FullPath));
        Assert.False(File.Exists(result.FullPath + ".part"));
        Assert.Equal(4, result.Bytes);
        Assert.True(await archive.VerifyAsync(
            result.FullPath,
            result.Sha256,
            result.Bytes,
            CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedPartIsReplacedOnRetry()
    {
        using var directory = new TemporaryDirectory();
        var archive = new FileArchiveStore(directory.Path);
        SourceMessage message = Message(directory.JobId, 7, 5);
        string path = archive.GetMessagePath(message);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".part", "bad");
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("valid"));

        ArchiveWriteResult result = await archive.WriteAtomicAsync(message, source, CancellationToken.None);

        Assert.Equal("valid", await File.ReadAllTextAsync(result.FullPath));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task UidValidityChangeMarksOldIncompleteAppearanceForReview()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteMigrationStore(Path.Combine(directory.Path, "state.db"));
        await store.InitializeAsync(CancellationToken.None);
        MigrationJob job = Job(directory);
        await store.SaveJobAsync(job, CancellationToken.None);
        await store.ObserveFolderEpochAsync(job.Id, "Inbox", 1, CancellationToken.None);
        await store.UpsertDiscoveredAsync([Message(job.Id, 1, 4)], CancellationToken.None);

        await store.ObserveFolderEpochAsync(job.Id, "Inbox", 2, CancellationToken.None);

        MigrationSnapshot snapshot = await store.GetSnapshotAsync(job.Id, CancellationToken.None);
        Assert.Contains(snapshot.ReviewItems, item => item.ErrorCode == "uidvalidity_changed");
    }

    [Fact]
    public async Task ConfirmedImportIsNeverReturnedAsPending()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteMigrationStore(Path.Combine(directory.Path, "state.db"));
        await store.InitializeAsync(CancellationToken.None);
        MigrationJob job = Job(directory);
        await store.SaveJobAsync(job, CancellationToken.None);
        await store.ObserveFolderEpochAsync(job.Id, "Inbox", 1, CancellationToken.None);
        SourceMessage message = Message(job.Id, 1, 4);
        await store.UpsertDiscoveredAsync([message], CancellationToken.None);
        SourceMessage saved = Assert.Single(await store.GetPendingDownloadsAsync(job.Id, CancellationToken.None));
        string eml = Path.Combine(directory.Path, "1.eml");
        await File.WriteAllTextAsync(eml, "mime");
        await store.CompleteDownloadAsync(
            saved.Id,
            new(eml, "1.eml", "abc", 4, false),
            "<one@example.test>",
            CancellationToken.None);
        LogicalMessage logical = Assert.Single(await store.GetPendingImportsAsync(job.Id, CancellationToken.None));
        await store.CompleteImportAsync(
            logical.Id,
            "gmail-1",
            MigrationStatus.Imported,
            CancellationToken.None);

        Assert.Empty(await store.GetPendingImportsAsync(job.Id, CancellationToken.None));
        await store.RequeueInterruptedAsync(job.Id, CancellationToken.None);
        Assert.Empty(await store.GetPendingImportsAsync(job.Id, CancellationToken.None));
    }

    private static SourceMessage Message(Guid jobId, uint uid, long size) => new(
        0,
        jobId,
        "Inbox",
        1,
        uid,
        new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
        "<one@example.test>",
        size,
        new HashSet<string> { "\\Seen" });

    private static MigrationJob Job(TemporaryDirectory directory) => new(
        directory.JobId,
        "test",
        directory.Path,
        new(),
        new(),
        TransferInterval: TimeSpan.Zero);
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ChronoMailBridge.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public Guid JobId { get; } = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
