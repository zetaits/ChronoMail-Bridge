# Folder and flag mapping

Default prefix: `Imported from Turbify/`.

| Source | Gmail result |
|---|---|
| Inbox | `INBOX` + `Imported from Turbify/Inbox` |
| Sent | `Imported from Turbify/Sent`; never forces `SENT` |
| Drafts | `Imported from Turbify/Drafts`; never forces `DRAFT` |
| Trash | `Imported from Turbify/Trash`; never applies `TRASH` |
| Spam | `Imported from Turbify/Spam`; never applies `SPAM` |
| Normal/nested folder | User label preserving `/` |
| Unread | Adds `UNREAD` |
| Read | Does not add or remove labels |
| Starred | Adds `STARRED` |

Operations on existing messages only add missing labels; they never remove the current state. Other flags and keywords remain in SQLite for auditing.

Reserved names remain under the user label prefix. Control characters are replaced, and local path collisions receive a stable hash.
