# Storage and resumability

## Safe download checkpoint

1. SQLite records `Downloading`.
2. IMAP is opened in read-only mode.
3. The MIME message is written to `<uid>.eml.part` while its SHA-256 is calculated.
4. Buffers are flushed and the advertised size is validated.
5. The file is renamed to `<uid>.eml` on the same volume.
6. A SQLite transaction stores the path, hash, logical message, and `Downloaded` state.

A `.part` file is never treated as complete and is replaced on the next attempt. An existing `.eml` file is read and hashed again; if its size is inconsistent, it is moved to `.invalid-<timestamp>` before another download.

## Safe import checkpoint

SQLite persists `Uploading` before calling Gmail. The resumable URI is encrypted with DPAPI together with the hash, size, and path. After a successful response, the Gmail ID and `Imported` state are committed together.

After a restart:

- `Downloading` returns to the queue;
- `Uploading` without a confirmed ID returns to deduplication;
- `Imported` and `Existing` never return to the queue;
- a Gmail session is reused only if it is still associated with the same hash, size, and path.

With a Message-ID, a lost response is resolved by querying Gmail again. Without a Message-ID, the import is not retried automatically.

## UIDVALIDITY

UIDs are meaningful only within a folder and UIDVALIDITY epoch. A change creates a new epoch and triggers another scan. Old incomplete occurrences are sent for review; complete files remain available, and the logical identity prevents them from being imported again.
