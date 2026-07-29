# Error recovery

## IMAP limit or disconnection

Do not increase concurrency. ChronoMail Bridge uses exponential backoff with jitter and opens the circuit after five rejections. You can pause, wait, and resume; never change flags or delete email in Turbify.

## Gmail 429, rateLimitExceeded, or 5xx error

The application waits and retries. If `Retry-After` is present, it is honored up to the configured maximum. Do not delete the SQLite database during an upload.

## Authentication

- Turbify: generate another app password and revoke the previous one.
- Google: revoke authorization, close the application, remove the token blobs from `state/secrets`, and authorize again.

## Disk full

Pause the application. Free space on the same volume or create a new job on another drive. Do not move individual files without also moving `state`; paths are verified.

## Inconsistent file

A download with the wrong size is not committed. An inconsistent `.eml` file receives an `.invalid-<timestamp>` suffix; keep the file until the investigation is complete. `.part` files may be replaced.

## UIDVALIDITY changed

Scan again. The previous incomplete epoch will appear in the review queue; do not edit its UIDs manually.

## Lost response after import

With a Message-ID, the application searches for `rfc822msgid:` before retrying. Without a Message-ID, the item remains in `NeedsReview`: check Gmail manually and set an explicit policy only if you accept the risk.

## Backup

With the application closed, copy `messages` and `state` together. `logs` and `reports` can be rebuilt. DPAPI secrets can only be decrypted by the same Windows user.
