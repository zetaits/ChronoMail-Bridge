namespace ChronoMailBridge.Core;

public sealed class MigrationCoordinator
{
    private const long GmailMaximumMessageBytes = 150L * 1024 * 1024;
    private readonly IImapSource _imap;
    private readonly IGmailDestination _gmail;
    private readonly IMigrationStore _store;
    private readonly IArchiveStore _archive;
    private readonly IErrorClassifier _classifier;
    private readonly IClock _clock;
    private readonly IDelay _delay;
    private readonly IPowerManagement _power;
    private readonly ITechnicalLog _log;
    private readonly FullJitterBackoff _backoff;
    private readonly CircuitBreaker _circuit;
    private readonly PauseController _pause = new();
    private CancellationTokenSource? _runCts;

    public MigrationCoordinator(
        IImapSource imap,
        IGmailDestination gmail,
        IMigrationStore store,
        IArchiveStore archive,
        IErrorClassifier classifier,
        IClock clock,
        IDelay delay,
        IRandomSource random,
        IPowerManagement power,
        ITechnicalLog log,
        RetrySettings? retrySettings = null)
    {
        _imap = imap;
        _gmail = gmail;
        _store = store;
        _archive = archive;
        _classifier = classifier;
        _clock = clock;
        _delay = delay;
        _power = power;
        _log = log;
        RetrySettings settings = retrySettings ?? RetrySettings.Conservative;
        _backoff = new(settings, random);
        _circuit = new(settings);
    }

    public event EventHandler<MigrationProgress>? ProgressChanged;

    public MigrationRunState State { get; private set; } = MigrationRunState.Idle;

    public void Pause()
    {
        _pause.Pause();
        State = MigrationRunState.Paused;
        _power.Restore();
        RaiseProgress(new() { RunState = State });
    }

    public void Resume()
    {
        _pause.Resume();
        State = MigrationRunState.Running;
        _power.PreventSleep();
        RaiseProgress(new() { RunState = State });
    }

    public void Cancel()
    {
        State = MigrationRunState.Cancelling;
        _pause.Resume();
        _runCts?.Cancel();
    }

    public async Task ScanAsync(
        MigrationJob job,
        string password,
        IReadOnlyCollection<string> selectedFolders,
        CancellationToken cancellationToken)
    {
        EnsureNotRunning();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = linked;
        State = MigrationRunState.Scanning;

        try
        {
            await _store.InitializeAsync(linked.Token).ConfigureAwait(false);
            await _archive.EnsureLayoutAsync(linked.Token).ConfigureAwait(false);
            await _store.SaveJobAsync(job, linked.Token).ConfigureAwait(false);
            await foreach ((SourceFolder folder, uint uidValidity, IReadOnlyList<SourceMessage> messages)
                in _imap.ScanAsync(job, password, selectedFolders, linked.Token).ConfigureAwait(false))
            {
                await _pause.WaitIfPausedAsync(linked.Token).ConfigureAwait(false);
                await _store.ObserveFolderEpochAsync(
                    job.Id,
                    folder.FullName,
                    uidValidity,
                    linked.Token).ConfigureAwait(false);
                IEnumerable<SourceMessage> included = messages.Where(
                    message => DateFilter.IsIncluded(message.InternalDate, job.MaximumInternalDate));
                await _store.UpsertDiscoveredAsync(included, linked.Token).ConfigureAwait(false);
                RaiseProgress(new()
                {
                    RunState = State,
                    CurrentFolder = folder.FullName,
                    Discovered = messages.Count
                });
            }

            State = MigrationRunState.Idle;
        }
        finally
        {
            _runCts = null;
        }
    }

