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

- Check the mode in Settings, Scheduling. **Draft only** saves the meeting
  without sending, and **Disabled** refuses.
- If the confirmation dialog timed out, the meeting was saved as a draft in
  your calendar. Open it in Outlook and send it.
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

## Logs

Logs are in `%LOCALAPPDATA%\ClippysRevenge\logs`, one file per day
(`clippy-YYYYMMDD.log`), kept for 7 days. To get more detail, set **Log level** to
Debug in Settings, General. It applies at once.

## Start over

Run the uninstaller with `-Purge` (see [Install](install.md#uninstall)) and
install again. This deletes your settings, token and index, so you will need to
add the server to Claude Code again.
