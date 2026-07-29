namespace ChronoMailBridge.Core;

public interface IImapSource : IAsyncDisposable
{
    Task TestConnectionAsync(ImapConnectionOptions options, string password, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceFolder>> ListFoldersAsync(
        ImapConnectionOptions options,
        string password,
        CancellationToken cancellationToken);
    IAsyncEnumerable<(SourceFolder Folder, uint UidValidity, IReadOnlyList<SourceMessage> Messages)> ScanAsync(
        MigrationJob job,
        string password,
        IReadOnlyCollection<string> selectedFolders,
        CancellationToken cancellationToken);
    Task<Stream> OpenMessageStreamAsync(
        MigrationJob job,
        string password,
        SourceMessage message,
        CancellationToken cancellationToken);
}

public interface IGmailDestination : IAsyncDisposable
{
    Task AuthorizeAsync(string clientSecretsJson, CancellationToken cancellationToken);
    Task<GmailLookupResult> FindByMessageIdAsync(string normalizedMessageId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string>> EnsureLabelsAsync(
        IReadOnlyCollection<string> labelNames,
        bool dryRun,
        CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> GetMessageLabelIdsAsync(string gmailMessageId, CancellationToken cancellationToken);
    Task AddMissingLabelsAsync(
        string gmailMessageId,
        IReadOnlyCollection<string> labelIds,
        bool dryRun,
        CancellationToken cancellationToken);
    Task<GmailImportResult> ImportAsync(GmailImportRequest request, CancellationToken cancellationToken);
}

public interface IMigrationStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task SaveJobAsync(MigrationJob job, CancellationToken cancellationToken);
    Task<FolderEpoch> ObserveFolderEpochAsync(
        Guid jobId,
        string folderName,
        uint uidValidity,
        CancellationToken cancellationToken);
    Task UpsertDiscoveredAsync(IEnumerable<SourceMessage> messages, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceMessage>> GetPendingDownloadsAsync(Guid jobId, CancellationToken cancellationToken);
    Task SetAppearanceStatusAsync(
        long appearanceId,
        MigrationStatus status,
        string? errorCode,
        CancellationToken cancellationToken);
    Task CompleteDownloadAsync(
        long appearanceId,
        ArchiveWriteResult result,
        string? normalizedMessageId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<LogicalMessage>> GetPendingImportsAsync(Guid jobId, CancellationToken cancellationToken);
    Task SetLogicalStatusAsync(
        long logicalMessageId,
        MigrationStatus status,
        string? errorCode,
        CancellationToken cancellationToken);
    Task CompleteImportAsync(
        long logicalMessageId,
        string gmailMessageId,
        MigrationStatus status,
        CancellationToken cancellationToken);
    Task SaveUploadSessionAsync(
        long logicalMessageId,
        string encryptedResumeUri,
        string sha256,
        long size,
        string path,
        CancellationToken cancellationToken);
    Task<string?> GetUploadSessionAsync(long logicalMessageId, CancellationToken cancellationToken);
    Task ClearUploadSessionAsync(long logicalMessageId, CancellationToken cancellationToken);
    Task RequeueInterruptedAsync(Guid jobId, CancellationToken cancellationToken);
    Task<MigrationSnapshot> GetSnapshotAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IArchiveStore
{
    string RootPath { get; }
    Task EnsureLayoutAsync(CancellationToken cancellationToken);
    string GetMessagePath(SourceMessage message);
    Task<ArchiveWriteResult> WriteAtomicAsync(
        SourceMessage message,
        Stream source,
        CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string fullPath, string expectedSha256, long expectedSize, CancellationToken cancellationToken);
    Task ReconcileAsync(IMigrationStore store, Guid jobId, CancellationToken cancellationToken);
    long GetAvailableBytes();
    bool HasRecommendedFreeSpace(long estimatedBytes);
}

public interface ISecretStore
{
    Task SaveAsync(string key, string secret, CancellationToken cancellationToken);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    string Protect(string value);
    string Unprotect(string protectedValue);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IRandomSource
{
    double NextDouble();
}

public interface IErrorClassifier
{
    ErrorClassification Classify(Exception exception);
}

public interface IPowerManagement
{
    void PreventSleep();
    void Restore();
}

public interface IReportWriter
{
    Task<IReadOnlyList<string>> WriteAsync(
        string reportsDirectory,
        MigrationSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface ITechnicalLog
{
    void Information(string eventName, long? technicalId = null, string? code = null);
    void Warning(string eventName, long? technicalId = null, string? code = null);
    void ErrorEvent(string eventName, long? technicalId = null, string? code = null);
}
