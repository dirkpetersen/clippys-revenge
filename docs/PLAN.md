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
- Review: [x] done (25628e1, approve after 2 fix rounds)
- [x] M5.1 `OutlookWorker`: STA thread, two priority queues, attach-only `GetActiveObject`, backoff, `OutlookUnavailable`, fail-fast for interactive work; tray "Start Outlook" `{arch:brief}`
- [x] M5.2 `OutlookStores` (list mounted Exchange mailboxes, SMTP, `Get(mailbox)`) `{arch:skip}`
- [x] M5.3 `OutlookMail` reads: `ReadForIndex`, `IsInline`, Table-based `EnumerateChanged` / `EnumerateRange` / `ListEntryIds`, the DASL date helper, `GetItem` fallback chain `{arch:brief}`

## M6 Sync (SPEC §10) — deps: M2, M5
- Review: [x] done (63ac9b4, approve after 1 fix round)
- [x] M6.1 `SyncService`: changes pass, newest-first backfill, watermarks, progress, purge, all mailboxes, pause/resume, Sync now, rebuild `{arch:brief}`
- [x] M6.2 Daily reconcile (delete missing, add missing, refresh unread); retention changes `{arch:brief}`

## M7 Mail tools (SPEC §11.1–11.6) — deps: M6
- Review: [x] done (188c405, approve after 1 fix round)
- [x] M7.1 `search_emails`, `get_message` (COM fallback), `index_status` (real) `{arch:skip}`
- [x] M7.2 `get_attachment`, `save_attachments` (PathPolicy, UniquePath, inline handling) `{arch:brief}`
- [x] M7.3 `Signatures`, `create_draft` (own + shared, attachments checked first) `{arch:brief}`
- [x] M7.4 `reply_draft` (`Re:: `, HTML insert, shared mailbox drafts) `{arch:skip}`

## M8 Calendar tools (SPEC §11.7–11.9, §6.6) — deps: M5, M1.8, M1.6
- Review: [x] done (da36574, approve after 1 fix round)
- [x] M8.1 `list_events` (+ shared calendars) `{arch:skip}`
- [x] M8.2 `GetFreeBusy` + `find_free_times` `{arch:brief}`
- [x] M8.3 `InviteConfirmForm` + confirmation queue + `AwaitingConfirmation` state `{arch:brief}`
- [x] M8.4 `CreateMeeting` + `send_meeting_invite` (all modes, Teams note, shared calendar) `{arch:brief}`

## M9 UI completion (SPEC §6) — deps: M6, M8.3
- Review: [x] done (edcfb50, approve after 2 fix rounds + 1 extra ordering fix; M9.6 added after the review, covered by M10.4)
- [x] M9.1 Shared mailboxes submenu (check / uncheck with delete prompt / not-in-Outlook entries) `{arch:skip}`
- [x] M9.2 `SettingsForm` (all tabs, validation messages, Test connection via `initialize`) `{arch:brief}`
- [x] M9.3 `ActivityForm` (progress, filters, copy) `{arch:skip}`
- [x] M9.4 Rolling file log: `Microsoft.Extensions.Logging` to `%LOCALAPPDATA%\ClippysRevenge\logs\clippy-YYYYMMDD.log`, 7 days kept, level from `LogLevel` setting; never the token, bodies or search text (SPEC §2, §5.1, §6.4). Found missing in the 2026-11-12 smoke test: no task built it. `{arch:skip}`
- [x] M9.5 COM call watchdog: a single hung Outlook call must not block the worker forever. Interactive calls fail fast with an actionable error after a timeout ("Outlook isn't responding…"), the tray shows it, and background work resumes when Outlook answers again. Also: avoid per-call GAL lookups (`GetExchangeUser().PrimarySmtpAddress`) in `OutlookStores.List`; cache per connection and prefer local properties. Needs a SPEC §4 change. `{arch:brief}`
- [x] M9.6 Offline-safe sender SMTP: in `OutlookMail` read paths (`ReadForIndex`, ~OutlookMail.cs:374/435) prefer `PR_SENDER_SMTP_ADDRESS` / `PR_SENT_REPRESENTING_SMTP_ADDRESS` via `PropertyAccessor` and only fall back to `GetExchangeUser()` (GAL) when empty, so a cached-mode offline sync doesn't hit the watchdog every cycle. SPEC §8. From the M9.5 brief and M9 review. `{arch:skip}`
## M10 Ship — deps: everything above
- Review: [ ] pending
- [x] M10.1 Safety source-scan test (one `.Send(`, no Outlook launch calls) `{arch:skip}`
- [x] M10.2 Live tests (`[Trait("live","outlook")]`), run once by the user with `CLIPPY_LIVE=1` `{arch:brief}` (code done 2f7cfbd; user run pending: `powershell -NoProfile -File scripts\run-live-tests.ps1`)
- [x] M10.3 `install.ps1` (install, Start menu shortcut, `-Uninstall`) and README (install, WSL mirrored networking, security model, index at rest) `{arch:skip}`
- [~] M10.4 Final review of the whole branch by `reviewer`, CI green on the PR, then hand to the user to merge (→ v0.0.1 release) `{arch:skip}`

