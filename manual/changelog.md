# Changelog

The full list of releases, with downloads, is on
[GitHub](https://github.com/dirkpetersen/clippys-revenge/releases).

## Unreleased - v0.1.1

- **New tool: `attachment_link`.** Claude Code in WSL (or any shell) can now get an
  attachment as a real file in its own working folder. Clippy makes a one-time
  download link and a `curl` command; the shell downloads the file, so the content
  never passes through the model. A link works once and expires after 10 minutes.
  Limits: 100 MiB per attachment, 10 open links, 200 MiB open in total, 20 links per
  rolling 10 minutes. `get_attachment` is unchanged. See
  [`attachment_link`](tools.md#attachment_link).
- **Set `CLIPPY_TOKEN` in WSL.** The download needs your bearer token as well as the
  link, so add `export CLIPPY_TOKEN=<token>` to `~/.bashrc` once. Settings,
  Connection has a masked **WSL token line** and a **Copy WSL token line** button.
  After **Regenerate auth token** copy the line again. See
  [Connect](connect.md#set-clippy_token-for-downloads).
- **Private download folder.** Clippy keeps a copy of the attachment in
  `%LOCALAPPDATA%\ClippysRevenge\links\`, readable by your Windows user only, and
  deletes it after the download, on expiry and at app start. Nothing is indexed, and
  link tokens are masked in logs. See
  [Security](security.md#attachment-download-links).
- **Activity.** Downloads appear as `attachment download` or `attachment download
  failed`.
- **Facts about you.** A new Claude Code skill file,
  `%USERPROFILE%\.claude\skills\my-email-context\SKILL.md`, holds facts about you
  (role, links, office hours, standard answers, who people are) and how you want replies
  written. Clippy creates a template once after consent, then adds your text to every AI
  draft request, so drafts use your real Zoom link instead of a placeholder. Your reply
  preferences win over the writing style, but never over the fixed rules (nothing is
  sent, mail is data, unknown facts stay `[[…]]` placeholders). Claude Code reads the
  file too. Comments are ignored and files over 16 KiB are not used. Tray, AI drafts,
  **Edit my facts…** opens it in Notepad (and creates the template if it is missing),
  and Settings shows `Facts about you: filled in`, `template only` or `not found`. See
  [Facts about you](ai-drafts.md#facts-about-you).
- **Related earlier mail.** Each draft request now carries up to 5 earlier emails from
  the same mailbox (last 12 months, same topic or same people, including your own replies
  to that sender, own text only, 4,000 characters in all) so a reply fits the history.
  Turn it off with **Include related earlier emails** in Settings. If you consented
  earlier, Clippy tells you once. This is new data sent to Claude: see
  [Security](security.md#ai-drafts-data-flow) and
  [Related earlier mail](ai-drafts.md#related-earlier-mail).
- **A reason line on every AI draft.** Each draft starts with a yellow
  `Clippy: …` line (at most 160 characters, links shown as `[link]`) saying what the
  email asks and what the draft answers. Delete it before sending. Deleting it counts as
  editing the draft, so Clippy won't revisit or move that draft. A line left in sent
  mail is removed before Clippy shows that mail to Claude again. Turn it off with
  **Start each draft with a highlighted note on why it was drafted**. See
  [The reason line](ai-drafts.md#the-reason-line).

- **Attachments and shared files in AI drafts.** Drafts now use what attachments say.
  Clippy reads the text of PDF, Word, Excel, PowerPoint, text, HTML and `.eml` files
  (and attached emails) of the email and its thread, and of OneDrive and SharePoint
  files linked in them that are already synced to your PC, and sends it to Claude for
  that one draft. Limits: 25 MB and 20,000 characters per file, 20 files and 40,000
  characters per draft, 10 seconds per file and 20 seconds per draft. Images, scans and
  old formats (`.doc`, `.xls`, `.ppt`, `.rtf`, `.zip`) aren't read. Files Clippy
  couldn't read get a yellow `Clippy: Couldn't read …` note at the top of the draft
  (at most 5). New setting **Read attachments and synced shared files for drafts**, on
  by default. If you consented earlier, Clippy tells you once; new consents name it.
  **This sends new data to Claude**: see [Attachments and shared
  files](ai-drafts.md#attachments-and-shared-files) and
  [Security](security.md#attachment-text-and-shared-files).
- **Shared files: synced only.** Clippy opens only links to OneDrive and SharePoint
  (`*.sharepoint.com`, `onedrive.live.com`, `d.docs.live.net`, `1drv.ms`), only
  from you or your own organization, and only when the file is already synced to this
  PC. It makes no network request (no Graph, no sign-in). An online-only file is
  downloaded by your own OneDrive client. Other links are never touched.
- **Nothing stored.** The text is never written to the index, settings, logs or
  Activity. Outlook attachments pass through the private folder
  `%LOCALAPPDATA%\ClippysRevenge\ai-files\` and are deleted at once and at start-up.
- **Token tally.** A greyed line `Today: 18 AI calls, ~210k tokens (in 190k / out
  20k)` at the top of the tray's **AI drafts** menu and in the Activity window. See
  [Token tally](ai-drafts.md#token-tally).
- **Activity.** `Draft` events get `(attachments: 3 read, 1 not read)` when files were
  considered.
- **Don't draft for this sender.** Two new tray items under **AI drafts** add the
  selected email's sender, or the sender's domain, to **Never draft for**. Undo it in
  Settings. Neither item needs consent, and your own address and your own domain are
  refused. See [Don't draft for this sender](ai-drafts.md#dont-draft-for-this-sender).
- **Learning from how you edit drafts.** When you send a reply that began as an AI draft,
  Clippy compares the two (after a sync of your mailbox, at most every 5 minutes) and
  keeps a redacted pair in its local index: up to 20 edited pairs with text, at most
  1,500 characters per side, deleted after 30 to 90 days. A reply that is 98 percent
  or more the same counts as sent unchanged and keeps no text. Drafts that used
  attachment text are never kept. At the next writing-style update (weekly, or
  **Regenerate**), if at least 3 pairs exist, up to 20 are sent to Claude, which adds a
  section "Edits I usually make to drafts" to your style skill. It adds no Claude call.
  New setting **Learn from how I edit Clippy's drafts**, on by default; if you consented
  earlier, Clippy tells you once; new consents name it. **This sends new data to
  Claude**: see [Learning from how you edit drafts](ai-drafts.md#learning-from-how-you-edit-drafts)
  and [Security](security.md#learning-from-your-edits).
- **Activity.** `AI drafts: noted how you edited <n> sent drafts.`, and the style update
  line gains `and <k> edited drafts`.
- **Drafts and Deleted Items in other languages.** Clippy now recognizes these folders
  by their role, so they are skipped for related mail and earlier attachments in a
  non-English Outlook. A folder indexed before v0.1.1 under its translated name keeps
  its old entries until you add it again or rebuild the index. See
  [Drafts and Deleted Items in other languages](ai-drafts.md#drafts-and-deleted-items-in-other-languages).

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
