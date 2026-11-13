# Tools reference

Clippy's Revenge gives Claude Code ten tools. You normally don't call them
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
| [`send_meeting_invite`](#send_meeting_invite) | Create a meeting (the only tool that can send) |
| [`index_status`](#index_status) | Show index and sync state |

## Common conventions

- **`mailbox`**: empty means your own mailbox. An SMTP address means that
  [shared mailbox](indexing.md#shared-mailboxes), which must be enabled in the
  tray. Search tools also accept `all`. Tools that take an `id` find the mailbox
  from the id.
- **Dates**: ISO date (`2026-03-01`) or date-time (`2026-03-01T14:30`), in local
  time. A date-only end date includes the whole day.
- **Recipients**: `"Last, First <you@example.com>; other@example.com"`. Separate
  people with `;`, never `,`, because names are often written "Last, First".
- **File paths**: every path must be inside an allowed folder. See
  [file access](security.md#file-access). Relative paths resolve under the
  attachment folder.
- **Errors**: tools return a message that tells Claude what to fix.

## Reading mail

### search_emails

Searches the local index. It is fast and works while Outlook is busy. All filters
are optional and combine with AND. Results are newest first. Only synced mail is
indexed; use [`index_status`](#index_status) to check coverage.

| Parameter | Type | Description |
|---|---|---|
| `query` | string | Free text over subject, body, sender, recipients and attachment file names. Words are ANDed. Use `OR` between words for alternatives, `"quotes"` for a phrase, and a trailing `*` for a prefix. |
| `sender` | string | Substring of the sender's name or email address |
| `recipient` | string | Substring of To or CC. Also accepts a list like `Ann <ann@example.com>; bob@example.com`, which matches mail involving any of them |
| `subject` | string | Substring of the subject |
| `date_from` | string | Earliest date, inclusive |
| `date_to` | string | Latest date, inclusive |
| `folder` | string | `inbox`, `sent`, or another indexed folder path. Subfolders are not included. Empty means all folders |
| `has_attachment` | bool | true: only mail with real (non-inline) attachments; false: only mail without |
| `attachment_name` | string | Substring of any attachment file name |
| `attachment_ext` | string | Exact attachment extension, with or without the dot (`pdf` or `.pdf`) |
| `unread` | bool | true: only unread; false: only read |
| `mailbox` | string | Empty, a shared mailbox address, or `all` |
| `limit` | int | Maximum results, 1 to 200 (default 50) |
| `offset` | int | Results to skip, for paging (default 0) |

Each result has `id`, `mailbox`, `folder`, `date` (local time), `from`,
`sender_email`, `to`, `subject`, `has_attachments`, `attachments[]` (name, size,
inline flag) and `snippet`. Pass a result's `id` to `get_message` to read the
body.

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

### index_status

Shows the state of the local index: per-mailbox counts, oldest and newest indexed
mail, last sync and whether the first backfill is complete. It also says whether
Outlook is running and what Clippy is doing right now. Use it to explain why a
search found nothing, or to check indexing progress.

| Parameter | Type | Description |
|---|---|---|
| `mailbox` | string | Optional shared mailbox address. Empty means all configured mailboxes |

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
pass neither, the error lists the attachments so Claude can choose.

### save_attachments

Finds emails with the same filters as `search_emails` and saves their attachments
into one folder. `attachment_name` and `attachment_ext` also choose which
attachments are saved. Inline images are skipped unless `include_inline` is true.
Returns `saved[]`, `failed[]` and `count`. One failure doesn't stop the rest.

| Parameter | Type | Description |
|---|---|---|
| `save_dir` | string | Folder to save into. Empty means the attachment folder |
| `query`, `sender`, `recipient`, `subject`, `date_from`, `date_to`, `folder`, `has_attachment`, `attachment_name`, `attachment_ext`, `unread`, `mailbox` | | Same as in `search_emails` |
| `include_inline` | bool | true: also save inline attachments such as embedded images (default false) |
| `limit` | int | Maximum number of **emails**, 1 to 200 (default 50) |
| `offset` | int | Emails to skip, for paging |

## Drafts

Drafts are only ever **saved** to the Drafts folder. You review and send them from
Outlook. Nothing is sent by these tools.

### create_draft

Creates a new draft. The default signature is added below the text when you have
one set. Every attachment path is checked before anything is created; if one is
refused, nothing is created.

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

Returns `id`, `subject`, `mailbox` and `signature_added`.

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

Returns `id`, `subject`, `mailbox` and `signature_added`.

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
`organizer`, `is_recurring` and `busy_status`. The result also has `count` and
`truncated`.

### find_free_times

Finds times when everyone is free, from Outlook free/busy. Nothing is booked.
Returns numbered suggestions with `start`, `end` and a ready-to-show `text`.
People whose free/busy can't be read (for example, people outside your
organization) are listed in `unknown_attendees` and ignored.

| Parameter | Type | Description |
|---|---|---|
| `recipients` | string, required | Attendees as `Name <email>; Name <email>` |
| `duration_minutes` | int | Minimum free block length, 1 to 1440 (default 60) |
| `days_ahead` | int | Calendar days to search, up to 60 (default 10) |
| `date_from` | string | Start date or date-time. Default: now |
| `earliest_hour` | int | Working-hours start, 0 to 23. Default: from [Settings](tray.md#settings) |
| `latest_hour` | int | Working-hours end, 1 to 24. Default: from Settings |
| `business_days_only` | bool | Skip Saturdays and Sundays. Default: from Settings |
| `include_me` | bool | Also check the calendar owner (default true) |
| `max_suggestions` | int | 1 to 20 (default 5) |
| `mailbox` | string | Empty means your calendar, or an enabled shared mailbox |

### send_meeting_invite

Creates a meeting and sends the invite. **This is the only tool that can send
anything.** What happens depends on the invite mode in Settings:

- **Send**: a confirmation dialog opens on your PC. The invite goes out when you
  click **Send**. You can instead save it as a draft or cancel.
- **Draft only**: the meeting is saved in your calendar, unsent. You send it from
  Outlook.
- **Disabled**: the tool returns an error.

Every recipient must resolve in Outlook, or nothing is created. If the result is
`cancelled`, Claude is told not to retry unless you ask.

| Parameter | Type | Description |
|---|---|---|
| `recipients` | string, required | Attendees as `Name <email>; Name <email>` or bare emails, separated by `;` |
| `subject` | string, required | Meeting subject |
| `start` | string, required | Local start date-time, e.g. `2026-03-01T14:30` |
| `end` | string, required | Local end date-time |
| `body` | string | Plain-text invite body |
| `location` | string | Location, for example a room |
| `teams` | bool | Requests a Teams link. See the note below |
| `mailbox` | string | A configured shared mailbox to organize the meeting from. Empty means yours |

!!! warning "Teams links"
    `teams=true` does not add a Teams link. Outlook's object model has no
    supported call for it, so the meeting is created without one and the result
    says so. Open the meeting in Outlook and click **Teams Meeting** if you need
    the link.

The result has `status` (`sent`, `saved_as_draft` or `cancelled`), a `reason` for
drafts (`user_choice`, `timeout` or `draft_only_mode`), and a `message` for Claude.
