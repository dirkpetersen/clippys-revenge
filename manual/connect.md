# Connect Claude Code

Clippy's Revenge serves MCP over streamable HTTP at
`http://127.0.0.1:<port>/mcp`. The default port is `8765`. Every request needs a
bearer token that the app creates on first run.

## Add the server

1. Right-click the paperclip in the system tray.
2. Choose **Copy "claude mcp add" command**.
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

If you change the port or regenerate the token, run the new `claude mcp add`
command again. The app warns you before either change.

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
and it has no dependency on WSL interop: WSL talks to it over TCP.

## Check that it works

In Claude Code, try:

> Use the clippy index_status tool and tell me how many emails are indexed.

Right after install the index is still filling. Recent mail becomes searchable
first. See [Indexing and search](indexing.md).

## Rotate the token

Use the tray menu item **Regenerate auth token…**. The old token stops working at
once, so you must add the server to Claude Code again with the new command.
