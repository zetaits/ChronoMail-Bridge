using System.Globalization;
using ChronoMailBridge.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace ChronoMailBridge.Infrastructure;

public sealed class MailKitImapSource : IImapSource
{
    public async Task TestConnectionAsync(
        ImapConnectionOptions options,
        string password,
        CancellationToken cancellationToken)
    {
        using ImapClient client = await ConnectAsync(options, password, cancellationToken).ConfigureAwait(false);
        await client.NoOpAsync(cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SourceFolder>> ListFoldersAsync(
        ImapConnectionOptions options,
        string password,
        CancellationToken cancellationToken)
    {
        using ImapClient client = await ConnectAsync(options, password, cancellationToken).ConfigureAwait(false);
        var results = new List<SourceFolder>();
        foreach (FolderNamespace folderNamespace in client.PersonalNamespaces)
        {
            IMailFolder root = client.GetFolder(folderNamespace);
            await AddChildrenAsync(root, results, cancellationToken).ConfigureAwait(false);
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        return results.OrderBy(folder => folder.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async IAsyncEnumerable<(
        SourceFolder Folder,
        uint UidValidity,
        IReadOnlyList<SourceMessage> Messages)> ScanAsync(
        MigrationJob job,
        string password,
        IReadOnlyCollection<string> selectedFolders,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using ImapClient client = await ConnectAsync(job.Imap, password, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> folderNames = selectedFolders.Count == 0
            ? (await ListFolderNamesAsync(client, cancellationToken).ConfigureAwait(false))
            : selectedFolders.ToArray();

        foreach (string folderName in folderNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMailFolder folder = client.GetFolder(folderName, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            IList<UniqueId> uids = await folder.SearchAsync(SearchQuery.All, cancellationToken)
                .ConfigureAwait(false);

            for (int offset = 0; offset < uids.Count; offset += Math.Max(1, job.Imap.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<UniqueId> batch = uids.Skip(offset).Take(job.Imap.BatchSize).ToList();
                var headers = new HashSet<HeaderId> { HeaderId.Date, HeaderId.MessageId };
                IList<IMessageSummary> summaries = await folder.FetchAsync(
                    batch,
                    MessageSummaryItems.UniqueId |
                    MessageSummaryItems.Flags |
                    MessageSummaryItems.InternalDate |
                    MessageSummaryItems.Size,
                    headers,
                    cancellationToken).ConfigureAwait(false);

                var messages = new List<SourceMessage>(summaries.Count);
                foreach (IMessageSummary summary in summaries)
                {
                    DateTimeOffset internalDate = summary.InternalDate ?? DateTimeOffset.MinValue;
                    if (!DateFilter.IsIncluded(internalDate, job.MaximumInternalDate))
                    {
                        continue;
                    }

                    DateTimeOffset? headerDate = TryParseDate(summary.Headers?[HeaderId.Date]);
                    string? messageId = MessageIdentityRules.NormalizeMessageId(
                        summary.Headers?[HeaderId.MessageId]);
                    HashSet<string> flags = ToFlags(summary.Flags);
                    messages.Add(new(
                        0,
                        job.Id,
                        folder.FullName,
                        folder.UidValidity,
                        summary.UniqueId.Id,
                        internalDate,
                        headerDate,
                        messageId,
                        summary.Size ?? 0,
                        flags));
                }

                yield return (
                    new SourceFolder(folder.FullName, folder.DirectorySeparator, InferKind(folder)),
                    folder.UidValidity,
                    messages);
            }

            await folder.CloseAsync(expunge: false, cancellationToken).ConfigureAwait(false);
        }

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenMessageStreamAsync(
        MigrationJob job,
        string password,
        SourceMessage message,
        CancellationToken cancellationToken)
    {
        using ImapClient client = await ConnectAsync(job.Imap, password, cancellationToken).ConfigureAwait(false);
        IMailFolder folder = client.GetFolder(message.FolderName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (folder.UidValidity != message.UidValidity)
        {
            throw new InvalidOperationException("uidvalidity_changed");
        }

        // MailKit usa BODY.PEEK para GetStream y no altera el flag \Seen.
        Stream stream = await folder.GetStreamAsync(
            new UniqueId(message.Uid),
            cancellationToken).ConfigureAwait(false);
        await folder.CloseAsync(expunge: false, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return stream;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<ImapClient> ConnectAsync(
        ImapConnectionOptions options,
        string password,
        CancellationToken cancellationToken)
    {
        var client = new ImapClient
        {
            Timeout = checked((int)(options.OperationTimeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds)
        };
        try
        {
            SecureSocketOptions socketOptions = options.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(
                options.Host,
                options.Port,
                socketOptions,
                cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(
                options.UserName,
                password,
                cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task AddChildrenAsync(
        IMailFolder parent,
        ICollection<SourceFolder> results,
        CancellationToken cancellationToken)
    {
        IList<IMailFolder> children = await parent.GetSubfoldersAsync(false, cancellationToken)
            .ConfigureAwait(false);
        foreach (IMailFolder child in children)
        {
            if (!child.Attributes.HasFlag(FolderAttributes.NonExistent) &&
                !child.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                results.Add(new(child.FullName, child.DirectorySeparator, InferKind(child)));
            }

            if (!child.Attributes.HasFlag(FolderAttributes.HasNoChildren))
            {
                await AddChildrenAsync(child, results, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ListFolderNamesAsync(
        ImapClient client,
        CancellationToken cancellationToken)
    {
        var folders = new List<SourceFolder>();
        foreach (FolderNamespace folderNamespace in client.PersonalNamespaces)
        {
            await AddChildrenAsync(client.GetFolder(folderNamespace), folders, cancellationToken)
                .ConfigureAwait(false);
        }

        return folders.Select(folder => folder.FullName).ToArray();
    }

    private static FolderKind InferKind(IMailFolder folder)
    {
        FolderAttributes attributes = folder.Attributes;
        if (attributes.HasFlag(FolderAttributes.Inbox))
        {
            return FolderKind.Inbox;
        }

        if (attributes.HasFlag(FolderAttributes.Sent))
        {
            return FolderKind.Sent;
        }

        if (attributes.HasFlag(FolderAttributes.Drafts))
        {
            return FolderKind.Drafts;
        }

        if (attributes.HasFlag(FolderAttributes.Trash))
        {
            return FolderKind.Trash;
        }

        return attributes.HasFlag(FolderAttributes.Junk)
            ? FolderKind.Spam
            : FolderKind.Normal;
    }

    private static HashSet<string> ToFlags(MessageFlags? flags)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (flags?.HasFlag(MessageFlags.Seen) == true) values.Add("\\Seen");
        if (flags?.HasFlag(MessageFlags.Flagged) == true) values.Add("\\Flagged");
        if (flags?.HasFlag(MessageFlags.Answered) == true) values.Add("\\Answered");
        if (flags?.HasFlag(MessageFlags.Draft) == true) values.Add("\\Draft");
        if (flags?.HasFlag(MessageFlags.Deleted) == true) values.Add("\\Deleted");
        return values;
    }

    private static DateTimeOffset? TryParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset parsed)
            ? parsed
            : null;
}
