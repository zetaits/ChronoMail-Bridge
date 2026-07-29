using System.Collections.ObjectModel;

namespace ChronoMailBridge.Core;

public enum MigrationStatus
{
    Discovered,
    Downloading,
    Downloaded,
    DuplicateCandidate,
    Uploading,
    Imported,
    Existing,
    Skipped,
    Failed,
    NeedsReview
}

public enum MissingMessageIdPolicy
{
    NeedsReview,
    Skip,
    Import
}

public enum ErrorKind
{
    Temporary,
    Permanent,
    Authentication,
    RateLimited,
    Unknown
}

public enum MigrationRunState
{
    Idle,
    Scanning,
    Running,
    Paused,
    Waiting,
    Cancelling,
    Completed,
    Faulted
}

public sealed record ImapConnectionOptions(
    string Host = "imap.mail.yahoo.com",
    int Port = 993,
    bool UseSsl = true,
    string UserName = "",
    int BatchSize = 250,
    TimeSpan? OperationTimeout = null);

public sealed record GmailOptions(
    string UserId = "me",
    string LabelPrefix = "Imported from Turbify",
    int ChunkSizeBytes = 10 * 1024 * 1024);

public sealed record MigrationJob(
    Guid Id,
    string Name,
    string ArchiveRoot,
    ImapConnectionOptions Imap,
    GmailOptions Gmail,
    DateTimeOffset? MaximumInternalDate = null,
    MissingMessageIdPolicy MissingMessageIdPolicy = MissingMessageIdPolicy.NeedsReview,
    bool PreventSleep = true,
    TimeSpan? TransferInterval = null,
    bool DryRun = false)
{
    public TimeSpan EffectiveTransferInterval => TransferInterval ?? TimeSpan.FromSeconds(1);
}

public sealed record SourceFolder(
    string FullName,
    char DirectorySeparator,
    FolderKind Kind = FolderKind.Normal);

public enum FolderKind
{
    Normal,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Spam
}

public sealed record FolderEpoch(
    long Id,
    Guid JobId,
    string FolderName,
    uint UidValidity,
    DateTimeOffset FirstSeenUtc,
    bool IsCurrent);

public sealed record SourceMessage(
    long Id,
    Guid JobId,
    string FolderName,
    uint UidValidity,
    uint Uid,
    DateTimeOffset InternalDate,
    DateTimeOffset? HeaderDate,
    string? MessageId,
    long Size,
    IReadOnlySet<string> Flags,
    MigrationStatus Status = MigrationStatus.Discovered,
    string? Sha256 = null,
    string? ArchivePath = null,
    long? LogicalMessageId = null,
    string? ErrorCode = null)
{
    public bool IsUnread => !Flags.Contains("\\Seen");
    public bool IsStarred => Flags.Contains("\\Flagged");
}

public sealed record LogicalMessage(
    long Id,
    Guid JobId,
    string? NormalizedMessageId,
    string? Sha256,
    MigrationStatus Status,
    string? GmailMessageId,
    IReadOnlyCollection<string> SourceFolders,
    string? ArchivePath,
    long Size,
    DateTimeOffset InternalDate,
    DateTimeOffset? HeaderDate,
    bool IsUnread,
    bool IsStarred);

public readonly record struct MessageIdentity(string? NormalizedMessageId, string? Sha256)
{
    public bool HasStrongIdentity =>
        !string.IsNullOrWhiteSpace(NormalizedMessageId) ||
        !string.IsNullOrWhiteSpace(Sha256);
}

public sealed record GmailLookupResult(
    IReadOnlyList<string> MessageIds)
{
    public static GmailLookupResult None { get; } = new([]);
    public bool IsUnique => MessageIds.Count == 1;
    public bool IsAmbiguous => MessageIds.Count > 1;
}

public sealed record GmailImportRequest(
    long LogicalMessageId,
    string FilePath,
    string Sha256,
    long Size,
    DateTimeOffset? HeaderDate,
    IReadOnlyCollection<string> LabelIds,
    string? ResumeUri);

public sealed record GmailImportResult(
    bool Succeeded,
    string? GmailMessageId,
    string? ResumeUri = null,
    bool ResponseMayHaveBeenLost = false,
    string? ErrorCode = null);

public sealed record ArchiveWriteResult(
    string FullPath,
    string RelativePath,
    string Sha256,
    long Bytes,
    bool ReusedExisting);

public sealed record ErrorClassification(
    ErrorKind Kind,
    string Code,
    TimeSpan? RetryAfter = null)
{
    public bool IsTemporary => Kind is ErrorKind.Temporary or ErrorKind.RateLimited;
}

public sealed record MigrationProgress
{
    public MigrationRunState RunState { get; init; } = MigrationRunState.Idle;
    public string? CurrentFolder { get; init; }
    public long Discovered { get; init; }
    public long Downloaded { get; init; }
    public long Imported { get; init; }
    public long Existing { get; init; }
    public long Skipped { get; init; }
    public long NeedsReview { get; init; }
    public long Failed { get; init; }
    public long BytesDownloaded { get; init; }
    public double BytesPerSecond { get; init; }
    public DateTimeOffset? LastTransferUtc { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public bool CircuitOpen { get; init; }

    public static MigrationProgress Empty { get; } = new();
}

public sealed record RetrySettings(
    TimeSpan InitialDelay,
    TimeSpan MaximumDelay,
    int CircuitFailureThreshold,
    TimeSpan CircuitOpenDuration,
    int PermanentAttempts)
{
    public static RetrySettings Conservative { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(5),
        5,
        TimeSpan.FromMinutes(30),
        3);
}

public sealed record ReportRow(
    string Folder,
    int Year,
    MigrationStatus Status,
    long Count,
    long Bytes);

public sealed record ReviewItem(
    long TechnicalId,
    string Folder,
    DateTimeOffset InternalDate,
    long Size,
    MigrationStatus Status,
    string? ErrorCode);

public sealed class MigrationSnapshot
{
    public MigrationSnapshot(IEnumerable<ReportRow> rows, IEnumerable<ReviewItem> reviewItems)
    {
        Rows = new ReadOnlyCollection<ReportRow>(rows.ToList());
        ReviewItems = new ReadOnlyCollection<ReviewItem>(reviewItems.ToList());
    }

    public IReadOnlyList<ReportRow> Rows { get; }
    public IReadOnlyList<ReviewItem> ReviewItems { get; }
}
