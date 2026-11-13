# Updates

Clippy's Revenge can keep itself up to date. Once a week it asks GitHub whether a
newer release exists and, by default, installs it the next time it starts. You
choose how much it does by itself.

## The three modes

Set the mode in the tray's **Settings…**, **General**, **Updates**. It applies
at once.

| Mode | What Clippy does |
|---|---|
| **Install automatically** (default) | Checks weekly, downloads and verifies a newer release, and installs it at the next start, without asking |
| **Only tell me** | Checks weekly and shows a balloon when a newer release exists. It downloads and installs nothing unless you press **Update now** in the About window |
| **Don't check** | Never checks by itself. You can still check by hand in the [About window](#the-about-window), and update with `install.ps1` |

!!! tip "Cautious? Pick Only tell me"
    The default installs new versions without asking. If you want to look at a
    release before it runs on your PC, choose **Only tell me**, read the
    [release notes](https://github.com/dirkpetersen/clippys-revenge/releases), and
    press **Update now** when you are ready. See the
    [trust model](#trust-model) for why this can matter.

## How and when it updates

- Only the **installed** copy updates itself: the one in
  `%LOCALAPPDATA%\Programs\ClippysRevenge\`, put there by
  [`install.ps1`](install.md). A copy you run from Downloads or a build folder
  never checks, downloads or installs anything by itself.
- The first check runs about a minute after Clippy starts, in the background, so
  start-up never waits for the network. A check is due when the last successful
  one is at least 7 days old. While Clippy runs, it looks again every 6 hours. A
  failed attempt is not repeated for 24 hours.
- Only **full** releases count. A pre-release is ignored. A release is installed
  only when its version is strictly newer than the one you run, so Clippy never
  downgrades.
- With **Install automatically**, Clippy downloads and verifies the new
  `ClippysRevenge.exe` in the background. The install itself happens **at
  start-up**: right at the next start, or at once when the check runs during the
  first 30 minutes after a start and Clippy is idle. A check later in a long
  session only downloads and verifies. Clippy then shows a balloon that the update
  installs the next time it starts. Press **Update now** in the About window to
  do it sooner.
- The swap is quick: Clippy renames the running exe to `ClippysRevenge.exe.old`,
  puts the new exe in place, shuts down, starts the new one and exits. The new
  instance shows a balloon, "It looks like I got an upgrade. I'm now v...", and
  deletes the `.old` file. Clippy starts nothing else during an update. It never
  runs `install.ps1`, a shell or any script.
- Sync and the first backfill may be running when the swap happens. They stop with
  the app and resume after the restart. Your settings, token and index are not
  touched by an update.

## What is sent to GitHub

Nothing about you. Clippy sends plain, unauthenticated `GET` requests, with no
cookies and no sign-in, to three kinds of address:

1. **Release lookup**: `https://api.github.com/repos/dirkpetersen/clippys-revenge/releases/latest`
2. **Downloads**: `ClippysRevenge.exe` and `SHA256SUMS.txt` from the release on
   `https://github.com/dirkpetersen/clippys-revenge/releases/download/<tag>/`.
   GitHub redirects these to its own download servers. Clippy follows at most 5
   redirects and only to `github.com`, `release-assets.githubusercontent.com` or
   `objects.githubusercontent.com`. Anything else fails the download.
3. Nothing else. No analytics, no telemetry, no crash reports.

Each request carries a few standard headers: `User-Agent: ClippysRevenge/<your
version>`, an `Accept` header, and for the lookup GitHub's API version header. That
is all. Your mailbox, the index, your settings and your PC's name are never sent.
GitHub sees your IP address, as any website does. Windows proxy settings apply,
and the HTTPS certificate checks are never relaxed.

The lookup times out after 15 seconds, and the download has a size limit and a
time limit. GitHub's unauthenticated API allows 60 requests per hour per IP
address, which a weekly check never gets near, but a busy office network might.

## The About window

Right-click the tray icon and choose **About Clippy's Revenge…**. Settings,
General, **Check now** opens the same window and starts a check. It shows:

- the version, with the commit when known, and the .NET and Windows versions
- where Clippy runs from: `Installed in <folder>`, or `Running from <folder>. Only
  the installed copy updates itself.`
- the mode: `Updates: install automatically`, `Updates: only tell me` or
  `Updates: don't check`
- `Last checked: never`, or the date and time with how long ago it was
- a status line: `Checking for updates…`, `Downloading v... 42 %`, `Verifying the
  download…`, `You have the latest version.`, `v... is available (released
  ...).`, or `v... is downloaded. It installs the next time Clippy's Revenge
  starts, or press Update now.` If something failed, it says why, after a
  `✗`, for example `✗ Couldn't check for updates: GitHub didn't answer within
  15 s`

Buttons and links:

- **Check for updates** looks up the latest release at once, in any mode. It
  downloads nothing.
- **Update to v...** (or **Update now**) downloads, verifies and installs the
  newest release, then restarts Clippy by itself. It waits up to 2 minutes for
  Clippy to finish what it is doing. It works in every mode, but only for the
  installed copy. If Clippy is busy, it says so, and you can try again in a
  minute.
- **Documentation** opens this manual. **Release notes** opens the page of the
  latest release on GitHub.

A copy that isn't installed can still check by hand. It then points you to the
release page instead of updating itself.

## If an update goes wrong

Clippy verifies a new version before it installs it, and it can undo an install.

- **Checks before installing.** The download must be exactly the size the release
  lists, its SHA-256 must match the one in the release's `SHA256SUMS.txt` (and the
  one GitHub lists for the file), it must be a Windows program, and its version
  must match the release. If any check fails, the file is deleted and nothing
  changes.
- **Rollback.** After the swap, the old process waits up to 60 seconds for the
  new one to confirm that it started. If the new exe crashes, exits or never
  confirms, the old exe is put back and started again. You see the balloon "It
  looks like v... didn't start, so I went back to v.... I won't install v... again."
  Clippy skips that version until a newer one is released. **Update now** in the
  About window can still try it.
- **Index too new.** If the failed version had already upgraded the index database,
  the older version can't open it and shows an error. Use **Rebuild index…** in the
  tray.
- **The exe is missing.** If something went wrong badly enough that both exes are
  gone, run `install.ps1` again. See [Troubleshooting](troubleshooting.md#an-update-failed-or-was-rolled-back).

The Activity window lists every update event (`update check: v... available`,
`update v... downloaded and verified`, `updated from v... to v...`,
`update failed: ...`), and the log has the details.

## Update by hand

Run the install line again. It always installs the latest release, whatever the
mode, and works even when Clippy can't update itself:

```powershell
irm https://github.com/dirkpetersen/clippys-revenge/releases/latest/download/install.ps1 | iex
```

See [Install or update](install.md#install-or-update). To go back to an older
version, download that release's `ClippysRevenge.exe` and run
`.\install.ps1 -Source .\ClippysRevenge.exe`. Clippy never downgrades by itself.

## Trust model

An update is trusted because of two things: it comes over HTTPS from the
project's GitHub release, and its SHA-256 matches the `SHA256SUMS.txt` published in
that same release. That proves the file is the one published in the release and
arrived intact.

It does **not** prove who published it. The checksum comes from the same place as the
file, so it can't tell you that the publisher is who you think it is. Anyone who
could publish a release in the project's GitHub repository (a stolen account or
access token, a compromised build workflow) could publish an exe with a matching
checksum, and installed copies in **Install automatically** mode would install it
within a week without asking.

The exe is also **not code-signed** yet (see [Security](security.md#unsigned-executable)).
Windows SmartScreen shows its warning for a file you download in a browser, but
not for a file Clippy downloads and installs itself, so there is no prompt at
update time.

If that risk is too high for you, choose **Only tell me** or **Don't check**, and
review each release before you install it with `install.ps1`.

## Turn updates off

Settings, General, **Updates**: pick **Don't check**. Clippy then makes no network
connection for updates at all, until you press **Check for updates** in the About
window.
