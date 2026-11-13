# Clippy's Revenge — implementation spec

This file is the source of truth. Every component listed here must be
built with the stated behavior. Anything not listed is out of scope until
it is added here first. The Python prototype in
[`outlook-mcp`](https://github.com/dirkpetersen/outlook-mcp) is reference
material only. Where it disagrees with this file, this file wins.

**Name.** The product is called "Clippy's Revenge". The tray, exe,
namespace and data folders use `ClippysRevenge`. The MCP server name is
`clippy` (`claude mcp add … clippy …`). Clippy is a Microsoft character,
so the tray icons are an original paperclip drawing, not Microsoft's
artwork.

## 1. Goals and hard rules

1. A Windows tray application that drives **classic Outlook through COM**.
   It does not use the Microsoft Graph API.
2. It serves MCP over **streamable HTTP on `127.0.0.1` only**. Every request
   needs a bearer token. This lets Claude Code inside WSL2 reach it without
   WSL interop (§13).
3. It starts at Windows logon and lives in the **system tray**. The tray icon
   shows live activity, and the tray menu is where everything is configured
   (§6).
4. It **never sends email**. New mail and replies are only ever saved as
   drafts. The only thing that can send is `send_meeting_invite` (§11.9),
   and in `Send` mode every invite needs the user's click in a **tray
   confirmation dialog** (§6.6). Invites can also be set to draft-only or
   disabled.
5. Reply drafts always have a subject that starts with **`Re:: `**.
6. Search runs against local **SQLite + FTS5** indexes: one database for
   the user's own mailbox, plus **one database per shared mailbox** the user
   opts into (§9.3). The index covers the last **12 months** by default.
   Attachment **content is never stored**, only file names.
7. It **never launches Outlook by itself**. It only attaches to an Outlook
   that is already running. When Outlook isn't running, the tray says so and
   offers a **Start Outlook** item (§4, §6).
8. Tools only read and write files under the **allowed folders** (§12,
   `PathPolicy`). This blocks prompt-injected writes to places like the
   Startup folder, and reads of things like `~/.ssh`.
9. Keep it small: one application project, one core library, and one test
   project.

## 2. Tech stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), `net10.0-windows` for the app and `net10.0` for the core library |
| MCP | `ModelContextProtocol.AspNetCore` (official C# SDK), `WithHttpTransport()` + `MapMcp("/mcp")` |
| HTTP | Kestrel, `Listen(IPAddress.Loopback, port)`, never `Any`/`IPv6Any` |
| Outlook | `Microsoft.Office.Interop.Outlook` 15.0.4797.1004 (early-bound COM; NuGet package, not a `COMReference`, so it builds without Office; interop types embedded, the DLL is not shipped) |
| Database | `Microsoft.Data.Sqlite` 10.0.x (bundled SQLite with FTS5) + `Dapper` |
| Tray UI | WinForms `NotifyIcon` + `ContextMenuStrip`, plus three small forms |
| Secrets | DPAPI (`ProtectedData`, `DataProtectionScope.CurrentUser`; comes with the Windows Desktop framework, no package) |
| Logging | `Microsoft.Extensions.Logging` → rolling file + the in-memory activity feed |
| Tests | xUnit v3 (`xunit.v3.mtp-off` + `xunit.runner.visualstudio`), run by VSTest `dotnet test`, so `--filter "live!=outlook"` works |
| Packaging | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` |
| CI / release | GitHub Actions (§16). The version lives in `Directory.Build.props` |

## 3. Solution layout

```
ClippysRevenge.slnx
Directory.Build.props       version + shared build settings (Nullable, warnings as errors)
install.ps1
.github/workflows/          ci.yml, release.yml (§16)
src/ClippysRevenge.Core/        net10.0: no Windows, no COM, testable on Linux/WSL
    Settings.cs             settings model + defaults + validation
    EmailIndex.cs           SQLite schema, upsert, search, purge, status (one mailbox)
    EmailSearch.cs          SearchFilter, EmailSummary, SearchDates (date_from/date_to parsing)
    MailboxIndexes.cs       one EmailIndex per mailbox; id lookup across them
    FtsQuery.cs             safe FTS5 MATCH builder
    Recipients.cs           parse/format "Name <email>; ..." lists
    FreeBusy.cs             free/busy string -> busy intervals -> free slots
    SlotFormatter.cs        "1. Tue 10/15 1-3 PT"
    TimeZoneLabels.cs       Windows time zone -> "PT"/"ET"/... label
    ReplySubject.cs         "Re:: " normalization
    HtmlBody.cs             plain text -> escaped HTML; insert at top of an HTML body
    FileNames.cs            SafeFileName, UniquePath
    PathPolicy.cs           allowed-folder checks for every file read/write
    InviteGate.cs           mode + user decision + timeout -> Send / Draft / Refuse
    BearerAuth.cs           token middleware + constant-time compare
    ActivityHub.cs          activity state + event feed (UI-agnostic)
    McpHttpHost.cs          Kestrel on 127.0.0.1 + MapMcp + BearerAuth, tool-call activity filter, restart
    SingleInstance.cs       named mutex + named-pipe "show-settings" command
    AuthToken.cs            random 32-byte hex token
    TrayView.cs             tray presentation: icon key, tooltip/status text, Serving pulse
    Balloons.cs             balloon rate limit, Clippy texts, 2-minute rules
    TrayIconArt.cs          paperclip rasterizer + .ico encoder (icons are generated, then checked in)
src/ClippysRevenge.App/         net10.0-windows (WinExe): COM, tray, host
    Program.cs              single-instance check, wiring, message loop
    OutlookWorker.cs        dedicated STA thread owning all COM; attach-only
    OutlookStores.cs        list mounted Exchange mailboxes, resolve their SMTP
    OutlookMail.cs          COM reads/writes (items, attachments, drafts)
    OutlookCalendar.cs      events, free/busy, meeting invites
    Signatures.cs           locate + load the default Outlook signature
    SyncService.cs          background indexing (BackgroundService), all mailboxes
    McpTools.cs             [McpServerToolType] tool definitions
    SettingsStore.cs        load/save settings.json, DPAPI token
    Tray/TrayApp.cs         NotifyIcon, menu, icon state
    Tray/Icons/*.ico        generated by TrayIconArt; embedded resources
    Tray/SettingsForm.cs    settings window
    Tray/ActivityForm.cs    recent activity window
    Tray/InviteConfirmForm.cs  per-invite Send / Save as draft / Cancel dialog
tests/ClippysRevenge.Core.Tests/
```

All logic that does not touch Outlook, the registry or WinForms goes in
**Core**, so it can be tested anywhere.

## 4. Process and threading model

There is one process and three kinds of threads:

1. **UI thread (STA)**: the WinForms message loop that owns the tray icon
   and the forms. Nothing else touches UI objects; other threads go through
   `ActivityHub` → `SynchronizationContext.Post`.
2. **Kestrel / thread pool**: handles HTTP and MCP requests. It **never
   touches COM directly**.
3. **`OutlookWorker` (one dedicated STA thread)**: holds the
   `Outlook.Application` instance and runs every COM call.
   - API: `Task<T> RunAsync<T>(Func<Outlook.Application, T> work, Priority p, CancellationToken ct)`.
   - Two queues. **Interactive** work (tool calls) always runs before
     **Background** work (sync batches).
   - Background work is split into chunks of at most 50 items, so a tool
     call never waits behind a long backfill.
   - **Attach-only.** The worker gets Outlook with `GetActiveObject("Outlook.Application")`
     (a P/Invoke to `oleaut32`, since `Marshal.GetActiveObject` doesn't exist
     in .NET 5+). It **never** calls `new Outlook.Application()`,
     `Activator.CreateInstance` or `CoCreateInstance`, because those launch
     Outlook. A source scan enforces this (§15).
   - When there is no running Outlook (`MK_E_UNAVAILABLE`) or a call fails
     with `RPC_E_DISCONNECTED` / `RPC_E_SERVERCALL_RETRYLATER`, the worker
     drops the Application object and reports `OutlookUnavailable`. It
     retries the attach with backoff (1, 2, 4… up to 60 s). Background work
     waits. Interactive work fails fast with
     `McpException("Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.")`.
   - COM objects created while enumerating are released with
     `Marshal.ReleaseComObject` inside loops. Exchange limits how many
     items can be open at once, and leaking them breaks large syncs.

The Outlook object model is single-threaded internally anyway. One STA
worker makes that explicit and prevents cross-thread COM bugs.

## 5. Configuration

### 5.1 Storage

| What | Where |
|---|---|
| Settings | `%APPDATA%\ClippysRevenge\settings.json` |
| Auth token | `%APPDATA%\ClippysRevenge\token.bin`, DPAPI-encrypted for the current user |
| Own mailbox index | `%LOCALAPPDATA%\ClippysRevenge\index.db` (+ `-wal`, `-shm`) |
| Shared mailbox index | `%LOCALAPPDATA%\ClippysRevenge\index-<smtp>.db`, with `<smtp>` lower-cased and passed through `FileNames.SafeFileName` |
| Logs | `%LOCALAPPDATA%\ClippysRevenge\logs\clippy-YYYYMMDD.log`, 7 days kept |
| Attachment exports (default) | `%USERPROFILE%\Downloads\Clippy's Revenge` |

The token is never written to `settings.json` or to any log.

The indexes hold mail bodies in plain text, like Outlook's own OST cache.
They are protected by the user profile's ACLs and by disk encryption
(BitLocker). There is no extra encryption layer. The README says this.

### 5.2 Settings

| Setting | Default | Applied |
|---|---|---|
| `Port` | `8765` | Live: Kestrel restarts in-process |
| Auth token | random 32 bytes, hex | Live (old token rejected immediately) |
| `RetentionMonths` | `12` (1–120) | Live: a larger value starts a backfill, a smaller one purges |
| `SyncIntervalSeconds` | `300` (30–3600) | Live |
| `IndexedFolders` | Inbox, Sent Items | Live: sync is re-planned. Applies to the own mailbox |
| `SharedMailboxes` | `[]` | Live. See below |
| `WorkStartHour` / `WorkEndHour` | `8` / `17` | Live |
| `BusinessDaysOnly` | `true` | Live |
| `TimeZoneLabel` | empty = derived from Windows (`TimeZoneLabels`) | Live |
| `TentativeIsBusy` | `true` | Live |
| `MeetingInviteMode` | `Send` (`Send` / `DraftOnly` / `Disabled`) | Live |
| `InviteConfirmTimeoutSeconds` | `120` (30–900) | Live |
| `AttachmentDir` | see 5.1 | Live |
| `AllowedFolders` | `[AttachmentDir, %USERPROFILE%\Documents]` | Live. `AttachmentDir` is always allowed, even if removed from the list |
| `StartWithWindows` | `true` | Live: writes or removes the HKCU Run key |
| `NotifyOnErrors` | `true` | Live |
| `LogLevel` | `Information` | Live |

Each `SharedMailboxes` entry is:

| Field | Default | Meaning |
|---|---|---|
| `Smtp` | — | Primary SMTP address. This is the key used in tool arguments |
| `DisplayName` | from Outlook | Shown in menus |
| `Enabled` | `true` | Indexed and usable by tools |
| `IndexedFolders` | Inbox, Sent Items | Folders of that mailbox to index |
| `RetentionMonths` | `null` = use the global value | Per-mailbox override (1–120) |

`Settings.Validate()` lives in Core. Invalid values are rejected in the
form, with the reason shown next to the field.

Bounds not given in the table: `Port` 1024–65535; `WorkStartHour` 0–23,
`WorkEndHour` 1–24 and later than the start; `TimeZoneLabel` empty or at
most 16 printable characters; `IndexedFolders` needs at least one
folder, without duplicates. `AttachmentDir` and every `AllowedFolders`
entry must be a fully qualified local path (no UNC or device paths). An
empty `AllowedFolders` list is valid. `SharedMailboxes` entries need a
valid, unique `Smtp`. `Validate()` returns every error as (field,
message) and never throws.

`IndexedFolders` entries are folder paths relative to the mailbox root,
`\`-separated (e.g. `Inbox\Projects`). `Inbox` and `Sent Items` always
mean the mailbox's default Inbox and Sent Items folders, whatever
Outlook's display language.

`settings.json` uses the names in this table (PascalCase), with enums as
names. Missing or `null` values take their defaults, and unknown
properties are ignored. The model and its JSON shape live in Core
(`Settings`). `SettingsStore` only reads and writes the file.

## 6. System tray

### 6.1 Icon states

`ActivityHub.State` drives the icon. When several states are active, the
highest one in this table wins.

| State | Icon | Tooltip (≤127 chars) |
|---|---|---|
| `Error` | red | `Clippy — error: <short reason>` |
| `AwaitingConfirmation` | amber | `Clippy — waiting for you to confirm a meeting invite` |
| `OutlookUnavailable` | grey with `!` | `Clippy — Outlook is not running` |
| `Backfilling` | blue, animated | `Clippy — indexing Inbox 1,240 / 8,000 (2025-11 → now)` |
| `Syncing` | blue | `Clippy — pulling new mail (Inbox +12)` |
| `Purging` | blue | `Clippy — removing mail older than 12 months` |
| `Paused` | grey | `Clippy — indexing paused` |
| `Serving` | green pulse, ≥1 s | `Clippy — search_emails (8 ms)` |
| `Idle` | green | `Clippy — idle · 48,210 emails · synced 2 min ago` |

When shared mailboxes are syncing, the label names the mailbox, e.g.
`indexing helpdesk@… Inbox 300 / 2,000`.

The icons are embedded resources: a 16/20/24/32 px `.ico` for each state.
The animation is 4 frames on a 250 ms timer, and only runs while
backfilling.

The icons are original paperclip drawings generated by
`TrayIconArt` (Core) and checked in under `src/ClippysRevenge.App/Tray/Icons/`. A test fails
when they drift from the generator; run the tests with
`CLIPPY_REGEN_ICONS=1` to regenerate them. The `Begin` label is the
variable part of the tooltip:
- `Error`: the short reason.
- `Backfilling`: `<what>` optionally followed by ` (<range>)`. The
  counts go between them (`Inbox (2025-11 → now)` → `indexing Inbox 1,240 / 8,000 (2025-11 → now)`).
- `Syncing`: the text inside the parentheses (`Inbox +12`).
- `Purging`: the age (`12 months`).
- `Serving`: the tool name. After the call ends, the icon stays on
  `Serving` until 1 s after the call started. The tooltip then adds
  the duration of the call's `Tool` event.

The other rules:
- Numbers use invariant formatting (`48,210`).
- Relative times read `just now`, `N min ago`, `N h ago` or
  `N d ago`.
- Text longer than 127 characters is cut and ends in `…`.

### 6.2 Tray menu (right-click; double-click opens Activity)

```
Clippy — idle · 48,210 emails                 (disabled status line)
Last sync: 2 min ago · next in 3 min          (disabled)
────────────────────────────
Start Outlook                                 (only shown while Outlook is not running)
Sync now
Pause indexing / Resume indexing
Rebuild index…                                (confirm dialog; own mailbox or all)
Shared mailboxes                           ▸
    ✓ Helpdesk <helpdesk@oregonstate.edu>     (checkable, one per mailbox)
      Research Office <ro@oregonstate.edu>
    ─────────
    Manage in Settings…
────────────────────────────
Copy "claude mcp add" command
Copy auth token
Regenerate auth token…                        (confirm: invalidates current Claude config)
────────────────────────────
Activity…
Settings…
Open log folder
Open data folder
────────────────────────────
Start with Windows                            (checkable)
Exit
```

The first status line equals the tooltip, except that when idle it
omits "· synced …". The second line is
`Last sync: <relative|never>`, followed by ` · paused` while paused
or ` · next in N min` otherwise. Regenerate auth token asks
"Regenerate the auth token?" with OK/Cancel, and Cancel is the
default button.

**Outlook not running.** The first status line reads
`Clippy — Outlook is not running`, and **Start Outlook** appears. It
starts `OUTLOOK.EXE` with `Process.Start` (path from the
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE`
key) and is the only place the app ever starts Outlook. A balloon reports
the state (§6.5).

**Shared mailboxes submenu.** It lists the Exchange mailboxes mounted in
the current Outlook profile (`OutlookStores`: stores whose
`ExchangeStoreType` is `olExchangeMailbox` or
`olAdditionalExchangeMailbox`), excluding the user's own. It also lists
configured mailboxes that are no longer mounted, marked "(not in
Outlook)". Then:

- **Checking** a mailbox adds or enables it in `SharedMailboxes` and starts
  its backfill.
- **Unchecking** asks "Stop indexing <name>?" with three buttons: **Delete
  its index**, **Keep index file**, **Cancel**. The mailbox is disabled
  either way.
- While Outlook isn't running, the submenu shows the configured mailboxes
  only, plus a disabled line "Start Outlook to see more mailboxes".

**Start with Windows** is the same setting as in Settings → General. Both
places stay in sync.

"Copy `claude mcp add` command" puts this on the clipboard:

```
claude mcp add --scope user --transport http clippy http://127.0.0.1:<Port>/mcp --header "Authorization: Bearer <token>"
```

After copying, the tray shows a short "Copied — paste it in WSL" balloon.

### 6.3 Settings window (`SettingsForm`)

The window has tabs. Changes take effect when you press OK or Apply; Cancel
discards them.

- **Connection**:
  - Port.
  - Token field, masked, with Show, Copy and Regenerate buttons.
  - Read-only box with the full `claude mcp add` command and a Copy button.
  - **Test connection** button. It sends `POST /mcp` with an MCP
    `initialize` request twice: without the token (expects `401`) and with
    it (expects a JSON-RPC result with `serverInfo`). Then it shows ✓ or ✗.
  - The `.wslconfig` mirrored-networking snippet (§13).
- **Index**:
  - A mailbox picker at the top: "My mailbox" plus each shared mailbox.
    These fields belong to the selected mailbox:
    - Retention (months). For shared mailboxes, "Use global" is a checkbox.
    - Indexed folders: checkbox tree of that mailbox's store.
    - Live stats from that mailbox's `EmailIndex.Status()`: email count,
      oldest and newest item, DB size, last sync.
    - Rebuild button.
  - Add / remove shared mailbox buttons, with the same behavior as the
    tray submenu.
  - Global: sync interval.
- **Scheduling**:
  - Work hours.
  - Business days only.
  - Time-zone label (empty = automatic; shows the derived value).
  - Tentative counts as busy.
  - Meeting invites: Send (with confirmation) / Draft only / Disabled.
  - Confirmation timeout.
- **General**:
  - Attachment folder (with a browse button).
  - Allowed folders: a list with Add / Remove.
  - Start with Windows.
  - Notify on errors.
  - Log level.

The window warns before any change that breaks existing Claude
configurations, which means changing the port or regenerating the token.

### 6.4 Activity window (`ActivityForm`)

- The top shows the current state with a progress bar. The bar is only
  shown during a backfill and is driven by `done/total`.
- Below it is a list of the last 500 events: time, mailbox, kind, summary,
  duration. The list can be filtered by kind and by mailbox, and has a
  Copy button.
- Event kinds:
  - `Sync`: "Inbox +12, Sent +3 (1.8 s)".
  - `Backfill`: progress milestones.
  - `Purge`: "removed 310 emails older than 2025-11-12".
  - `Tool`: "search_emails · 23 results · 7 ms".
  - `Draft`: "reply draft created: Re:: Budget".
  - `Invite`: shown in **bold**, e.g. "meeting invite SENT to 7 attendees:
    Tue 10/15 1-3", "… saved as draft (no answer)", "… cancelled by you".
  - `Blocked`: a refused file path or a refused invite.
  - `Error`.
- Events carry tool names, counts and subjects only. Email bodies, search
  text and attachment contents are never shown.

### 6.5 Notifications

Balloon text is in Clippy's voice. Examples: "It looks like you're trying
to schedule a meeting. I sent 7 invites." and "It looks like Outlook
isn't running. I'll wait — or right-click me and pick Start Outlook."

Balloon notifications are rate-limited to one per minute. They fire on:

- the first run ("Clippy's Revenge is running — right-click to copy the Claude
  setup command");
- Outlook becoming unavailable for more than 2 minutes;
- an error state lasting more than 2 minutes, when `NotifyOnErrors` is on;
- an invite being sent or saved as a draft after a timeout;
- an initial backfill finishing (per mailbox).

The limit applies per balloon. A balloon that hits the limit is
dropped, not queued. The 2-minute balloons fire once per
uninterrupted episode of the tray's current state. Direct feedback to
a click ("Copied — paste it in WSL") is not rate-limited and doesn't
count toward the limit. The error balloon reads "It looks like
something went wrong: <reason>. Right-click me and pick Activity… for
details."

### 6.6 Invite confirmation (`InviteConfirmForm`)

In `Send` mode, `send_meeting_invite` shows this dialog before anything is
sent.

- It is a small topmost window, shown on the UI thread. It doesn't steal
  focus while the user is typing (`ShowWithoutActivation`), and the tray
  icon goes `AwaitingConfirmation`.
- It shows:
  - the organizer (own or shared mailbox);
  - the subject, the time (`SlotFormatter` label plus the full date), the
    location and whether it's a Teams meeting;
  - the full recipient list, with recipients **outside the user's
    organization highlighted**;
  - the invite body. This is the only place the app shows text that is
    about to leave the machine, and the user needs to see it to approve it.
- There are three buttons: **Send**, **Save as draft**, **Cancel**.
  **Cancel** is the default button and Esc triggers it, so Enter never
  sends.
- A countdown shows the remaining time. When `InviteConfirmTimeoutSeconds`
  runs out, the meeting is **saved as a draft**.
- Invite requests are queued, and only one dialog is shown at a time.
- The COM worker is **not** blocked while the dialog is open. The tool
  first resolves the recipients (COM), then awaits the dialog (UI), then
  creates and sends or saves the meeting (COM). `InviteGate` in Core turns
  the mode, the user's choice and the timeout into the action.

## 7. Activity reporting (`ActivityHub`, Core)

- `State` (enum from §6.1) and `Progress` (`done`, `total`, `label`).
- `IDisposable Begin(ActivityState state, string label)` sets a state for a
  scope. States are ref-counted, so overlapping scopes work.
- `Report(int done, int total)` updates progress.
- `Event(ActivityKind kind, string summary, TimeSpan? duration = null, string? mailbox = null)`
  appends to a ring buffer of 500 events.
- `event Action Changed` fires on changes and is throttled to at most 10 Hz.
  The tray subscribes and re-posts to the UI thread.
- `State` is the highest state (§6.1 order) among active `Begin` scopes,
  or `Idle` when none. `Begin(Idle, …)` is rejected. Disposing a scope
  twice has no effect.
- `Progress` is that of the newest active scope in the winning state;
  `(0, 0, "")` when idle. `Report(done, total)` updates the newest active
  `Backfilling`, `Syncing` or `Purging` scope and is a no-op when there is
  none, so a tool call or invite dialog never captures sync progress.
- Events carry an increasing `Id` and a UTC `Time`; `GetEvents()` returns
  a snapshot, oldest first.
- `Changed` is throttled leading + trailing edge over a 100 ms window: the
  first change fires at once on the caller's thread, later ones within the
  window coalesce into one call at the window's end (timer thread), so the
  final state is always delivered. Subscriber exceptions are swallowed.
- The hub takes a `TimeProvider` (default `TimeProvider.System`) for
  testability.
- Every MCP tool call is wrapped by a filter that runs
  `using (hub.Begin(Serving, toolName))` and records a `Tool` event with the
  call's duration.

## 8. Outlook layer (`OutlookStores`, `OutlookMail`, `OutlookCalendar`; App)

All calls run through `OutlookWorker`.

- `OutlookStores.List()` → mounted Exchange mailboxes with display name,
  store id and primary SMTP address. The SMTP comes from the store owner's
  `AddressEntry.GetExchangeUser().PrimarySmtpAddress`.
- `OutlookStores.Get(mailbox)` → the `Store` for `""` (own) or an SMTP
  address. It throws `McpException` listing the configured mailboxes when
  the address isn't one of them.
- `GetItem(string id)` → `MailItem`:
  1. `MailboxIndexes.Locate(id)` finds the row and the mailbox (§9.3).
  2. Try `Session.GetItemFromID(entryId, storeId)`.
  3. If that fails, the item has probably moved. Search that mailbox's
     indexed folders with
     `@SQL="http://schemas.microsoft.com/mapi/proptag/0x1035001F" = '<internet message id>'`.
  4. If it is still not found, throw `ItemNotFoundException`.
- `ReadForIndex(MailItem)` → `IndexedEmail`: entry id, store id, internet
  message id, folder, subject, sender name, **SMTP** sender (via
  `GetExchangeUser().PrimarySmtpAddress` for Exchange senders), To, CC,
  time (`ReceivedTime` for Inbox, `SentOn` for Sent), last modification
  time, unread, body, and attachments (position, file name, size,
  `IsInline`).
- `EnumerateChanged(folder, modifiedAfter, cutoff)` and
  `EnumerateRange(folder, from, to)`: both use **`Folder.GetTable(filter)`**
  with columns `EntryID`, `LastModificationTime`, `ReceivedTime`/`SentOn`,
  `UnRead` and `PR_INTERNET_MESSAGE_ID`. Table reads are much faster than
  iterating `Items`. Open the full `MailItem` only for rows that are new or
  changed, to read the body and attachments. DASL date literals are built
  by a single helper, never with a hard-coded locale format, and a live
  test covers it.
- `ListEntryIds(folder, since)` → entry id + `UnRead`, also from a Table.
  Used by reconcile.
- `SaveAttachment(id, position|filename, dir)` → saved path. `dir` has
  already passed `PathPolicy`. It uses `FileNames.UniquePath`.
- `IsInline(Attachment)`: true when the attachment type is `olOLE`, when
  `PR_ATTACHMENT_HIDDEN` is set, or when it is an image with
  `PR_ATTACH_CONTENT_ID` referenced in the HTML body.
- `CreateDraft(mailbox, …)`: for the own mailbox, `CreateItem(olMailItem)`.
  For a shared mailbox, the item is added to **that mailbox's Drafts
  folder** and `SentOnBehalfOfName` is set to the shared address. Then
  `.Save()`.
- `CreateReplyDraft(id, mailbox?, …)`: `Reply()` / `ReplyAll()`. The
  mailbox defaults to the one the original email lives in. For a shared
  mailbox, `SentOnBehalfOfName` is set and the saved draft is moved into
  that mailbox's Drafts folder. Then `.Save()`.
- **Draft bodies.** With `html=false`, the text goes through
  `HtmlBody.FromPlainText` and is inserted at the top of the item's
  `HTMLBody` with `HtmlBody.InsertAtTop`, above the quoted original. With
  `html=true`, the caller's fragment is inserted as-is. The default
  signature from `Signatures` is appended below the new text when one
  exists (§12). `CreateDraft` and `CreateReplyDraft` call **`.Save()`
  only**. Code review must reject any `.Send()` on a `MailItem` anywhere in
  the codebase. A unit test greps the source for this (§15).
- `Signatures.Default(kind)` (`New` or `Reply`): reads the signature name
  from `HKCU\Software\Microsoft\Office\16.0\Common\MailSettings`
  (`NewSignature` / `ReplySignature`) and loads
  `%APPDATA%\Microsoft\Signatures\<name>.htm`. It returns null when it is
  not set or not stored locally (cloud-roaming signatures may not be), and
  the tool result then says `signature_added: false`.
- `GetFreeBusy(email, startDate, intervalMinutes)` → `string?`: calls
  `Session.CreateRecipient(email)`, then `Resolve()`, then
  `AddressEntry.GetFreeBusy(start, interval, true)`. Outlook returns about
  30 days per call, so longer ranges are fetched in **28-day chunks** and
  joined. It returns null when the recipient can't be resolved.
- `CreateMeeting(mailbox?, …)`: an `AppointmentItem` with
  `MeetingStatus = olMeeting`, recipients added as `olRequired`, and
  `Recipients.ResolveAll()` (it fails if any recipient is unresolved). For
  a shared mailbox, the item is created in that mailbox's calendar
  (`GetSharedDefaultFolder(…, olFolderCalendar)`). This needs delegate or
  editor rights, and the error says so when they're missing. It then calls
  `.Send()` or `.Save()` as decided by `InviteGate`. This is the **only**
  `.Send()` call in the codebase.

## 9. Index (`EmailIndex`, `MailboxIndexes`, Core)

### 9.1 Schema (one database per mailbox)

```sql
CREATE TABLE emails (
  id INTEGER PRIMARY KEY,              -- stable rowid; FTS points here, VACUUM-safe
  entry_id TEXT NOT NULL UNIQUE, store_id TEXT, internet_message_id TEXT,
  folder TEXT NOT NULL, subject TEXT, sender_name TEXT, sender_email TEXT,
  to_addrs TEXT, cc_addrs TEXT, event_time INTEGER NOT NULL,   -- unix seconds UTC
  last_modified INTEGER, unread INTEGER,
  has_attachments INTEGER,             -- non-inline attachments only
  attachment_names TEXT,               -- all file names, newline-joined (for FTS)
  body TEXT);
CREATE INDEX ix_emails_time ON emails(event_time);
CREATE INDEX ix_emails_folder_time ON emails(folder, event_time);
CREATE INDEX ix_emails_imid ON emails(internet_message_id);
CREATE INDEX ix_emails_sender ON emails(sender_email);

CREATE VIRTUAL TABLE emails_fts USING fts5(
  subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names,
  content='emails', content_rowid='id',
  tokenize='unicode61 remove_diacritics 2');
-- kept in sync by triggers emails_ai (AFTER INSERT), emails_ad (AFTER DELETE) and
-- emails_au (AFTER UPDATE OF subject, body, sender_name, sender_email, to_addrs,
-- cc_addrs, attachment_names): the standard external-content pattern ('delete'
-- with old values, then insert). SetUnread doesn't touch FTS.

CREATE TABLE attachments (email_id INTEGER NOT NULL REFERENCES emails(id) ON DELETE CASCADE,
  position INTEGER NOT NULL, filename TEXT NOT NULL, size INTEGER, is_inline INTEGER,
  PRIMARY KEY (email_id, position));
CREATE INDEX ix_att_name ON attachments(filename COLLATE NOCASE);

CREATE TABLE sync_state (folder_key TEXT PRIMARY KEY,  -- folder EntryID
  folder_path TEXT,
  modified_watermark INTEGER,     -- max LastModificationTime seen (changes pass)
  oldest_synced INTEGER,          -- lower edge reached by backfill
  last_reconcile INTEGER, backfill_complete INTEGER);
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
  -- schema_version, mailbox_smtp, store_id, retention_months
```

There is **no attachment content column, ever**. The FTS table is
**external-content**: the text is stored once, in `emails`, and
`snippet()` still works. `id` is an explicit `INTEGER PRIMARY KEY` because
`VACUUM` may renumber implicit rowids, which would break the FTS mapping.

### 9.2 `EmailIndex` API

Every connection is opened with `journal_mode=WAL`, `synchronous=NORMAL`,
`foreign_keys=ON` and `busy_timeout=5000`. Each operation opens its own
connection; the library pools them.

- `EmailIndex(string dbPath)` creates or migrates the schema, keyed on
  `meta.schema_version`.
- `UpsertBatch(IReadOnlyList<IndexedEmail>)` writes in one transaction with
  `INSERT … ON CONFLICT(entry_id) DO UPDATE` (**never** `INSERT OR REPLACE`,
  which changes the rowid), and replaces that email's attachment rows.
- `Delete(IEnumerable<string> entryIds)`.
- `SetUnread(IEnumerable<(string entryId, bool unread)>)`.
- `PurgeOlderThan(DateTimeOffset cutoff)` → count. It runs `VACUUM` when
  more than 10% of rows were removed.
- `Search(SearchFilter f)` → `IReadOnlyList<EmailSummary>`, as one
  parameterized query (filters in §11.1). `EmailSummary.Mailbox` is `""`
  here; `MailboxIndexes` fills it in. An internal
  `SearchPage(filter, skip, take)` skips the 200 cap so the `all` merge can
  page after merging (`MailboxIndexes` may instead page through `Search` in
  chunks of at most 200, which gives the same result).
- `Get(string entryId)` → `EmailDetail?`.
- The API is synchronous (Microsoft.Data.Sqlite has no real async I/O).
  `Delete` and `SetUnread` return the number of rows affected; unknown
  ids are ignored. `UpsertBatch` validates the whole batch first
  (non-empty entry id and folder, unique attachment positions) and
  writes nothing on an `ArgumentException`. Times are stored as whole
  unix seconds and read back as UTC. `has_attachments` counts non-inline
  attachments only, and `attachment_names` holds every name, inline
  included, in position order. A database whose `schema_version` is
  newer than the app's makes the constructor throw; it is never
  modified or deleted automatically.
- `Status()` → counts per folder, oldest and newest item, DB size, last
  sync, backfill complete.
- `GetSyncState`/`SetSyncState(folderKey, …)`.
- `Close()` → `SqliteConnection.ClearPool(…)` so the file can be deleted
  (rebuild, mailbox removal).
- `FtsQuery.Build(string? userText)` → `string?`: quotes every token, so
  input like `budget-2026`, `C++` or `O'Brien` never causes an FTS syntax
  error. It ANDs the tokens, keeps a trailing `*` as a prefix match, and
  turns `OR` between tokens into an FTS `OR`. Details: only uppercase `OR`
  is an operator, and OR binds tighter than AND (`a b OR c` → `"a" AND ("b" OR "c")`).
  A balanced `"…"` is kept as one phrase, and an unbalanced `"` is a separator.
  `AND`, `NOT`, `NEAR`, `-`, `:` and the like are literal text. Tokens
  with no letter or digit are dropped. It returns `null` when nothing
  searchable remains, and the caller then omits the `MATCH` clause. The
  result is always bound as a parameter. It never throws.

### 9.3 `MailboxIndexes`

- Holds one `EmailIndex` per enabled mailbox. The key is `""` for the own
  mailbox and the lower-cased SMTP address for shared ones. Paths follow
  §5.1.
- `For(mailbox)` → the index, or an `McpException` naming the configured
  mailboxes.
- `Search(filter, mailbox)`: `""` searches the own mailbox, an address
  searches that mailbox, and `all` searches every enabled mailbox. It
  merges the results by date and applies `limit`/`offset` after merging.
- `Locate(entryId)` → `(mailbox, row)`, checking the own index first. Ids
  are globally unique Outlook EntryIDs, so tools that take an `id` never
  need a `mailbox` argument.
- `Add`/`Disable`/`Remove(mailbox, deleteFile)`, used by the tray and
  settings.
- Mailbox arguments are trimmed and lower-cased; empty means the own
  mailbox. Unknown or disabled mailboxes give
  `Mailbox "<input>" is not configured or not enabled. Available mailboxes: "" (your own mailbox), "<smtp>", …. To use another shared mailbox, ask the user to enable it in the Clippy tray menu under Shared mailboxes.`
  `all` outside search gives
  `Mailbox "all" only works with search tools. Use one mailbox: <list>.`
- The `all` merge sorts newest first; ties go own mailbox first, then
  shared mailboxes in the order they were added. `limit`/`offset` apply
  after the merge; the 200 cap is applied by the tool.
- `Locate` returns nothing when no enabled index has the id (the tool then
  falls back to COM, §11.2).
- `Disable` closes the index and keeps the file. `Remove(mailbox, deleteFile)`
  also works on a disabled mailbox and deletes the `.db`, `-wal` and `-shm`
  files. The own mailbox can't be disabled or removed here; rebuilding it
  is `SyncService`'s job (§10).

## 10. Sync (`SyncService`, App, `BackgroundService`)

- Loop: wait for the interval, a **Sync now** click, or a settings change.
  Do nothing while paused or while Outlook is unavailable. Run the cycle
  below for the own mailbox, then for each enabled shared mailbox.
- Cycle, for each indexed folder of the mailbox:
  1. `cutoff = now - RetentionMonths` (that mailbox's value).
  2. **First run of a folder:** set `modified_watermark = now` and
     `oldest_synced = now` **before** the backfill starts, so anything that
     changes during the backfill is picked up by the changes pass.
  3. **Changes pass:** `EnumerateChanged(folder, modified_watermark, cutoff)`,
     in ascending `LastModificationTime`, in batches of 50 → `ReadForIndex`
     → `UpsertBatch`. Advance `modified_watermark` after each batch. This
     picks up new mail, edits, and older mail moved into the folder. It
     reports as `Syncing`.
  4. **Backfill pass:** while `oldest_synced > cutoff`, use
     `EnumerateRange(folder, cutoff, oldest_synced)`, **newest first**, in
     batches of 50. Lower `oldest_synced` after each batch, so an
     interrupted backfill resumes where it stopped and recent mail becomes
     searchable first. It reports as `Backfilling` with `done/total`. When
     it reaches the cutoff, set `backfill_complete`.
  5. **Reconcile, once a day:** `ListEntryIds(folder, cutoff)` compared with
     the index. Delete rows for mail that is gone, add mail that is missing,
     and refresh `unread` with `SetUnread`. Read-state changes may not
     update `LastModificationTime`; this catches them either way (§17).
- After all folders: `PurgeOlderThan(cutoff)` (`Purging` state), and emit a
  `Sync` event with the per-folder counts, tagged with the mailbox.
- **Rebuild index** (one mailbox or all): stop sync, `Close()`, delete the
  DB, and start again from step 2.
- **Changing RetentionMonths:** a larger value just moves the cutoff. The
  backfill pass then runs again because `oldest_synced > cutoff`. A smaller
  value runs a purge immediately.

## 11. MCP tools (`McpTools`, App)

Tool names are snake_case (`[McpServerTool(Name = "search_emails")]`).
Descriptions come from `[Description]` and are written for the model.
Errors are thrown as `McpException` with a message the model can act on.

**`mailbox` argument.** Tools that work on a mailbox take an optional
`mailbox`. Empty means the user's own mailbox. An SMTP address means that
shared mailbox, which must be configured and enabled; the error otherwise
lists the available ones and tells the model to ask the user to enable it
in the tray. Search tools also accept `all`. Tools that take an `id` find
the mailbox from the id.

**File paths.** Every path argument goes through `PathPolicy` (§12).
Relative `save_dir` values resolve under `AttachmentDir`. A refused path
returns an error naming the allowed folders and records a `Blocked`
event.

### 11.1 `search_emails`

`(query, sender, recipient, subject, date_from, date_to, folder, has_attachment, attachment_name, attachment_ext, unread, mailbox, limit=50, offset=0)`

| Filter | Meaning |
|---|---|
| `query` | Full text over subject, body, sender, recipients and attachment names (`FtsQuery.Build`) |
| `sender` | Substring of the sender's name or SMTP address |
| `recipient` | Substring of To or CC. It also accepts a raw `Name <email>; ...` list, which matches mail involving **any** of those addresses |
| `subject` | Substring of the subject |
| `date_from` / `date_to` | ISO date or date-time in local time. A date-only `date_to` includes the whole day |
| `folder` | `inbox`, `sent`, another indexed folder's path, or empty for all |
| `has_attachment` | true/false/null. Inline attachments are excluded |
| `attachment_name` | Substring of any attachment file name |
| `attachment_ext` | Exact extension, with or without the dot (`pdf` or `.pdf`) |
| `unread` | true/false/null |
| `mailbox` | empty (own), an address, or `all` |
| `limit` | Capped at 200 |

Each result is `{id, mailbox, folder, date, from, sender_email, to, subject, has_attachments, attachments[], snippet}`.
`snippet` is FTS `snippet()` when `query` is set; otherwise it is the first
200 characters of the body.

Filter details (M2.2): text filters are trimmed, and blank means no
filter. `sender`, `recipient`, `subject` and `attachment_name` are
case-insensitive substring matches (SQLite `LIKE`, ASCII case folding,
with `%`, `_` and `\` matched literally). `recipient` is treated as a list
when it contains `;` or `<` and `Recipients.Parse` accepts it; otherwise
it is one substring. `folder` is compared case-insensitively and exactly
with the stored folder path (`inbox` = `Inbox`, `sent` = `Sent Items`,
`/` is accepted for `\`); subfolders are not included. `attachment_name`
and `attachment_ext` look at every attachment, inline ones included;
only `has_attachment` ignores inline attachments. `date_from`/`date_to`
accept `yyyy-MM-dd` and `yyyy-MM-ddTHH:mm[:ss[.fffffff]]` (a space may
replace `T`) in local time, or a date-time ending in `Z` or `±hh:mm`,
which is honored. Both ends are inclusive. Anything else is an error:
`date_from "<text>" is not a valid date. Use an ISO date like 2026-03-01 or a date-time like 2026-03-01T14:30 (local time).`
(`date_to` likewise). Results are newest first (by `event_time`, then
`id`). `limit` is clamped to 1–200 and a negative `offset` counts as 0.
The FTS snippet is `snippet(emails_fts, -1, '**', '**', '…', 16)` and
falls back to the body start when empty; whitespace runs in snippets are
collapsed to one space. `attachments[]` lists every attachment (position,
file name, size, inline flag) in position order.

### 11.2 `get_message(id)`

Returns the full email from the index. If the email isn't indexed yet, it
falls back to a live read through COM.

### 11.3 `get_attachment(id, filename?, position?, save_dir?)` → `{path, filename, size}`

Fetches the attachment live through COM. `save_dir` must pass `PathPolicy`.

### 11.4 `save_attachments(save_dir?, <all search filters incl. mailbox>, include_inline=false, limit=50)`

`attachment_name` and `attachment_ext` also choose *which* attachments get
saved. Files with the same name are saved as `name (2).ext`.

Returns `{saved[], failed[], count}`.

### 11.5 `create_draft(to, subject, body, cc?, bcc?, html=false, attachments[]?, mailbox?)` → `{id, subject, mailbox, signature_added}`

Only saves the draft. Every attachment path is checked with `PathPolicy`
and for existence **before** anything is created. With `mailbox`, the
draft goes into that mailbox's Drafts folder and is sent on its behalf.

### 11.6 `reply_draft(id, body, reply_all=false, html=false, mailbox?)` → `{id, subject, mailbox, signature_added}`

The subject goes through `ReplySubject.Normalize`, so it starts with
`Re:: `. The body is placed above the quoted original (§8, draft bodies).
`mailbox` defaults to the mailbox of the original email. Only saves the
draft.

### 11.7 `list_events(start?, end?, days=7, mailbox?)`

Sets `IncludeRecurrences` **before** `Sort` and `Restrict`. Capped at 500
items. With `mailbox`, it reads that mailbox's calendar.

### 11.8 `find_free_times(recipients, duration_minutes=60, days_ahead=10, date_from?, earliest_hour?, latest_hour?, business_days_only?, include_me=true, max_suggestions=5, mailbox?)`

Unset optional arguments fall back to the tray settings. `days_ahead` is
capped at 60.

1. Parse `recipients` with `Recipients.Parse`. It is the raw pasted list,
   for example:
   `Thompson, Christopher Scott <chris.thompson@oregonstate.edu>; Petersen, Dirk <dirk.petersen@oregonstate.edu>; ...`
2. When `include_me` is true, add the calendar owner: the current user, or
   the shared mailbox when `mailbox` is set.
3. Get free/busy for each attendee (§8). Turn it into busy intervals with
   `FreeBusy.BusyIntervals`.
4. `FreeBusy.FreeSlots` combines everyone's busy time and the working
   hours. It keeps free blocks of at least `duration_minutes`.
5. Format each slot with `SlotFormatter`.

Returns:

```json
{"suggestions":[{"n":1,"label":"1. Tue 10/15 1-3 PT","start":"2024-10-15T13:00:00-07:00","end":"2024-10-15T15:00:00-07:00"}],
 "text":"1. Tue 10/15 1-3 PT\n2. Wed 10/16 9:30-11 PT",
 "unknown_attendees":["someone@external.org"],
 "attendees":[{"name":"Petersen, Dirk","email":"dirk.petersen@oregonstate.edu"}]}
```

`unknown_attendees` are people with no readable free/busy, such as people
outside the Exchange org. They are left out of the availability check and
reported back. The server keeps no state between calls: for "send #2", the
model passes the `start` and `end` of suggestion 2.

### 11.9 `send_meeting_invite(recipients, subject, start, end, body?, location?, teams=false, mailbox?)` — **the only sending tool**

What it does depends on `MeetingInviteMode`:

- **`Send`**: resolve every recipient (if any is unresolved, nothing is
  created). Then show the confirmation dialog (§6.6) and wait for it:
  - **Send** → the meeting is sent. Result `{status: "sent", …}`.
  - **Save as draft**, or no answer before the timeout → it is saved in the
    calendar unsent. Result `{status: "saved_as_draft", reason: "user_choice" | "timeout", …}`,
    and the message tells the model the user needs to review it in Outlook.
  - **Cancel** → nothing is created. Result `{status: "cancelled"}` with
    "The user declined this invite. Do not retry unless they ask."
- **`DraftOnly`**: saves the meeting in the calendar without sending, and
  says so in the result. Result
  `{status: "saved_as_draft", reason: "draft_only_mode", …}`.
- **`Disabled`**: returns an error that tells the model to ask the user to
  enable invites in the tray settings.

The tool description must say plainly that in Send mode **the user is asked
to confirm in a dialog, and the invite goes out immediately once they
click Send**. Every outcome produces a bold `Invite` activity event, and
sends and timeouts produce a notification.

`teams=true` adds a Teams meeting through the Teams add-in when it is
available. If it isn't, the meeting is still created and a note is
returned. With `mailbox`, the meeting is organized from that mailbox's
calendar (§8).

### 11.10 `index_status(mailbox?)`

Returns `Status()` for each configured mailbox (or just one), whether
Outlook is running, and the current `ActivityHub` state. This is for
questions like "why didn't search find X" and for checking backfill
progress.

## 12. Core helpers

- `Recipients.Parse(string raw)` → `IReadOnlyList<Recipient(Name, Email)>`:
  - Split on `;` **only**, never on `,`, because names are written
    `Last, First Middle`.
  - Accept `Name <email>`, a bare `email`, and `"Name" <email>`. `Name` is
    `""` for a bare email or `<email>`.
  - An email is valid when it has exactly one `@`, text on both sides, and
    no whitespace or `< > " , ;`.
  - Trim whitespace, skip empty entries, and de-duplicate case-insensitively
    by email while keeping the original case. The first occurrence wins.
    Empty input returns an empty list.
  - When an entry has no valid email, throw `FormatException` with
    `Recipient "<entry>" has no valid email address. Use "Name <email>" or a bare email, and separate recipients with ";".`
- `Recipients.Format(list)`: the inverse, `Name <email>; ...`. A recipient
  with an empty name is written as the bare email.
- `FreeBusy.BusyIntervals(string fb, DateTime start, int intervalMinutes, bool tentativeIsBusy)` → `IReadOnlyList<TimeRange>`:
  - Codes: `0` free, `1` tentative, `2` busy, `3` out of office, `4`
    working elsewhere.
  - Busy means `2` or `3`, plus `1` when `tentativeIsBusy` is set. `4`
    counts as free.
  - Adjacent busy intervals are merged.
  - Any other character throws `FormatException`. The caller treats that
    attendee as unknown, and unknown data is never treated as free.
- `FreeBusy.FreeSlots(busyPerAttendee, from, to, startHour, endHour, businessDaysOnly, minMinutes, now, intervalMinutes = 30)`:
  - For each day, take the working-hours window and remove the busy time.
  - Free time before `now` rounded up to the next `intervalMinutes`
    boundary is clipped off. Then gaps of at least `minMinutes` are kept,
    each as one maximal block.
  - `find_free_times` fetches free/busy at 30-minute intervals
    (`FreeBusy.DefaultIntervalMinutes`).
- `FreeBusy.ChunkStarts(from, to)` / `FreeBusy.JoinChunks(chunks, intervalMinutes)`:
  free/busy is fetched from midnight of `from` in 28-day chunks. Every chunk
  but the last is cut to exactly 28 days before joining. A non-last chunk
  shorter than 28 days is an error, and that attendee is reported as unknown.
- `SlotFormatter.Format(n, start, end, tzLabel, windowStartHour, windowEndHour)` → `"1. Tue 10/15 1-3 PT"`:
  - Weekday as 3 letters, date as `M/d`.
  - 12-hour times with `:mm` only when the minutes are not zero
    (`9:30-11`).
  - am/pm is added only when the work window is wider than 7am–7pm.
    Otherwise the time can't be ambiguous.
  - The window is the search's working hours (`earliest_hour`/`latest_hour`
    or `WorkStartHour`/`WorkEndHour`); "wider than 7am–7pm" means
    start < 7 or end > 19. Suffixes are lowercase `am`/`pm`, written once
    on the end time when both times share it (`9-11am`, `11am-1pm`).
  - Formatting uses the invariant culture and the wall-clock values as
    given. A blank `tzLabel` omits the label.
- `TimeZoneLabels.For(TimeZoneInfo, DateTime? at = null)`: `PT`, `MT`, `CT`,
  `ET`, `AKT`, `HT` for the US zones (a fixed table of Windows and IANA ids,
  no Mexican/Canadian/Aleutian variants); `UTC` for UTC itself; otherwise
  the standard name when it is an abbreviation (2–5 capital letters), else
  `UTC±hh:mm` using the offset at `at` (base offset when null).
  `TimeZoneLabels.Resolve(override, tz, at)`: the `TimeZoneLabel` setting,
  trimmed, wins when non-blank. All scheduling math uses the local Windows
  time zone.
- `ReplySubject.Normalize(string?)`:
  - Trim, then repeatedly strip a leading `re`, `fw`, `fwd`, `aw` or `wg`
    followed directly by one or more colons, in any case, and the
    whitespace after it.
  - Return `"Re:: " + rest` (rest trimmed). An empty subject becomes `"Re::"`.
- `HtmlBody.FromPlainText(string)`: normalizes `\r\n` and `\r` to `\n`,
  then encodes exactly `&` `<` `>` `"` `'` (as `&amp;` `&lt;` `&gt;`
  `&quot;` `&#39;`); other characters, including non-ASCII, pass through.
  One or more blank (empty or whitespace-only) lines separate paragraphs;
  each paragraph becomes `<p>…</p>` and single newlines inside it become
  `<br>`. Leading and trailing blank lines are dropped, the output has no
  whitespace between tags, and text that is empty or all blank gives `""`.
  URLs stay plain text (no `<a>`). Spaces and tabs are not converted to
  `&nbsp;`.
  `HtmlBody.InsertAtTop(string html, string fragment)`: inserts the
  fragment right after the first opening `<body…>` tag (case-insensitive,
  attributes allowed, including quoted values that contain `>`; `<tbody>`
  and `<bodyx>` don't count), or prepends it when there is no complete body
  tag. Nothing else in `html` changes.
- `FileNames.SafeFileName(string?)` replaces `\ / : * ? " < > |` and
  control characters with `_` (the same set on every OS), trims whitespace
  and trailing dots, prefixes `_` when the part before the first `.` is a
  Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM0`–`COM9`,
  `LPT0`–`LPT9`), and shortens names longer than 200 characters while
  keeping the extension. It returns `"attachment"` if nothing is left.
  `FileNames.UniquePath(dir, name)` sanitizes `name` the same way and
  returns a path in `dir` that doesn't exist yet, as a file or a directory,
  using `name (n).ext` with n = 2, 3, …
- `PathPolicy.Check(path, allowedRoots, mode, relativeTo?)` → full path, or
  throws `PathNotAllowedException` (with a `Reason`; the message names the
  allowed folders):
  - Relative paths resolve against `relativeTo` (the tools pass
    `AttachmentDir`); without it they are refused. Drive-relative (`C:x`)
    and root-relative (`\x`) paths are always refused. The current
    directory is never used.
  - `Path.GetFullPath` first, then the path must be equal to or under one
    root, compared case-insensitively on a **separator boundary**
    (`C:\Docs2` is not under `C:\Docs`). When roots overlap, the longest
    match is used.
  - Reject device and UNC forms (`\\?\`, `\\.\`, `\\server\share`),
    alternate data streams (`:` after the drive), control characters, and
    reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM0-9`, `LPT0-9`)
    in any component.
  - Reject when any existing component of the path below the root is a
    junction or symbolic link (including dangling links), so a link can't
    escape the root. The root itself may be a link. Other reparse points
    (OneDrive Files-On-Demand placeholders, dedup) are not links and are
    allowed. If a component can't be inspected, the path is refused.
  - `mode` is `Read` (draft attachments, must exist and be a file;
    otherwise `FileNotFoundException`) or `Write` (save directories,
    created if missing; `IOException` if the path is an existing file).
    Only policy refusals are `PathNotAllowedException`, so only they record
    a `Blocked` event.
- `InviteGate.Decide(mode, userChoice?, timedOut)` → `Send`,
  `SaveDraft(reason)`, `Cancel` or `Refuse(reason)`. It is the only place
  that maps these inputs, so it is fully unit-tested. The most restrictive
  input wins:
  - `Disabled` → `Refuse` with "Meeting invites are disabled in
    Clippy's Revenge. Ask the user to enable them in the tray settings
    (Meeting invite mode: Send or Draft only), then try again."
  - A user **Cancel** → `Cancel`, in any mode other than `Disabled`,
    even if the timeout fired at the same moment.
  - `DraftOnly` → `SaveDraft(draft_only_mode)` for any other input.
  - `Send` mode: timeout → `SaveDraft(timeout)`, even if a Send click
    raced with it; **Send** → `Send`; **Save as draft** →
    `SaveDraft(user_choice)`; dialog closed with no choice and no
    timeout → `Cancel`.
  - `Send` is returned only for (`Send` mode, Send click, no timeout).
    Undefined enum values throw instead of falling through.

## 13. HTTP host, auth and WSL networking

- Kestrel listens on `IPAddress.Loopback:Port` only. On startup it checks
  that no other binding exists.
- `BearerAuth` middleware runs before `MapMcp`:
  - It reads the expected token from a `TokenHolder` that the tray updates
    directly, so a regenerated token takes effect without a restart.
  - The comparison is constant-time: both tokens are hashed with SHA-256
    and compared with `CryptographicOperations.FixedTimeEquals`, so the
    length doesn't leak either.
  - Anything other than exactly one `Authorization: Bearer <token>` header
    with a matching token gets a 401 with an empty body. The scheme is
    case-insensitive; the token is case-sensitive and is not trimmed.
  - Requests that carry an `Origin` header (any value, even empty) also get a
    401, which blocks browser pages from calling the server. CORS preflights
    fail the same way.
  - The 401 carries no `WWW-Authenticate` header, so MCP clients don't start
    OAuth discovery.
- **WSL networking requirement.** In WSL2's default **NAT** mode, WSL
  cannot reach the Windows host's `127.0.0.1`. Use **mirrored networking**
  (Windows 11 22H2+), set in `%USERPROFILE%\.wslconfig`:
  ```
  [wsl2]
  networkingMode=mirrored
  ```
  Then run `wsl --shutdown`. The Settings → Connection tab shows this
  snippet. To check from WSL: `curl -i -X POST http://127.0.0.1:8765/mcp`
  should return `401`. Binding to the WSL virtual adapter instead is
  rejected, because it breaks the localhost-only rule.

## 14. Startup and install

- **Single instance**: a named mutex `Local\ClippysRevenge`. A second launch
  tells the first instance to open Settings over a named pipe, then exits.
  The pipe is `ClippysRevenge-<session id>`, created with
  `PipeOptions.CurrentUserOnly`; the only command is the line
  `show-settings`, and anything else is ignored.
- **Start with Windows**: the
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ClippysRevenge` value
  points to the exe. This needs no admin rights and runs in the
  interactive session that the tray and Outlook COM require. It can be
  toggled from the tray menu and from Settings.
- **First run**:
  1. Create the data folders, the token and default settings.
  2. Set the Run key.
  3. Start the HTTP server and sync. If Outlook isn't running, sync waits
     for it (§4). The app does not start Outlook.
  4. Show the first-run balloon (§6.5).
- **Install**:
  1. Download `ClippysRevenge.exe` from the GitHub release, or build it
     with `dotnet publish` (§2).
  2. `install.ps1` copies it to `%LOCALAPPDATA%\Programs\ClippysRevenge\`,
     creates a Start menu shortcut, and launches it.
  3. `install.ps1 -Uninstall` stops it, removes the Run key and the
     shortcut, and optionally deletes the data and logs.
- If the app crashes, there is no automatic restart. Unhandled exceptions
  are logged, and a wrapper around the host restarts the HTTP and sync
  services in-process. If the process itself dies, the user restarts it
  from the Start menu shortcut.

## 15. Tests (`ClippysRevenge.Core.Tests`, runs on Linux/WSL)

| Area | Cases |
|---|---|
| `ReplySubject` | `RE:`, `re:`, `Re: Re:`, `FW:`, `Fwd: Re:`, `AW:`, `Re::` (idempotent), empty |
| `Recipients` | The 7-person OSU list (commas inside names), trailing `;`, bare email, quoted name, duplicates, missing email → error |
| `FtsQuery` | `-`, quotes, `'`, `*` prefix, `OR`, empty input |
| `EmailIndex` | schema/migration, batch upsert (rowid stays stable on update), delete, every filter alone and combined, paging, purge, `VACUUM` keeps FTS working, snippet, `has_attachment` ignores inline, `SetUnread` |
| `MailboxIndexes` | separate files per mailbox, `all` merge + paging, `Locate` across mailboxes, unknown mailbox error, remove with and without deleting the file |
| `FreeBusy` | synthetic strings, overlaps across attendees, tentative toggle, weekends, work-hour edges, "now" cut-off, 28-day chunk joining |
| `SlotFormatter` | `1. Tue 10/15 1-3 PT`, `2. Wed 10/16 9:30-11 PT`, am/pm when the window is wide |
| `TimeZoneLabels` | Pacific/Mountain/Central/Eastern, a non-US zone, override |
| `HtmlBody` | escaping (`<`, `&`, quotes), paragraphs and line breaks, insert after `<BODY class=…>`, no body tag |
| `PathPolicy` | inside/outside a root, `..` traversal, prefix trick (`C:\Docs2`), UNC and `\\?\`, ADS, junction escape (Windows-only test), read vs write mode |
| `InviteGate` | every mode × choice × timeout combination |
| `BearerAuth` | missing, wrong or correct token; `Origin` header → 401; live token swap |
| `Settings` | defaults, validation bounds, shared mailbox entries, `AttachmentDir` always allowed |
| Safety | A source scan of `src/` finds exactly one `.Send(`, in `OutlookCalendar.CreateMeeting`, and no `new Application(`, `Activator.CreateInstance` or `CoCreateInstance` for Outlook |

Live tests (`[Trait("live","outlook")]`, opt-in, never in CI) cover: a
draft round-trip, the reply-draft subject and HTML body, saving an
attachment, free/busy for the current user, an invite in `DraftOnly`
mode, the DASL date helper, and whether marking a message read changes
`LastModificationTime`.

## 16. Build, CI and release

- `Directory.Build.props` holds `<Version>` and the shared build settings
  (`Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`).
- **`ci.yml`** (pushes to `main`, PRs): Core tests on `ubuntu-latest`;
  full build, tests and a single-file publish smoke test on
  `windows-latest`. Live tests are filtered out (`--filter "live!=outlook"`).
- **`release.yml`** (pushes to `main`): if tag `v<Version>` doesn't exist,
  it runs the tests, publishes `ClippysRevenge.exe` (§2) and creates a
  GitHub release with the exe, `install.ps1` and `SHA256SUMS.txt`. 0.x
  versions are marked as pre-releases. **To ship, bump `<Version>` and
  merge to `main`.**
- Both workflows skip cleanly while there is no solution file.
- The exe is not code-signed yet, so SmartScreen warns on first launch.

## 17. Performance targets

- `search_emails` p95 under 20 ms with 50k indexed emails (single
  mailbox), under 50 ms with `mailbox=all` across 3 mailboxes.
- A steady-state sync cycle with nothing new under 1 s per mailbox,
  because the Table API scans only metadata.
- Initial backfill of 12 months: at least 50 emails/s, with tool calls
  staying responsive during it (interactive priority).
- `find_free_times` with 8 attendees over 10 business days under 5 s.
- Tray icon updates within 250 ms of a state change.
- The DB is not compressed. It holds text only, about tens of MB per year
  of mail.

## 18. Decisions and open questions

Decided (2026-11-12):

- Shared mailboxes are picked from the stores mounted in Outlook, from the
  tray or Settings. Each has its own database, folders and retention.
- One auth token in v1. Named per-client tokens are deferred.
- Time zones: local Windows time only. Other attendees' zones are out of
  scope.
- External attendees: reported in `unknown_attendees` and ignored for
  availability.
- New Outlook (`olk.exe`) has no COM API and is not supported.
- Build (M0.1): no central package management; each project pins exact
  versions in its own `.csproj`. Core has a `FrameworkReference` to
  `Microsoft.AspNetCore.App` because `BearerAuth` is ASP.NET Core
  middleware; that shared framework is cross-platform, so Core still
  builds and tests on Linux.
- Settings (M1.7): `MeetingInviteMode` is its own Core file, shared by
  `Settings` and `InviteGate`. Unchecking a shared mailbox sets
  `Enabled = false` and keeps the entry, so its folders and retention
  survive re-checking it.
- Index (M2.4): Core references `ModelContextProtocol.Core` (same version
  as the App's `ModelContextProtocol.AspNetCore`) so Core code can throw
  `McpException` directly, as §9.3 and §11 require.
- Host (M4.1): Core references `ModelContextProtocol.AspNetCore` (same
  pinned version as App) so the HTTP/MCP host is tested on Linux; tool
  classes stay in App. (This and the M2.4 `ModelContextProtocol.Core`
  reference coexist at the same version.) The host is a `WebApplication`
  built with no configuration sources (env vars can't add bindings) and
  the MCP transport is stateless. A port change rebuilds the whole host
  in-process; `TokenHolder`, `ActivityHub` and `Settings` are created once
  in `Program` and survive restarts. If the port can't be bound the tray
  shows `Error` with `port <n> is in use`. Each tool call records a `Tool`
  event whose summary is the tool name, or `<name> failed`.

Open:

1. **Read state and `LastModificationTime`**: if marking a message read
   doesn't bump `LastModificationTime`, `unread` can be up to a day stale
   (until reconcile). The live test in §15 answers this. If it's stale,
   consider a cheap hourly `UnRead` refresh over the last 30 days.
2. **Signatures on shared-mailbox drafts**: v1 appends the user's own
   default signature. Outlook may hold a separate per-account signature
   for a shared mailbox.
3. **Outlook security prompts**: reading addresses or free/busy can trigger
   the object-model guard when no up-to-date antivirus is registered. The
   tray shows this as the `Error` state "Outlook blocked programmatic
   access".
