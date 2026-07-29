using System.Text;
using ChronoMailBridge.Core;

namespace ChronoMailBridge.Tests;

internal sealed class FakeImapSource : IImapSource
{
    private readonly IReadOnlyList<(SourceFolder Folder, uint UidValidity, IReadOnlyList<SourceMessage> Messages)> _batches;
    private readonly IReadOnlyDictionary<(string Folder, uint Uid), byte[]> _content;

    public FakeImapSource(
        IReadOnlyList<(SourceFolder Folder, uint UidValidity, IReadOnlyList<SourceMessage> Messages)> batches,
        IReadOnlyDictionary<(string Folder, uint Uid), byte[]> content)
    {
        _batches = batches;
        _content = content;
    }

    public int OpenCount { get; private set; }

    public Task TestConnectionAsync(
        ImapConnectionOptions options,
        string password,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<SourceFolder>> ListFoldersAsync(
        ImapConnectionOptions options,
        string password,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceFolder>>(_batches.Select(batch => batch.Folder).ToArray());

    public async IAsyncEnumerable<(SourceFolder Folder, uint UidValidity, IReadOnlyList<SourceMessage> Messages)> ScanAsync(
        MigrationJob job,
        string password,
        IReadOnlyCollection<string> selectedFolders,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var batch in _batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return batch;
        }
    }

    public Task<Stream> OpenMessageStreamAsync(
        MigrationJob job,
        string password,
        SourceMessage message,
        CancellationToken cancellationToken)
    {
        OpenCount++;
        return Task.FromResult<Stream>(
            new MemoryStream(_content[(message.FolderName, message.Uid)], writable: false));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static FakeImapSource Create(Guid jobId, int count, bool withDuplicates = false)
    {
        var inboxMessages = new List<SourceMessage>();
        var nestedMessages = new List<SourceMessage>();
        var content = new Dictionary<(string Folder, uint Uid), byte[]>();

        for (int index = 1; index <= count; index++)
        {
            string folder = index % 2 == 0 ? "Inbox" : "Clients/Old";
            int identity = withDuplicates && index % 10 == 0 ? index - 1 : index;
            byte[] bytes = Encoding.UTF8.GetBytes(
                $"Date: Tue, 01 Jan 2019 10:00:00 +0000\r\nMessage-ID: <m{identity}@example.test>\r\n\r\nBody {identity}");
            var message = new SourceMessage(
                0,
                jobId,
                folder,
                42,
                checked((uint)index),
                new DateTimeOffset(2019, 1, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(index),
                new DateTimeOffset(2019, 1, 1, 10, 0, 0, TimeSpan.Zero),
                $"<m{identity}@example.test>",
                bytes.Length,
                index % 3 == 0 ? new HashSet<string>() : new HashSet<string> { "\\Seen" });
            (folder == "Inbox" ? inboxMessages : nestedMessages).Add(message);
            content[(folder, checked((uint)index))] = bytes;
        }

        return new(
            [
                (new SourceFolder("Inbox", '/', FolderKind.Inbox), 42, inboxMessages),
                (new SourceFolder("Clients/Old", '/', FolderKind.Normal), 42, nestedMessages)
            ],
            content);
    }
}

internal sealed class FakeGmailDestination : IGmailDestination
{
    private readonly Dictionary<string, string> _byMessageId = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;

    public int ImportCount { get; private set; }
    public List<IReadOnlyCollection<string>> ImportedLabels { get; } = [];

    public Task AuthorizeAsync(string clientSecretsJson, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<GmailLookupResult> FindByMessageIdAsync(
        string normalizedMessageId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_byMessageId.TryGetValue(normalizedMessageId, out string? id)
            ? new GmailLookupResult([id])
            : GmailLookupResult.None);

    public Task<IReadOnlyDictionary<string, string>> EnsureLabelsAsync(
        IReadOnlyCollection<string> labelNames,
        bool dryRun,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            labelNames.ToDictionary(label => label, label => $"id:{label}"));

    public Task<IReadOnlySet<string>> GetMessageLabelIdsAsync(
        string gmailMessageId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task AddMissingLabelsAsync(
        string gmailMessageId,
        IReadOnlyCollection<string> labelIds,
        bool dryRun,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<GmailImportResult> ImportAsync(
        GmailImportRequest request,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(request.FilePath);
        using var reader = new StreamReader(file);
        string mime = await reader.ReadToEndAsync(cancellationToken);
        string marker = "Message-ID:";
        int start = mime.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length;
        int end = mime.IndexOf("\r\n", start, StringComparison.Ordinal);
        string messageId = MessageIdentityRules.NormalizeMessageId(mime[start..end])!;
        string id = $"gmail-{Interlocked.Increment(ref _nextId)}";
        _byMessageId[messageId] = id;
        ImportCount++;
        ImportedLabels.Add(request.LabelIds);
        return new(true, id);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeDelay : IDelay
{
    public List<TimeSpan> Delays { get; } = [];
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}

internal sealed class FixedRandom : IRandomSource
{
    private readonly double _value;
    public FixedRandom(double value) => _value = value;
    public double NextDouble() => _value;
}
