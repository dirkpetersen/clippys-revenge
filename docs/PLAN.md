# Plan to v0.0.1

The `/dev-cycle` loop works from this file. It picks the first unchecked
task whose dependencies are done, runs architect → coder → reviewer, and
commits. Task ids are used for briefs (`docs/briefs/<id>.md`) and commit
messages (`M3.2: …`).

Status markers: `[ ]` todo · `[x]` done · `[!]` blocked (see
`docs/QUESTIONS.md`) · `[~]` in progress.

Branch: all work happens on `dev`. `main` only moves when the user merges a
PR, and a merge to `main` with `<Version>0.0.1</Version>` publishes the
v0.0.1 release.

## M0 Scaffold
- [ ] M0.1 Solution `ClippysRevenge.slnx` with the three projects (SPEC §3), package references from SPEC §2, one placeholder test. `dotnet build` and `dotnet test` pass with zero warnings. Core builds and tests on Linux.

## M1 Core helpers (SPEC §12) — deps: M0
- [ ] M1.1 `ReplySubject`, `FileNames`, `Recipients` + tests
- [ ] M1.2 `FtsQuery` + tests
- [ ] M1.3 `SlotFormatter`, `TimeZoneLabels` + tests
- [ ] M1.4 `HtmlBody` + tests
- [ ] M1.5 `PathPolicy` + tests (junction test Windows-only)
- [ ] M1.6 `InviteGate` + tests
- [ ] M1.7 `Settings` model, defaults, `Validate()`, shared mailbox entries + tests
- [ ] M1.8 `FreeBusy.BusyIntervals` / `FreeSlots`, 28-day chunk joining + tests

## M2 Index (SPEC §9) — deps: M1.2
- [ ] M2.1 `EmailIndex` schema, migrations, external-content FTS + triggers, `UpsertBatch`, `Delete`, `SetUnread`, `Get` + tests
- [ ] M2.2 `EmailIndex.Search` with every filter, paging, snippet + tests
- [ ] M2.3 `PurgeOlderThan` (+ VACUUM keeps FTS working), `Status`, sync state, `Close` + tests
- [ ] M2.4 `MailboxIndexes` (per-mailbox files, `all` merge, `Locate`, add/disable/remove) + tests
- [ ] M2.5 Perf check: 50k synthetic emails, `search_emails` p95 < 20 ms (opt-in benchmark test, not in CI)

## M3 Host plumbing (SPEC §7, §13) — deps: M0
- [ ] M3.1 `BearerAuth` middleware + tests (Origin → 401, live token swap)
- [ ] M3.2 `ActivityHub` (ref-counted states, ring buffer, 10 Hz throttle) + tests

## M4 App shell — deps: M1.7, M3
- [ ] M4.1 `Program`: single-instance mutex + named pipe, generic host, Kestrel on loopback, `MapMcp("/mcp")` behind `BearerAuth`, an `index_status` stub tool, tool-call activity filter
- [ ] M4.2 `SettingsStore` (settings.json, DPAPI token, first run, Run key)
- [ ] M4.3 Tray: `TrayApp` with the menu from SPEC §6.2 (handlers can be stubs), icon states from `ActivityHub`, original paperclip icons (generated `.ico` resources), balloons with rate limit

## M5 Outlook core (SPEC §4, §8) — deps: M4
- [ ] M5.1 `OutlookWorker`: STA thread, two priority queues, attach-only `GetActiveObject`, backoff, `OutlookUnavailable`, fail-fast for interactive work; tray "Start Outlook"
- [ ] M5.2 `OutlookStores` (list mounted Exchange mailboxes, SMTP, `Get(mailbox)`)
- [ ] M5.3 `OutlookMail` reads: `ReadForIndex`, `IsInline`, Table-based `EnumerateChanged` / `EnumerateRange` / `ListEntryIds`, the DASL date helper, `GetItem` fallback chain

## M6 Sync (SPEC §10) — deps: M2, M5
- [ ] M6.1 `SyncService`: changes pass, newest-first backfill, watermarks, progress, purge, all mailboxes, pause/resume, Sync now, rebuild
- [ ] M6.2 Daily reconcile (delete missing, add missing, refresh unread); retention changes

## M7 Mail tools (SPEC §11.1–11.6) — deps: M6
- [ ] M7.1 `search_emails`, `get_message` (COM fallback), `index_status` (real)
- [ ] M7.2 `get_attachment`, `save_attachments` (PathPolicy, UniquePath, inline handling)
- [ ] M7.3 `Signatures`, `create_draft` (own + shared, attachments checked first)
- [ ] M7.4 `reply_draft` (`Re:: `, HTML insert, shared mailbox drafts)

## M8 Calendar tools (SPEC §11.7–11.9, §6.6) — deps: M5, M1.8, M1.6
- [ ] M8.1 `list_events` (+ shared calendars)
- [ ] M8.2 `GetFreeBusy` + `find_free_times`
- [ ] M8.3 `InviteConfirmForm` + confirmation queue + `AwaitingConfirmation` state
- [ ] M8.4 `CreateMeeting` + `send_meeting_invite` (all modes, Teams note, shared calendar)

## M9 UI completion (SPEC §6) — deps: M6, M8.3
- [ ] M9.1 Shared mailboxes submenu (check / uncheck with delete prompt / not-in-Outlook entries)
- [ ] M9.2 `SettingsForm` (all tabs, validation messages, Test connection via `initialize`)
- [ ] M9.3 `ActivityForm` (progress, filters, copy)

## M10 Ship — deps: everything above
- [ ] M10.1 Safety source-scan test (one `.Send(`, no Outlook launch calls)
- [ ] M10.2 Live tests (`[Trait("live","outlook")]`), run once by the user with `CLIPPY_LIVE=1`
- [ ] M10.3 `install.ps1` (install, Start menu shortcut, `-Uninstall`) and README (install, WSL mirrored networking, security model, index at rest)
- [ ] M10.4 Final review of the whole branch by `reviewer`, CI green on the PR, then hand to the user to merge (→ v0.0.1 release)

## Log

Append one line per finished task: `YYYY-MM-DD M#.# <short summary> (<commit>)`.
