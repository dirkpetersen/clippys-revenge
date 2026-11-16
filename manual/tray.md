# Tray menu

Clippy's Revenge has no main window. Everything lives in the paperclip icon in the
system tray (you may need to click the `^` to see hidden icons). This page covers the
icon, the right-click menu and the balloons. The other windows have their own pages:
[Settings](settings.md) and [Activity](activity.md).

## Icon colors and tooltip

The icon shows what the app is doing. Hover over it for a short description. When
several things happen at once, the highest row in this table wins.

| Icon | State | Tooltip |
|---|---|---|
| Red | Error | `Clippy — error: <short reason>` |
| Grey with `!` | Outlook is not running, or not responding | `Clippy — Outlook is not running` or `Clippy — Outlook is not responding` |
| Blue, animated | First backfill (indexing mail) | `Clippy — indexing Inbox 1,240 / 8,000 (2025-11 → now)` |
| Blue | Pulling new mail | `Clippy — pulling new mail (Inbox +12)` |
| Blue | Removing mail older than the retention window | `Clippy — removing mail older than 12 months` |
| Grey | Indexing is paused | `Clippy — indexing paused` |
| Blue | [AI drafts](ai-drafts.md) are being written | `Clippy — drafting replies (2 / 5)` |
| Green, brief pulse | Answering a tool call | `Clippy — search_emails (8 ms)` |
| Green | Idle | `Clippy — idle · 48,210 emails · synced 2 min ago` |

Tooltips are cut at 127 characters. Times read `just now`, `N min ago`, `N h ago` or
`N d ago`. While a shared mailbox is syncing, the text names it, for example
`indexing helpdesk@… Inbox 300 / 2,000`.

**Double-click** the icon to open the [Activity window](activity.md).

### Attention badge

