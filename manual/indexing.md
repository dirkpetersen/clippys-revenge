# Indexing and search

## How it works

Clippy keeps a local SQLite database with full-text search (FTS5) of your mail.
Claude's searches run against this database, so they are fast and still work while
Outlook is busy. Full messages and attachments are fetched live from Outlook when
needed.

Indexing needs Outlook to be running. Clippy attaches to a running Outlook and
never starts it. If Outlook isn't running, indexing waits.

What is indexed: subject, body, sender, recipients, dates, folder, read state and
attachment **file names**. Attachment contents are never indexed.

## First run: backfill

On the first run, Clippy reads your mail from the retention cutoff up to now,
**newest first**, so recent mail becomes searchable first. The tray icon is blue and
animated, and the tooltip shows progress such as
`indexing Inbox 1,240 / 8,000 (2025-11 → now)`. If you quit in the middle, it
resumes where it stopped.

Ask Claude to use [`index_status`](tools.md#index_status) to see whether the
backfill is complete.

## What is indexed

| Setting | Default | Where |
|---|---|---|
| Retention | 12 months (1 to 120) | Settings, Index |
| Folders | Inbox and Sent Items | Settings, Index |
| Sync interval | 5 minutes (30 s to 1 h) | Settings, Index |

**Retention.** Raising it starts a new backfill for the older mail. Lowering it
removes older mail from the index right away. This only affects Clippy's index,
never your mailbox.

**Folders.** Pick any folders in the Settings tree. Subfolders are separate
entries, and a search for `folder="inbox"` does not include subfolders.

## Keeping up to date

Every sync interval (and when you click **Sync now**), Clippy picks up new and
changed mail, then removes mail past the retention cutoff from its index.

Once a day for each folder, a **reconcile** pass compares the index with Outlook. It
adds missing mail, refreshes read/unread state, and removes index rows for mail
that is gone from Outlook. Reconcile only touches the index. As a safety measure,
it refuses to delete rows when Outlook returns nothing, when none of its ids match
the index, or when it would drop more than half of the index and at least 20 rows.

## Pause, sync now and rebuild

- **Pause indexing** stops the current cycle at the next batch. Batches that are
  finished are kept.
- **Sync now** runs a cycle immediately.
- **Rebuild index…** deletes the index database for your mailbox or for all
  mailboxes and starts again from scratch. Search finds less until it finishes. Use
  it if results look wrong. It never changes your mail.

## Shared mailboxes

Exchange mailboxes mounted in your Outlook profile (shared or additional
mailboxes) appear under the tray menu **Shared mailboxes**.

- **Check** one to start indexing it into its own database file.
- **Uncheck** one to stop. You choose **Delete its index**, **Keep index file** or
  **Cancel**.
- Each shared mailbox can have its own folders and retention in Settings, Index.
- If Outlook isn't running, the list shows only configured mailboxes.

Tools use a shared mailbox with `mailbox="helpdesk@example.com"`, or search all
enabled ones with `mailbox="all"`.

## Search tips

You rarely write queries yourself, but these are what Claude can use in
`query`:

| Query | Meaning |
|---|---|
| `budget report` | Both words (AND) |
| `budget OR forecast` | Either word |
| `"annual review"` | The exact phrase |
| `proj*` | Words starting with `proj` |

Filters such as `sender`, `subject`, `date_from` and `attachment_ext` narrow the
results. See the [search_emails reference](tools.md#search_emails).

## Where the index lives

One database per mailbox in `%LOCALAPPDATA%\ClippysRevenge\`: `index.db` for your
mailbox and `index-<address>.db` for each shared mailbox. It holds message bodies
as plain text, like Outlook's own cache file. See
[index at rest](security.md#index-at-rest).
