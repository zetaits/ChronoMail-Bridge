# Pilot test with 50–100 messages

## Preparation

- Use old messages that do not affect current work.
- Include Inbox, Sent, a nested folder, read and unread messages, starred messages, conversations, a test Word document, messages without a Message-ID, and at least one relatively large message.
- Back up the `state` folder before repeating comparisons.
- Begin in simulation mode and export the report.

## Procedure

1. Select an inclusive cutoff date earlier than the history already migrated by Movebot.
2. Disable simulation mode.
3. Test IMAP and authorize Google.
4. Scan, then compare the message count and byte count with the source.
5. Start the migration and let the batch finish.
6. Close the application halfway through a second run, reopen it, and confirm that it resumes.

## Gmail checklist

- [ ] The displayed date matches the message's `Date` header.
- [ ] Inbox messages appear in Inbox and under the archive label.
- [ ] Sent messages and drafts use user labels.
- [ ] Nested folders preserve their hierarchy.
- [ ] Read/unread and starred states are preserved.
- [ ] Word attachments open correctly when opened manually.
- [ ] Conversations are grouped reasonably.
- [ ] Messages that were already migrated are not duplicated.
- [ ] Messages without a Message-ID appear in the review queue.
- [ ] `errors.csv` contains no email content.

Do not scale up to 40 GB until every discrepancy has been resolved.
