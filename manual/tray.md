# Tray and Settings

Clippy's Revenge has no main window. Everything lives in the paperclip icon in the
system tray (you may need to click the `^` to see hidden icons).

## Icon colors

The icon shows what the app is doing. Hover over it for a short description.

| Icon | Meaning |
|---|---|
| Green | Idle, or answering a tool call (brief pulse) |
| Blue | Indexing: pulling new mail, purging old mail, or the first backfill (animated) |
| Grey | Indexing is paused |
| Grey with `!` | Outlook is not running, or not responding |
| Amber | Waiting for you to confirm a meeting invite |
| Red | Error. The tooltip gives the short reason |

Double-click the icon to open the Activity window.

## Tray menu

Right-click the icon.

| Item | What it does |
|---|---|
| Status lines | Current state and email count, then the last sync time and when the next one runs |
| **Start Outlook** | Shown only while Outlook isn't running. Starts Outlook. This is the one place the app ever starts it |
| **Sync now** | Starts a sync cycle at once (disabled while paused) |
| **Pause indexing** / **Resume indexing** | Pauses or resumes syncing. The pause isn't remembered after a restart |
| **Rebuild index…** | Deletes the index and reads your mail from Outlook again. Asks whether to rebuild **My mailbox** or **All mailboxes** |
| **Shared mailboxes** | Check a mailbox to index it; uncheck to stop. See [shared mailboxes](indexing.md#shared-mailboxes) |
| **AI drafts** | Submenu: **Draft replies automatically** (a check mark; asks for [consent](ai-drafts.md#consent-and-what-is-sent) the first time), **Draft a reply to the selected email**, **Show Drafts in Outlook** (hidden while Outlook isn't running) and **AI drafts settings...**. See [AI drafts](ai-drafts.md) |
| **Copy "claude mcp add" command** | Puts the full setup command on the clipboard |
| **Copy auth token** | Copies the token |
| **Regenerate auth token…** | Replaces the token after you confirm. Claude Code must be set up again |
| **Activity…** | Opens the Activity window |
| **Settings…** | Opens Settings |
| **Open log folder** | Opens `%LOCALAPPDATA%\ClippysRevenge\logs` |
| **Open data folder** | Opens the data folder |
| **Start with Windows** | Toggles starting at logon |
| **About Clippy's Revenge…** | Opens the About window: version, update status, **Update now** and links to the documentation and release notes. See [Updates](updates.md#the-about-window) |
| **Exit** | Quits the app |

## Activity window

Shows the current state, a progress bar during the first backfill, and the last
500 events. You can filter by kind and by mailbox and copy the list.

Events show tool names, counts and subjects. They never show email bodies, search
text or attachment contents. Event kinds are `Sync`, `Backfill`, `Purge`, `Tool`,
`Draft`, `Invite` (shown in bold), `Blocked` (a refused file path or invite) and
`Error`.

## Settings

Open it from the tray menu. Changes apply when you press **OK** or **Apply**;
**Cancel** discards them. If you launch the app a second time, it opens Settings in
the running copy.

### Connection

- **Port** (default 8765, range 1024 to 65535). Changing it asks for confirmation,
  because Claude Code must be set up again. If the new port can't be opened, the
  server stays on the old one.
- **Token**, hidden, with **Show**, **Copy** and **Regenerate**. Regenerating only
  takes effect when you press OK or Apply.
- The full `claude mcp add` command, with **Copy**.
- **Test connection**: sends a request without the token (expecting `401`) and with
  it (expecting an MCP answer) and shows a check mark or a reason it failed. It
  tests the running server, not unsaved changes.
- The `.wslconfig` snippet for [WSL](connect.md#from-wsl).

### Index

Pick **My mailbox** or a shared mailbox, then set:

- **Retention** in months (1 to 120, default 12). Shared mailboxes can use the
  global value or their own.
- **Indexed folders**: a tree of checkboxes. Default is Inbox and Sent Items.
- Live statistics: email count, oldest and newest item, database size, last sync.
- **Rebuild**.

Also here: add or remove shared mailboxes, and the global **sync interval**
(30 to 3600 seconds, default 300).

### AI drafts

Turns on and tunes [AI drafts](ai-drafts.md): the switch **Draft replies to new
email automatically**, **Use** (Claude Code or an Anthropic API key, with **Check**
and **Save key** / **Remove key**), Claude Code path, Model, Effort, Daily limit,
Minimum confidence, **Ignore mail older than** (days), **Only when I'm in To or CC**,
**Never draft for** and **Only draft for** (one address or domain per line), the
mailboxes, and the writing style (**View**, **Regenerate**, **Open in editor**,
**Update weekly**). A link at the bottom leads to the manual page that explains what
is sent to Claude.

### Scheduling

These are the defaults for [`find_free_times`](tools.md#find_free_times) and
[invites](calendar.md):

- **Work hours** (default 8 to 17) and **business days only** (default on).
- **Time-zone label** used in suggestions, such as `PT`. Empty means automatic.
- **Tentative counts as busy** (default on).
- **Meeting invites**: **Send** (with confirmation), **Draft only**, or **Disabled**.
- **Confirmation timeout** in seconds (30 to 900, default 120).

### General

- **Attachment folder** (default `%USERPROFILE%\Downloads\Clippy's Revenge`).
- **Allowed folders**: where Claude may read and write files. See
  [file access](security.md#file-access).
- **Start with Windows** (default on).
- **Notify on errors** (default on).
- **Log level** (default Information). Applies at once.
- **Updates**: **Install automatically** (default), **Only tell me** or **Don't check**. **Check now** opens the About window and checks for a new version at once. See [Updates](updates.md).

Settings are stored in `%APPDATA%\ClippysRevenge\settings.json`.

## Notifications

The app shows a short balloon for the first run, when an initial index finishes,
when Outlook has been unavailable for more than two minutes, for errors lasting
more than two minutes (if **Notify on errors** is on), and when an invite is sent
or saved as a draft after a timeout. After an AI drafting pass it says how many replies are in your Drafts folder; click it to open Drafts. Balloons are limited to one per minute.

Updates add four more balloons: a newer version is available, a newer version is downloaded and waits for the next start, Clippy was just updated, and an update didn't start so Clippy went back to the old version. See [Updates](updates.md).
