# Changelog

The full list of releases, with downloads, is on
[GitHub](https://github.com/dirkpetersen/clippys-revenge/releases).

## v0.1.0

2026-11-15

- **Outdated AI drafts are re-checked.** When a newer message arrives in a thread where
  Clippy left an unsent AI draft, Clippy looks at the draft again. It keeps it if it is
  still right, replaces it with a fresh draft, or moves it to **Deleted Items** when no
  reply is needed any more. It only touches a draft that you haven't edited, opened or
  sent, and it never empties Deleted Items. The Activity window records each move. This
  is the one case where Clippy moves anything in your mailbox. See
  [Outdated drafts](ai-drafts.md#outdated-drafts).
- **New tray item: Rescan emails from the last 24 hours…** (AI drafts submenu). It goes
  through the last day of mail, drafts replies where one is needed and re-checks
  Clippy's drafts in those threads. It asks first, shows how many Claude calls it may
  use, stops at your daily limit, and turns into **Cancel rescan (d/t)** while it runs.
  See [Rescan](ai-drafts.md#rescan-emails-from-the-last-24-hours).
- **New setting: Update or remove my AI drafts when a thread moves on** (Settings, AI
  drafts, on by default). Turn it off to leave all AI drafts as they are. See
  [Settings](settings.md#ai-drafts).
- **Sent Items are indexed first.** On the first run (and after a rebuild), Clippy now
  indexes Sent Items before the Inbox, so your writing-style skill is built early and
  drafts sound like you sooner, without waiting for the whole Inbox.
- **Latest Opus by default.** AI drafts use the model `opus` (the newest Opus). This
  arrived in v0.0.9, below.
- **Clearer summaries.** The AI drafts line in Activity now also counts drafts that were
  updated, moved to Deleted Items or kept.
- **Docs.** The manual has new pages: [Settings](settings.md) lists every setting with
  its default, and [Activity](activity.md) lists the messages you can see there. The
  [Tray menu](tray.md) page now covers every item.
- **Index upgrade.** Clippy upgrades the index database to remember its own drafts. If
  you go back to an older version, it reports that the index is too new, and you rebuild
  it from the tray.

## v0.0.9

2026-11-15

- **AI drafts use the latest Opus.** The default model is now `opus`, which always means
  the newest Opus model. If you never changed the model, Clippy switches you over. A model
  you typed yourself stays as it is. Effort stays `medium`.
- **With an API key**, Clippy looks up the newest model of that family once a day and falls
  back to a known one if the lookup fails. **Check** shows which model `opus` means.
- **Your writing-style skill is visible.** If Clippy can't read your `my-email-style` skill,
  Activity and the log say so once, and again when it's back.

## v0.0.8

2026-11-14

- **Clippy never sends anything now.** `send_meeting_invite` and its confirmation
  dialog are gone. The new tool `create_meeting_draft` saves the meeting unsent in
  your calendar. You add the Zoom or Teams link and send it in Outlook. See
  [Meeting drafts](calendar.md#meeting-drafts) and
  [`create_meeting_draft`](tools.md#create_meeting_draft).
- Meeting drafts get the orange category **Clippy draft invite**. Clippy removes it
  once you have sent the meeting. Other categories stay.
- With `reply_to_id`, Claude also leaves a highlighted note in a reply draft in Drafts,
  so you don't forget to send the invite.
- New tray item **Draft invites (n)** lists your meeting drafts. Click one to open it
  in Outlook.
- New yellow **attention badge** in the top-right corner of the tray icon, with the
  number of draft invites. The tooltip says what needs your attention.
- Settings, Scheduling: **Meeting invites** is now **Draft only** or **Disabled**. The
  **Send** mode and the confirmation timeout are removed, and a saved **Send** loads
  as **Draft only**. The amber tray icon is gone.
- The writing-style skill's meeting section tells Claude Code to call
  `create_meeting_draft` when you pick one of the offered times. Unedited skills get
  it automatically at the next start.

## v0.0.7

2026-11-14

- Meeting times: `find_free_times` now ranks its suggestions by your calendar (times
  next to your meetings first, then next to tentative ones) and returns `offers` of
  exactly the meeting length plus a ready-to-paste `offer_text`, one option per line.
  New `offer_cet` option adds the time in CET and puts European office hours first,
  with the others under "Outside European office hours:".
- AI drafts offer meeting times: when an email asks for a meeting, the draft gets
  options from your calendar, computed by Clippy, not by the model. Two new settings,
  **Offer meeting times when an email asks for a meeting** and **Number of times to
  offer**. See [Meeting times](ai-drafts.md#meeting-times).
- The writing-style skill has a new section, "Offering meeting times", so Claude Code
  offers times the same way. Unedited skills get it automatically at the next start.
- New tool `email_stats`: counts mail by sender, domain, recipient, folder, month,
  week, weekday or hour in one call. Counts and names only.
- New tool `query_index`: one read-only `SELECT` over the views `mail`, `mail_fts` and
  `mail_attachments`, with full-text search and snippets. See
  [Tools reference](tools.md#email_stats) and
  [Security](security.md#statistics-and-sql-queries).
- AI drafts: an email Claude isn't confident enough about now shows a line in the
  Activity window with how to draft it anyway, and the pass summary counts
  "low confidence" separately.
- Privacy fix: the log no longer contains the text of tool error messages, which could
  quote search text or SQL back. Only the kind of error is logged.

## v0.0.6

2026-11-13

- Auto-update: the installed copy checks GitHub once a week and installs a newer
  full release at its next start. The download is checked against the release's
  SHA-256 and rolled back if the new version doesn't start. Set Settings, General,
  **Updates** to **Install automatically** (default), **Only tell me** or
  **Don't check**. See [Updates](updates.md).
- New **About Clippy's Revenge…** window in the tray menu: version, update status,
  **Check for updates**, **Update now** and links to the documentation and release
  notes.
- New balloons for a newer version, a finished update and a rollback.

## v0.0.5

2026-11-13

- AI drafts: Clippy can draft replies to new mail in your writing style with Claude
  Code or an Anthropic API key. Off by default, asks for consent, saves drafts only,
  never sends, and the model gets no tools. See [AI drafts](ai-drafts.md).
- Writing-style skill built from your own sent mail, saved as
  my-email-style in your Claude Code skills folder. Your edits are never
  overwritten automatically.
- New tray submenu **AI drafts** and a Settings tab with the same name.

## v0.0.2

Released as a full (latest) release, so the installer and
`releases/latest` links pick it up.

- The installer and the README use the `releases/latest` download.
- One-line install: paste `irm ... | iex` into PowerShell.
- `install.ps1 -WhatIf` downloads and verifies the release without installing.
- Never deletes email: this is now a documented hard rule, checked by a test that
  scans the source.
- Reconcile refuses mass deletes from the index when Outlook's ids don't match the
  index.

## v0.0.1

First release.

- Tray app with a local MCP server on `127.0.0.1`, protected by a bearer token.
- Local SQLite full-text index of your mailbox and shared mailboxes.
- Tools: `search_emails`, `get_message`, `get_attachment`, `save_attachments`,
  `create_draft`, `reply_draft`, `list_events`, `find_free_times`,
  `send_meeting_invite` and `index_status`.
- Tray menu, Settings and Activity windows, and a confirmation dialog for meeting
  invites.
- `install.ps1` installer and uninstaller.
