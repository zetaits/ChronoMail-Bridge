using System.Globalization;
using System.Text.Json;
using ChronoMailBridge.Core;
using Microsoft.Data.Sqlite;

namespace ChronoMailBridge.Infrastructure;

public sealed class SqliteMigrationStore : IMigrationStore
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public SqliteMigrationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The database requires a directory."));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                archive_root TEXT NOT NULL,
                configuration_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS folder_epochs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                folder_name TEXT NOT NULL,
                uid_validity INTEGER NOT NULL,
                first_seen_utc TEXT NOT NULL,
                is_current INTEGER NOT NULL,
                UNIQUE(job_id, folder_name, uid_validity),
                FOREIGN KEY(job_id) REFERENCES jobs(id)
            );
            CREATE TABLE IF NOT EXISTS appearances (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                folder_name TEXT NOT NULL,
                uid_validity INTEGER NOT NULL,
                uid INTEGER NOT NULL,
                internal_date TEXT NOT NULL,
                header_date TEXT,
                message_id TEXT,
                size INTEGER NOT NULL,
                flags_json TEXT NOT NULL,
                sha256 TEXT,
                archive_path TEXT,
                status TEXT NOT NULL,
                logical_id INTEGER,
                error_code TEXT,
                updated_utc TEXT NOT NULL,
                UNIQUE(job_id, folder_name, uid_validity, uid),
                FOREIGN KEY(job_id) REFERENCES jobs(id),
                FOREIGN KEY(logical_id) REFERENCES logical_messages(id)
            );
            CREATE TABLE IF NOT EXISTS logical_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                normalized_message_id TEXT,
                sha256 TEXT,
                size INTEGER NOT NULL,
                archive_path TEXT NOT NULL,
                internal_date TEXT NOT NULL,
                header_date TEXT,
                is_unread INTEGER NOT NULL,
                is_starred INTEGER NOT NULL,
                status TEXT NOT NULL,
                gmail_message_id TEXT,
                error_code TEXT,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY(job_id) REFERENCES jobs(id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_logical_hash
                ON logical_messages(job_id, sha256) WHERE sha256 IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_logical_message_id
                ON logical_messages(job_id, normalized_message_id);
            CREATE TABLE IF NOT EXISTS gmail_labels (
                job_id TEXT NOT NULL,
                label_name TEXT NOT NULL,
                gmail_label_id TEXT NOT NULL,
                PRIMARY KEY(job_id, label_name)
            );
            CREATE TABLE IF NOT EXISTS attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                technical_id INTEGER,
                operation TEXT NOT NULL,
                classification TEXT,
                error_code TEXT,
                occurred_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS upload_sessions (
                logical_id INTEGER PRIMARY KEY,
                protected_uri TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                size INTEGER NOT NULL,
                archive_path TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY(logical_id) REFERENCES logical_messages(id)
            );
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                event_name TEXT NOT NULL,
                technical_id INTEGER,
                code TEXT,
                occurred_utc TEXT NOT NULL
            );
            """, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM schema_info;";
        long rows = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (rows == 0)
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_info(version) VALUES ($version);";
            insert.Parameters.AddWithValue("$version", SchemaVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveJobAsync(MigrationJob job, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(id, name, archive_root, configuration_json, updated_utc)
            VALUES ($id, $name, $root, $configuration, $now)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name,
                archive_root=excluded.archive_root,
                configuration_json=excluded.configuration_json,
                updated_utc=excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$root", job.ArchiveRoot);
        command.Parameters.AddWithValue("$configuration", JsonSerializer.Serialize(new
        {
            job.MaximumInternalDate,
            job.MissingMessageIdPolicy,
            job.PreventSleep,
            job.DryRun,
            job.Gmail.LabelPrefix,
            job.Imap.Host,
            job.Imap.Port,
            job.Imap.BatchSize
        }));
        command.Parameters.AddWithValue("$now", UtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FolderEpoch> ObserveFolderEpochAsync(
        Guid jobId,
        string folderName,
        uint uidValidity,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, """
            UPDATE folder_epochs SET is_current=0
            WHERE job_id=$job AND folder_name=$folder AND uid_validity<>$validity;
            UPDATE appearances SET status='NeedsReview', error_code='uidvalidity_changed', updated_utc=$now
            WHERE job_id=$job AND folder_name=$folder AND uid_validity<>$validity
              AND status IN ('Discovered','Downloading','Failed');
            INSERT INTO folder_epochs(job_id, folder_name, uid_validity, first_seen_utc, is_current)
            VALUES ($job, $folder, $validity, $now, 1)
            ON CONFLICT(job_id, folder_name, uid_validity) DO UPDATE SET is_current=1;
            """,
            cancellationToken,
            ("$job", jobId.ToString("D")),
            ("$folder", folderName),
            ("$validity", (long)uidValidity),
            ("$now", UtcNow())).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT id, first_seen_utc, is_current FROM folder_epochs
            WHERE job_id=$job AND folder_name=$folder AND uid_validity=$validity;
            """;
        query.Parameters.AddWithValue("$job", jobId.ToString("D"));
        query.Parameters.AddWithValue("$folder", folderName);
        query.Parameters.AddWithValue("$validity", (long)uidValidity);
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(
            reader.GetInt64(0),
            jobId,
            folderName,
            uidValidity,
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetBoolean(2));
    }

    public async Task UpsertDiscoveredAsync(
        IEnumerable<SourceMessage> messages,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (SourceMessage message in messages)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO appearances(
                    job_id, folder_name, uid_validity, uid, internal_date, header_date,
                    message_id, size, flags_json, status, updated_utc)
                VALUES ($job, $folder, $validity, $uid, $internal, $header, $messageId,
                    $size, $flags, 'Discovered', $now)
                ON CONFLICT(job_id, folder_name, uid_validity, uid) DO UPDATE SET
                    internal_date=excluded.internal_date,
                    header_date=excluded.header_date,
                    message_id=excluded.message_id,
                    size=excluded.size,
                    flags_json=excluded.flags_json,
                    updated_utc=excluded.updated_utc
                WHERE appearances.status NOT IN ('Downloaded','Imported','Existing');
                """;
            command.Parameters.AddWithValue("$job", message.JobId.ToString("D"));
            command.Parameters.AddWithValue("$folder", message.FolderName);
            command.Parameters.AddWithValue("$validity", (long)message.UidValidity);
            command.Parameters.AddWithValue("$uid", (long)message.Uid);
            command.Parameters.AddWithValue("$internal", message.InternalDate.ToString("O"));
            command.Parameters.AddWithValue("$header", Db(message.HeaderDate?.ToString("O")));
            command.Parameters.AddWithValue("$messageId", Db(MessageIdentityRules.NormalizeMessageId(message.MessageId)));
            command.Parameters.AddWithValue("$size", message.Size);
            command.Parameters.AddWithValue("$flags", JsonSerializer.Serialize(message.Flags));
            command.Parameters.AddWithValue("$now", UtcNow());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SourceMessage>> GetPendingDownloadsAsync(
        Guid jobId,
        CancellationToken cancellationToken) =>
        ReadAppearancesAsync(
            jobId,
            "status IN ('Discovered','Failed')",
            cancellationToken);

    public async Task SetAppearanceStatusAsync(
        long appearanceId,
        MigrationStatus status,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE appearances SET status=$status, error_code=$error, updated_utc=$now
            WHERE id=$id;
            """,
            cancellationToken,
            ("$status", status.ToString()),
            ("$error", Db(errorCode)),
            ("$now", UtcNow()),
            ("$id", appearanceId)).ConfigureAwait(false);
    }

    public async Task CompleteDownloadAsync(
        long appearanceId,
        ArchiveWriteResult result,
        string? normalizedMessageId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, """
            UPDATE appearances SET
                sha256=$hash, archive_path=$path, status='Downloaded',
                message_id=COALESCE($messageId, message_id), error_code=NULL, updated_utc=$now
            WHERE id=$id;
            """,
            cancellationToken,
            ("$hash", result.Sha256),
            ("$path", result.FullPath),
            ("$messageId", Db(normalizedMessageId)),
            ("$now", UtcNow()),
            ("$id", appearanceId)).ConfigureAwait(false);

        await using SqliteCommand source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = """
            SELECT job_id, internal_date, header_date, message_id, size, flags_json
            FROM appearances WHERE id=$id;
            """;
        source.Parameters.AddWithValue("$id", appearanceId);
        await using SqliteDataReader reader =
            await source.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The downloaded message occurrence does not exist.");
        }

        string jobId = reader.GetString(0);
        string internalDate = reader.GetString(1);
        object headerDate = reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2);
        object messageId = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
        long size = reader.GetInt64(4);
        HashSet<string> flags = JsonSerializer.Deserialize<HashSet<string>>(reader.GetString(5)) ?? [];
        await reader.DisposeAsync().ConfigureAwait(false);

        long logicalId = await FindLogicalIdAsync(
            connection,
            transaction,
            jobId,
            result.Sha256,
            messageId as string,
            cancellationToken).ConfigureAwait(false);
        if (logicalId == 0)
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO logical_messages(
                    job_id, normalized_message_id, sha256, size, archive_path,
                    internal_date, header_date, is_unread, is_starred, status, updated_utc)
                VALUES ($job, $messageId, $hash, $size, $path, $internal, $header,
                    $unread, $starred, 'Downloaded', $now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$job", jobId);
            insert.Parameters.AddWithValue("$messageId", messageId);
            insert.Parameters.AddWithValue("$hash", result.Sha256);
            insert.Parameters.AddWithValue("$size", size);
            insert.Parameters.AddWithValue("$path", result.FullPath);
            insert.Parameters.AddWithValue("$internal", internalDate);
            insert.Parameters.AddWithValue("$header", headerDate);
            insert.Parameters.AddWithValue("$unread", flags.Contains("\\Seen") ? 0 : 1);
            insert.Parameters.AddWithValue("$starred", flags.Contains("\\Flagged") ? 1 : 0);
            insert.Parameters.AddWithValue("$now", UtcNow());
            logicalId = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The logical message could not be created."));
        }

        await ExecuteAsync(connection, transaction, """
            UPDATE appearances SET logical_id=$logical, updated_utc=$now WHERE id=$appearance;
            UPDATE logical_messages SET
                is_unread=MAX(is_unread, $unread),
                is_starred=MAX(is_starred, $starred),
                updated_utc=$now
            WHERE id=$logical;
            """,
            cancellationToken,
            ("$logical", logicalId),
            ("$appearance", appearanceId),
            ("$unread", flags.Contains("\\Seen") ? 0 : 1),
            ("$starred", flags.Contains("\\Flagged") ? 1 : 0),
            ("$now", UtcNow())).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LogicalMessage>> GetPendingImportsAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var results = new List<LogicalMessage>();
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.id, l.normalized_message_id, l.sha256, l.status, l.gmail_message_id,
                   l.archive_path, l.size, l.internal_date, l.header_date, l.is_unread,
                   l.is_starred, GROUP_CONCAT(DISTINCT a.folder_name)
            FROM logical_messages l
            JOIN appearances a ON a.logical_id=l.id
            WHERE l.job_id=$job AND l.status IN ('Downloaded','Failed','Uploading')
            GROUP BY l.id
            ORDER BY l.internal_date, l.id;
            """;
        command.Parameters.AddWithValue("$job", jobId.ToString("D"));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string[] folders = reader.IsDBNull(11)
                ? []
                : reader.GetString(11).Split(',', StringSplitOptions.RemoveEmptyEntries);
            results.Add(new(
                reader.GetInt64(0),
                jobId,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Enum.Parse<MigrationStatus>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                folders,
                reader.GetString(5),
                reader.GetInt64(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.IsDBNull(8)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                reader.GetBoolean(9),
                reader.GetBoolean(10)));
        }

        return results;
    }

    public async Task SetLogicalStatusAsync(
        long logicalMessageId,
        MigrationStatus status,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE logical_messages SET status=$status, error_code=$error, updated_utc=$now
            WHERE id=$id;
            UPDATE appearances SET status=$status, error_code=$error, updated_utc=$now
            WHERE logical_id=$id;
            """,
            cancellationToken,
            ("$status", status.ToString()),
            ("$error", Db(errorCode)),
            ("$now", UtcNow()),
            ("$id", logicalMessageId)).ConfigureAwait(false);
    }

    public async Task CompleteImportAsync(
        long logicalMessageId,
        string gmailMessageId,
        MigrationStatus status,
        CancellationToken cancellationToken)
    {
        if (status is not (MigrationStatus.Imported or MigrationStatus.Existing))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            UPDATE logical_messages SET
                gmail_message_id=$gmailId, status=$status, error_code=NULL, updated_utc=$now
            WHERE id=$id;
            UPDATE appearances SET status=$status, error_code=NULL, updated_utc=$now
            WHERE logical_id=$id;
            """,
            cancellationToken,
            ("$gmailId", gmailMessageId),
            ("$status", status.ToString()),
            ("$now", UtcNow()),
            ("$id", logicalMessageId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveUploadSessionAsync(
        long logicalMessageId,
        string encryptedResumeUri,
        string sha256,
        long size,
        string path,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            INSERT INTO upload_sessions(logical_id, protected_uri, sha256, size, archive_path, updated_utc)
            VALUES ($id, $uri, $hash, $size, $path, $now)
            ON CONFLICT(logical_id) DO UPDATE SET
                protected_uri=excluded.protected_uri, sha256=excluded.sha256,
                size=excluded.size, archive_path=excluded.archive_path, updated_utc=excluded.updated_utc;
            """,
            cancellationToken,
            ("$id", logicalMessageId),
            ("$uri", encryptedResumeUri),
            ("$hash", sha256),
            ("$size", size),
            ("$path", path),
            ("$now", UtcNow())).ConfigureAwait(false);
    }

    public async Task<string?> GetUploadSessionAsync(
        long logicalMessageId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT protected_uri FROM upload_sessions WHERE logical_id=$id;";
        command.Parameters.AddWithValue("$id", logicalMessageId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task ClearUploadSessionAsync(long logicalMessageId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "DELETE FROM upload_sessions WHERE logical_id=$id;",
            cancellationToken, ("$id", logicalMessageId)).ConfigureAwait(false);
    }

    public async Task RequeueInterruptedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            UPDATE appearances SET status='Discovered', error_code='interrupted_download', updated_utc=$now
            WHERE job_id=$job AND status='Downloading';
            UPDATE logical_messages SET status='Downloaded', error_code='interrupted_upload', updated_utc=$now
            WHERE job_id=$job AND status='Uploading' AND gmail_message_id IS NULL;
            """,
            cancellationToken,
            ("$job", jobId.ToString("D")),
            ("$now", UtcNow())).ConfigureAwait(false);
    }

    public async Task<MigrationSnapshot> GetSnapshotAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var rows = new List<ReportRow>();
        var review = new List<ReviewItem>();
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT folder_name, CAST(strftime('%Y', internal_date) AS INTEGER),
                       status, COUNT(*), SUM(size)
                FROM appearances WHERE job_id=$job
                GROUP BY folder_name, strftime('%Y', internal_date), status
                ORDER BY folder_name, 2, status;
                """;
            command.Parameters.AddWithValue("$job", jobId.ToString("D"));
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    Enum.Parse<MigrationStatus>(reader.GetString(2)),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4)));
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, folder_name, internal_date, size, status, error_code
                FROM appearances
                WHERE job_id=$job AND status IN ('Failed','NeedsReview')
                ORDER BY internal_date, id;
                """;
            command.Parameters.AddWithValue("$job", jobId.ToString("D"));
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                review.Add(new(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetInt64(3),
                    Enum.Parse<MigrationStatus>(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        return new(rows, review);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IReadOnlyList<SourceMessage>> ReadAppearancesAsync(
        Guid jobId,
        string predicate,
        CancellationToken cancellationToken)
    {
        var results = new List<SourceMessage>();
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, folder_name, uid_validity, uid, internal_date, header_date,
                   message_id, size, flags_json, status, sha256, archive_path,
                   logical_id, error_code
            FROM appearances WHERE job_id=$job AND {predicate}
            ORDER BY folder_name, uid;
            """;
        command.Parameters.AddWithValue("$job", jobId.ToString("D"));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new(
                reader.GetInt64(0),
                jobId,
                reader.GetString(1),
                checked((uint)reader.GetInt64(2)),
                checked((uint)reader.GetInt64(3)),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7),
                JsonSerializer.Deserialize<HashSet<string>>(reader.GetString(8)) ?? [],
                Enum.Parse<MigrationStatus>(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return results;
    }

    private static async Task<long> FindLogicalIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        string sha256,
        string? messageId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM logical_messages
            WHERE job_id=$job AND (sha256=$hash OR ($messageId IS NOT NULL AND normalized_message_id=$messageId))
            ORDER BY CASE WHEN sha256=$hash THEN 0 ELSE 1 END, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$hash", sha256);
        command.Parameters.AddWithValue("$messageId", Db(messageId));
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : (long)result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");
}
