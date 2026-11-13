# Plan to v0.0.1

The `/dev-cycle` loop works from this file. It picks the first unchecked
task whose dependencies are done, runs architect → coder → reviewer, and
commits. Task ids are used for briefs (`docs/briefs/<id>.md`) and commit
messages (`M3.2: …`).

Status markers: `[ ]` todo · `[x]` done · `[!]` blocked (see
`docs/QUESTIONS.md`) · `[~]` in progress.

Each task ends with `{arch:brief}` or `{arch:skip}`:

- `arch:brief`: the `architect` (Opus) writes `docs/briefs/<id>.md` first.
- `arch:skip`: the spec is precise enough; the `coder` works straight from
  the task line and the SPEC sections it cites, with no brief.

**Reviews run once per milestone**, not per task. Each task is committed
once its build and tests pass. When every task in a milestone is `[x]`,
the `reviewer` (Opus, medium effort) reviews the milestone's whole commit
range, and fixes land as one more commit. Each milestone has a
`Review:` line that records this. A milestone's dependents may start
before its review is done.

Branch: all work happens on `dev`. `main` only moves when the user merges a
PR, and a merge to `main` with `<Version>0.0.1</Version>` publishes the
v0.0.1 release.

## M0 Scaffold
- Review: [x] done (per-task review, 0a01a66)
- [x] M0.1 Solution `ClippysRevenge.slnx` with the three projects (SPEC §3), package references from SPEC §2, one placeholder test. `dotnet build` and `dotnet test` pass with zero warnings. Core builds and tests on Linux.

## M1 Core helpers (SPEC §12) — deps: M0
- Review: [x] done (fbe093e, approve, 0 fix rounds)
- [x] M1.1 `ReplySubject`, `FileNames`, `Recipients` + tests `{arch:brief}`
- [x] M1.2 `FtsQuery` + tests `{arch:brief}`
- [x] M1.3 `SlotFormatter`, `TimeZoneLabels` + tests `{arch:brief}`
- [x] M1.4 `HtmlBody` + tests `{arch:brief}`
- [x] M1.5 `PathPolicy` + tests (junction test Windows-only) `{arch:brief}`
- [x] M1.6 `InviteGate` + tests `{arch:brief}`
- [x] M1.7 `Settings` model, defaults, `Validate()`, shared mailbox entries + tests `{arch:brief}`
- [x] M1.8 `FreeBusy.BusyIntervals` / `FreeSlots`, 28-day chunk joining + tests `{arch:brief}`

## M2 Index (SPEC §9) — deps: M1.2
- Review: [x] done (8fcc2ac, approve after 1 fix round: perf p95 23→10 ms)
- [x] M2.1 `EmailIndex` schema, migrations, external-content FTS + triggers, `UpsertBatch`, `Delete`, `SetUnread`, `Get` + tests `{arch:brief}`
- [x] M2.2 `EmailIndex.Search` with every filter, paging, snippet + tests `{arch:brief}`
- [x] M2.3 `PurgeOlderThan` (+ VACUUM keeps FTS working), `Status`, sync state, `Close` + tests `{arch:skip}`
- [x] M2.4 `MailboxIndexes` (per-mailbox files, `all` merge, `Locate`, add/disable/remove) + tests `{arch:brief}`
- [x] M2.5 Perf check: 50k synthetic emails, `search_emails` p95 < 20 ms (opt-in benchmark test, not in CI) `{arch:skip}`

## M3 Host plumbing (SPEC §7, §13) — deps: M0
- Review: [x] done (fbe093e, approve, 0 fix rounds)
- [x] M3.1 `BearerAuth` middleware + tests (Origin → 401, live token swap) `{arch:brief}`
- [x] M3.2 `ActivityHub` (ref-counted states, ring buffer, 10 Hz throttle) + tests `{arch:brief}`

## M4 App shell — deps: M1.7, M3
- Review: [x] done (440597e, approve after 1 fix round: log folder → %LOCALAPPDATA%)
- [x] M4.1 `Program`: single-instance mutex + named pipe, generic host, Kestrel on loopback, `MapMcp("/mcp")` behind `BearerAuth`, an `index_status` stub tool, tool-call activity filter `{arch:brief}`
- [x] M4.2 `SettingsStore` (settings.json, DPAPI token, first run, Run key) `{arch:skip}`
- [x] M4.3 Tray: `TrayApp` with the menu from SPEC §6.2 (handlers can be stubs), icon states from `ActivityHub`, original paperclip icons (generated `.ico` resources), balloons with rate limit `{arch:brief}`

## M5 Outlook core (SPEC §4, §8) — deps: M4
- Review: [ ] pending
- [ ] M5.1 `OutlookWorker`: STA thread, two priority queues, attach-only `GetActiveObject`, backoff, `OutlookUnavailable`, fail-fast for interactive work; tray "Start Outlook" `{arch:brief}`
- [ ] M5.2 `OutlookStores` (list mounted Exchange mailboxes, SMTP, `Get(mailbox)`) `{arch:skip}`
- [ ] M5.3 `OutlookMail` reads: `ReadForIndex`, `IsInline`, Table-based `EnumerateChanged` / `EnumerateRange` / `ListEntryIds`, the DASL date helper, `GetItem` fallback chain `{arch:brief}`

## M6 Sync (SPEC §10) — deps: M2, M5
- Review: [ ] pending
- [ ] M6.1 `SyncService`: changes pass, newest-first backfill, watermarks, progress, purge, all mailboxes, pause/resume, Sync now, rebuild `{arch:brief}`
- [ ] M6.2 Daily reconcile (delete missing, add missing, refresh unread); retention changes `{arch:brief}`

