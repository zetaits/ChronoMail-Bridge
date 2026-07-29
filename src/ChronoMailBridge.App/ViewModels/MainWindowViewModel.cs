using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ChronoMailBridge.Core;
using ChronoMailBridge.Infrastructure;
using Microsoft.Win32;

namespace ChronoMailBridge.App.ViewModels;

public enum NotificationKind
{
    Information,
    Success,
    Warning,
    Error
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Guid _jobId = Guid.NewGuid();
    private CancellationTokenSource? _operation;
    private MigrationCoordinator? _coordinator;
    private SqliteMigrationStore? _store;
    private GmailDestination? _gmail;
    private MailKitImapSource? _imap;
    private SerilogTechnicalLog? _log;
    private DpapiSecretStore? _secrets;
    private string _password = string.Empty;
    private string _status = "Ready. Simulation mode is enabled.";
    private string _archiveRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "ChronoMail Bridge Archive");
    private string _clientJsonPath = string.Empty;
    private string _selectedFolders = "Inbox,Sent";
    private bool _simulationMode = true;
    private bool _preventSleep = true;
    private bool _isBusy;
    private bool _isMigrationRunning;
    private bool _isNotificationVisible;
    private string _activityTitle = "Working\u2026";
    private string _notificationTitle = string.Empty;
    private string _notificationMessage = string.Empty;
    private NotificationKind _notificationKind = NotificationKind.Information;
    private MigrationProgress _progress = MigrationProgress.Empty;

    public MainWindowViewModel()
    {
        TestImapCommand = new AsyncRelayCommand(TestImapAsync, () => !IsBusy);
        SelectClientJsonCommand = new RelayCommand(SelectClientJson);
        SelectArchiveCommand = new RelayCommand(SelectArchive);
        AuthorizeGoogleCommand = new AsyncRelayCommand(AuthorizeGoogleAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        PauseCommand = new RelayCommand(Pause, () => IsBusy && _coordinator is not null);
        ResumeCommand = new RelayCommand(Resume, () => IsBusy && _coordinator is not null);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        RetryCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
        DismissNotificationCommand = new RelayCommand(DismissNotification);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ImapHost { get; set; } = "imap.mail.yahoo.com";
    public int ImapPort { get; set; } = 993;
    public string ImapUser { get; set; } = string.Empty;
    public bool RememberPassword { get; set; }
    public string LabelPrefix { get; set; } = "Imported from Turbify";
    public DateTime? MaximumDate { get; set; }

    public string ArchiveRoot
    {
        get => _archiveRoot;
        set => Set(ref _archiveRoot, value);
    }

    public string ClientJsonPath
    {
        get => _clientJsonPath;
        set => Set(ref _clientJsonPath, value);
    }

    public string SelectedFolders
    {
        get => _selectedFolders;
        set => Set(ref _selectedFolders, value);
    }

    public bool SimulationMode
    {
        get => _simulationMode;
        set => Set(ref _simulationMode, value);
    }

    public bool PreventSleep
    {
        get => _preventSleep;
        set => Set(ref _preventSleep, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public bool IsMigrationRunning
    {
        get => _isMigrationRunning;
        private set => Set(ref _isMigrationRunning, value);
    }

    public bool IsNotificationVisible
    {
        get => _isNotificationVisible;
        private set => Set(ref _isNotificationVisible, value);
    }

    public string ActivityTitle
    {
        get => _activityTitle;
        private set => Set(ref _activityTitle, value);
    }

    public string NotificationTitle
    {
        get => _notificationTitle;
        private set => Set(ref _notificationTitle, value);
    }

    public string NotificationMessage
    {
        get => _notificationMessage;
        private set => Set(ref _notificationMessage, value);
    }

    public NotificationKind NotificationKind
    {
        get => _notificationKind;
        private set => Set(ref _notificationKind, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public MigrationProgress Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public AsyncRelayCommand TestImapCommand { get; }
    public RelayCommand SelectClientJsonCommand { get; }
    public RelayCommand SelectArchiveCommand { get; }
    public AsyncRelayCommand AuthorizeGoogleCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand DismissNotificationCommand { get; }

    public void SetSessionPassword(string password) => _password = password;

    public async ValueTask DisposeAsync()
    {
        Cancel();
        if (_gmail is not null) await _gmail.DisposeAsync().ConfigureAwait(false);
        if (_imap is not null) await _imap.DisposeAsync().ConfigureAwait(false);
        if (_store is not null) await _store.DisposeAsync().ConfigureAwait(false);
        _log?.Dispose();
        _operation?.Dispose();
    }

    private async Task TestImapAsync()
    {
        if (SimulationMode)
        {
            Status = "Simulated IMAP connection successful; no server was contacted.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ImapUser))
        {
            Status = "Enter your email address and app password.";
            return;
        }

        IsBusy = true;
        try
        {
            await EnsureServicesAsync();
            string password = await GetImapPasswordAsync();
            if (string.IsNullOrEmpty(password))
            {
                Status = "Enter the app password.";
                return;
            }

            await _imap!.TestConnectionAsync(BuildImapOptions(), password, CancellationToken.None);
            await SavePasswordPreferenceAsync(password);
            Status = "IMAP connection successful and opened in safe mode.";
        }
        catch (Exception exception)
        {
            Status = $"Could not connect: {new DefaultErrorClassifier().Classify(exception).Code}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AuthorizeGoogleAsync()
    {
        if (SimulationMode)
        {
            Status = "Simulated Google authorization successful; the browser was not opened.";
            return;
        }

        if (!File.Exists(ClientJsonPath))
        {
            Status = "Select the desktop OAuth client JSON file first.";
            return;
        }

        IsBusy = true;
        try
        {
            await EnsureServicesAsync();
            string json = await File.ReadAllTextAsync(ClientJsonPath);
            await _gmail!.AuthorizeAsync(json, CancellationToken.None);
            Status = "Google authorized with the gmail.modify scope.";
        }
        catch (Exception exception)
        {
            Status = $"Authorization not completed: {new DefaultErrorClassifier().Classify(exception).Code}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ScanAsync()
    {
        if (SimulationMode)
        {
            await RunUiSimulationAsync(scanOnly: true);
            return;
        }

        await RunCoordinatorAsync(scanOnly: true);
    }

    private async Task StartAsync()
    {
        if (SimulationMode)
        {
            await RunUiSimulationAsync(scanOnly: false);
            return;
        }

        await RunCoordinatorAsync(scanOnly: false);
    }

    private async Task RunCoordinatorAsync(bool scanOnly)
    {
        BeginMigrationActivity(scanOnly);
        _operation = new CancellationTokenSource();
        try
        {
            await EnsureServicesAsync();
            string password = await GetImapPasswordAsync();
            if (string.IsNullOrEmpty(password))
            {
                Status = "Enter the app password or enable a saved password.";
                ShowNotification(
                    "Migration could not start",
                    "Enter the IMAP app password or enable a saved password, then try again.",
                    NotificationKind.Error);
                return;
            }

            await SavePasswordPreferenceAsync(password);
            MigrationJob job = BuildJob(dryRun: scanOnly);
            string[] folders = SelectedFolders.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (scanOnly)
            {
                await _coordinator!.ScanAsync(job, password, folders, _operation.Token);
                Status = "Scan completed. No message bodies were downloaded and Gmail was not modified.";
                ShowNotification(
                    "Scan completed",
                    "The safety scan finished without downloading message bodies or changing Gmail.",
                    NotificationKind.Success);
            }
            else
            {
                await _coordinator!.RunAsync(job, password, _operation.Token);
                Status = "Migration completed. Review the report and any issues.";
                ShowNotification(
                    "Migration completed",
                    "The migration finished. Review the report for its summary and any items needing attention.",
                    NotificationKind.Success);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Operation canceled at a safe checkpoint; it can be resumed.";
            ShowNotification(
                "Operation canceled",
                "Work stopped at a safe checkpoint and can be resumed later.",
                NotificationKind.Warning);
        }
        catch (Exception exception)
        {
            string code = new DefaultErrorClassifier().Classify(exception).Code;
            Status = $"Operation stopped: {code}";
            ShowNotification(
                "Migration needs attention",
                $"The operation stopped with technical code {code}. You can retry after checking the configuration.",
                NotificationKind.Error);
        }
        finally
        {
            EndMigrationActivity();
        }
    }

    private async Task RunUiSimulationAsync(bool scanOnly)
    {
        BeginMigrationActivity(scanOnly);
        _operation = new CancellationTokenSource();
        try
        {
            Status = scanOnly ? "Scanning 100 synthetic messages…" : "Simulated migration in progress…";
            await Application.Current.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle);

            long bytes = 0;
            for (int index = 1; index <= 100; index++)
            {
                _operation.Token.ThrowIfCancellationRequested();
                await Task.Delay(40, _operation.Token);
                bytes += 128 * 1024 + (index % 7) * 4096;
                Progress = new()
                {
                    RunState = scanOnly ? MigrationRunState.Scanning : MigrationRunState.Running,
                    CurrentFolder = index % 3 == 0 ? "Clients/2020" : index % 2 == 0 ? "Sent" : "Inbox",
                    Discovered = index,
                    Downloaded = scanOnly ? 0 : index,
                    Imported = scanOnly ? 0 : index - (index / 10),
                    Existing = scanOnly ? 0 : index / 10,
                    NeedsReview = index / 33,
                    BytesDownloaded = scanOnly ? 0 : bytes,
                    BytesPerSecond = 8 * 1024 * 1024,
                    LastTransferUtc = DateTimeOffset.UtcNow
                };
            }

            Progress = Progress with { RunState = MigrationRunState.Completed };
            Status = scanOnly
                ? "Scan simulation completed: 100 messages, no message bodies or external changes."
                : "Simulated migration completed: resumable and no real accounts used.";
            ShowNotification(
                scanOnly ? "Scan completed" : "Migration completed",
                scanOnly
                    ? "The simulation scanned 100 messages without downloading content or making external changes."
                    : "The simulated migration finished successfully without using real accounts.",
                NotificationKind.Success);
        }
        catch (OperationCanceledException)
        {
            Status = "Simulation canceled.";
            ShowNotification(
                "Simulation canceled",
                "The simulated operation stopped safely.",
                NotificationKind.Warning);
        }
        finally
        {
            EndMigrationActivity();
        }
    }

    private async Task ExportAsync()
    {
        if (SimulationMode)
        {
            Directory.CreateDirectory(Path.Combine(ArchiveRoot, "reports"));
            MigrationSnapshot snapshot = new(
                [new("Inbox", 2020, MigrationStatus.Imported, Progress.Imported, Progress.BytesDownloaded)],
                []);
            await new CsvReportWriter().WriteAsync(
                Path.Combine(ArchiveRoot, "reports"),
                snapshot,
                CancellationToken.None);
            Status = "Simulated report exported to the reports folder.";
            return;
        }

        await EnsureServicesAsync();
        MigrationSnapshot realSnapshot = await _store!.GetSnapshotAsync(_jobId, CancellationToken.None);
        await new CsvReportWriter().WriteAsync(
            Path.Combine(ArchiveRoot, "reports"),
            realSnapshot,
            CancellationToken.None);
        Status = "Report exported without sensitive content.";
    }

    private async Task EnsureServicesAsync()
    {
        if (_coordinator is not null)
        {
            return;
        }

        var archive = new FileArchiveStore(ArchiveRoot);
        await archive.EnsureLayoutAsync(CancellationToken.None);
        _store = new SqliteMigrationStore(Path.Combine(ArchiveRoot, "state", "chronomail.db"));
        await _store.InitializeAsync(CancellationToken.None);
        _secrets = new DpapiSecretStore(Path.Combine(ArchiveRoot, "state", "secrets"));
        _imap = new MailKitImapSource();
        _gmail = new GmailDestination(_secrets, _store);
        _log = new SerilogTechnicalLog(Path.Combine(ArchiveRoot, "logs"));
        _coordinator = new(
            _imap,
            _gmail,
            _store,
            archive,
            new DefaultErrorClassifier(),
            new SystemClock(),
            new TaskDelay(),
            new ThreadSafeRandomSource(),
            new WindowsPowerManagement(),
            _log);
        _coordinator.ProgressChanged += (_, progress) =>
            Application.Current.Dispatcher.Invoke(() => Progress = MergeProgress(Progress, progress));
    }

    private async Task<string> GetImapPasswordAsync()
    {
        if (!string.IsNullOrEmpty(_password))
        {
            return _password;
        }

        if (_secrets is null || string.IsNullOrWhiteSpace(ImapUser))
        {
            return string.Empty;
        }

        return await _secrets.GetAsync(ImapPasswordKey(), CancellationToken.None) ?? string.Empty;
    }

    private async Task SavePasswordPreferenceAsync(string password)
    {
        if (_secrets is null)
        {
            return;
        }

        if (RememberPassword)
        {
            await _secrets.SaveAsync(ImapPasswordKey(), password, CancellationToken.None);
        }
        else
        {
            await _secrets.DeleteAsync(ImapPasswordKey(), CancellationToken.None);
        }
    }

    private string ImapPasswordKey() => $"imap-password:{ImapUser.Trim().ToLowerInvariant()}";

    private MigrationJob BuildJob(bool dryRun) => new(
        _jobId,
        "Turbify migration",
        ArchiveRoot,
        BuildImapOptions(),
        new GmailOptions(LabelPrefix: LabelPrefix),
        MaximumDate is null
            ? null
            : new DateTimeOffset(MaximumDate.Value.Date.AddDays(1).AddTicks(-1)),
        MissingMessageIdPolicy.NeedsReview,
        PreventSleep,
        TimeSpan.FromSeconds(1),
        dryRun);

    private ImapConnectionOptions BuildImapOptions() =>
        new(ImapHost, ImapPort, true, ImapUser, 250);

    private void Pause()
    {
        _coordinator?.Pause();
        Status = "Paused at a cooperative checkpoint; Windows may now sleep.";
    }

    private void Resume()
    {
        _coordinator?.Resume();
        Status = "Resumed.";
    }

    private void Cancel()
    {
        _coordinator?.Cancel();
        _operation?.Cancel();
    }

    private void BeginMigrationActivity(bool scanOnly)
    {
        DismissNotification();
        IsMigrationRunning = !scanOnly;
        ActivityTitle = scanOnly ? "Safety scan in progress\u2026" : "Migration in progress\u2026";
        Status = scanOnly
            ? "Starting the safety scan\u2026"
            : "Migration started. Messages are being processed safely\u2026";
        IsBusy = true;
    }

    private void EndMigrationActivity()
    {
        IsMigrationRunning = false;
        IsBusy = false;
    }

    private void ShowNotification(string title, string message, NotificationKind kind)
    {
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationKind = kind;
        IsNotificationVisible = true;
    }

    private void DismissNotification() => IsNotificationVisible = false;

    private void SelectClientJson()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Google OAuth client (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            ClientJsonPath = dialog.FileName;
        }
    }

    private void SelectArchive()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder on a BitLocker-protected drive",
            InitialDirectory = Directory.Exists(ArchiveRoot) ? ArchiveRoot : null
        };
        if (dialog.ShowDialog() == true)
        {
            ArchiveRoot = dialog.FolderName;
        }
    }

    private static MigrationProgress MergeProgress(MigrationProgress old, MigrationProgress update) => new()
    {
        RunState = update.RunState,
        CurrentFolder = update.CurrentFolder ?? old.CurrentFolder,
        Discovered = update.Discovered == 0 ? old.Discovered : old.Discovered + update.Discovered,
        Downloaded = update.Downloaded == 0 ? old.Downloaded : old.Downloaded + update.Downloaded,
        Imported = update.Imported == 0 ? old.Imported : old.Imported + update.Imported,
        Existing = update.Existing == 0 ? old.Existing : old.Existing + update.Existing,
        Skipped = update.Skipped == 0 ? old.Skipped : old.Skipped + update.Skipped,
        NeedsReview = update.NeedsReview == 0 ? old.NeedsReview : old.NeedsReview + update.NeedsReview,
        Failed = update.Failed == 0 ? old.Failed : old.Failed + update.Failed,
        BytesDownloaded = update.BytesDownloaded == 0
            ? old.BytesDownloaded
            : old.BytesDownloaded + update.BytesDownloaded,
        BytesPerSecond = update.BytesPerSecond == 0 ? old.BytesPerSecond : update.BytesPerSecond,
        LastTransferUtc = update.LastTransferUtc ?? old.LastTransferUtc,
        NextAttemptUtc = update.NextAttemptUtc,
        CircuitOpen = update.CircuitOpen
    };

    private void RaiseCommands()
    {
        TestImapCommand.RaiseCanExecuteChanged();
        AuthorizeGoogleCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
}
