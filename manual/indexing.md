# Indexing and search

## How it works

Clippy keeps a local SQLite database with full-text search (FTS5) of your mail.
Claude's searches run against this database, so they are fast and still work while
Outlook is busy. Full messages and attachments are fetched live from Outlook when
needed.

Indexing needs Outlook to be running. Clippy attaches to a running Outlook and
never starts it (only the tray's **Start Outlook** item does, when you click it). If
Outlook isn't running, indexing waits and the tray icon turns grey with a `!`.

What is indexed: subject, body, sender, recipients (To and CC), dates, folder, read
state, the conversation topic and the attachment **file names**, sizes and whether
they are inline. **Attachment contents are never indexed**, and no file is ever
stored in the index. A very long email is stored cut at 2,000,000 characters.

Indexing only reads. It never changes, moves or deletes your mail.

## First run: backfill

On the first run, Clippy reads your mail from the retention cutoff up to now,
**newest first**, so recent mail becomes searchable first. The tray icon is blue and
animated.

**Folder order: Sent Items first.** In each mailbox the folders are processed in
this order: **Sent Items** (and its subfolders), then **Inbox**, then every other
folder you chose. Each folder is finished before the next one starts. So on a first
run you see the Sent Items backfill complete before the Inbox backfill begins. The
reason: [AI drafts](ai-drafts.md#your-writing-style) learn your writing style from
your sent mail, so Clippy builds the style file as soon as Sent Items are indexed,
while the Inbox is still being read. The order is the same in every cycle; only the
first run makes it visible.

What you see while it runs:

| Where | Text |
|---|---|
| Tray tooltip | `Clippy — indexing Inbox 1,240 / 8,000 (2025-11 → now)`. For a shared mailbox the name is added, for example `indexing helpdesk@… Inbox 300 / 2,000` |
| Activity, start of a folder | `Sent: backfill started, 3,200 emails to index (2025-11 → now)` |
| Activity, end of a folder | `Sent: backfill complete` |
| Activity, each cycle with changes | `Inbox +12, Sent +3 (1.8 s)` |
| Activity, purge | `removed 310 emails older than 2025-11-12` |
| Tray tooltip, new mail | `Clippy — pulling new mail (Inbox +12)` |
| Tray tooltip, purge | `Clippy — removing mail older than 12 months` |
| Tray tooltip, when done | `Clippy — idle · 48,210 emails · synced 2 min ago` |
| Tray tooltip, paused | `Clippy — indexing paused` |

`Sent Items` is shown as `Sent`. The progress bar in the [Activity](activity.md)
window is only shown during a backfill. If you quit in the middle, the backfill
resumes where it stopped.

Ask Claude to use [`index_status`](tools.md#index_status) to see whether the
backfill is complete.

An older index may run one quiet extra pass after an update that adds conversation
data to existing rows. It shows as `Updating thread data` and reads no mail bodies.

## What is indexed

| Setting | Default | Where |
|---|---|---|
| Retention | 12 months (1 to 120) | Settings, Index |
| Folders | Inbox and Sent Items | Settings, Index |
| Sync interval | 5 minutes (30 s to 1 h) | Settings, Index |

All settings are listed in the [Settings reference](settings.md).

**Retention.** Raising it starts a new backfill for the older mail. Lowering it
removes older mail from the index right away. This only affects Clippy's index,
never your mailbox.

**Folders.** Pick any folders in the Settings tree. Subfolders are separate
entries, and a search for `folder="inbox"` does not include subfolders. `Inbox` and
`Sent Items` always mean your default Inbox and Sent Items, whatever language
Outlook uses.

## Keeping up to date

Every sync interval (and when you click **Sync now**), Clippy picks up new and
changed mail, then removes mail past the retention cutoff from its index. The
interval counts from the end of the last cycle, and the first cycle runs at
start-up.

Once a day for each folder, a **reconcile** pass compares the index with Outlook. It
adds missing mail, refreshes read/unread state, and removes index rows for mail
that is gone from Outlook. Reconcile only touches the index. As a safety measure,
it refuses to delete rows when Outlook returns nothing, when none of its ids match
the index, or when it would drop more than half of the index and at least 20 rows.

## Pause, sync now and rebuild

- **Pause indexing** stops the current cycle at the next batch. Batches that are
  finished are kept. **Sync now** is ignored while paused; **Resume indexing**
  starts a cycle at once. The paused state is not saved: Clippy starts unpaused.
- **Sync now** runs a cycle immediately.
- **Rebuild index…** (tray menu, or the Rebuild button in Settings, Index) asks for
  confirmation, then deletes the index database for your mailbox or for all
  mailboxes and starts again from scratch. Search finds less until it finishes. Use
  it if results look wrong. It never changes your mail. After a rebuild the folder
  order above applies again: Sent Items first.

## Purge and retention

After each cycle, mail older than the retention cutoff is removed from the index.
The `Purging` state shows only when there is something to remove. When many rows go
at once, the database file is compacted. Nothing in Outlook is touched.

Clippy's own records about AI drafts (which emails it decided on, and so on) are
kept for 30 days. They hold no mail text. See [Security](security.md#index-at-rest).

## Shared mailboxes

Exchange mailboxes mounted in your Outlook profile (shared or additional
mailboxes) appear under the tray menu **Shared mailboxes**.

- **Check** one to start indexing it into its own database file.
- **Uncheck** one to stop. You choose **Delete its index**, **Keep index file** or
  **Cancel**. The mailbox is disabled either way.
- Each shared mailbox can have its own folders and retention in Settings, Index.
  Its Sent Items are also indexed before its Inbox. Because mailboxes run one after
  the other (yours first), a shared mailbox's Sent Items come after the last folder
  of your own mailbox.
- If Outlook isn't running, the list shows only configured mailboxes.
- A shared mailbox that isn't mounted, or a folder that doesn't exist, is skipped
  with one error line in Activity.

Tools use a shared mailbox with `mailbox="helpdesk@example.org"`, or search all
enabled ones with `mailbox="all"`. AI drafts for a shared mailbox are a separate
opt-in; see [AI drafts](ai-drafts.md#shared-mailboxes).

## Search tips

You rarely write queries yourself, but these are what Claude can use in
`query`:

| Query | Meaning |
|---|---|
| `budget report` | Both words (AND) |
| `budget OR forecast` | Either word |
| `"annual review"` | The exact phrase |
| `proj*` | Words starting with `proj` |

Search is not case-sensitive and ignores accents. Characters such as `-`, `+` or
`:` are treated as plain text, so `budget-2026` or `C++` never cause an error.
Only an uppercase `OR` works as an operator.

Filters such as `sender`, `subject`, `date_from` and `attachment_ext` narrow the
results. See the [search_emails reference](tools.md#search_emails). For counts and
custom questions, see [`email_stats`](tools.md#email_stats) and
[`query_index`](tools.md#query_index).

## Where the index lives

One database per mailbox in `%LOCALAPPDATA%\ClippysRevenge\`: `index.db` for your
mailbox and `index-<address>.db` for each shared mailbox, plus the `-wal` and `-shm`
files next to them. It holds message bodies as plain text, like Outlook's own cache
file. See [index at rest](security.md#index-at-rest).

The database has a schema version. If a newer version of Clippy changed it, the
database is upgraded when the app starts. An older Clippy refuses a newer database
and does not touch it; use **Rebuild index…** after a downgrade.

**Settings, Index** shows live numbers for the selected mailbox: number of emails,
oldest and newest mail, database size and last sync.