When something needs you, a small **yellow badge** with a number appears in the
**top-right** corner of the icon, on top of whatever color the icon has. It shows 1 to
9, or `9+` for more. For now it counts your [draft invites](calendar.md#meeting-drafts):
meeting drafts that Claude saved and that you haven't sent yet.

The tooltip then ends with ` · 1 draft invite needs your attention` (or
` · 3 draft invites need your attention`). The badge goes away when the count reaches
zero. While Outlook isn't running, it keeps the last count, because the meetings still
wait for you.

## Tray menu

Right-click the icon. The menu, top to bottom:

```
Clippy — idle · 48,210 emails                  (status line, greyed out)
Last sync: 2 min ago · next in 3 min           (status line, greyed out)
────────────────────────────
Start Outlook                                  (only while Outlook isn't running)
Sync now
Pause indexing / Resume indexing
Rebuild index…
Shared mailboxes                            ▸
AI drafts                                   ▸
Draft invites (2)                           ▸  (only while meeting drafts wait)
────────────────────────────
Copy "claude mcp add" command
Copy auth token
Regenerate auth token…
────────────────────────────
Activity…
Settings…
Open log folder
Open data folder
────────────────────────────
Start with Windows                             (check mark)
About Clippy's Revenge…
Exit
```

### Status lines

- **First line.** The same text as the tooltip, but without "· synced …" while idle. For
  example `Clippy — idle · 48,210 emails`. While Outlook isn't running it reads
  `Clippy — Outlook is not running`.
- **Second line.** `Last sync: <time ago or never>`, followed by ` · paused` while
  paused, or ` · next in N min`.

Both lines are greyed out and can't be clicked.

### Items

| Item | What it does | When it is unavailable |
|---|---|---|
| **Start Outlook** | Starts Outlook (`OUTLOOK.EXE`). This is the only place Clippy ever starts it. If Outlook can't be found, a balloon says so | Hidden while Outlook is running. Also hidden while Outlook is open but not responding |
| **Sync now** | Starts a sync cycle at once | Greyed out while indexing is paused |
| **Pause indexing** / **Resume indexing** | Pauses or resumes syncing. The pause is not remembered after a restart | |
| **Rebuild index…** | Asks "Rebuild the email index?". It deletes the index and reads your mail from Outlook again. Buttons: **My mailbox**, **All mailboxes** (only when a shared mailbox is enabled) and **Cancel** (the default). Your mail is not touched | |
| **Shared mailboxes** | Submenu, see below | |
| **AI drafts** | Submenu, see below | |
| **Draft invites (n)** | Submenu of your unsent meeting drafts, see below | Hidden when there are none |
| **Copy "claude mcp add" command** | Puts the full setup command on the clipboard and shows the balloon `Copied — paste it in WSL` | |
| **Copy auth token** | Copies the token. No balloon | |
| **Regenerate auth token…** | Asks "Regenerate the auth token?" (Cancel is the default). The old token stops working at once, so Claude Code must be set up again, and `CLIPPY_TOKEN` in WSL must be updated ([how](connect.md#set-clippy_token-for-downloads)) | |
| **Activity…** | Opens the [Activity window](activity.md) | |
| **Settings…** | Opens [Settings](settings.md) on the Connection tab. If it is already open, it comes to the front | |
| **Open log folder** | Opens `%LOCALAPPDATA%\ClippysRevenge\logs` | |
| **Open data folder** | Opens the data folder | |
| **Start with Windows** | Check mark. Starts Clippy at logon. The same setting as in [Settings](settings.md#general) | |
| **About Clippy's Revenge…** | Opens the About window: version, update status, **Update now** and links to the documentation and release notes. See [Updates](updates.md#the-about-window) | |
| **Exit** | Hides the icon and quits the app | |

If you start the app a second time, it opens Settings in the copy that is already
running.

### Shared mailboxes

Lists the Exchange mailboxes added to your Outlook profile, except your own, with a
check mark on the ones Clippy indexes. See [shared mailboxes](indexing.md#shared-mailboxes).

- **Check** a mailbox to start indexing it (its backfill starts at once).
- **Uncheck** a mailbox and Clippy asks `Stop indexing <name>?` with **Delete its
  index**, **Keep index file** and **Cancel**. The mailbox is switched off either way.
- A mailbox you configured but that Outlook no longer lists shows `(not in Outlook)`.
- While Outlook isn't running, only configured mailboxes are listed, plus a greyed-out
  line `Start Outlook to see more mailboxes`.
- **Manage in Settings…** opens the Index tab.

### AI drafts

The submenu for [AI drafts](ai-drafts.md):

| Item | What it does | When it is unavailable |
|---|---|---|
| `Today: 18 AI calls, ~210k tokens (in 190k / out 20k)` | The first line, greyed out. Today's [token tally](ai-drafts.md#token-tally): Claude calls since midnight and the tokens they used. It reads `Today: no AI calls` when there were none, and `tokens unknown` when Claude gave no numbers. Refreshed each time you open the menu | Hidden until you have given AI drafts consent |
| **Draft replies automatically** | Check mark. Turns automatic drafting on or off. The first time, Clippy shows the [consent dialog](ai-drafts.md#consent-and-what-is-sent). If Claude isn't ready (not signed in, no API key), it opens Settings on the AI drafts tab instead of switching on | Greyed out while the email index isn't available, or while a toggle is in progress |
| **Draft a reply to the selected email** | Drafts a reply to the one email you selected in Outlook, now, even when automatic drafting is off. Asks for consent first if you haven't given it. A balloon reports the result | Greyed out while Outlook isn't running, while the index isn't available and while a rescan runs |
| **Don't draft for this sender** | Adds the sender of the email selected in Outlook to **Never draft for** in Settings, with no dialog. Needs no consent. A balloon says `I won't draft replies to <address> any more. You can undo this in Settings → AI drafts.` Refuses your own address and an address that is already covered. See [Don't draft for this sender](ai-drafts.md#dont-draft-for-this-sender) | Greyed out while Outlook isn't running, while the index isn't available and while a rescan runs |
| **Don't draft for this sender's domain…** | The same for the sender's whole domain (and its subdomains), after asking `Never draft replies to anyone at <domain>?` (**Add** or **Cancel**, the default). Refuses your own organization's domain | Same as above |
| **Rescan emails from the last 24 hours…** | Goes through the last day of mail, oldest first, drafts replies where one is needed and re-checks Clippy's own drafts in those threads. Works with automatic drafting off. See below | Greyed out while Outlook isn't running, while the index isn't available and while the rescan is starting |
| **Show Drafts in Outlook** | Opens your Drafts folder in Outlook | Hidden while Outlook isn't running |
| **AI drafts settings…** | Opens Settings on the AI drafts tab | |
| **Edit my facts…** | Opens your [facts file](ai-drafts.md#facts-about-you) (`%USERPROFILE%\.claude\skills\my-email-context\SKILL.md`) in Notepad. If the file is missing, Clippy creates the template first, even without AI drafts consent, because nothing is sent. If Notepad can't start, a balloon says `Couldn't open the file: <reason>` | Greyed out while the AI drafts services aren't available |

**Rescan emails from the last 24 hours…** works like this:

1. It asks for consent if you haven't given it, and checks that Claude is ready. If not,
   a balloon says `Claude isn't ready: <status>`.
2. It counts the emails it would look at. With none, a balloon says `There are no emails
   from the last 24 hours to rescan.` If today's limit is already used, a balloon says
   `Today's AI draft limit (<cap>) is used up.`
3. It shows a confirmation, **Rescan the last 24 hours?**, with the number of emails,
   the number of Claude calls (one per email at most) and how many of today's calls are
   left. Choose **Rescan** or **Cancel** (the default).
4. While it runs, the item reads **Cancel rescan (d/t)**, for example `Cancel rescan
   (3/12)`, and the tray tooltip reads `Clippy — Rescanning the last 24 hours (3 / 12)`. Click it to stop. The
   rescan stops before the next email. An email whose answer has already arrived is
   finished first, so nothing is left half done.
5. When it ends, a balloon shows the summary line (see [Activity](activity.md#rescan)).
   Click the balloon to open Drafts if anything was drafted or updated.

A rescan stops by itself at the daily limit, when Claude's usage limit is reached, when
Claude isn't signed in, when Outlook isn't running, or after 3 failed calls in a row.
Nothing is ever sent. Details: [Rescan emails from the last 24
hours](ai-drafts.md#rescan-emails-from-the-last-24-hours).

### Draft invites

Shown only while [meeting drafts](calendar.md#meeting-drafts) wait for you. The header
carries the count, for example **Draft invites (2)**. Below it, one line per meeting,
sorted by start time:

```
Tue 10/15 10:00–11:00  Project sync
Wed 10/16 14:00–14:30  Budget review
```

- Subjects are cut at 40 characters. A meeting in a shared mailbox's calendar shows the
  mailbox name in parentheses.
- Only the first 15 meetings are listed. A last greyed-out line says `and <n> more`.
- **Click a line** to open that meeting in Outlook. Add the Zoom or Teams link and send
  it there. Clippy then removes its category.
- While Outlook isn't running, the lines are greyed out and the last line says `Start
  Outlook to open them`.
- If the meeting is gone from your calendar, a balloon says `That draft invite is no
  longer in your calendar.` If Outlook can't open it, the balloon says `It looks like
  Outlook couldn't open that draft invite. Open it from your calendar.`

## Balloons

Short balloon notifications appear above the tray for about 5 seconds. They are written
in Clippy's voice and titled "Clippy's Revenge".

| When | Text |
|---|---|
| First run | `Clippy's Revenge is running — right-click to copy the Claude setup command` |
| Outlook not running for more than 2 minutes | `It looks like Outlook isn't running. I'll wait — or right-click me and pick Start Outlook.` |
| Outlook not responding for more than 2 minutes | `It looks like Outlook isn't responding. I'll keep trying — check Outlook for an open dialog box.` |
| An error lasts more than 2 minutes (if **Notify on errors** is on) | `It looks like something went wrong: <reason>. Right-click me and pick Activity… for details.` |
| A first backfill finishes (once per mailbox) | `It looks like you're trying to find an email. I finished indexing <your mailbox or address>: <n> emails.` |
| Automatic drafting saved replies | `It looks like you got mail. I drafted <n> reply — they're in your Drafts folder.` (`replies` for more than one). Click it to open Drafts |
| An update is available, is downloaded, was installed or was rolled back | See [Updates](updates.md) |

Balloons that report on their own are limited to **one per minute**. A balloon that hits
the limit is dropped, not queued. The 2-minute balloons fire once per episode.

Balloons that answer one of your clicks are not limited:

| Click | Balloon |
|---|---|
| Copy "claude mcp add" command | `Copied — paste it in WSL` |
| Start Outlook, when Outlook can't be found | `It looks like I can't find Outlook on this PC. Start it from the Start menu.` |
| Draft a reply to the selected email | `It looks like you want a reply. It's in your Drafts folder.`, or why not: `Select one email in Outlook first.`, `That email isn't in your mailbox or an enabled shared mailbox.`, `That mailbox isn't indexed yet.`, `Claude isn't ready: <status>`, `Today's AI draft limit (<cap>) is used up.`, `I couldn't draft that reply: <reason>` |
| Rescan emails from the last 24 hours | The messages in the section above, then the summary |
| Don't draft for this sender / this sender's domain | `I won't draft replies to <address> any more. You can undo this in Settings → AI drafts.` (`…to anyone at <domain>…` for the domain). Or why not: `Select one email in Outlook first.`, `That email isn't in your mailbox or an enabled shared mailbox.`, `I couldn't find that sender's email address.`, `That's your own address.`, `That's your own organization's domain. To stop drafting for all internal mail, add it in Settings → AI drafts.`, `<address or domain> is already on "Never draft for".`, `"Never draft for" already has 200 entries. Remove some in Settings → AI drafts.` |
| Show Drafts in Outlook | `It looks like Outlook has no open window. Open Outlook and look in Drafts.` when Outlook has no window to show it in |
| A draft invite that is gone | `That draft invite is no longer in your calendar.` |
| Draft replies automatically fails to switch | `Couldn't change AI drafts: <reason>` |

Event lines in the Activity window are listed on the [Activity page](activity.md).
