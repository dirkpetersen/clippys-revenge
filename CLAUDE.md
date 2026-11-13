# CLAUDE.md

Guidance for Claude Code in this repository.

## What this is

Clippy's Revenge: a Windows tray app (C#, .NET 10) that exposes classic
Outlook to Claude Code as an MCP server over `http://127.0.0.1:<port>/mcp`,
driving Outlook through COM. **`SPEC.md` is the source of truth.** If code
and spec disagree, the spec wins. Anything the spec doesn't list is out of
scope until it's added to the spec first.

The Python prototype at <https://github.com/dirkpetersen/outlook-mcp> is
reference material only.

## Hard rules — never relax these

- **Never send email.** `MailItem` drafts only ever `.Save()`. The single
  `.Send()` in the codebase is in `OutlookCalendar.CreateMeeting`, and it
  only runs after `InviteGate` says `Send`, which needs the user's click in
  `InviteConfirmForm` (SPEC §6.6, §11.9). A test scans the source for this.
- **Never delete email.** The app never calls `Delete`, `PermanentDelete`
  or `EmptyDeletedItems` on Outlook items or folders, and never moves
  existing mail. The one `.Move(` puts a reply draft Clippy just created
  into Drafts. Removing rows from Clippy's own SQLite index is allowed:
  that's a catalogue, not mail. A test scans the source for this. (Only the
  live test suite deletes items, and only the ones it created in that run.)
- **Never launch Outlook.** Attach with `GetActiveObject` only. No
  `new Outlook.Application()`, `Activator.CreateInstance` or
  `CoCreateInstance`. The tray's **Start Outlook** item is the one exception
  (SPEC §4).
- **Reply subjects start with `Re:: `** (double colon) via
  `ReplySubject.Normalize`. This is intentional. Don't "fix" it.
- **No Graph API**, no Entra app registrations, no OAuth.
- **Attachment content never enters the index.** Only file names.
- **Localhost-only and token-gated.** Bind `IPAddress.Loopback` only, keep
  `BearerAuth` with a constant-time compare, reject requests with an `Origin`
  header, and never log the token.
- **Every file path from a tool goes through `PathPolicy`.**
- **No WSL interop dependency.** WSL talks to the app over TCP only.
- **Stay small:** one app project, one Core library, one test project. All
  logic that doesn't need COM, the registry or WinForms goes in Core.

## Layout

```
src/ClippysRevenge.Core/    net10.0, pure logic, tested on Linux too
src/ClippysRevenge.App/     net10.0-windows WinExe: COM, tray, Kestrel host
tests/ClippysRevenge.Core.Tests/
docs/PLAN.md                milestones and the task checklist the loop works from
docs/QUESTIONS.md           open questions for the user (the loop writes here)
docs/briefs/                architect briefs, one per task
```

## Commands

```bash
dotnet build -c Release
dotnet test -c Release --filter "live!=outlook"      # what CI runs
dotnet test tests/ClippysRevenge.Core.Tests          # Core only (also works in WSL)
dotnet publish src/ClippysRevenge.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

**Live Outlook tests** (`[Trait("live","outlook")]`) touch the user's real
mailbox (they create drafts and a `DraftOnly` meeting). Run them only when
the user asks, with `CLIPPY_LIVE=1` set. They must never send anything.

## Version and release

`<Version>` in `Directory.Build.props` is the product version. Every push
to `main` releases `v<Version>` if that tag doesn't exist yet (see
`.github/workflows/release.yml`). So **never push to `main` directly**.
Work happens on a branch and lands through a PR the user merges.

## Agent roles

Defined in `.claude/agents/`:

| Agent | Model | Job |
|---|---|---|
| `architect` | Opus | Turns a `docs/PLAN.md` task into a brief in `docs/briefs/`: files, types, signatures, test cases, spec references. Owns `SPEC.md`, but hard rules change only with user approval. Writes no product code. |
| `coder` | Sonnet | Implements exactly one brief, with tests. Builds and tests before handing back. Doesn't edit `SPEC.md`. |
| `reviewer` | Opus, medium effort | Runs **once per milestone**, over the milestone's whole commit range, against the briefs, `SPEC.md` and the hard rules. Read-only. Returns blocking and non-blocking findings, which the coder fixes in one more commit. |

Each task in `docs/PLAN.md` carries an `{arch:brief}` or `{arch:skip}`
tag. `arch:skip` tasks go straight to the coder, which works from the
task line and the SPEC sections it cites.

The loop that ties them together is `/dev-cycle` (`.claude/commands/dev-cycle.md`).
Run it once, or repeatedly with `/loop /dev-cycle`.

## Conventions

- C#: file-scoped namespaces, `Nullable` on, warnings are errors, `async`
  all the way, no `.Result`/`.Wait()` outside the COM worker.
- COM objects from enumerations are released with `Marshal.ReleaseComObject`
  inside the loop.
- Tool errors are `McpException`s with a message the model can act on.
- Tests use xUnit. Core tests must not need Windows unless marked
  `[Trait("os","windows")]`.
- Commits: `M<milestone>.<task>: <summary>`, small, one task per commit.
