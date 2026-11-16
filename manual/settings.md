# Settings

Open Settings from the tray menu, **Settings…**. The window has five tabs:
**Connection**, **Index**, **Scheduling**, **General** and **AI drafts**.

- Changes apply when you press **OK** or **Apply**. **Cancel** discards them.
- A wrong value is rejected, with the reason next to the field.
- Everything is saved in `%APPDATA%\ClippysRevenge\settings.json`, except the auth token
  and an Anthropic API key. Those are encrypted for your Windows user, in
  `token.bin` and a separate key file next to it.
- The window warns before a change that breaks your Claude Code setup: changing the port
  or regenerating the token.
- If you start the app a second time, it opens Settings in the running copy.
- Where a setting is "live", it applies at once, without a restart.

## Connection

| Setting | Default | What it does |
|---|---|---|
| **Port** | `8765` (1024 to 65535) | The port of `http://127.0.0.1:<port>/mcp`. Changing it asks `Change the port from <old> to <new>?` because Claude Code must be set up again. If the new port can't be opened, nothing is saved and the server stays on the old one |
| **Auth token** | Random, created on first start | Hidden. **Show** and **Copy** buttons. **Regenerate…** asks first and only takes effect when you press OK or Apply. The old token stops working at once |
| **Claude Code command** | | The full `claude mcp add` command with the token hidden until you press **Show**. **Copy command** always copies the whole command |
| **Test connection** | | Sends a request without the token (expects `401`) and one with it (expects an MCP answer) and shows a check mark or the reason it failed. It tests the running server, not unsaved changes. See [Troubleshooting](troubleshooting.md#test-connection-messages) |
| **From WSL** | | The `.wslconfig` snippet for [WSL](connect.md#from-wsl) |

## Index

At the top, pick **My mailbox** or one of your shared mailboxes. The fields below belong
to the mailbox you picked. See [Indexing](indexing.md).

| Setting | Default | What it does |
|---|---|---|
| **Mailbox** picker, **Add…**, **Remove…** | Only your own mailbox | **Add…** lists the mailboxes in your Outlook profile that aren't indexed yet. **Remove…** asks `Stop indexing <name>?` (Delete its index, Keep index file, Cancel). The same as checking and unchecking in the tray's **Shared mailboxes** |
| **Retention (months)** | `12` (1 to 120) | How far back Clippy keeps mail. A larger value starts a backfill of the older mail, a smaller one removes the older mail from the index. Live |
| **Use global** | On, for shared mailboxes | A shared mailbox can use the global retention or its own value |
| **Indexed folders** | Inbox and Sent Items | A tree of check boxes. At least one folder is needed. Live: sync is planned again |
| Statistics line | | Email count, oldest and newest item, database size and last sync of the mailbox you picked |
| **Rebuild…** | | Deletes that mailbox's index and reads it again |
| **Sync interval (seconds)** | `300` (30 to 3600) | How often Clippy looks for new mail. Applies to all mailboxes. Live |

The folder tree loads from Outlook in the background, for at most 15 seconds. If Outlook
doesn't answer, it shows only the folders you already configured, with a note.

## Scheduling

These are the defaults for [`find_free_times`](tools.md#find_free_times) and for
[meeting drafts](calendar.md). Claude can override them per call.

| Setting | Default | What it does |
|---|---|---|
| **Work hours: start** / **end** | `8` / `17` | Hours Clippy offers meetings in. Start 0 to 23, end 1 to 24 and later than the start. Live |
| **Business days only** | On | Skips Saturdays and Sundays |
| **Time-zone label** | Empty (automatic) | The label in suggestions, such as `PT`. At most 16 characters. When empty, the box shows `Automatic: <label>` |
| **Tentative counts as busy** | On | A tentative meeting blocks the time |
| **Meeting invites** | **Draft only** | **Draft only**: `create_meeting_draft` saves the meeting unsent in your calendar. **Disabled**: it refuses. Clippy never sends an invite. A saved `Send` from an older version loads as **Draft only** |

## General

| Setting | Default | What it does |
|---|---|---|
| **Attachment folder** | `%USERPROFILE%\Downloads\Clippy's Revenge` | Where attachments Claude saves go. Has a **Browse…** button. Always allowed, even if you remove it from the list below |
| **Allowed folders** | The attachment folder and `%USERPROFILE%\Documents` | The only folders Claude may read or write files in. **Add…** and **Remove**. Local paths only (no network shares). See [file access](security.md#file-access) |
| **Start with Windows** | On | Starts Clippy at logon. The same setting as the tray item |
| **Notify on errors** | On | Shows a balloon when an error lasts more than 2 minutes |
| **Log level** | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` or `None`. Applies at once to the log in `%LOCALAPPDATA%\ClippysRevenge\logs` |
| **Updates** | **Install automatically** | **Install automatically**, **Only tell me** or **Don't check**. Clippy checks GitHub once a week. See [Updates](updates.md) |
| **Check now** | | Opens the About window and starts an update check at once |

## AI drafts

Turns on and tunes [AI drafts](ai-drafts.md). The **Check** button tests the settings you
have already applied, so press **Apply** first. Giving consent and saving or removing an
API key take effect at once, not only when you press OK.

| Setting | Default | What it does |
|---|---|---|
| **Draft replies to new email automatically** | Off | The main switch. Shows the [consent dialog](ai-drafts.md#consent-and-what-is-sent) the first time. Mail received before you turned it on is ignored |
| **Use** | **Claude Code on this PC (your Claude subscription)** | Or **Anthropic API key**. A status line shows whether it is ready and how, with a **Check** button. Clippy never signs in to Claude for you: sign in to Claude Code in a terminal yourself |
| **API key** | None | A masked box with **Save key** and **Remove key…**. The saved key is never shown again. It is stored encrypted for your Windows user. Only used with **Anthropic API key** |
| **Claude Code path (optional)** | Empty (Clippy finds it) | A full local path to `claude.exe` or a `cli.js`. Has **Browse…** |
| **Model** | `opus` | `opus`, `sonnet` and `haiku` always mean the newest model of that family. A hint next to the box says `latest Opus` and so on. You can type a full model name such as `claude-opus-5-5`. Up to 64 letters, digits, `.`, `_`, `-`, `[` and `]`, starting with a letter. See [Model, effort and cost](ai-drafts.md#model-effort-and-cost) |
| **Effort** | `medium` | `low`, `medium`, `high`, `xhigh` or `max`. Higher is slower and uses more of your plan or API budget |
| **Daily limit (Claude calls)** | `30` (1 to 500) | Claude calls per day, all mailboxes together. Automatic drafting, rescans and re-checks all count. When it is used up, Clippy continues the next day |
| **Minimum confidence (0–1)** | `0.6` | Replies Claude is less sure about are not drafted. See [Emails Clippy decides not to draft](ai-drafts.md#emails-clippy-decides-not-to-draft) |
| **Ignore mail older than (days)** | `2` (1 to 14) | Automatic drafting skips mail older than this |
| **Only when I'm in To or CC** | On | Skips mail that doesn't have your mailbox address in To or CC |
| **Update or remove my AI drafts when a thread moves on** | On | Lets Clippy re-check its own unsent AI drafts when a newer message arrives in their thread. It keeps a draft that is still right, replaces one that is outdated, or moves it to **Deleted Items** when no reply is needed any more. It only ever touches drafts you haven't edited or opened. Turn it off to leave all drafts as they are. See [Outdated drafts](ai-drafts.md#outdated-drafts). This is the `ReviseDrafts` setting in `settings.json` |
| **Offer meeting times when an email asks for a meeting** | On | The draft offers free times from your calendar. See [Meeting times](ai-drafts.md#meeting-times) |
| **Number of times to offer** | `5` (1 to 10) | How many options per draft |
| **Never draft for** | Empty | One address or domain per line (`ann@example.org`, or `example.org` including subdomains). At most 200 lines |
| **Only draft for** | Empty | When not empty, Clippy drafts only for these senders. Same form |
| **Mailboxes** | **My mailbox** | Your own mailbox is always on while AI drafts are on. Each shared mailbox you index has a check box, off by default |
| **Writing style** | | Status line: `Generated <date> from <n> sent emails`, `Edited by you` or `Not generated yet`. See below |
| **Update weekly** | On | Refreshes your writing-style skill about once a week |
| **View** | | Shows the style text read-only |
| **Regenerate…** | | Builds the style skill now. If you edited it, Clippy asks `You edited your writing style skill. Replace your edits with a new version?` with **Replace** and **Keep mine** |
| **Open in editor** | | Opens your `SKILL.md` in Notepad. If there is no skill yet, it says so |
| `Today: <n> of <cap> Claude calls, <d> drafts` | | Read-only line with today's usage |
| `What gets sent to Claude` | | A link to the [AI drafts page](ai-drafts.md) |

The writing-style skill is built from your **Sent Items** in the index. Clippy builds the
first one as soon as Sent Items are indexed, which is early in the first backfill
because Sent Items are indexed before the Inbox. See
[Your writing style](ai-drafts.md#your-writing-style).

### Only in settings.json

One AI setting has no box in the window: `AiDrafting.TimeoutSeconds` (default `180`, 30
to 900), the time limit of one drafting call. You can change it in `settings.json`
while Clippy is not running.
