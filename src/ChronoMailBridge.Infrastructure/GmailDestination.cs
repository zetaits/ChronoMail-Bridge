using ChronoMailBridge.Core;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;

namespace ChronoMailBridge.Infrastructure;

public sealed class GmailDestination : IGmailDestination
{
    private static readonly string[] Scopes = [GmailService.Scope.GmailModify];
    private readonly IDataStore _tokenStore;
    private readonly ISecretStore _secretStore;
    private readonly IMigrationStore _migrationStore;
    private GmailService? _service;

    public GmailDestination(
        ISecretStore secretStore,
        IMigrationStore migrationStore)
    {
        _secretStore = secretStore;
        _migrationStore = migrationStore;
        _tokenStore = new DpapiGoogleDataStore(secretStore);
    }

    public async Task AuthorizeAsync(string clientSecretsJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecretsJson);
        await _secretStore.SaveAsync("google-client-json", clientSecretsJson, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(clientSecretsJson));
        GoogleClientSecrets clientSecrets = GoogleClientSecrets.FromStream(stream);
        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets.Secrets,
            Scopes,
            "chronomail-user",
            cancellationToken,
            _tokenStore).ConfigureAwait(false);
        _service?.Dispose();
        _service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ChronoMail Bridge"
        });
    }

    public async Task<GmailLookupResult> FindByMessageIdAsync(
        string normalizedMessageId,
        CancellationToken cancellationToken)
    {
        GmailService service = GetService();
        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");
        request.Q = $"rfc822msgid:{normalizedMessageId}";
        request.IncludeSpamTrash = true;
        request.MaxResults = 10;
        ListMessagesResponse response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new((response.Messages ?? []).Select(message => message.Id).ToArray());
    }

    public async Task<IReadOnlyDictionary<string, string>> EnsureLabelsAsync(
        IReadOnlyCollection<string> labelNames,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        GmailService service = GetService();
        IList<Label> existing = (await service.Users.Labels.List("me")
            .ExecuteAsync(cancellationToken).ConfigureAwait(false)).Labels ?? [];
        var byName = existing
            .Where(label => label.Name is not null && label.Id is not null)
            .ToDictionary(label => label.Name, label => label.Id, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in labelNames)
        {
            if (IsSystemLabel(name))
            {
                result[name] = name.ToUpperInvariant();
                continue;
            }

            if (byName.TryGetValue(name, out string? id))
            {
                result[name] = id;
                continue;
            }

            if (dryRun)
            {
                result[name] = $"dry-run:{name}";
                continue;
            }

            var label = new Label
            {
                Name = name,
                LabelListVisibility = "labelShow",
                MessageListVisibility = "show"
            };
            Label created = await service.Users.Labels.Create(label, "me")
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            result[name] = created.Id;
            byName[name] = created.Id;
        }

        return result;
    }

    public async Task<IReadOnlySet<string>> GetMessageLabelIdsAsync(
        string gmailMessageId,
        CancellationToken cancellationToken)
    {
        UsersResource.MessagesResource.GetRequest request =
            GetService().Users.Messages.Get("me", gmailMessageId);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Minimal;
        Message message = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(message.LabelIds ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public async Task AddMissingLabelsAsync(
        string gmailMessageId,
        IReadOnlyCollection<string> labelIds,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            return;
        }

        IReadOnlySet<string> current = await GetMessageLabelIdsAsync(
            gmailMessageId,
            cancellationToken).ConfigureAwait(false);
        string[] missing = labelIds.Where(id => !current.Contains(id)).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var modify = new ModifyMessageRequest { AddLabelIds = missing };
        await GetService().Users.Messages.Modify(modify, "me", gmailMessageId)
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GmailImportResult> ImportAsync(
        GmailImportRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            request.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            request.LabelIds.Count > 0 ? 1024 * 1024 : 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != request.Size)
        {
            return new(false, null, ErrorCode: "archive_size_changed");
        }

        var metadata = new Message { LabelIds = request.LabelIds.ToList() };
        UsersResource.MessagesResource.ImportMediaUpload upload =
            GetService().Users.Messages.Import(metadata, "me", stream, "message/rfc822");
        upload.InternalDateSource =
            UsersResource.MessagesResource.ImportMediaUpload.InternalDateSourceEnum.DateHeader;
        upload.ProcessForCalendar = false;
        upload.NeverMarkSpam = true;
        upload.Deleted = false;
        upload.ChunkSize = 10 * 1024 * 1024;

        string? sessionUri = null;
        upload.UploadSessionData += data =>
        {
            sessionUri = data.UploadUri.AbsoluteUri;
            string protectedUri = _secretStore.Protect(sessionUri);
            _migrationStore.SaveUploadSessionAsync(
                request.LogicalMessageId,
                protectedUri,
                request.Sha256,
                request.Size,
                request.FilePath,
                CancellationToken.None).GetAwaiter().GetResult();
        };

        IUploadProgress progress;
        try
        {
            if (request.ResumeUri is not null)
            {
                string uri = _secretStore.Unprotect(request.ResumeUri);
                progress = await upload.ResumeAsync(new Uri(uri), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                progress = await upload.UploadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (GoogleApiException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new(false, null, sessionUri, ResponseMayHaveBeenLost: true, "network_response_lost");
        }

        if (progress.Status != UploadStatus.Completed || upload.ResponseBody?.Id is null)
        {
            return new(
                false,
                null,
                sessionUri,
                ResponseMayHaveBeenLost: progress.Exception is not null,
                progress.Exception?.GetType().Name ?? "gmail_upload_incomplete");
        }

        return new(true, upload.ResponseBody.Id, sessionUri);
    }

    public ValueTask DisposeAsync()
    {
        _service?.Dispose();
        return ValueTask.CompletedTask;
    }

    private GmailService GetService() =>
        _service ?? throw new InvalidOperationException("Google has not been authorized yet.");

    private static bool IsSystemLabel(string name) =>
        name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("UNREAD", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("STARRED", StringComparison.OrdinalIgnoreCase);
}
