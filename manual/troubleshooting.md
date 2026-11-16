# Troubleshooting

## Quick checks

1. Look at the tray icon and hover over it. The tooltip says what is wrong.
2. Open **Settings, Connection** and press **Test connection**.
3. Ask Claude to run [`index_status`](tools.md#index_status). It reports whether
   Outlook is running and how far indexing is.
4. Open the logs: tray menu, **Open log folder**.

## Outlook is not running

The icon is grey with `!` and the status line says "Outlook is not running".
Start classic Outlook, or use the tray's **Start Outlook**. Indexing resumes by
itself. Searches against the existing index keep working meanwhile; reading a
message that isn't indexed yet, attachments, drafts and calendar tools need
Outlook.

## Outlook is open but Clippy says it isn't running

- Run both at the **same elevation**. Neither should run as administrator.
- It must be **classic Outlook**, not the new Outlook.
- After an Outlook restart, click into the Outlook window and then another window,
  or wait a minute. Clippy re-attaches by itself. If it doesn't, restart Outlook.

## Outlook is not responding

A dialog is open in Outlook, or it is busy. Clippy times out COM calls instead of
hanging. Close the dialog in Outlook and try again. The tray shows "Outlook is
not responding" and a notice appears if it lasts more than two minutes.

## 401 from curl or Claude

The token is wrong or was regenerated. Copy the command from the tray again and
re-add the server. Also check **Settings, Connection** for the port.

A `401` from `curl` with no token is expected, and tells you the server is up.

## Attachment download fails (`attachment_link`)

| What you see | Cause and fix |
|---|---|
| `curl` answers `401` | `CLIPPY_TOKEN` isn't set in that shell, or it is an old token. Press **Copy WSL token line** in **Settings, Connection**, put the line in `~/.bashrc` in WSL and run `source ~/.bashrc`. The link isn't used up by a `401`. See [Set CLIPPY_TOKEN](connect.md#set-clippy_token-for-downloads) |
| `curl` answers `404` | The link was already used, expired after 10 minutes, or Clippy restarted or changed port. Ask Claude for a new link |
| `curl` answers `405` | Something other than `GET` was sent. Use the exact `curl` command from the tool result |
| `curl: (7) Failed to connect` | Clippy isn't running, or WSL isn't in mirrored networking. See [Claude can't connect from WSL](#claude-cant-connect-from-wsl) |
| `Too many open download links (10)...` | Download or wait for open links to expire (10 minutes), then ask again |
| `The open download links already hold 200 MB...` | Same: download the open links or wait for them to expire |
| `Too many download links in the last 10 minutes (20). Try again in <n> min.` | Wait that many minutes |
| `The attachment is <n> MB; attachment_link handles up to 100 MB...` | Use `get_attachment` to save it on the Windows side |
| `Clippy can't use its private download folder. Use get_attachment instead.` | `%LOCALAPPDATA%\ClippysRevenge\links\` is a junction or symlink, or its permissions can't be set. Remove the folder (when Clippy is closed) and restart Clippy |
| Outlook errors | Same as for other attachment tools: start Outlook, close dialogs |

A failed or interrupted download shows as `attachment download failed` in
[Activity](activity.md#tool-calls). More: [`attachment_link`](tools.md#attachment_link).

## Claude can't connect from WSL

- Check `%USERPROFILE%\.wslconfig` has `networkingMode=mirrored` under `[wsl2]`.
- Run `wsl --shutdown` in Windows and open WSL again.
- From WSL, `curl -i -X POST http://127.0.0.1:8765/mcp` should answer `401`.

See [Connect Claude Code](connect.md#from-wsl).

## Test connection messages

| Message | What to do |
|---|---|
| The server isn't running | Check the tray icon for the reason. Restart the app if needed |
| Nothing answers on 127.0.0.1:port | Wrong port, or the app is not running |
| Didn't answer within 5 s | The app is busy or blocked. Check the logs |
| Answered without a token | Another program uses that port. Pick a different port |
| Rejected the token (401) | Regenerate and re-add the server |
| Another MCP server is answering | Another program uses that port. Pick a different port |

## The port is already in use

Settings shows "Port N is in use or can't be opened. The server stays on port M."
Choose a different port between 1024 and 65535, then run the new `claude mcp add`
command.

## Search finds nothing

- The first backfill may still be running. Ask Claude for `index_status`.
- The mail may be outside your retention window (12 months by default) or in a
  folder that isn't indexed. Check Settings, Index.
- A search with `folder="inbox"` does not include subfolders.
- If results look wrong, use **Rebuild index…** from the tray.

## Rebuild the index

Tray menu, **Rebuild index…**, then choose **My mailbox** or **All mailboxes**.
Clippy deletes its index and reads your mail again, newest first. Your mail is not
touched.

## An invite wasn't sent

Clippy never sends invites. It saves the meeting unsent in your calendar. Open it from
**Draft invites** in the tray (or from your calendar), add the Zoom or Teams link and
send it in Outlook.

- Check the mode in Settings, Scheduling. **Draft only** saves the meeting,
  and **Disabled** refuses.
- If a recipient didn't resolve in Outlook, nothing was created.

## An update failed or was rolled back

Open **About Clippy's Revenge…** from the tray menu. Its status line gives the
reason, for example `✗ The last update attempt failed: GitHub didn't answer
within 15 s`. See [Updates](updates.md) for how updating works.

- **Couldn't check or download.** Usually the network, a proxy or GitHub's rate
  limit. Press **Check for updates** later. Clippy retries by itself.
- **The checksum or size doesn't match.** The download is deleted and nothing
  changes. Try again later. If it keeps happening, install with `install.ps1`.
- **The previous version is still in use.** Restart Windows and try again.
- **"v... didn't start on this PC, so Clippy's Revenge went back to v...".** The
  new version didn't start, so Clippy put the old one back. It won't install that
  version again by itself. Look in the log folder for the reason and wait for a
  newer release, or press **Update now** to try again.
- **Clippy shows "Update Clippy's Revenge or rebuild the index" after a
  rollback.** The failed version had already upgraded the index. Choose
  **Rebuild index…** in the tray.
- **Clippy doesn't start and `ClippysRevenge.exe` is missing.** Run the install
  line again, which puts a good copy back:

  ```powershell
  irm https://github.com/dirkpetersen/clippys-revenge/releases/latest/download/install.ps1 | iex
  ```

  Your settings, token and index are kept.
- **Updates keep rolling back and another program permanently uses Clippy's
  port.** A new version confirms itself by starting its server on the port. If
  that port is taken for good, the update can't confirm and rolls back. Choose a
  different port in **Settings, Connection**, then run the new `claude mcp add`
  command.

## AI drafts

See [AI drafts](ai-drafts.md) for how it works. Open the [Activity window](activity.md)
first: it says why a mail got no draft.

### No drafts appear

- Check that **Draft replies automatically** is ticked in the tray's **AI drafts**
  submenu, and that Settings, AI drafts shows a ready status after **Check**.
- If Activity shows `AI drafts: Claude isn't signed in`, sign in to Claude Code in a
  terminal (run `claude`), then press **Check** in Settings. Clippy never signs in for
  you.
- `AI drafts: daily limit of <n> reached` means the **Daily limit** is used up. It
  continues tomorrow, or raise the limit in Settings. Rescans and re-checks of old
  drafts count too.
- `AI drafts: Claude's usage limit was reached` is your Claude plan's limit. Clippy
  tries again at the time shown.
- `no draft for the email received …: Claude's confidence … is below your minimum`
  means Claude wasn't sure a reply was needed. Select the email in Outlook and use
  **Draft a reply to the selected email**, or lower **Minimum confidence**.
- Mail that arrived before you turned AI drafts on, mail older than **Ignore mail older
  than (days)**, mail where you are not in To or CC, newsletters and automatic mail get
  no draft. See [Rules](ai-drafts.md#rules).
- Drafting waits while Outlook isn't running or indexing is paused.

### Where did my draft go?

If Clippy's AI draft vanished from Drafts, look in **Deleted Items**. When a newer
message arrived in the thread and the draft was no longer right, Clippy re-checked it.
It either replaced it with a new draft or moved it to Deleted Items because no reply
was needed any more. The Activity window says which, for example `Outdated AI reply
draft moved to Deleted Items (no reply needed any more): <subject>`. You can drag the
draft back to Drafts. Clippy only does this for an AI draft that you haven't edited,
opened or sent, and never deletes anything for good. To stop it, turn off **Update or
remove my AI drafts when a thread moves on** in Settings, AI drafts. See
[Outdated drafts](ai-drafts.md#outdated-drafts).

### An old draft is still in Drafts after a new one appeared

Clippy keeps an old draft when you edited it or have it open, and says so in Activity:
`The old draft is still in Drafts because it was changed or is open.` Delete it
yourself when you no longer need it. If a move to Deleted Items fails, Activity says
`AI drafts: an outdated draft couldn't be moved to Deleted Items. It's still in Drafts.`

### Rescan emails from the last 24 hours is greyed out

It needs Outlook running and the email index available. While a rescan runs, the item
reads **Cancel rescan (d/t)** and **Draft a reply to the selected email** is greyed out.
If a click only shows a balloon, read it: `There are no emails from the last 24 hours
to rescan.`, `Today's AI draft limit (<n>) is used up.` or `Claude isn't ready: …`. A
rescan stops early for the reasons in [Activity](activity.md#rescan).

### The draft has a placeholder where my Zoom link should be

Check **Facts about you** in Settings, AI drafts. `not found` means the file
`%USERPROFILE%\.claude\skills\my-email-context\SKILL.md` is missing, larger than
16 KiB or not valid UTF-8: use **Edit my facts…** in the tray. `template only` means you
haven't written anything outside the `<!-- … -->` comments yet. Activity shows
`Facts about you: my-email-context SKILL.md not found or unreadable — drafts go without it.`
when the file goes away. See [Facts about you](ai-drafts.md#facts-about-you).

### There is no yellow `Clippy:` line, or a draft isn't updated any more

No line: **Start each draft with a highlighted note on why it was drafted** may be off
in Settings, AI drafts, or Claude gave no note for that email. A draft you removed the
line from (or changed in any way) counts as edited, so Clippy leaves it alone and never
replaces or moves it. That is intended. See [The reason line](ai-drafts.md#the-reason-line).

### The draft ignored my attachment, or has a yellow `Couldn't read …` note

- Check that **Read attachments and synced shared files for drafts** is ticked in
  Settings, AI drafts. If it is off, only file names are sent.
- Read the note. It names the reason: `not synced to this PC` (the link doesn't match a
  file in a library or folder your OneDrive syncs: sync it or add a shortcut),
  `Clippy only opens shared files from people in your organization`, `password-protected`,
  `larger than 25 MB`, `no text in it, maybe a scan`, `Clippy can't read .<ext> files`,
  `the file seems damaged`, `it took too long` and `too much attachment text for one
  draft`. See [Why couldn't Clippy read my shared file?](faq.md#why-couldnt-clippy-read-my-shared-file).
- Images, and links that aren't OneDrive or SharePoint links, are skipped with no note.
- Outlook must be running: with Outlook closed, files that need Outlook are skipped.
- The Activity suffix `(attachments: 3 read, 1 not read)` shows how many files were used.
  It has counts only. The log never names a file.
- `%LOCALAPPDATA%\ClippysRevenge\ai-files\` should be empty. If a file stays there, Clippy
  deletes it at the next start.

### The tray shows `tokens unknown` or no `Today:` line

The line appears only after you gave AI drafts consent. `tokens unknown` means Claude
reported no token numbers for today's calls. See [Token tally](ai-drafts.md#token-tally).

### Clippy never says it noted my edits, or the style has no "Edits I usually make" section

Clippy [learns from your edits](ai-drafts.md#learning-from-how-you-edit-drafts) only
when all of this holds:

- **Learn from how I edit Clippy's drafts** is on in Settings, **AI drafts**, and you
  gave consent.
- The draft was saved after you updated to version 0.1.1, for an email with an internet
  message id, and did not use attachment text (those are never kept).
- You sent it as a reply to that email, from your own mailbox, and it still carries
  Clippy's marker. A reply you wrote from scratch, or a draft you made with a tool,
  isn't matched.
- Clippy has synced your Sent Items since (it checks at most every 5 minutes, after a
  sync), and it is within 30 days.

The section appears after the next writing-style update (**Regenerate**, or the weekly
one) once **at least 3** edited drafts with text exist. Drafts you sent unchanged only
raise the "unchanged" count, so they don't count toward the 3. If you edited your style
skill yourself, an automatic update leaves it alone: use **Regenerate**. Clippy shows
`AI drafts: noted how you edited <n> sent drafts.` in Activity when it finds some.

### The writing style isn't there yet

Clippy builds your writing-style skill from your sent mail, and waits until your **Sent
Items** are indexed. Sent Items are indexed before the Inbox, so it normally appears
early in the first backfill: finishing Sent Items wakes the drafting worker, which
builds the skill if **Draft replies automatically** and **Update weekly** are on. Check progress with `index_status` or the Activity window
(`Sent: backfill complete`). Until then, drafts have no personal style. If Activity says
`writing style not updated: only <n> sent emails (need 20)`, there is too little sent
mail yet: Clippy tries again after 24 hours, or press **Regenerate…** in Settings when
you have more. An edited skill is never overwritten without asking.

## Logs

Logs are in `%LOCALAPPDATA%\ClippysRevenge\logs`, one file per day
(`clippy-YYYYMMDD.log`), kept for 7 days. To get more detail, set **Log level** to
Debug in Settings, General. It applies at once.

## Start over

Run the uninstaller with `-Purge` (see [Install](install.md#uninstall)) and
install again. This deletes your settings, token and index, so you will need to
add the server to Claude Code again.