    public async Task RunAsync(MigrationJob job, string password, CancellationToken cancellationToken)
    {
        EnsureNotRunning();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = linked;
        State = MigrationRunState.Running;
        if (job.PreventSleep)
        {
            _power.PreventSleep();
        }

        try
        {
            await _store.InitializeAsync(linked.Token).ConfigureAwait(false);
            await _archive.EnsureLayoutAsync(linked.Token).ConfigureAwait(false);
            await _store.RequeueInterruptedAsync(job.Id, linked.Token).ConfigureAwait(false);
            await _archive.ReconcileAsync(_store, job.Id, linked.Token).ConfigureAwait(false);

            IReadOnlyList<SourceMessage> downloads =
                await _store.GetPendingDownloadsAsync(job.Id, linked.Token).ConfigureAwait(false);
            foreach (SourceMessage message in downloads)
            {
                await CheckpointAsync(linked.Token).ConfigureAwait(false);
                await DownloadOneAsync(job, password, message, linked.Token).ConfigureAwait(false);
                await _delay.WaitAsync(job.EffectiveTransferInterval, linked.Token).ConfigureAwait(false);
            }

            IReadOnlyList<LogicalMessage> imports =
                await _store.GetPendingImportsAsync(job.Id, linked.Token).ConfigureAwait(false);
            foreach (LogicalMessage message in imports)
            {
                await CheckpointAsync(linked.Token).ConfigureAwait(false);
                await ImportOneAsync(job, message, linked.Token).ConfigureAwait(false);
                await _delay.WaitAsync(job.EffectiveTransferInterval, linked.Token).ConfigureAwait(false);
            }

            State = MigrationRunState.Completed;
            RaiseProgress(await BuildProgressAsync(job.Id, linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            State = MigrationRunState.Idle;
            throw;
        }
        catch
        {
            State = MigrationRunState.Faulted;
            throw;
        }
        finally
        {
            _power.Restore();
            _runCts = null;
        }
    }

    private async Task DownloadOneAsync(
        MigrationJob job,
        string password,
        SourceMessage message,
        CancellationToken cancellationToken)
    {
        await _store.SetAppearanceStatusAsync(
            message.Id,
            MigrationStatus.Downloading,
            null,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await using Stream source = await ExecuteWithRetryAsync(
                token => _imap.OpenMessageStreamAsync(job, password, message, token),
                cancellationToken).ConfigureAwait(false);
            ArchiveWriteResult result = await _archive.WriteAtomicAsync(
                message,
                source,
                cancellationToken).ConfigureAwait(false);
            await _store.CompleteDownloadAsync(
                message.Id,
                result,
                MessageIdentityRules.NormalizeMessageId(message.MessageId),
                cancellationToken).ConfigureAwait(false);
            _circuit.Success();
            _log.Information("message_downloaded", message.Id);
            RaiseProgress(new()
            {
                RunState = State,
                CurrentFolder = message.FolderName,
                Downloaded = 1,
                BytesDownloaded = result.Bytes,
                LastTransferUtc = _clock.UtcNow
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorClassification classification = _classifier.Classify(exception);
            await _store.SetAppearanceStatusAsync(
                message.Id,
                classification.IsTemporary ? MigrationStatus.Failed : MigrationStatus.NeedsReview,
                classification.Code,
                cancellationToken).ConfigureAwait(false);
            _log.ErrorEvent("message_download_failed", message.Id, classification.Code);
        }
    }

    private async Task ImportOneAsync(
        MigrationJob job,
        LogicalMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Size > GmailMaximumMessageBytes)
        {
            await MarkReviewAsync(message.Id, "gmail_message_too_large", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.HeaderDate is null)
        {
            await MarkReviewAsync(message.Id, "mime_date_missing_or_invalid", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message.NormalizedMessageId is null)
        {
            if (job.MissingMessageIdPolicy == MissingMessageIdPolicy.Skip)
            {
                await _store.SetLogicalStatusAsync(
                    message.Id,
                    MigrationStatus.Skipped,
                    "message_id_missing",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (job.MissingMessageIdPolicy == MissingMessageIdPolicy.NeedsReview)
            {
                await MarkReviewAsync(message.Id, "message_id_missing", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        IReadOnlyCollection<string> desiredLabels = GmailLabelMapper.Map(
            message.SourceFolders.Select(folder => (folder, InferFolderKind(folder))),
            message.IsUnread,
            message.IsStarred,
            job.Gmail.LabelPrefix);
        IReadOnlyDictionary<string, string> labels = await _gmail.EnsureLabelsAsync(
            desiredLabels,
            job.DryRun,
            cancellationToken).ConfigureAwait(false);

        if (message.NormalizedMessageId is not null)
        {
            GmailLookupResult lookup = await ExecuteWithRetryAsync(
                token => _gmail.FindByMessageIdAsync(message.NormalizedMessageId, token),
                cancellationToken).ConfigureAwait(false);
            if (lookup.IsAmbiguous)
            {
                await MarkReviewAsync(message.Id, "gmail_message_id_ambiguous", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (lookup.IsUnique)
            {
                string gmailId = lookup.MessageIds[0];
                await _gmail.AddMissingLabelsAsync(
                    gmailId,
                    labels.Values.ToArray(),
                    job.DryRun,
                    cancellationToken).ConfigureAwait(false);
                await _store.CompleteImportAsync(
                    message.Id,
                    gmailId,
                    MigrationStatus.Existing,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (job.DryRun)
        {
            return;
        }

        await _store.SetLogicalStatusAsync(
            message.Id,
            MigrationStatus.Uploading,
            null,
            cancellationToken).ConfigureAwait(false);
        string? resumeUri = await _store.GetUploadSessionAsync(message.Id, cancellationToken).ConfigureAwait(false);
        var request = new GmailImportRequest(
            message.Id,
            message.ArchivePath ?? throw new InvalidOperationException("The message has no local archive file."),
            message.Sha256 ?? throw new InvalidOperationException("The message has no hash."),
            message.Size,
            message.HeaderDate,
            labels.Values.ToArray(),
            resumeUri);

        GmailImportResult result;
        try
        {
            result = await ExecuteWithRetryAsync(
                token => _gmail.ImportAsync(request, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorClassification classification = _classifier.Classify(exception);
            await MarkReviewAsync(message.Id, classification.Code, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!result.Succeeded && result.ResponseMayHaveBeenLost && message.NormalizedMessageId is not null)
        {
            GmailLookupResult recovered = await _gmail.FindByMessageIdAsync(
                message.NormalizedMessageId,
                cancellationToken).ConfigureAwait(false);
            if (recovered.IsUnique)
            {
                result = result with { Succeeded = true, GmailMessageId = recovered.MessageIds[0] };
            }
        }

        if (!result.Succeeded || result.GmailMessageId is null)
        {
            await MarkReviewAsync(
                message.Id,
                result.ErrorCode ?? "gmail_import_uncertain",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await _store.CompleteImportAsync(
            message.Id,
            result.GmailMessageId,
            MigrationStatus.Imported,
            cancellationToken).ConfigureAwait(false);
        await _store.ClearUploadSessionAsync(message.Id, cancellationToken).ConfigureAwait(false);
        _log.Information("message_imported", message.Id);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        int failures = 0;
        while (true)
        {
            await CheckpointAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                T result = await operation(cancellationToken).ConfigureAwait(false);
                _circuit.Success();
                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ErrorClassification classification = _classifier.Classify(exception);
                failures++;
                if (!classification.IsTemporary)
                {
                    throw;
                }

                if (_circuit.Failure(_clock.UtcNow) && _circuit.OpenUntilUtc is { } openUntil)
                {
                    State = MigrationRunState.Waiting;
                    RaiseProgress(new()
                    {
                        RunState = State,
                        CircuitOpen = true,
                        NextAttemptUtc = openUntil
                    });
                    await _delay.WaitAsync(openUntil - _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    TimeSpan wait = _backoff.GetDelay(failures, classification.RetryAfter);
                    State = MigrationRunState.Waiting;
                    RaiseProgress(new() { RunState = State, NextAttemptUtc = _clock.UtcNow + wait });
                    await _delay.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                }

                State = MigrationRunState.Running;
            }
        }
    }

    private async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task MarkReviewAsync(long id, string code, CancellationToken cancellationToken) =>
        _store.SetLogicalStatusAsync(id, MigrationStatus.NeedsReview, code, cancellationToken);

    private async Task<MigrationProgress> BuildProgressAsync(Guid jobId, CancellationToken cancellationToken)
    {
        MigrationSnapshot snapshot = await _store.GetSnapshotAsync(jobId, cancellationToken).ConfigureAwait(false);
        long Count(MigrationStatus status) =>
            snapshot.Rows.Where(row => row.Status == status).Sum(row => row.Count);
        return new()
        {
            RunState = State,
            Discovered = snapshot.Rows.Sum(row => row.Count),
            Downloaded = Count(MigrationStatus.Downloaded),
            Imported = Count(MigrationStatus.Imported),
            Existing = Count(MigrationStatus.Existing),
            Skipped = Count(MigrationStatus.Skipped),
            NeedsReview = Count(MigrationStatus.NeedsReview),
            Failed = Count(MigrationStatus.Failed),
            BytesDownloaded = snapshot.Rows.Sum(row => row.Bytes)
        };
    }

    private void EnsureNotRunning()
    {
        if (_runCts is not null)
        {
            throw new InvalidOperationException("An operation is already active.");
        }
    }

    private void RaiseProgress(MigrationProgress progress) =>
        ProgressChanged?.Invoke(this, progress);

    private static FolderKind InferFolderKind(string name)
    {
        string leaf = name.Replace('\\', '/').Split('/').Last();
        return leaf.ToLowerInvariant() switch
        {
            "inbox" => FolderKind.Inbox,
            "sent" or "sent items" => FolderKind.Sent,
            "drafts" => FolderKind.Drafts,
            "trash" or "deleted items" => FolderKind.Trash,
            "spam" or "junk" => FolderKind.Spam,
            _ => FolderKind.Normal
        };
    }
}
