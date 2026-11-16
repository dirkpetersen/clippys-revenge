# Settings

Open Settings from the tray menu, **Settings…**. The window is titled
`Clippy's Revenge — Settings` and has five tabs: **Connection**, **Index**,
**Scheduling**, **General** and **AI drafts**.

- Changes apply when you press **OK** or **Apply**. **Cancel** discards them. The
  exceptions are on the AI drafts tab: giving consent and saving or removing an API key
  take effect at once.
- A wrong value is rejected, with the reason next to the field (for example
  `Must be between 1024 and 65535.` or `Must be a whole number.`). Pressing OK or Apply
  then switches to the tab with the first problem.
- After Apply the line at the bottom says `Saved.`, or names a side effect that failed
  (for example `Couldn't update the Windows startup entry: <reason>`).
- Everything is saved in `%APPDATA%\ClippysRevenge\settings.json`, except the auth token
  (`token.bin`) and an Anthropic API key (`anthropic-key.bin`). Both files are next to
  `settings.json` and are encrypted for your Windows user (DPAPI).
- Before a port change takes effect, the window asks first, because Claude Code must be
  set up again. The same goes for regenerating the token.
- If you start the app a second time, the running copy opens Settings (on the
  Connection tab, or brings an open Settings window to the front).
- Where a setting is "live", it applies at once, without a restart.