## M7 Mail tools (SPEC §11.1–11.6) — deps: M6
- Review: [ ] pending
- [ ] M7.1 `search_emails`, `get_message` (COM fallback), `index_status` (real) `{arch:skip}`
- [ ] M7.2 `get_attachment`, `save_attachments` (PathPolicy, UniquePath, inline handling) `{arch:brief}`
- [ ] M7.3 `Signatures`, `create_draft` (own + shared, attachments checked first) `{arch:brief}`
- [ ] M7.4 `reply_draft` (`Re:: `, HTML insert, shared mailbox drafts) `{arch:skip}`

## M8 Calendar tools (SPEC §11.7–11.9, §6.6) — deps: M5, M1.8, M1.6
- Review: [ ] pending
- [ ] M8.1 `list_events` (+ shared calendars) `{arch:skip}`
- [ ] M8.2 `GetFreeBusy` + `find_free_times` `{arch:brief}`
- [ ] M8.3 `InviteConfirmForm` + confirmation queue + `AwaitingConfirmation` state `{arch:brief}`
- [ ] M8.4 `CreateMeeting` + `send_meeting_invite` (all modes, Teams note, shared calendar) `{arch:brief}`

## M9 UI completion (SPEC §6) — deps: M6, M8.3
- Review: [ ] pending
- [ ] M9.1 Shared mailboxes submenu (check / uncheck with delete prompt / not-in-Outlook entries) `{arch:skip}`
- [ ] M9.2 `SettingsForm` (all tabs, validation messages, Test connection via `initialize`) `{arch:brief}`
- [ ] M9.3 `ActivityForm` (progress, filters, copy) `{arch:skip}`

## M10 Ship — deps: everything above
- Review: [ ] pending
- [ ] M10.1 Safety source-scan test (one `.Send(`, no Outlook launch calls) `{arch:skip}`
- [ ] M10.2 Live tests (`[Trait("live","outlook")]`), run once by the user with `CLIPPY_LIVE=1` `{arch:brief}`
- [ ] M10.3 `install.ps1` (install, Start menu shortcut, `-Uninstall`) and README (install, WSL mirrored networking, security model, index at rest) `{arch:skip}`
- [ ] M10.4 Final review of the whole branch by `reviewer`, CI green on the PR, then hand to the user to merge (→ v0.0.1 release) `{arch:skip}`

## Carry-forward notes from reviews

- M4.3: `ActivityHub.Changed` can fire on the caller's thread and on the timer thread at the same time, so the tray handler must only `BeginInvoke` to the UI thread. (M1/M3 review)
- M4.1, M4.2: `TokenHolder.Current` exposes the raw token by design; make sure it never reaches a log line or exception message. (M1/M3 review)
- M5.2: `Recipients.IsValidEmail` and `Settings.IsValidAddress` have different rules; unify them when SMTP addresses for shared mailboxes are resolved. (M1/M3 review)
- M10.1: add PathPolicy tests with junction path components written as `link.` and 8.3 short names (`LINK~1`). (M1/M3 review)
- SPEC drift (architect, next brief): §9.1 `ix_emails_folder_time` is now `(folder COLLATE NOCASE, event_time, sender_name, sender_email)` in code; `SettingsStore` lives in Core behind `ISecretProtector` (App supplies `DpapiProtector`), not in App as §3 says. Update the spec to match. (M2/M4 reviews)
- M6.1: write `meta` keys `mailbox_smtp`, `store_id`, `retention_months` (nothing writes them yet). (M2 review)
- MailboxIndexes small fixes, fold into M6.1 or M7.1: `Search` error should echo the caller's mailbox text, not the normalized key; `Add` must not open SQLite while holding `_lock`; constructor must `Close()` already-opened indexes if a later one throws; `SearchDates` must turn `ArgumentOutOfRangeException` (e.g. `9999-12-31`) into the standard `FormatException`. (M2 review)

## Log

Append one line per finished task: `YYYY-MM-DD M#.# <short summary> (<commit>)`.
2026-11-12 M0.1 Solution scaffold: slnx, Core/App/Tests projects, pinned packages, 2 tests (review: approve, 0 fix rounds)
2026-11-12 M1.1 implemented in parallel worktree, merged to dev (4208a5b)
2026-11-12 M1.2 implemented in parallel worktree, merged to dev (6af376a)
2026-11-12 M1.3 implemented in parallel worktree, merged to dev (0dd217b)
2026-11-12 M1.4 implemented in parallel worktree, merged to dev (ba2b9f9)
2026-11-12 M1.5 implemented in parallel worktree, merged to dev (1368b33)
2026-11-12 M1.6 implemented in parallel worktree, merged to dev (c1717b2)
2026-11-12 M1.7 implemented in parallel worktree, merged to dev (290dd06)
2026-11-12 M1.8 implemented in parallel worktree, merged to dev (391f309)
2026-11-12 M3.1 implemented in parallel worktree, merged to dev (bfa56cb)
2026-11-12 M3.2 ActivityHub: states, ring buffer, throttled Changed, 27 tests
2026-11-12 M1+M3 milestone review: approve, 590 tests, 6 non-blocking notes (see carry-forward)
2026-11-12 M2.1–M2.5 index: schema, search, purge/status, MailboxIndexes, perf benchmark (chain/M2 → 8fcc2ac)
2026-11-12 M4.1–M4.3 app shell: MCP host on loopback, SettingsStore + DPAPI, tray (chain/M4 → 440597e)
2026-11-12 M2 + M4 milestone reviews approved after 1 fix round each; dev at 946 tests passing