## Carry-forward notes from reviews

- M4.3: `ActivityHub.Changed` can fire on the caller's thread and on the timer thread at the same time, so the tray handler must only `BeginInvoke` to the UI thread. (M1/M3 review)
- M4.1, M4.2: `TokenHolder.Current` exposes the raw token by design; make sure it never reaches a log line or exception message. (M1/M3 review)
- M5.2: `Recipients.IsValidEmail` and `Settings.IsValidAddress` have different rules; unify them when SMTP addresses for shared mailboxes are resolved. (M1/M3 review)
- M10.1: add PathPolicy tests with junction path components written as `link.` and 8.3 short names (`LINK~1`). (M1/M3 review)
- M6.1: write `meta` keys `mailbox_smtp`, `store_id`, `retention_months` (nothing writes them yet). (M2 review)
- MailboxIndexes small fixes, fold into M6.1 or M7.1: `Search` error should echo the caller's mailbox text, not the normalized key; `Add` must not open SQLite while holding `_lock`; constructor must `Close()` already-opened indexes if a later one throws; `SearchDates` must turn `ArgumentOutOfRangeException` (e.g. `9999-12-31`) into the standard `FormatException`. (M2 review)

- COM release on error paths (M5 review): `OutlookMail.WithTable` (table leaks if column setup throws), `ResolveFolder` (child/match leak on non-gone errors), `OutlookStores.List` (`session.Stores` outside try, enumerator RCW). `FindByMessageId`: `OpenFolder` outside try. Fold into M10.1 or a cleanup task.
- Perf (M5/M8 reviews): `OutlookStores.Get` runs `List()` (GAL lookup per store) on every call; cache per worker connection.
- M6 review: `NotifySettingsChanged` cancels the running cycle on every settings change (should only for RetentionMonths); own-mailbox tag is `""` in sync events but `null` in reconcile/retention events; `RebuildAsync` may run `Reset` on the UI thread; no engine-level reconcile test.
- M7 review: duplicate search-filter mapping (`MailQueries.Search` vs `SearchArgs.ToFilter`); duplicated mailbox validation in CreateDraft/ReplyDraft; `index_status` with `all`/unknown mailbox throws; `Optional<T>` and reply-draft Move-before-Save need SPEC §3/§8 notes.
- M8 review: `list_events` `truncated` true at exactly 500; shared organizer name should fall back to store display name; `owner.Resolve()` result ignored; `InviteConfirmPresenter` leaks form if Show throws.
- **Verify live (M10.2):** `CalendarEvents` builds DASL dtstart/dtend in local time; Outlook may compare in UTC (would shift the list_events window).
- Flaky: `ComWorkerTests.StartsUnavailableAndFailsFast` failed 3/3 once on chain/A, passed after a clean rebuild. Investigate in M10.1.

- M9 review (non-blocking): ComWorker Tick/AbandonedExit race window when a hung call returns; interactive `LookupOwnerSmtp` timeout counts queue wait; an item dequeued near its deadline can flip the worker to NotResponding; `SharedMailboxToggler` waits on `Settings.Gate` on the UI thread (blocks during a port-change restart); log files get a UTF-8 BOM; `ConnectionTester` may leave MCP sessions open.
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
2026-11-12 M5.1 OutlookWorker: Core ComWorker + attach-only App attacher, Start Outlook launcher, 65 tests
2026-11-12 M5.2–M8.4 built in 3 parallel tracks, M5/M6/M7/M8 reviews approved; merged to dev, 1618 tests
2026-11-12 Smoke test vs cached Outlook: auth OK (401 no token, 401 Origin, 200 token), loopback-only bind, 10 tools listed; sync indexed 0 emails, list_events hung 180 s, no log file; Outlook itself then showed Not Responding. Added M9.4, M9.5.
2026-11-12 M9.1–M9.6, M10.1, M10.3 built in parallel; M9 review approved after fixes; merged to dev, 1776 tests, ComWorker tests 5/5 runs green
2026-11-12 M10.2 live suite (30 tests, skip without CLIPPY_LIVE) + runner merged; 1780 tests; M10.4 final review started
