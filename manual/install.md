# Install

## Requirements

- Windows 11 with **classic Outlook** (not the new Outlook), signed in.
- An Exchange or Microsoft 365 mailbox. IMAP/POP-only profiles are not supported.
- No administrator rights are needed.

## Install or update

Paste this into PowerShell:

```powershell
irm https://github.com/dirkpetersen/clippys-revenge/releases/latest/download/install.ps1 | iex
```

The script downloads the latest release, checks `ClippysRevenge.exe` against the
release's `SHA256SUMS.txt`, installs it and starts it. Run the same line again to
update.

It installs to `%LOCALAPPDATA%\Programs\ClippysRevenge\`, adds a Start menu
shortcut called "Clippy's Revenge", and starts the app. A paperclip appears in
the system tray. The app is set to start with Windows (you can turn that off in
the tray menu or in [Settings](tray.md)).

!!! note "SmartScreen"
    The exe is not code-signed yet, so Windows SmartScreen warns the first time
    it runs. Choose **More info**, then **Run anyway**. The installer verifies the
    SHA-256 checksum of the download before it installs anything.

## Preview first with -WhatIf

`-WhatIf` downloads and verifies the release but changes nothing on your PC:

```powershell
& ([scriptblock]::Create((irm https://github.com/dirkpetersen/clippys-revenge/releases/latest/download/install.ps1))) -WhatIf
```

## Install from a file

Download `install.ps1` from the
[latest release](https://github.com/dirkpetersen/clippys-revenge/releases/latest)
and run it:

```powershell
.\install.ps1                                # same as the one-liner
.\install.ps1 -Source .\ClippysRevenge.exe   # install a local build instead
.\install.ps1 -NoStart                       # install but don't launch
```

You can also download `ClippysRevenge.exe` from the release and run it directly.

| Option | Meaning |
|---|---|
| `-Source <path>` | Install a local `ClippysRevenge.exe` instead of downloading |
| `-NoStart` | Don't start the app after installing |
| `-WhatIf` | Show what would happen; on the download path, download and verify only |
| `-Uninstall` | Remove the app (see below) |
| `-Purge` | With `-Uninstall`: also delete settings, token, index and logs, without asking |

## Uninstall

```powershell
& ([scriptblock]::Create((irm https://github.com/dirkpetersen/clippys-revenge/releases/latest/download/install.ps1))) -Uninstall
```

This stops the app and removes the exe, the Start menu shortcut and the "Start
with Windows" entry. It then asks whether to delete your settings, token, index
and logs. Add `-Purge` to delete them without asking. If you answer no, they stay
on disk.

## What gets stored where

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\ClippysRevenge\ClippysRevenge.exe` |
| Settings and token | `%APPDATA%\ClippysRevenge\` |
| Index and logs | `%LOCALAPPDATA%\ClippysRevenge\` |
| Saved attachments | `%USERPROFILE%\Downloads\Clippy's Revenge` |

## Next step

[Connect Claude Code](connect.md).
