# Connect Claude Code

Clippy's Revenge serves MCP over streamable HTTP at
`http://127.0.0.1:<port>/mcp`. The default port is `8765`. Every request needs a
bearer token that the app creates on first run. The server only listens on your own
PC (`127.0.0.1`); see [Security](security.md#network).

## Add the server

1. Right-click the paperclip in the system tray.
2. Choose **Copy "claude mcp add" command**. A balloon says "Copied — paste it in
   WSL".
3. Paste it into a terminal where Claude Code runs (Windows or WSL).

The command looks like this:

```text
claude mcp add --scope user --transport http clippy http://127.0.0.1:8765/mcp --header "Authorization: Bearer <token>"
```

Then start Claude Code and run `/mcp` to check that `clippy` is connected.

!!! tip
    **Settings, Connection** has the same command, a **Test connection** button
    and the token. The command box hides the token until you press **Show**;
    **Copy** always copies the full command.

The command contains your token, so treat it like a password. Don't paste it into
chats, tickets or shared notes.

## The port

The default is `8765`. You can change it in **Settings, Connection** to any number
from 1024 to 65535. Clippy asks before it applies the change, because Claude Code
must then be set up again with the new command. If the new port can't be opened
(another program uses it), the server stays on the old port, nothing is saved, and
the Port field says so.

## The token

The token is a random 32-byte value in hex. It is stored encrypted for your Windows
user and never written to `settings.json` or to the log.

- **Copy auth token** in the tray menu copies it.
- **Regenerate auth token…** in the tray menu (or **Regenerate** in Settings,
  Connection) creates a new one. The old token stops working at once, so run the new
  `claude mcp add` command again. Clippy asks you to confirm first, and Cancel is
  the default. In the Settings window the new token is applied when you press OK
  or Apply.

If you add the server again under the same name, remove the old entry first with
`claude mcp remove clippy`.

## From WSL

In WSL2's default NAT mode, WSL cannot reach the Windows host's `127.0.0.1`. Use
**mirrored networking** (Windows 11 22H2 or later). In
`%USERPROFILE%\.wslconfig` on Windows:

```ini
[wsl2]
networkingMode=mirrored
```

Then run this in Windows and reopen your WSL shell:

```powershell
wsl --shutdown
```

Check from WSL:

```bash
curl -i -X POST http://127.0.0.1:8765/mcp     # expect: 401 Unauthorized
```

A `401` means the server is reachable and wants a token. Then paste the
`claude mcp add` command in WSL.

The app listens on `127.0.0.1` only. It does not bind to the WSL virtual adapter,
and it has no dependency on WSL interop: WSL talks to it over TCP. The same
snippet is shown in **Settings, Connection**.

!!! note "Files go to Windows folders"
    Attachments saved with `get_attachment` or `save_attachments` land in a Windows
    folder (by default `%USERPROFILE%\Downloads\Clippy's Revenge`). From WSL you
    reach it as `/mnt/c/Users/<you>/Downloads/Clippy's Revenge`. To get an
    attachment straight into the folder where Claude Code works in WSL, use
    [`attachment_link`](tools.md#attachment_link) and set `CLIPPY_TOKEN` as below.

### Set CLIPPY_TOKEN for downloads

The `claude mcp add` command stores your token in Claude Code's own config, but the
shell where Claude Code runs commands doesn't have it. An
[attachment download link](tools.md#attachment_link) is fetched by that shell with
`curl`, and it needs your token as well as the link. So you put the token in the
shell's environment once:

1. Open **Settings, Connection**. Next to **From WSL** there is a line that reads
   `export CLIPPY_TOKEN=••••••••`. The token is masked until you press **Show**
   next to the auth token.
2. Press **Copy WSL token line**. This copies the full line,
   `export CLIPPY_TOKEN=<your token>`, to the clipboard, even while the line on
   screen is masked.
3. In WSL, add the line to the end of `~/.bashrc`, then reload it:

    ```bash
    echo 'export CLIPPY_TOKEN=<paste the token here>' >> ~/.bashrc
    source ~/.bashrc
    ```

    Or open `~/.bashrc` in an editor and paste the copied line.

Check it from WSL. This should print the length of your token (64 characters) and
not `0`:

```bash
echo -n "$CLIPPY_TOKEN" | wc -c
```

To test the endpoint itself, ask Claude Code for an attachment (see
[`attachment_link`](tools.md#attachment_link)), or try an unknown link, which must
answer `404` once the token is accepted and `401` when it isn't:

```bash
curl -i -H "Authorization: Bearer $CLIPPY_TOKEN" \
  http://127.0.0.1:8765/files/0000000000000000000000000000000000000000000
# 404 = token accepted, no such link.  401 = token missing or wrong.
```

Use your own port instead of `8765` if you changed it.

After **Regenerate auth token**, update the export as well, exactly like the
`claude mcp add` command: copy the line again, replace the old one in `~/.bashrc`
and run `source ~/.bashrc` (or open a new shell). Changing the port needs no change
to `CLIPPY_TOKEN`; the link Claude gets always names the current port.

!!! warning "Treat the line like a password"
    `~/.bashrc` now holds your token. It is the same secret that is already in
    Claude Code's config, and it only works on your own PC (`127.0.0.1`). Don't
    paste the line into chats or tickets.

## Check that it works

In Claude Code, try:

> Use the clippy index_status tool and tell me how many emails are indexed.

Right after install the index is still filling. Recent mail becomes searchable
first, and Sent Items are indexed before the Inbox. See
[Indexing and search](indexing.md).

All the tools Claude can then use are in the [Tools reference](tools.md).

## Test connection

**Settings, Connection, Test connection** sends two requests to the running server:
one without the token (it must answer `401`) and one with it (it must answer as the
`clippy` MCP server). It tests the running server, not changes you haven't applied
yet. The result is one of:

| Message | What to do |
|---|---|
| `✓ Connection works: 127.0.0.1:<port> needs the token and answers MCP.` | Nothing. |
| `✗ The server isn't running. Check the tray icon for the reason.` | Look at the tray icon and the [Activity](activity.md) window. A port that is in use shows as an error. |
| `✗ Nothing answers on 127.0.0.1:<port>.` | The server isn't listening there. Check the port number. |
| `✗ The server on 127.0.0.1:<port> didn't answer within 5 s.` | Wait a moment and try again. |
| `✗ The server answered without a token (HTTP <code>). Another program may be using port <port>.` | Change the port. |
| `✗ The server rejected the token (HTTP 401).` | Restart Clippy. If it persists, regenerate the token. |
| `✗ Another MCP server (<name>) is answering on port <port>.` | Another program holds the port. Change the port. |

## Troubleshooting

- **`/mcp` shows `clippy` as failed, or Claude gets `401`.** The token in Claude
  Code is old (you regenerated it) or was cut when pasting. Run **Copy "claude mcp
  add" command** again and add the server again.
- **Connection refused.** Clippy isn't running (check the tray), or the port
  differs from the one in the command.
- **From WSL nothing connects.** WSL is probably in NAT mode. Use mirrored
  networking as above and run the `curl` check.
- **Tools work but searches find nothing.** The index is still filling. Ask Claude
  to call `index_status`.
- **A browser can't call it.** On purpose: any request that carries an `Origin`
  header is refused, so web pages can't reach the server.

More help is in [Troubleshooting](troubleshooting.md).
