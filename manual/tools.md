# Tools reference

Clippy's Revenge gives Claude Code twelve tools. You normally don't call them
yourself: you ask Claude in plain language and it picks the right tool. This page
lists every tool and its parameters, which is useful when you want to know what
Claude can and can't do.

| Tool | What it does |
|---|---|
| [`search_emails`](#search_emails) | Search the local index |
| [`get_message`](#get_message) | Read one email in full |
| [`get_attachment`](#get_attachment) | Save one attachment to disk |
| [`save_attachments`](#save_attachments) | Save attachments of all emails matching a search |
| [`create_draft`](#create_draft) | Save a new email draft |
| [`reply_draft`](#reply_draft) | Save a reply draft |
| [`list_events`](#list_events) | List calendar events |
| [`find_free_times`](#find_free_times) | Suggest meeting times from free/busy |
| [`create_meeting_draft`](#create_meeting_draft) | Save a meeting unsent in your calendar. Nothing is sent |
| [`index_status`](#index_status) | Show index and sync state |
| [`email_stats`](#email_stats) | Count mail by sender, domain, folder or time |
| [`query_index`](#query_index) | Run one read-only SELECT over the index |

There is no `send_meeting_invite` tool. Older versions had one; it was replaced by
`create_meeting_draft`, which never sends.

## What the tools can and can't do

- **Nothing is ever sent.** Clippy has no send function. Mail tools only
  **save drafts** in your Drafts folder, and the meeting tool only **saves a
  meeting unsent** in your calendar. You review and send both in Outlook.
- **Nothing is deleted.** No tool deletes or moves your mail. The only mail Clippy
  ever moves is an outdated AI draft of its own, and that is not a tool: see
  [Outdated drafts](ai-drafts.md#outdated-drafts).
- **Read-only tools.** `search_emails`, `get_message`, `index_status`,
  `email_stats`, `query_index`, `list_events` and `find_free_times` change nothing.
  `get_attachment` and `save_attachments` write files to disk, only inside the
  [allowed folders](security.md#file-access). `create_draft`, `reply_draft` and
  `create_meeting_draft` create items in Outlook.
- **Which tools need Outlook running.** `search_emails`, `email_stats`,
  `query_index` and `index_status` use the local index and work while Outlook is
  busy or closed. `get_message` falls back to Outlook for mail that isn't indexed.
  Attachments, drafts, `list_events`, `find_free_times` and `create_meeting_draft`
  always need Outlook.

## Common conventions

- **`mailbox`**: empty means your own mailbox. An SMTP address means that
  [shared mailbox](indexing.md#shared-mailboxes), which must be enabled in the
  tray. Search tools (`search_emails`, `email_stats`, `query_index`,
  `save_attachments`) also accept `all`. Tools that take an `id` find the mailbox
  from the id.
- **Dates**: ISO date (`2026-03-01`) or date-time (`2026-03-01T14:30`), in local
  time. A space may replace the `T`, and a date-time ending in `Z` or `+hh:mm` is
  honored. A date-only end date includes the whole day.
- **Recipients**: `"Last, First <you@example.org>; other@example.org"`. Separate
  people with `;`, never `,`, because names are often written "Last, First".
- **File paths**: every path must be inside an allowed folder. See
  [file access](security.md#file-access). Relative paths resolve under the
  attachment folder (see [Settings](settings.md)). A refused path returns an error
  that names the allowed folders, and is recorded in [Activity](activity.md).
- **Errors**: tools return a message that tells Claude what to fix. The most
  common ones are in [Errors Claude may see](#errors-claude-may-see).

## Reading mail

### search_emails

Searches the local index. It is fast and works while Outlook is busy. All filters
are optional and combine with AND. Results are newest first. Only synced mail is
indexed; use [`index_status`](#index_status) to check coverage.

| Parameter | Type | Description |
|---|---|---|
| `query` | string | Free text over subject, body, sender, recipients and attachment file names. Words are ANDed. Use `OR` between words for alternatives, `"quotes"` for a phrase, and a trailing `*` for a prefix. |
| `sender` | string | Substring of the sender's name or email address |
| `recipient` | string | Substring of To or CC. Also accepts a list like `Ann <ann@example.org>; bob@example.org`, which matches mail involving any of them |
| `subject` | string | Substring of the subject |
| `date_from` | string | Earliest date, inclusive |
| `date_to` | string | Latest date, inclusive |
| `folder` | string | `inbox`, `sent`, or another indexed folder path such as `Inbox\Projects` (not case-sensitive). Subfolders are not included. Empty means all folders |
| `has_attachment` | bool | true: only mail with real (non-inline) attachments; false: only mail without |
| `attachment_name` | string | Substring of any attachment file name (inline images included) |
| `attachment_ext` | string | Exact attachment extension, with or without the dot (`pdf` or `.pdf`) |
| `unread` | bool | true: only unread; false: only read |
| `mailbox` | string | Empty, a shared mailbox address, or `all` |
| `limit` | int | Maximum results, 1 to 200 (default 50) |
| `offset` | int | Results to skip, for paging (default 0) |

The text filters (`sender`, `recipient`, `subject`, `attachment_name`) ignore
upper and lower case. A blank text filter means no filter. A `recipient` that
contains `;` or `<` is read as a list of people.

Each result has `id`, `mailbox`, `folder`, `date` (local time), `from`,
`sender_email`, `to`, `subject`, `has_attachments`, `attachments[]` (position,
name, size, inline flag) and `snippet`. With `query`, the snippet shows the matching
words between `**`; otherwise it is the start of the body. Pass a result's `id` to
`get_message` to read the body.

Errors: a date that can't be read gives `date_from "<text>" is not a valid date. Use
an ISO date like 2026-03-01 or a date-time like 2026-03-01T14:30 (local time).`

??? example "Example request"
    "Find unread mail from Ann with a PDF attached since 1 March."

    Claude calls `search_emails` with `sender="Ann"`, `unread=true`,
    `attachment_ext="pdf"`, `date_from="2026-03-01"`.

### get_message

Returns one email in full: headers, complete body and attachment list. It reads
the index first. If the email isn't indexed yet, it reads it live from Outlook,
which needs Outlook to be running.

| Parameter | Type | Description |
|---|---|---|
| `id` | string, required | The id of an email, as returned by `search_emails` |

The result has `id`, `mailbox`, `folder`, `date`, `from`, `sender_email`, `to`, `cc`,
`subject`, `unread`, `has_attachments`, `attachments[]`, `internet_message_id`,
`body` and `source` (`index` or `outlook`). A very long email is stored cut at
2,000,000 characters and ends with the line `[Clippy indexed only the first
2,000,000 characters of this email.]`. If Outlook has to be asked and isn't running,
the error is `Outlook is not running. Ask the user to start Outlook (tray → Start
Outlook) and try again.`

### index_status

Shows the state of the local index: per-mailbox counts, oldest and newest indexed
mail, last sync and whether the first backfill is complete. It also says whether
Outlook is running and what Clippy is doing right now. Use it to explain why a
search found nothing, or to check indexing progress.

| Parameter | Type | Description |
|---|---|---|
| `mailbox` | string | Optional shared mailbox address. Empty means all configured mailboxes |

The result has `state` (what Clippy is doing, such as `Idle` or `Backfilling`),
`progress` (text such as `indexing Inbox 1,240 / 8,000 (2025-11 → now)`),
`outlook_running` (true, false, or null before the first attempt) and `mailboxes[]`.
Each mailbox has `mailbox`, `total_emails`, `folders` (count per folder),
`oldest`, `newest`, `db_size_bytes`, `last_sync` and `backfill_complete`.

### email_stats

Counts emails in the local index, grouped, in one call. Use it for questions such
as "top 10 people who emailed me last year" or "how much mail do I get per month".
It returns **counts and names only, never mail text**. It is read-only and fast,
because the counting runs inside the index.

All the filters of [`search_emails`](#search_emails) work here (`query`, `sender`,
`recipient`, `subject`, `date_from`, `date_to`, `folder`, `has_attachment`,
`attachment_name`, `attachment_ext`, `unread`, `mailbox`), with the same meaning.
There is no `limit` or `offset`.

| Parameter | Type | Description |
|---|---|---|
| `group_by` | string, required | What to group by. See the table below |
| `top` | int | Number of groups to return, 1 to 200 (default 10) |
| `order` | string | `count`: largest groups first (default). `key`: by key, chronological for `month`, `week` and `hour`, Mon to Sun for `weekday` |
| `include_cc` | bool | For `recipient` and `recipient_domain`: also count CC addresses (default true) |
| `mailbox` | string | Empty, a shared mailbox address, or `all` |

**`group_by` values**

| Value | Key |
|---|---|
| `sender` | The sender's email address, lower case. `name` is the display name in the newest email of the group |
| `sender_domain` | The part of the sender address after the `@` |
| `recipient` | Each To address, plus each CC address when `include_cc` is true. An address counts once per email. `name` is the display name seen in the newest email |
| `recipient_domain` | The same, by domain. A domain counts once per email |
| `folder` | The folder path |
| `month` | `yyyy-MM` |
| `week` | ISO week, `yyyy-Www` |
| `weekday` | `Mon` to `Sun` |
| `hour` | `00` to `23` |

`month`, `week`, `weekday` and `hour` use your Windows time zone. `(unknown)` means
an email had no address. Any other `group_by` value returns an error that lists the
valid ones.

The result is `group_by`, `mailbox`, `total` (matching emails, not recipient
entries), `distinct` (number of groups), `truncated` (true when there are more
groups than `top`) and `rows`. Each row has `key`, `count`, `first` and `last` (the
dates of the oldest and newest email in the group, local time with offset), and
`name` for `sender` and `recipient`.

With `mailbox="all"`, the counts of every enabled mailbox are added together by
key. An email that is in two mailboxes counts twice.

??? example "Example requests"
    "Who emailed me most in the last year? Top 10 with counts."

    Claude calls `email_stats` with `group_by="sender"`, `folder="inbox"`,
    `date_from` one year ago.

    "How much mail do I get per month?" is `group_by="month"`, `order="key"`.
    "Who do I write to most?" is `group_by="recipient"`, `folder="sent"`.
    "Which companies send me the most mail?" is `group_by="sender_domain"`.

For joins, snippets or any other question that grouping can't answer, use
[`query_index`](#query_index).

### query_index

Runs **one read-only SQLite `SELECT`** over the local email index. Use it for
questions the other tools can't answer in one call: joins, custom grouping and
full-text search with snippets. For plain counts, [`email_stats`](#email_stats) is
simpler. It works while Outlook is busy or closed, and only synced mail is indexed.

| Parameter | Type | Description |
|---|---|---|
| `sql` | string, required | One `SELECT` (`WITH` and `VALUES` also work). Write values into the SQL; `?` parameters are not supported |
| `mailbox` | string | Empty, a shared mailbox address, or `all` |
| `max_rows` | int | Maximum rows returned, 1 to 1000 (default 200) |

**Views.** The query sees exactly these three views, which hold the same data that
`search_emails` and `get_message` return.

| View | Columns |
|---|---|
| `mail` | `id` (the id that `get_message` takes), `doc` (join key), `mailbox` (empty for your own), `folder`, `date` (local time, `yyyy-MM-ddTHH:mm:ss`), `unix_time` (UTC seconds), `sender_name`, `sender_email`, `to_addrs`, `cc_addrs` (`Name <email>; ...`), `subject`, `topic` (conversation topic), `body` (plain text), `unread` (0 or 1), `has_attachments` (0 or 1, not counting inline images), `attachment_names` (one file name per line) |
| `mail_fts` | The full-text index: `mail_fts` (use it with `MATCH`, `snippet()`, `highlight()`), `rank`, `doc`, `subject`, `body`, `sender_name`, `sender_email`, `to_addrs`, `cc_addrs`, `attachment_names`. Join to `mail` on `doc` |
| `mail_attachments` | `doc`, `position`, `filename`, `size`, `is_inline` (0 or 1). Only file names are indexed, never attachment content |

For full-text search, filter with `WHERE mail_fts MATCH '...'` (FTS5 syntax: words
are ANDed, `OR`, `NOT`, `"a phrase"`, `prefix*`, `column:word`) and sort with
`ORDER BY rank`. `snippet(mail_fts, n, ...)` and `highlight(mail_fts, n, ...)` take
a column number: 0 subject, 1 body, 2 sender name, 3 sender email, 4 To, 5 CC, 6
attachment names.

**Limits**

- One statement only. `PRAGMA`, `ATTACH`, every write and every other table are
  refused, with an error that names what was not allowed.
- 10 seconds in total, at most 1000 rows (`max_rows`), at most 100 columns, and
  long text cells are cut at 2,000 characters. On a large mailbox add a `LIMIT` and
  a date or folder filter.
- With `mailbox="all"` each mailbox is queried separately and the rows are joined
  with a leading `mailbox` column. Counts and other aggregates are therefore
  **per mailbox**; use `email_stats` for counts merged across mailboxes.

The result has `columns`, `rows` (numbers stay numbers, empty values are `null`),
`row_count`, `truncated`, `truncated_cells` and `elapsed_ms`. When it stops early
it also gives `truncated_reason`: `max_rows` or `size`.

??? example "Example queries"
    Top senders in your Inbox in the last year:

    ```sql
    SELECT sender_email, count(*) AS n FROM mail
    WHERE folder = 'Inbox' AND unix_time >= strftime('%s','now','-1 year')
    GROUP BY sender_email ORDER BY n DESC LIMIT 10
    ```

    Mail per month:

    ```sql
    SELECT substr(date,1,7) AS month, count(*) AS n FROM mail
    GROUP BY month ORDER BY month
    ```

    Full-text search with snippets:

    ```sql
    SELECT m.id, m.date, m.subject,
           snippet(mail_fts, 1, '**', '**', '…', 12) AS hit
    FROM mail_fts JOIN mail m ON m.doc = mail_fts.doc
    WHERE mail_fts MATCH 'budget AND 2026' ORDER BY rank LIMIT 20
    ```

    PDF attachments by sender:

    ```sql
    SELECT m.sender_email, count(*) AS n
    FROM mail_attachments a JOIN mail m ON m.doc = a.doc
    WHERE lower(a.filename) LIKE '%.pdf' GROUP BY 1 ORDER BY n DESC LIMIT 20
    ```

!!! note "Read-only, by several layers"
    The query runs on a read-only connection to the index with writing switched
    off, and every table and column access is checked. Only the three views above
    can be read, not sync state, settings or anything else in the database. See
    [Security](security.md#statistics-and-sql-queries).

The main `query_index` errors:

| Situation | Message (shortened) |
|---|---|
| Empty `sql` | `sql is required: one SELECT over the views mail, mail_fts and mail_attachments.` |
| Over 20,000 characters | `sql is longer than 20,000 characters. Shorten the query.` |
| More than one statement | `query_index runs exactly one SELECT statement. Remove everything after the first statement.` |
| A write, `PRAGMA`, `ATTACH`, another table or column | `query_index only reads the views mail, mail_fts and mail_attachments with one SELECT. Not allowed here: <what>.` |
| `?` parameters | `query_index takes no parameters; write the values into the SQL.` |
| More than 100 columns | `query_index returns at most 100 columns.` |
| Longer than 10 seconds | `The query ran longer than 10 seconds and was stopped. Add a date range, a folder or a LIMIT, or use email_stats for counts.` |
| SQL syntax or other SQLite error | `SQL error: <SQLite's message>. query_index reads the views ...` |
| Index not ready (not indexed yet, being rebuilt) | `The index for <mailbox> isn't available right now. Check index_status.` |

`email_stats` errors: `group_by must be one of sender, sender_domain, recipient,
recipient_domain, folder, month, week, weekday, hour.` and `order must be count or
key.`

## Attachments

Attachments are saved with a live call to Outlook, so Outlook must be running.
Files are never overwritten: a duplicate name becomes `name (2).ext`.

### get_attachment

Saves one attachment of an email to disk and returns its path, file name and
size.

| Parameter | Type | Description |
|---|---|---|
| `id` | string, required | The id of an email, from `search_emails` |
| `filename` | string | File name of the attachment (whole name, case-insensitive) |
| `position` | int | 1-based position in the email's attachment list |
| `save_dir` | string | Folder to save into. Empty means the attachment folder. Relative paths go under it. Must be inside an allowed folder |

If the email has more than one attachment, pass `filename` or `position`. If you
pass neither, the error lists the attachments (for example `1 "report.pdf", 2
"image001.png" (inline)`) so Claude can choose. If you pass both, they must agree.
Inline attachments can be fetched by name or position. The returned `filename` is
the attachment's original name; the saved file may be called `name (2).ext`.

### save_attachments

Finds emails with the same filters as `search_emails` and saves their attachments
into one folder. `attachment_name` and `attachment_ext` also choose which
attachments are saved. Inline images are skipped unless `include_inline` is true.
Returns `saved[]`, `failed[]`, `count` (number saved) and a `note` when nothing
matched or nothing was eligible. Each `saved` entry has `id`, `mailbox`, `position`,
`filename`, `path` and `size`; each `failed` entry has `id`, `mailbox`, `position`,
`filename` and `error`. One failure doesn't stop the rest. Emails are saved newest
first.

| Parameter | Type | Description |
|---|---|---|
| `save_dir` | string | Folder to save into. Empty means the attachment folder |
| `query`, `sender`, `recipient`, `subject`, `date_from`, `date_to`, `folder`, `has_attachment`, `attachment_name`, `attachment_ext`, `unread`, `mailbox` | | Same as in `search_emails` |
| `include_inline` | bool | true: also save inline attachments such as embedded images (default false) |
| `limit` | int | Maximum number of **emails**, 1 to 200 (default 50) |
| `offset` | int | Emails to skip, for paging |

## Drafts

Drafts are only ever **saved** to the Drafts folder. You review and send them from
Outlook. Nothing is sent by these tools. Drafts made by these tools carry a hidden
marker that says Clippy's tools created them (AI drafts have their own marker, see
[AI drafts](ai-drafts.md)).

### create_draft

Creates a new draft. The default signature is added below the text when you have
one set. Every attachment path is checked before anything is created; if one is
refused, nothing is created, and the error ends with `Nothing was created.`
Checks run in this order: mailbox (`all` is refused), recipients (`to` needs at
least one), attachments. Line breaks in the subject become spaces.

| Parameter | Type | Description |
|---|---|---|
| `to` | string, required | Recipients, separated by `;` |
| `subject` | string, required | Subject line |
| `body` | string, required | Plain text, or an HTML fragment when `html` is true |
| `cc` | string | Same format as `to` |
| `bcc` | string | Same format as `to` |
| `html` | bool | true when `body` is HTML (default false) |
| `attachments` | string[] | Full paths to existing files in allowed folders |
| `mailbox` | string | Empty means your own mailbox, or an enabled shared mailbox. The draft goes to that mailbox's Drafts folder and is sent on its behalf |

Returns `id`, `subject`, `mailbox` (empty for your own) and `signature_added`.

??? example "Example request"
    "Draft an email to Ann Lee <ann@example.org> asking for the Q3 numbers."

    Claude calls `create_draft` with `to="Ann Lee <ann@example.org>"`,
    `subject="Q3 numbers"` and a `body`. The draft appears in Drafts. You edit and
    send it yourself.

### reply_draft

Creates a reply draft to an email. The body is placed above the quoted original,
followed by your reply signature when one is set.

!!! note "Subjects start with `Re::`"
    Reply subjects start with `Re:: ` (a double colon). This is intentional.

| Parameter | Type | Description |
|---|---|---|
| `id` | string, required | Id of the email to reply to, from `search_emails` |
| `body` | string, required | Plain text reply, or an HTML fragment when `html` is true |
| `reply_all` | bool | true: reply to all recipients (default false) |
| `html` | bool | true when `body` is HTML (default false) |
| `mailbox` | string | Empty means the mailbox the original is in, or an enabled shared mailbox |

Returns `id`, `subject`, `mailbox` and `signature_added`. `mailbox` defaults to the
mailbox the original email is in.

## Calendar

See [Calendar and meetings](calendar.md) for how these fit together.

### list_events

Lists calendar events in a time window, with recurring events expanded into
instances. Sorted by start, at most 500. Defaults to the next 7 days.

| Parameter | Type | Description |
|---|---|---|
| `start` | string | Window start. Default: today 00:00 |
| `end` | string | Window end. Default: `start` plus `days` |
| `days` | int | Window length in days when `end` isn't given, 1 to 366 (default 7) |
| `mailbox` | string | A shared mailbox's calendar. Empty means yours |

Each event has `id`, `subject`, `start`, `end`, `all_day`, `location`,
`organizer`, `is_recurring` and `busy_status`. The result also has the resolved
`start` and `end` of the window, `count` and `truncated` (true when the 500 limit
was reached).

### find_free_times

Finds times when everyone is free, from Outlook free/busy. Nothing is booked.
Returns numbered suggestions (whole free blocks) with `start`, `end`, `adjacent` and
a ready-to-show `text`, and `offers`: options of exactly `duration_minutes`, with
`offer_text` ready to paste into an email. People whose free/busy can't be read (for
example, people outside your organization) are listed in `unknown_attendees` and
ignored.

**Ranking.** Times right before or after one of your meetings come first (within 15
minutes, on the same day), then times next to a tentative meeting, then everything
else, each group in date order. `adjacent` says which: `busy`, `tentative` or `none`.
Free/busy can't tell a meeting from other busy time, so "next to a meeting" means
"next to busy time". Without your own calendar (`include_me=false`, or your free/busy
can't be read), every time is `none`.

**Offers.** `offers` and `offer_text` hold at most `max_suggestions` options of
exactly `duration_minutes`, in the same order, one per line:

```
1. Tue 11/18  10:00–11:00 PT  (19:00–20:00 CET)
2. Wed 11/19  14:00–15:00 PT  (23:00–00:00 CET)
```

With `offer_cet=true` (for someone in Europe) each line also shows the time in
Central European time, always labelled `CET`, also in summer. Options between 08:00
and 18:00 CET come first. If there are fewer than wanted, the rest follow in a second
list headed `Outside European office hours:`. Each offer has `list`: `main` or
`outside_hours`. See [Meeting times in AI drafts](ai-drafts.md#meeting-times).

| Parameter | Type | Description |
|---|---|---|
| `recipients` | string, required | Attendees as `Name <email>; Name <email>`. At most 50 |
| `duration_minutes` | int | Minimum free block length, and the exact length of each offer, 1 to 1440 (default 60) |
| `days_ahead` | int | Calendar days to search, counted from the day of `date_from`, 1 to 60 (default 10) |
| `date_from` | string | Start date or date-time. Default: now |
| `earliest_hour` | int | Working-hours start, 0 to 23. Default: from [Settings](settings.md) |
| `latest_hour` | int | Working-hours end, 1 to 24. Default: from Settings |
| `business_days_only` | bool | Skip Saturdays and Sundays. Default: from Settings |
| `include_me` | bool | Also check the calendar owner (default true) |
| `max_suggestions` | int | 1 to 20 (default 5). Also the most `offers` returned |
| `mailbox` | string | Empty means your calendar, or an enabled shared mailbox |
| `offer_cet` | bool | true when the person you are writing to is in Europe: offers also show CET, Europe office hours first (default false) |

The result has `suggestions[]` (`n`, `label`, `start`, `end`, `adjacent`), `text`,
`offers[]` (`n`, `label`, `start`, `end`, `adjacent`, `list`), `offer_text`,
`unknown_attendees[]` and `attendees[]` (the people whose free/busy was read, with
name and email). `start` and `end` are local times with offset, for example
`2026-10-15T13:00:00-07:00`. With no suggestions, `text` says why: no readable
free/busy, or no free block in the days searched. The server remembers nothing
between calls, so to book option N, Claude passes that option's `start` and `end`
to `create_meeting_draft`.

If `offer_cet` is true but the PC has no Central European time zone, the offers have
no CET part, and a warning is written to the log once.

??? example "Example request"
    "Find an hour for me, Ann Lee <ann@example.org> and Bo Kim <bo@example.org> next
    week. Ann is in Berlin."

    Claude calls `find_free_times` with `recipients="Ann Lee <ann@example.org>; Bo
    Kim <bo@example.org>"`, `duration_minutes=60`, `date_from` set to next Monday
    and `offer_cet=true`, then pastes `offer_text` into the email it drafts.

### create_meeting_draft

Saves a meeting in your calendar **without sending it**. Clippy never sends
anything. You add the Zoom or Teams link in Outlook and send the invite yourself. See
[Meeting drafts](calendar.md#meeting-drafts) for the whole workflow.

The meeting gets the category **Clippy draft invite**, is listed in the tray under
**Draft invites**, and counts toward the tray icon's badge. If the invite answers an
email, pass `reply_to_id` and a note is saved in Drafts.

Every recipient must resolve in Outlook, or nothing is created. In **Disabled** mode
(see [Settings, Scheduling](settings.md#scheduling)) the tool refuses with `Meeting drafts
are disabled in Clippy's Revenge. Ask the user to enable them in the tray settings
(Meeting invites: Draft only), then try again.` Clippy checks the arguments first, then
`reply_to_id`, then the recipients, and only then creates and saves the meeting. Each
call adds one line to [Activity](activity.md), such as `meeting draft saved for 2
attendees: Thu 10/15 10:00–11:00`. The body never appears there.

| Parameter | Type | Description |
|---|---|---|
| `recipients` | string, required | Attendees as `Name <email>; Name <email>` or bare emails, separated by `;` |
| `subject` | string, required | Meeting subject |
| `start` | string, required | Local start date-time, e.g. `2026-03-01T14:30`. A date without a time is not accepted. An offset is converted to local time |
| `end` | string, required | Local end date-time, after `start` and in the future |
| `body` | string | Plain-text invite body |
| `location` | string | Location, for example a room |
| `mailbox` | string | A configured shared mailbox to save the meeting in. Empty means yours |
| `reply_to_id` | string | The `id` of the email that asked for the meeting (from `search_emails`). Saves a note in Drafts as a reply to it. An unknown id is an error and nothing is created |

To book option N of a `find_free_times` result, or of times offered in a draft, pass
that option's start and end.

!!! note "No Teams or Zoom link"
    Outlook's object model has no supported call to add a Teams or Zoom link. The
    result always tells Claude that the meeting has none and that you add it in
    Outlook. The old `teams` argument is gone.

The result has:

| Field | Meaning |
|---|---|
| `status` | Always `saved_as_draft` |
| `message` | A sentence for Claude: the meeting was saved without sending, it has no link, and it is listed in the tray under Draft invites |
| `subject`, `start`, `end` | What was saved. Times are in the same format as `find_free_times` |
| `mailbox`, `organizer`, `recipients` | Whose calendar it is in, who organizes it and who is invited |
| `category` | `Clippy draft invite`, or null if the category couldn't be set (the meeting is still saved) |
| `note_draft_id` | Only with `reply_to_id`: the id of the note draft |
| `note_error` | Only if the note failed: the meeting is saved, but the note in Drafts couldn't be created |

!!! example
    Claude calls `create_meeting_draft` with `recipients="Ann Lee <ann@example.org>"`,
    `subject="Project sync"`, `start="2026-10-15T10:00"`, `end="2026-10-15T11:00"` and
    `reply_to_id` set to the id of Ann's email. The meeting appears unsent in your
    calendar, a note reply appears in Drafts, and the tray shows **Draft invites (1)**.

## Errors Claude may see

Every tool error is a plain sentence that tells Claude what to do next. Common ones:

| Situation | What the message says |
|---|---|
| Outlook isn't running | `Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.` |
| Outlook is busy | `Outlook isn't responding. It may be busy, offline or showing a dialog box. Ask the user to check Outlook, then try again.` For a draft or meeting, it adds that Claude should check Outlook for it before retrying |
| Index can't be opened | `The email index could not be opened; see the tray (Error state). Restart Clippy or use Rebuild index.` |
| Unknown or disabled mailbox | `Mailbox "<input>" is not configured or not enabled.` and the list of available mailboxes, with the hint to enable it in the tray under **Shared mailboxes** |
| `mailbox="all"` on a tool that doesn't support it | `Mailbox "all" only works with search tools. Use one mailbox: <list>.` |
| Unknown email `id` | `Email "<id>" was not found in Outlook. It may have been deleted or moved to a folder that isn't indexed. Run search_emails again to get a current id.` |
| Path outside the allowed folders | An error naming the allowed folders. See [file access](security.md#file-access) |
| Meeting drafts turned off | See [create_meeting_draft](#create_meeting_draft) |

Tool errors are not written to the log with their text, because they can quote what
was searched for.