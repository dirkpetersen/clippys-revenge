# Activity window

The Activity window shows what Clippy is doing and what it did lately. Open it with
**Activity…** in the [tray menu](tray.md), or by double-clicking the tray icon.

## What the window shows

- **The current state** at the top: `Idle`, `Serving a request`, `Drafting replies`,
  `Paused`, `Purging old mail`, `Syncing`, `Indexing your mail`, `Waiting for Outlook` or
  `Error`. Where it has a label, it follows after a colon, for example `Indexing your
  mail: Inbox (2025-11 → now)`.
- **The token tally**, under the state line, while you have given AI drafts consent:
  for example `Today: 18 AI calls, ~210k tokens (in 190k / out 20k)`. See
  [Token tally](ai-drafts.md#token-tally).
- **A progress bar** during the first backfill, with `<done> of <total>`.
  While drafting, the state reads `Drafting replies (<done> / <total>)` instead, with no
  bar.
- **The last 500 events**, newest first, with the columns time (`HH:mm:ss`), mailbox,
  kind, summary and duration. Events from your own mailbox, or that belong to no mailbox,
  show `—` in the mailbox column. Older events drop off.
- **Filters** for kind and for mailbox, and a **Copy** button that copies the shown rows
  as tab-separated text.

Durations read `7 ms`, `1.8 s` or `1 min 4 s`.

Events carry tool names, counts, folder names and email subjects. They **never** show
email bodies, search text, prompts, Claude's answers, attachment contents or attachment
file names. Subjects
are cut at 80 characters with `…`. The list is kept in memory only: it is empty after a
restart. The [log files](troubleshooting.md#logs) hold more technical detail, also
without mail text.

## Event kinds

| Kind | What it records |
|---|---|
| `Sync` | New mail pulled into the index, the daily check against Outlook |
| `Backfill` | Progress of the first (or a rebuilt) index |
| `Purge` | Old mail removed from the index |
| `Tool` | Every call Claude makes to a [tool](tools.md) |
| `Draft` | A draft saved in Outlook, or an AI draft replaced or moved |
| `Invite` (bold) | A meeting draft saved, or refused |
| `Blocked` | A file path that was refused |
| `Ai` | AI drafting summaries, limits, style-skill news |
| `Update` | Update checks, downloads and installs |
| `Error` | Something that needs your attention |

Placeholders such as `<n>` below stand for numbers, subjects or names that Clippy fills
in.

## Sync and index

| Kind | Text |
|---|---|
| `Sync` | `Inbox +12, Sent +3 (1.8 s)`. New mail per folder, in the order of your indexed folders. A mailbox name is in the mailbox column |
| `Backfill` | `Sent: backfill started, 4,210 emails to index (2025-11 → now)` |
| `Backfill` | `Sent: backfill complete`. Then the same two lines for `Inbox` and any other folder. Sent Items are indexed first, then Inbox. See [First run](indexing.md#first-run-backfill) |
| `Backfill` | `index deleted for rebuild` |
| `Purge` | `removed 310 emails older than 2025-11-12` |
| `Sync` | `Retention changed to 24 months: backfilling older mail` |
| `Purge` | `Retention changed to 6 months: purged 1204 emails` |
| `Sync` | `Reconciled Inbox: 3 removed, 2 added, 5 read-state updated` (once a day per folder, when something differed; with `, <n> unreadable` if some mail couldn't be read) |
| `Sync` | `Reconcile Inbox: Outlook listed no items, kept 4102 indexed emails`, or `Reconcile Inbox abandoned: Outlook ids did not match the index, will retry` (with `, kept <n> indexed emails` before `, will retry` when it held some back). Clippy refused to remove mail from its index because Outlook's answer looked wrong |
| `Error` | `sync failed: <reason>` |
| `Error` | `<mailbox> is not in Outlook; skipped`, `folder "<path>" not found; skipped` |
| `Error` | `Outlook isn't responding: a call took longer than <n> s` |

When the first backfill of a mailbox finishes, a [balloon](tray.md#balloons) says so as
well.

## Tool calls

`Tool` events show the tool name and how long it took, for example `search_emails` with
`8 ms`. A call that failed reads `<tool> failed`.

An [attachment download](tools.md#attachment_link) shows as its own `Tool` event:
`attachment download` with how long the transfer took, or `attachment download
failed` when the transfer broke off (for example the shell stopped `curl`). The
event has no file name, email id or token. Requests that were refused (`401`, `404`,
`405`) leave no event. Making the link is an ordinary `attachment_link` tool call.

## Drafts and invites

| Kind | Text |
|---|---|
| `Draft` | `draft created: <subject>` (from `create_draft`) |
| `Draft` | `reply draft created: Re:: <subject>` (from `reply_draft`) |
| `Invite` | `meeting draft saved for 7 attendees: Tue 10/15 13:00–15:00` |
| `Invite` | `meeting draft not created: meeting drafts are disabled`, or `…: the request was cancelled`, or `…: <reason>` |
| `Blocked` | `get_attachment refused save_dir "<path>": <reason>`, or `create_draft: attachment 1 refused (<reason>)`. The path is outside your [allowed folders](security.md#file-access) |

## AI drafts

These appear while [AI drafts](ai-drafts.md) are on, or when you use the manual draft or
rescan items.

| Kind | Text |
|---|---|
| `Draft` | `AI reply draft created: <subject>`. When Clippy considered attachments or shared files for it, a suffix follows: `AI reply draft created: Re:: Budget (attachments: 3 read, 1 not read)`. "Read" counts files read in full or in part, "not read" counts files that were skipped. Only counts, never names. A replaced draft carries the same suffix before its final period. See [Attachments and shared files](ai-drafts.md#attachments-and-shared-files) |
| `Ai` | `AI drafts: 3 drafted, 5 no reply needed, 2 skipped (41 s)`. One summary line after each pass. `<d> drafted` is always there; the other parts appear only when they are not 0, in this order: `<u> updated`, `<m> moved to Deleted Items`, `<k> kept`, `<x> no reply needed`, `<l> low confidence`, `<s> skipped`, `<f> failed` |
| `Ai` | `AI drafts: no draft for the email received 2026-11-15 09:42: Claude's confidence 0.45 is below your minimum of 0.60. To draft it anyway, select it in Outlook and use "Draft a reply to the selected email" in the tray menu, or lower Minimum confidence in Settings → AI drafts.` Low confidence. It shows the received time, no subject or sender. The email is decided once |
| `Ai` | `AI drafts: daily limit of 30 reached. I'll continue tomorrow.` |
| `Ai` | `AI drafts: Claude's usage limit was reached. I'll try again at 14:05.` |
| `Ai` | `AI drafts: a Claude call failed (<kind>). I'll try again in 4 min.` Retries wait 1, 2, 4, 8, 16, 32, then 60 minutes |
| `Ai` | `AI drafts: a reply draft couldn't be saved in Outlook. I'll try again later.` |
| `Error` | `AI drafts: Claude isn't signed in`. Drafting waits until you sign in to Claude Code and press **Check** in Settings |

### Outdated drafts

When a newer message arrives in a thread where Clippy left an unsent AI draft, Clippy
re-checks the draft. See [Outdated drafts](ai-drafts.md#outdated-drafts). Results:

| Kind | Text |
|---|---|
| `Draft` | `AI reply draft updated (the thread moved on): <subject>. The old draft is in Deleted Items.` The draft was replaced and the unedited old one moved |
| `Draft` | `AI reply draft updated (the thread moved on): <subject>. The old draft is still in Drafts because it was changed or is open.` You had edited or opened the old one, so Clippy left it |
| `Draft` | `AI reply draft updated (the thread moved on): <subject>.` The old draft was already gone or sent |
| `Draft` | `Outdated AI reply draft moved to Deleted Items (no reply needed any more): <subject>` |
| `Ai` | `AI drafts: an outdated draft couldn't be moved to Deleted Items. It's still in Drafts.` |

A draft that is still right is kept without an event of its own. It only counts in the
summary line as `<k> kept`. Drafts you changed, opened or sent are never touched.

### Rescan

The **Rescan emails from the last 24 hours** item writes one summary line when it is
done, and shows the same text in a balloon:

| Kind | Text |
|---|---|
| `Ai` | `Rescan: 12 emails, 3 drafts, 1 outdated draft moved to Deleted Items, 8 no reply needed (94 s)` |
| `Ai` | `Rescan stopped (cancelled) after 5 of 12 emails: 1 draft, 4 no reply needed (38 s)` |

The line is `Rescan: <n> emails, <d> drafts`, followed by these parts, each only when it is not
0: `<u> drafts updated`, `<m> outdated drafts moved to Deleted Items`, `<k> drafts still
fine`, `<x> no reply needed`, `<l> low confidence`, `<s> skipped`, `<f> failed`, then the
time in seconds. A rescan that ends early starts `Rescan stopped (<reason>) after <i> of
<n> emails:`, where the reason is one of:

- `cancelled`
- `daily limit of <cap> reached`
- `Claude's usage limit was reached`
- `Claude isn't signed in`
- `Outlook isn't running`
- `3 Claude calls failed in a row`

The usual `Draft` and low-confidence events appear for each email as well.

### Facts about you

See [Facts about you](ai-drafts.md#facts-about-you). These texts never contain the file's content or path.

| Kind | Text |
|---|---|
| `Ai` | `Facts about you: my-email-context SKILL.md not found or unreadable — drafts go without it. Use "Edit my facts…" in the AI drafts menu.` Shown once when the file goes missing, becomes unreadable, is larger than 16 KiB or isn't valid UTF-8 |
| `Ai` | `Facts about you: using my-email-context SKILL.md again.` The file is back (filled in or template only) |

An untouched template is normal and reports nothing. Clippy checks the file each time it
builds a draft, so these lines appear at the next draft after the change.

### Related mail

| Kind | Text |
|---|---|
| `Ai` | `AI drafts now also send up to 5 related earlier emails from your mailbox to Claude, to write better replies. You can turn this off in Settings → AI drafts.` Shown once (with a balloon), if you consented before v0.1.1 and **Include related earlier emails** is on |

### Attachments and Never draft for

| Kind | Text |
|---|---|
| `Ai` | `AI drafts now also read attachments and shared files synced to this PC, and send their text to Claude, to write better replies. You can turn this off in Settings → AI drafts.` Shown once (with a balloon), if you consented before v0.1.1 and **Read attachments and synced shared files for drafts** is on |
| `Ai` | `AI drafts: added an entry to "Never draft for" (<n> entries).` You used [Don't draft for this sender](ai-drafts.md#dont-draft-for-this-sender). The address is not shown |

### Learning from your edits

See [Learning from how you edit drafts](ai-drafts.md#learning-from-how-you-edit-drafts).
These lines hold counts only, never text, subjects or addresses.

| Kind | Text |
|---|---|
| `Ai` | `AI drafts: noted how you edited 1 sent draft.` or `AI drafts: noted how you edited <n> sent drafts.` (for example `12`, or `1,234`). After a check of your sent mail that matched at least one sent reply to an AI draft. The number counts every draft you sent, including those sent unchanged. At most one check every 5 minutes, so several sent drafts usually appear in one line |
| `Ai` | `AI drafts now learn from how you edit Clippy's drafts before sending. The next writing-style update sends up to 20 of your edited replies (addresses, links and phone numbers removed) to Claude. You can turn this off in Settings → AI drafts.` Shown once (with a balloon) at the first AI draft Clippy saves after the update, if you consented before v0.1.1 and **Learn from how I edit Clippy's drafts** is on |

### Writing style

| Kind | Text |
|---|---|
| `Ai` | `writing style updated from 240 sent emails (52 s)` |
| `Ai` | `writing style updated from 240 sent emails and 7 edited drafts (52 s)`. The edited drafts were given to Claude (at least 3, at most 20) |
| `Ai` | `writing style not updated: only 12 sent emails (need 20)` |
| `Ai` | `writing style not updated: you edited it. Use Regenerate in Settings to replace it.` |
| `Ai` | `writing style not updated: <reason>`. The reason is `Claude's usage limit was reached`, `Claude isn't signed in`, `a Claude call failed (<kind>)`, `Claude's answer wasn't usable`, `couldn't save the skill file`, `couldn't read the index` or `couldn't prepare your sent emails` |
| `Ai` | `writing style: added how to offer meeting times` |
| `Ai` | `Writing style: SKILL.md not found or unreadable — drafts use no style guide. Regenerate it from AI drafts.` |
| `Ai` | `Writing style: your SKILL.md is missing or unreadable, so drafts use Clippy's saved copy. Regenerate it from AI drafts, or fix the file.` |
| `Ai` | `Writing style: using your SKILL.md again.` |

The first style skill is built once your Sent Items are indexed. Because Clippy indexes
Sent Items before the Inbox, this happens early in the first backfill, and you don't
need to wait for the whole Inbox. See [Your writing style](ai-drafts.md#your-writing-style).

## Updates

| Kind | Text |
|---|---|
| `Update` | `update check: v0.1.0 available` |
| `Update` | `update check: up to date (v0.1.0)` |
| `Update` | `update check failed: <reason>` |
| `Update` | `update v0.1.0 downloaded and verified` |
| `Update` | `update download failed: <reason>` |
| `Update` | `installing update v0.1.0` |
| `Update` | `update failed: <reason>` |
| `Update` | `updated from v0.0.9 to v0.1.0` |
| `Update` | `update v0.1.0 didn't start; back on v0.0.9` |

See [Updates](updates.md).