The tables list each setting's name in `settings.json`. You don't need to edit the file:
see [Only in settings.json](#only-in-settingsjson).

## Connection

| Setting | `settings.json` | Default | What it does |
|---|---|---|---|
| **Port** | `Port` | `8765` (1024 to 65535) | The port of `http://127.0.0.1:<port>/mcp`. Apply asks `Change the port from <old> to <new>?`, because Claude Code must be set up again with the new command. If the new port can't be opened, nothing is saved and the server stays on the old one: `Port <new> is in use or can't be opened. The server stays on port <old>.` |
| **Auth token** | (`token.bin`) | Random, created on first start | Hidden. **Show** (then **Hide**) and **Copy** buttons. **Regenerate…** asks `Regenerate the auth token?` (Cancel is the default) and only takes effect when you press OK or Apply. From then on the old token no longer works |
| **Claude Code command** | | | The full `claude mcp add` command, with the token shown as `••••••••` until you press **Show**. **Copy command** always copies the whole command, with the real token |
| **Test connection** | | | Sends a request without the token (expects `401`) and one with it (expects an MCP answer) and shows `✓ Connection works: …` or `✗` and the reason. It tests the running server, not unsaved changes: while a port or token change is pending, it says `Testing the running server. Press Apply first to test your changes.` See [Troubleshooting](troubleshooting.md#test-connection-messages) |
| **From WSL** | | | The `.wslconfig` snippet for [WSL](connect.md#from-wsl) and a `curl` line to check it |
| WSL token line | | | A read-only line, `export CLIPPY_TOKEN=••••••••`, with the token masked until you press **Show** for the auth token. For [attachment download links](tools.md#attachment_link) from WSL |
| **Copy WSL token line** | | | Copies `export CLIPPY_TOKEN=<token>` (the real token, even while the line is masked). Add it to `~/.bashrc` in WSL. After **Regenerate**, copy it again. See [Set CLIPPY_TOKEN](connect.md#set-clippy_token-for-downloads) |

## Index

At the top, pick **My mailbox** or one of your shared mailboxes. The fields below belong
to the mailbox you picked. See [Indexing](indexing.md).

| Setting | `settings.json` | Default | What it does |
|---|---|---|---|
| **Mailbox** picker, **Add…**, **Remove…** | `SharedMailboxes` (`Smtp`, `DisplayName`, `Enabled`) | Only your own mailbox | **Add…** lists the shared mailboxes in your Outlook profile that aren't indexed yet (`Start Outlook to see more mailboxes` while Outlook isn't running). **Remove…** asks `Stop indexing <name>?` with **Delete its index**, **Keep index file** and **Cancel** (the default). The mailbox is switched off either way; it happens when you press OK or Apply. The same as checking and unchecking in the tray's [Shared mailboxes](tray.md#shared-mailboxes) |
| **Retention (months)** | `RetentionMonths` | `12` (1 to 120) | How far back Clippy keeps mail. A larger value starts a backfill of the older mail, a smaller one removes the older mail from the index. Live |
| **Use global** | `SharedMailboxes[].RetentionMonths` (`null` = global) | On, for shared mailboxes | Only shown for a shared mailbox. Off: the mailbox keeps its own retention |
| **Indexed folders** | `IndexedFolders`, `SharedMailboxes[].IndexedFolders` | `Inbox` and `Sent Items` | A tree of check boxes, Inbox and Sent Items first. At least one folder is needed. A configured folder Outlook doesn't have is marked `(not found)`. Live: sync is planned again |
| Statistics line | | | For the mailbox you picked, for example `48,210 emails · oldest 2025-11-12 · newest 2026-11-12 · 512.4 MB · last sync 2 min ago`, or `Not indexed yet.` |
| **Rebuild…** | | | Asks `Rebuild the index for <name>?`, then deletes that mailbox's index and reads it again. A shared mailbox you just added can be rebuilt after Apply |
| **Sync interval (seconds)** | `SyncIntervalSeconds` | `300` (30 to 3600) | How often Clippy looks for new mail. Applies to all mailboxes. Live |

The folder tree loads from Outlook in the background, for at most 15 seconds. If Outlook
doesn't answer, it shows only the folders you already configured, with the note
`Outlook didn't answer, so only the configured folders are shown.`

## Scheduling

These are the defaults for [`find_free_times`](tools.md#find_free_times) and for
[meeting drafts](calendar.md). Claude can override them per call.

| Setting | `settings.json` | Default | What it does |
|---|---|---|---|
| **Work hours: start** / **end** | `WorkStartHour`, `WorkEndHour` | `8` / `17` | Hours Clippy offers meetings in. Start 0 to 23, end 1 to 24 and later than the start. Live |
| **Business days only** | `BusinessDaysOnly` | On | Skips Saturdays and Sundays |
| **Tentative counts as busy** | `TentativeIsBusy` | On | A tentative meeting blocks the time |
| **Time-zone label** | `TimeZoneLabel` | Empty (automatic) | The label in suggestions, such as `PT`. At most 16 printable characters. Next to the box, `Automatic: <label>` shows the label Clippy uses when the box is empty |
| **Meeting invites** | `MeetingInviteMode` (`DraftOnly`, `Disabled`) | **Draft only** | **Draft only**: `create_meeting_draft` saves the meeting unsent in your calendar. **Disabled**: it refuses. Clippy never sends an invite. A saved `Send` from an older version loads as **Draft only** |

## General

| Setting | `settings.json` | Default | What it does |
|---|---|---|---|
| **Attachment folder** | `AttachmentDir` | `%USERPROFILE%\Downloads\Clippy's Revenge` | Where attachments Claude saves go. Has a **Browse…** button. Always allowed, even if it isn't in the list below (`The attachment folder is always allowed.`) |
| **Allowed folders** | `AllowedFolders` | The attachment folder and `%USERPROFILE%\Documents` | The only folders Claude may read or write files in. **Add…** and **Remove**. Full local paths only: no network shares (`\\server\…`). See [file access](security.md#file-access) |
| **Start with Windows** | `StartWithWindows` | On | Starts Clippy at logon. The same setting as the tray item |
| **Notify on errors** | `NotifyOnErrors` | On | Shows a balloon when an error lasts more than 2 minutes |
| **Log level** | `LogLevel` | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` or `None`. Applies at once to the log in `%LOCALAPPDATA%\ClippysRevenge\logs` |
| **Updates** | `Updates` (`Install`, `Notify`, `Off`) | **Install automatically** | **Install automatically**, **Only tell me** or **Don't check**. Clippy checks GitHub once a week. See [Updates](updates.md) |
| **Check now** | | | Opens the [About window](updates.md#the-about-window) and starts an update check at once |

## AI drafts

Turns on and tunes [AI drafts](ai-drafts.md). The settings are under `AiDrafting` in
`settings.json`; the second column gives the name inside it.

The status line next to **Use** is checked the first time you open the tab, and again
when you press **Check**. It checks the settings you have already applied: while a
change to **Use**, the path or the model is pending, the tab says
`Checking the saved settings. Press Apply first to check your changes.`

| Setting | `AiDrafting.…` | Default | What it does |
|---|---|---|---|
| **Draft replies to new email automatically** | `Enabled` | Off | The main switch. Shows the [consent dialog](ai-drafts.md#consent-and-what-is-sent) the first time; the consent is saved at once (`ConsentedAtUtc`). Mail received before you turned it on is ignored (`EnabledAtUtc`) |
| **Use:** | `Backend` (`ClaudeCode`, `ApiKey`) | **Claude Code on this PC (your Claude subscription)** | Or **Anthropic API key**. The status line shows whether it is ready and how. When Claude Code isn't ready, a hint says `Clippy never signs in to Claude for you. Run claude in a terminal and sign in there, or use an Anthropic API key.` |
| **API key** | (`anthropic-key.bin`) | None | A masked box with **Save key** and **Remove key…** (asks `Remove the saved API key?`). Below it: `A key is saved. It is never shown.`, `No key saved.` or `The saved key can't be read. Save it again.` Stored encrypted for your Windows user. Only used with **Anthropic API key** |
| **Claude Code path (optional)** | `ClaudeCliPath` | Empty (Clippy finds it) | A full local path, with backslashes, to `claude.exe` or Claude Code's `cli.js`. Has **Browse…** |
| **Model** | `Model` | `opus` | `opus`, `sonnet` and `haiku` always mean the newest model of that family. A hint next to the box says `latest Opus`, `latest Sonnet` or `latest Haiku`. You can type a full model name such as `claude-opus-5-5`. Up to 64 letters, digits, `.`, `_`, `-`, `[` and `]`, starting with a letter. A saved `claude-opus-5-5` from before v0.1.0 loads as `opus`. See [Model, effort and cost](ai-drafts.md#model-effort-and-cost) |
| **Effort** | `Effort` | `medium` | `low`, `medium`, `high`, `xhigh` or `max`. Higher is slower and uses more of your plan or API budget |
| **Daily limit (Claude calls)** | `DailyCap` | `30` (1 to 500) | Claude calls per day, all mailboxes together. Automatic drafting, **Draft a reply to the selected email**, rescans and re-checks of outdated drafts all count. When it is used up, Clippy continues the next day |
| **Minimum confidence (0–1)** | `MinConfidence` | `0.6` (0 to 1) | Replies Claude is less sure about are not drafted. See [Emails Clippy decides not to draft](ai-drafts.md#emails-clippy-decides-not-to-draft) |
| **Ignore mail older than (days)** | `MaxAgeDays` | `2` (1 to 14) | Automatic drafting skips mail older than this |
| **Only when I'm in To or CC** | `RequireDirectRecipient` | On | Skips mail that doesn't have your mailbox address in To or CC |
| **Update or remove my AI drafts when a thread moves on** | `ReviseDrafts` | On | Lets Clippy re-check its own unsent AI drafts when a newer message arrives in their thread. It keeps a draft that is still right, replaces one that is outdated, or moves it to **Deleted Items** when no reply is needed any more. It only ever touches drafts you haven't edited or opened. Off: all drafts stay as they are. See [Outdated drafts](ai-drafts.md#outdated-drafts) |
| **Include related earlier emails** | `RelatedMail` | On | Adds up to 5 earlier emails from the same mailbox (last 12 months) to each draft request, so a reply fits the history. Off: none are sent. See [Related earlier mail](ai-drafts.md#related-earlier-mail) |
| **Start each draft with a highlighted note on why it was drafted** | `ReasonLine` | On | Every AI draft starts with a yellow `Clippy: …` line (at most 160 characters) saying what the email asks and what the draft answers. Delete it before sending. Off: no line. See [The reason line](ai-drafts.md#the-reason-line) |
| `Facts about you: filled in`, `Facts about you: template only` or `Facts about you: not found` | | | Read-only line about your [facts file](ai-drafts.md#facts-about-you). Edit the file with **Edit my facts…** in the tray's AI drafts menu |
| **Read attachments and synced shared files for drafts** | `ReadAttachments` | On | Clippy reads the text of the attachments of the email and its thread, and of OneDrive/SharePoint files that are already synced to this PC (only links from you or your own organization), and sends it to Claude with the draft request. Off: only attachment names are sent. Nothing is stored, and nothing is downloaded from the network. See [Attachments and shared files](ai-drafts.md#attachments-and-shared-files) |
| **Offer meeting times when an email asks for a meeting** | `MeetingTimes` | On | The draft offers free times from your calendar. See [Meeting times](ai-drafts.md#meeting-times) |
| **Number of times to offer** | `MeetingTimeOptions` | `5` (1 to 10) | How many options per draft. Greyed out while meeting times are off |
| **Never draft for** | `ExcludeSenders` | Empty | One address or domain per line (a full address, or a domain on its own, which also covers its subdomains). At most 200 lines. The tray items [Don't draft for this sender](ai-drafts.md#dont-draft-for-this-sender) add lines here. To undo one, delete the line |
| **Only draft for** | `IncludeOnlySenders` | Empty | When not empty, Clippy drafts only for these senders. Same form |
| **Mailboxes** | `SharedMailboxes[].AiDrafts` | **My mailbox** | Your own mailbox is always on while AI drafts are on (its box can't be changed). Each shared mailbox you index has a check box, off by default |
| **Writing style** | | | Status line: `Generated <date> from <n> sent emails`, `Edited by you` or `Not generated yet` |
| **Update weekly** | `AutoRegenerateStyle` | On | Refreshes your writing-style skill about once a week |
| **Learn from how I edit Clippy's drafts** | `LearnFromEdits` | On | Next to **Update weekly**. Clippy keeps a redacted copy of each AI draft it saves and compares it with the reply you send. The next writing-style update sends up to 20 of your edited replies (at least 3 are needed; addresses, links and phone numbers removed) to Claude, to add an "Edits I usually make to drafts" section to your style. Off: nothing new is recorded or matched and no edits are sent; what is already stored ages out or goes with **Rebuild index…** (it is not deleted at once). See [Learning from how you edit drafts](ai-drafts.md#learning-from-how-you-edit-drafts) |
| **View** | | | Shows the style text read-only, in a window titled `Writing style skill` |
| **Regenerate…** | | | Builds the style skill now (asks for consent first if you haven't given it). If you edited it, Clippy asks `You edited your writing style skill. Replace your edits with a new version?` with **Replace** and **Keep mine** (the default). The result shows below, for example `Writing style updated from 180 sent emails.` |
| **Open in editor** | | | Opens your `SKILL.md` in the app Windows uses for `.md` files. With no such app, Clippy opens the folder instead. With no skill yet: `There is no writing style skill yet. Press Regenerate to create one.` |
| `Today: <n> of <cap> Claude calls, <d> drafts` | | | Today's usage against the daily limit. The [token tally](ai-drafts.md#token-tally) (`Today: 18 AI calls, ~210k tokens (in 190k / out 20k)`) is in the tray's AI drafts menu and in the Activity window |
| `What gets sent to Claude: see AI drafts in the manual` | | | A link to the [AI drafts page](ai-drafts.md) |

If the email index couldn't be opened, the tab says `AI drafts need the email index,
which couldn't be opened. Check the tray icon for the reason.` and the writing-style
controls (**Update weekly**, **Learn from how I edit Clippy's drafts**, **View**,
**Regenerate…** and **Open in editor**) are greyed out.

The writing-style skill is built from your **Sent Items** in the index. Clippy builds the
first one as soon as Sent Items are indexed, which is early in the first backfill
because Sent Items are indexed before the Inbox. See
[Your writing style](ai-drafts.md#your-writing-style).

## Only in settings.json

One setting has no box in the window: `AiDrafting.TimeoutSeconds` (default `180`, 30 to
900), the time limit of one drafting call. Change it in `settings.json` while Clippy is
not running; Clippy rewrites the file whenever a setting changes.

The file accepts `//` comments and trailing commas. If it isn't valid JSON or a value is
out of range, Clippy keeps it as `settings.json.bad`, starts with defaults and notes
this in the log.

Some values are internal. Leave them alone:

- `AiDrafting.ConsentedAtUtc` and `AiDrafting.EnabledAtUtc`: when you gave consent and
  when you last turned automatic drafting on.
- `AiDrafting.RelatedMailNoticeShown`, `AiDrafting.AttachmentsNoticeShown` and
  `AiDrafting.EditsNoticeShown`: the one-time notices about related mail, attachments
  and [learning from edits](ai-drafts.md#notice-for-existing-users) were shown.
