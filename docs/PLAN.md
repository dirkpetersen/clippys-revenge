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
- Review: [x] covered by M10.4 (whole-branch review before v0.0.1)
- [x] M10.1 Safety source-scan test (one `.Send(`, no Outlook launch calls) `{arch:skip}`
- [x] M10.2 Live tests (`[Trait("live","outlook")]`), run once by the user with `CLIPPY_LIVE=1` `{arch:brief}` (code done 2f7cfbd; user run 2026-11-13: 27 passed, 3 skipped (no shared mailbox), 0 failed)
- [x] M10.3 `install.ps1` (install, Start menu shortcut, `-Uninstall`) and README (install, WSL mirrored networking, security model, index at rest) `{arch:skip}`
- [x] M10.4 Final review of the whole branch by `reviewer`, CI green on the PR, then hand to the user to merge (→ v0.0.1 release) `{arch:skip}`

## M11 AI drafting (SPEC §19, §1.10; user-approved 2026-11-13) — deps: M10
- Review: [x] done (ef4d7e8; 3 parallel reviewers, all APPROVED, non-blocking fixes applied)
- Parallel tracks: **A** M11.1 → M11.2 · **B** M11.3 (no deps, start any time) · **C** M11.1 → M11.4 → M11.5. M11.6 and M11.7 start when A, B and M11.4 are done (M11.5 can run beside them). M11.8 joins everything; M11.9–M11.11 come last.
- [x] M11.1 Settings and plumbing: `AiDraftingSettings` in `Settings` with defaults, `Validate()` (incl. the consent rule) and JSON (§19.2, §5.2); `SharedMailbox.AiDrafts`; `SettingsStore.LoadApiKey`/`SaveApiKey` over `ISecretProtector` (`anthropic-key.bin`, §5.1); log redaction of the API key and `sk-ant-…` (§5.1); `DraftMarker` constants (§19.4); `ActivityState.Drafting` (rank, tooltip, `Report`) and `ActivityKind.Ai` (§6.1, §6.4, §7) `{arch:brief}` (deps: none)
- [x] M11.2 Index schema 2 and the draft marker: migration 1→2, `ai_decisions`, new `EmailIndex` members and 30-day decision purge (§9.1, §9.2); `ReadForIndex` thread properties + marker (§8); Table-only thread-metadata refresh (§10); `ClippyDraft = tool` on `create_draft`/`reply_draft`, marker and work priority on `ReplyPlan`/`OutlookReplyDraftWriter` (§8, §19.9); live draft tests assert the marker `{arch:brief}` (deps: M11.1)
- [x] M11.3 `MailText` (own-text extraction, redaction, header parsing) and `DraftHtml` (allow-list sanitizer, placeholder highlight) + tests (§19.6, §19.8) `{arch:brief}` (deps: none; parallel with M11.1, M11.2, M11.4)
- [x] M11.4 `IDraftModel` and `DraftModel` (request/result types, reply schema, `DraftReply.Parse`), Claude Code CLI backend: `ClaudeCliLocator`, status probe, `ClaudeCliArgs`, `ClaudeCliRunner`, `ClaudeCliOutput` + fake-`claude` tests (§19.3) `{arch:brief}` (deps: M11.1)
- [x] M11.5 API-key backend `AnthropicApiModel` (official SDK, pinned; `output_config` effort + json_schema, no `thinking`, no tools; `CheckAsync` via the models endpoint) + tests with a fake HTTP handler (§19.3) `{arch:brief}` (deps: M11.4; parallel with M11.6, M11.7)
- [x] M11.6 Writing-style skill: `StyleCorpus` sampling, style system prompt, generation, `SKILL.md` + copy + `state.json` writer with edit detection, automatic weekly trigger, skill reading for prompts (§19.6) `{arch:brief}` (deps: M11.2, M11.3, M11.4)
- [x] M11.7 Drafting pipeline: `DraftRules`, `DraftPrompt`, `DraftWorker` (candidates, COM facts, daily limit, backoff, decisions, events, `Drafting` state, manual path) (§19.7); `SyncService.CycleCompleted` (§10); `OutlookAiMailbox` (§19.9); wiring in `Program` `{arch:brief}` (deps: M11.2, M11.3, M11.4; uses M11.6 for the style block, falls back to "No style guide yet" if it isn't merged yet)
- [x] M11.8 Tray and Settings UI: AI drafts submenu, consent dialog, drafts balloon with click → `ShowDrafts`, AI drafts tab (status + Check, key save/remove, model/effort/limits/lists, per-mailbox opt-in, style View/Regenerate/Open in editor, today's count), manual draft action (§6.2, §6.3, §6.5, §19.10) `{arch:brief}` (deps: M11.5, M11.6, M11.7)
- [x] M11.9 Safety scan additions (§19.12, §15) and the test filter `live!=outlook&live!=claude` in `ci.yml`, `release.yml`, `CLAUDE.md` and the README (§16) `{arch:skip}` (deps: M11.4, M11.7)
- [x] M11.10 Live tests: `[Trait("live","claude")]` CLI status + one tiny structured call; Outlook + Claude manual-draft round trip (draft in Drafts, `Re:: `, marker `ai`, sanitized body, Sent/Outbox unchanged) (§19.12). Run once by the user with `CLIPPY_LIVE=1` and `CLIPPY_LIVE_CLAUDE=1` `{arch:brief}` (deps: M11.8)
- [x] M11.11 User docs: `manual/ai-drafts.md` (setup, consent, what is sent, rules, style skill, limits, troubleshooting), data flow in `manual/security.md`, `tray.md`, `faq.md`, `changelog.md`, `zensical.toml` nav, README; `zensical build --clean --strict` passes (§19, CLAUDE.md docs rule) `{arch:skip}` (deps: M11.8)

## M12 Auto-update and About window (SPEC §20, §6.7, §6.2, §5.2; user request 2026-11-13) — deps: M11
- Review: [x] done (e117079, 4eb4b96; 2 reviewers: 2 blocking fixed, re-review APPROVED)
- Parallel tracks: **A** M12.1 → M12.2 · **B** M12.1 → M12.3 (M12.2 and M12.3 run side by side; neither uses the other's types). M12.4 joins A and B. M12.5 comes after M12.4. M12.6 is the user's check after the next release.
- [x] M12.1 Core update primitives: `SemVersion`, `AppVersionInfo` (§20.2); `UpdateEndpoints`, `ReleaseParser`, `Sha256Sums`, `UpdateHash` (§20.3–§20.5); `UpdateMode`, `UpdatePaths`, `UpdatePolicy`, `UpdateState` + `UpdateStateStore` (§20.6–§20.8); `UpdateTexts`, `UpdateStatus`, `AboutView` (§6.7, §20.10); `Settings.Updates` (§5.2); `ActivityKind.Update` (§6.4); `ActivityHub.IsActive` (§7); update balloons (§6.5) + tests `{arch:brief}` (deps: none)
- [x] M12.2 `UpdateClient` (GitHub lookup, verified download, redirect allow-list, caps, stall timeout) and `UpdateService` (weekly due check, notify/stage, start-up-window install when idle, Check for updates, Update now, `Status`/`Changed`) + fake-handler and fake-clock tests, opt-in live GitHub lookup test (§20.3–§20.5, §20.8) `{arch:brief}` (deps: M12.1)
- [x] M12.3 `UpdateInstall` (swap), `UpdateHandover` (confirm wait, kill, rollback, restart over `IUpdateProcess`), `UpdateStartup` (pending resolution, cleanup, install-now decision, confirm, `.old` cleanup) + temp-folder tests and a Windows-only "rename a running exe" test (§20.9) `{arch:brief}` (deps: M12.1; parallel with M12.2)
- [x] M12.4 App: `UpdateRestarter` (the one updater `Process.Start`, `IUpdateProcess`, exe `ProductVersion`), `Program` wiring (start-up install before anything runs, confirm after the tray and server are up, notices, `UpdateService`, exit-for-update and handover after shutdown), tray **About Clippy's Revenge…**, `AboutForm`, Settings → General **Updates** + **Check now**, safety-scan additions (§3, §6.2, §6.3, §6.7, §14, §15, §20.9, §20.11) `{arch:brief}` (deps: M12.2, M12.3)
- [x] M12.5 User docs: new `manual/updates.md` (how and when it updates, the three modes, what is sent, the About window, rollback, manual update with `install.ps1`, trust model), `tray.md` (About item, General → Updates), `security.md` (outbound connections, update trust model, unsigned exe), `install.md` ("Install or update"), `troubleshooting.md` (update failed / rolled back / exe missing → run `install.ps1`), `faq.md` (turn updates off), `changelog.md` (entry for the version that ships M12), `zensical.toml` nav, README; `zensical build --clean --strict` passes (§20, §6.7, CLAUDE.md docs rule) `{arch:skip}` (deps: M12.4)
- [ ] M12.6 End-to-end check by the user: with the M12 release installed via `install.ps1`, publish the next version; delete `%LOCALAPPDATA%\ClippysRevenge\update-state.json`, restart Clippy, and within ~2 minutes see the "I got an upgrade" balloon and the new version in About; `ClippysRevenge.exe.old` is gone. Record the result in the Log `{arch:skip}` (deps: M12.5 released)

## M13 Meeting times in drafts (SPEC §21, §11.8, §19.2, §19.6, §19.7, §19.10; user-approved 2026-11-14) — deps: M12
- Review: [ ] pending
- Parallel tracks: **A** M13.1 → M13.2 · **B** M13.3 (no deps, beside M13.1) · **C** M13.5 (no deps, any time). M13.4 joins A and B. M13.6 after M13.3. M13.7 after M13.4 and M13.5. M13.8 docs, M13.9 the user's check last.
- [x] M13.1 Core meeting-times primitives: `OwnerCalendar.FromFreeBusy`, `MeetingRanking` (tiers, 15-minute adjacency), `MeetingOffers` (exact-length candidates, selection, Europe office-hours split with continued numbering and the heading rules, `FormatLine` `1. Tue 11/18  10:00–11:00 PT  (19:00–20:00 CET)`, text form), `SenderRegion` (Date offset + TLD decision table, `CentralEurope()`), `MeetingTimesHtml.Insert` (placeholder variants, breaks, no-placeholder paragraph rule, failure placeholders) + tests incl. split cases all-in-hours / mixed / none-in-hours (§21.2–§21.6, §21.9) `{arch:brief}` (deps: none)
- [~] M13.2 `find_free_times`: ranked `suggestions` with `adjacent`, `offers`/`offer_text`, `offer_cet` argument and description; `IFreeBusySource` methods take a `WorkPriority` (default `Interactive`), `OutlookFreeBusySource` passes it through (§11.8, §21.8) `{arch:brief}` (deps: M13.1)
- [x] M13.3 Model contract: `meeting` in `DraftModel.ReplySchema`, `DraftReply.Meeting` (`MeetingAsk`) with tolerant parsing, the meeting rules in the drafting system prompt (only while `MeetingTimes` is on; exact text pinned in the brief), `AiDraftingSettings.MeetingTimes` / `MeetingTimeOptions` with validation and JSON (§19.2, §19.7, §21.6) `{arch:brief}` (deps: none)
- [ ] M13.4 Drafting integration: `DraftWorker` step 6a in automatic and manual paths (attendees, range, Europe from facts headers / manual `GetFactsAsync`, free/busy at the path's priority, insertion, nothing-to-offer placeholders, Outlook-gone handling, Debug log with counts only); `DraftWorkerServices.FreeBusy`; wiring in `Program` (§19.7, §21.6, §19.11) `{arch:brief}` (deps: M13.1, M13.2, M13.3)
- [~] M13.5 Style skill meeting section: `StyleSkillText.MeetingSection` (fixed v1 text with markers), `Render` and the new description, optional `meeting_phrasing` in the style schema and prompt with validation, `StyleSkillFiles.RefreshMeetingSection` (unedited skills, no model call, state kept except hash) at app start and in `RunIfDueAsync`, `ReadBodyForPrompt` drops the marker block; tests for hash/edit detection and refresh (§19.6, §21.7) `{arch:brief}` (deps: none)
- [~] M13.6 Settings → AI drafts tab: *Offer meeting times when an email asks for a meeting* and *Number of times to offer* (1–10), staged like the other AI fields (§19.10) `{arch:skip}` (deps: M13.3)
- [ ] M13.7 Live tests: `find_free_times` with `offer_cet=true` returns §21.5-shaped `offer_text`; Outlook + Claude manual draft of a synthetic "can we meet next week for 30 minutes?" email has option lines and no `[[meeting_times]]` left; nothing sent, only own items deleted (§21.9) `{arch:brief}` (deps: M13.4, M13.5)
- [ ] M13.8 User docs: `manual/ai-drafts.md` (meeting times, Europe/CET rules and limits, the two settings, the skill section and how existing skills get it), `manual/calendar.md` and `manual/tools.md` (`find_free_times` ranking, `offers`, `offer_cet`), `faq.md`, `changelog.md`, README; `zensical build --clean --strict` passes (§21, CLAUDE.md docs rule) `{arch:skip}` (deps: M13.4, M13.5, M13.6)
- [ ] M13.9 End-to-end check by the user: after the M13 release, restart Clippy and confirm `%USERPROFILE%\.claude\skills\my-email-style\SKILL.md` ends with "Offering meeting times" (unedited skill) and that Settings → Regenerate keeps it; send yourself a meeting request from a European address (or a `.de`/`.fr` test account) and from a US one, use **Draft a reply to the selected email**, and check the options (busy-adjacent first, CET only for the European sender, second list when needed, one line per option, deletable); in Claude Code ask for times for an email and check it calls `find_free_times` and pastes `offer_text`. Record the result in the Log `{arch:skip}` (deps: M13.8 released)

## M14 Index statistics and read-only SQL (SPEC §11.11, §11.12, §17; user-approved 2026-11-14) — deps: M12
- Review: [ ] pending
- Parallel tracks: **A** M14.1 · **B** M14.2 (independent; neither needs M13). M14.3 joins A and B. M14.4 docs, M14.5 the user's check last.
- [~] M14.1 `email_stats` Core: one shared WHERE builder for `Search` and stats (no second filter mapping), `EmailStats` (all `group_by` values, recipient splitting with `include_cc`, `order`, `top`, `mailbox=all` merge, `total`/`distinct`/`truncated`, local time buckets with an injected zone) + tests and a `[PerfFact]` 100k-row benchmark (§11.11, §17) `{arch:brief}` (deps: none)
- [~] M14.2 `query_index` Core: `IndexQuery` (read-only, non-pooled connection, temp views `mail`/`mail_fts`/`mail_attachments`, `query_only`, limits, SQLitePCLRaw authorizer + progress handler, one-statement and read-only checks, 10 s deadline, row/cell/size caps, result shaping, error texts), `IndexQueryAuthorizer.Decide` table tests, FTS `MATCH`/`snippet`/`highlight`/`bm25`/`rank` through the view, file unchanged after queries (§11.12) `{arch:brief}` (deps: none)
- [ ] M14.3 App tools: `email_stats` and `query_index` in `McpTools` with the pinned descriptions (views, limits, examples), `mailbox` handling (`all` per mailbox), no SQL or cell text in logs; safety scan: no logger call in `IndexQuery.cs`/`EmailStats.cs` takes the SQL or cell values (§11.11, §11.12, §5.1, §15) `{arch:brief}` (deps: M14.1, M14.2)
- [ ] M14.4 User docs: `manual/tools.md` (both tools, view reference, examples), `security.md` (same data as `search_emails`/`get_message`, read-only layers), `faq.md` ("top senders", "mail per month"), `changelog.md`, README; `zensical build --clean --strict` passes `{arch:skip}` (deps: M14.3)
- [ ] M14.5 End-to-end check by the user: in Claude Code ask "top 10 people who emailed me in the last year, with counts" (one `email_stats` call, under a second) and "find mails about the budget from last spring with snippets" (`query_index` with FTS); try a write (`DELETE FROM mail`) and a `PRAGMA` and confirm both are refused. Record the result in the Log `{arch:skip}` (deps: M14.4 released)

## M15 Draft-only meeting invites (SPEC §22, §1.4, §6.1, §6.2, §6.6, §8, §11.9, §15; user-approved 2026-11-14, incl. the hard-rule change) — deps: M13
- Review: [ ] pending
- Parallel tracks: M15.1 first. Then **A** M15.2 → M15.4 · **B** M15.3 (beside M15.2) · **C** M15.2 → M15.5 (beside M15.4). M15.6 after M15.2 and M15.3. M15.7 docs, M15.8 the user's check last.
- [ ] M15.1 Remove sending: `send_meeting_invite` → `create_meeting_draft` (no `teams`, new result and texts), `OutlookCalendar.CreateMeeting` `.Save()` only, `MeetingInviteMode` without `Send` (stored `Send` → `DraftOnly`, `InviteConfirmTimeoutSeconds` ignored), delete `InviteGate`, the confirmation queue, `InviteConfirmForm`, `ActivityState.AwaitingConfirmation` and its icon; safety scan: zero `.Send(` in `src/`; update **CLAUDE.md** hard rule to the approved wording ("Never send anything. …", user-approved 2026-11-14), the agent files, README and the Settings invite-mode control (§1.4, §5.2, §22.1) `{arch:brief}` (deps: M13 done)
- [ ] M15.2 Category and tracking: `Clippy draft invite` on created meetings (master list entry added once, orange, only when missing), `DraftInvitesStore` (`draft-invites.json`), `DraftInvites.CheckAsync` over an `IDraftInviteCalendar` COM seam (`PidLidFInvited`, category removal keeping other categories, Categories-only `.Save()`, skip items open in an Inspector, deleted → drop with no write, 30-day expiry, 50 per check), triggers after sync cycles (≤ every 5 min, Background) and from `create_meeting_draft`; tests for sent / unsent / category already gone / deleted (§22.2, §22.3) `{arch:brief}` (deps: M15.1)
- [ ] M15.3 Note in Drafts: `reply_to_id` on `create_meeting_draft` (checked before creating), the reply draft with the exact highlighted note text through the normal reply path (marker `tool`), `note_error`; skill meeting section v2 (`create_meeting_draft` bullet) picked up by `RefreshMeetingSection`; tool description on booking an offered option (§22.4, §22.5) `{arch:brief}` (deps: M15.1; parallel with M15.2)
- [ ] M15.4 Tray **Draft invites (n)** submenu: last known list at once, Interactive check on menu open, lines sorted by start, disabled lines + "Start Outlook to open them" while Outlook is down, click → `OpenDraftInvite` (`.Display()`, the scan's single allowed `.Display(`), gone → balloon and drop (§6.2, §22.3) `{arch:brief}` (deps: M15.2)
- [ ] M15.5 Attention badge: Core `AttentionCount` (generic sources, draft invites only for now, last count kept while Outlook is unavailable), `AttentionBadge.Text`/`Layout` for 16/20/24/32 px with tests; App `TrayBadgeRenderer` (GDI+ composition over every state icon and animation frame, cache per (icon, frame, text), all GDI objects disposed, `DestroyIcon`), tooltip suffix; Windows-only GDI-handle leak test (§22.6) `{arch:brief}` (deps: M15.2; parallel with M15.4)
- [ ] M15.6 Live tests: the M10.2 `DraftOnly` meeting test becomes `create_meeting_draft` (unsent, `FInvited` false, category set, Categories-only save keeps it unsent, note draft with `reply_to_id`, Outbox/Sent unchanged, only own items deleted); never sends (§22.7) `{arch:brief}` (deps: M15.2, M15.3)
- [ ] M15.7 User docs: `manual/calendar.md` and `tools.md` (`create_meeting_draft`, no sending, add the link in Outlook, the category, Draft invites, the badge, the note), `tray.md` (menu, badge, invite mode without Send, confirmation dialog removed), `security.md` (Clippy never sends anything), `faq.md`, `changelog.md`, README; `zensical build --clean --strict` passes `{arch:skip}` (deps: M15.4, M15.5)
- [ ] M15.8 End-to-end check by the user: ask Claude Code to make the invite for option 2 of an AI draft's times; check the meeting is unsent in the calendar with the orange "Clippy draft invite" category, the note reply is in Drafts, the tray shows the yellow badge "1" and **Draft invites (1)**; open it from the tray, add a Teams/Zoom link and send it; within ~5 minutes (or on opening the menu) the category is gone, any other category you set stays, and the badge disappears. Record the result in the Log `{arch:skip}` (deps: M15.7 released)

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
- ~~Verify live (M10.2): calendar DASL local vs UTC~~ resolved: Outlook compares in UTC; fixed in 8ac84bc.
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
2026-11-13 M10.4 final review fixes merged (ec9d88c, 3a6bd12); app smoke test vs cached Outlook OK (attach, backfill, search_emails, get_message, list_events 1.6 s, sender SMTP 50/50)
2026-11-13 Live run 1: 23 passed, 4 failed -> fixed calendar DASL in UTC + free/busy code 5 (8ac84bc), long-term table EntryIDs (a264667), attachment test folder clash (89706dd); 1788 tests
2026-11-13 Live run 2: 27 passed, 3 skipped (shared mailbox), 0 failed; PR dev -> main opened
2026-11-13 CI fixed (.slnx detection, 4b4a956); PR #1 green: build-windows, core-tests-linux; handed to the user to merge
2026-11-13 M11.1 AI settings, API key store, redaction, DraftMarker, Drafting state (e8a7377); M11.3 MailText + DraftHtml sanitizer (995e0e7); built in parallel, 2081 tests
2026-11-13 M11.4 IDraftModel + Claude Code CLI backend, fake-claude tests (ede2804); 2208 tests
2026-11-13 M11.2 index schema 2 + migration, ai_decisions, thread metadata, ClippyDraft marker (ee4e861); 2284 tests
2026-11-13 M11.5 Anthropic API-key backend on SDK 13.3.0, wire-schema reduction, fake-handler tests (dfac1a3); 2371 tests
2026-11-13 M11.6 writing-style skill: corpus, generation, SKILL.md + copy + state.json, weekly trigger (6e5551d); 2446 tests
2026-11-13 M11.7 drafting pipeline: DraftRules, DraftPrompt, DraftWorker, DraftModelSwitch, OutlookAiMailbox, wiring (5225e76); StyleGuideSource adapter wires M11.6 into M11.7; 2593 tests
2026-11-13 M11.9 AI safety scans + live!=claude test filter in CI, release, CLAUDE.md, README, agent files (dd317d8); 2599 tests
2026-11-13 M11.7 fix: manual draft never throws; style generation outside the pass gate (0e98b40, found by spec sync); 2606 tests
2026-11-13 M11.8 AI drafts tray submenu, consent dialog, settings tab, manual draft, balloon action (a1bbaf5); UI not click-tested yet; 2676 tests
2026-11-13 M11.11 user docs: ai-drafts page, security data flow, tray, FAQ, changelog v0.0.5, README (8bdb69c); zensical strict build passes
2026-11-13 M11.10 live Claude tests + AI manual-draft round trip, run-live-tests.ps1 -Claude (5f1df1e); not run yet (user runs once); 2706 tests
2026-11-13 M11 review: 3 reviewers (safety/App, Core drafting, backends/settings/docs) APPROVED; 12 non-blocking fixes (ef4d7e8); 2719 tests; release v0.0.5
2026-11-13 M12.1 auto-update Core basics: SemVersion, release/SHA256SUMS parsing, update state, texts, About model, Updates setting, IsActive (86323b6); 2830 tests
2026-11-13 M12.3 exe swap, handover with rollback, start-up decision (e10ebd6); 2869 tests
2026-11-13 M12.2 UpdateClient (lookup, verified download) + UpdateService (weekly check, stage, notify, manual) (4112011); 2939 tests
2026-11-13 M12.4 About window, UpdateRestarter, start-up install + handover wiring, Settings Updates (9716735); M12.5 user docs (6c59282); 2944 tests
2026-11-13 M12 review: updater-security + App-wiring reviewers; blocking B1 port handover, B2 confirm write race fixed (e117079); re-review APPROVED; follow-ups (4eb4b96); 2961 tests; release v0.0.6
2026-11-14 M13.3 model contract for meeting times: ReplySchema meeting, MeetingAsk, prompt note, settings (0bd9587); 3015 tests
2026-11-14 M13.1 meeting-times primitives: ranking, offers, sender region, HTML insertion (e34533e); 3117 tests
