---
description: Run one development step for Clippy's Revenge: a pending milestone review if one is due, otherwise the next task in docs/PLAN.md (architect if needed → coder → commit). Pushes to dev.
---

Run one development cycle for Clippy's Revenge. Follow these steps exactly.

1. **Preflight.**
   - `git switch dev`. Never commit or push to `main`.
   - The working tree must be clean. If it isn't, stop and report.
   - `dotnet --version` must report a 10.x SDK. If not, stop and tell the
     user to install it.
2. **Milestone review due?** If a milestone has all tasks `[x]` and its
   `Review:` line is `[ ] pending`, do the review instead of a task:
   - Find the range: `<base>` is the commit before the milestone's first
     task commit, `<head>` is `HEAD`.
   - Launch the `reviewer` agent with the milestone, the task ids and the
     range.
   - On `CHANGES_REQUESTED`, send the blocking findings to the `coder`
     agent, then have the `reviewer` re-check just the fix. Allow at most
     **2 fix rounds**. Unresolved findings go into `docs/QUESTIONS.md`.
   - Run step 6, mark the line `Review: [x] done (<commit>)` (or
     `[!] see Q<n>`), commit as `M<n>: review fixes`, push.
   - **Ship it** (standing rule, CLAUDE.md "Version and release"): bump
     `<Version>` in `Directory.Build.props` (patch +1), commit, push, open a
     PR `dev` → `main`, wait for CI to pass, then `gh pr merge --merge`
     without waiting for the user. Confirm the Release run published the new
     version as latest. Report, and end this cycle.
3. **Pick a task.** Read `docs/PLAN.md`. Take the first `[ ]` task whose
   milestone dependencies are all `[x]`. If none is left and only `[!]`
   tasks remain, report the open questions in `docs/QUESTIONS.md` and stop
   (when running under /loop, end the loop). If everything is `[x]`, report
   that v0.0.1 is ready for a PR and end the loop. Mark the task `[~]`.
4. **Architect** (only for `arch:brief`). Read the task's `{arch:…}`
   tag. For `arch:brief`, launch the `architect` agent with the task id and
   text; it writes `docs/briefs/<id>.md`. If it reports a blocking question,
   mark the task `[!]`, commit `docs/` with message `<id>: blocked on Q<n>`,
   push, and go back to step 3 for a different task (at most once per
   cycle). For `arch:skip`, skip this step.
5. **Coder.** Launch the `coder` agent with the brief path, or for
   `arch:skip` with the task id, the task line and the SPEC sections it
   cites (it treats those as the brief). If it reports a spec question
   instead, handle it like a blocking architect question. It must report
   a passing build and tests.
6. **Verify yourself.** Run `dotnet build -c Release` and
   `dotnet test -c Release --filter "live!=outlook&live!=claude"`. Both must pass.
7. **Commit and push.** Mark the task `[x]` in `docs/PLAN.md` and append a
   log line. Commit everything with message `<id>: <summary>` (plus the
   required attribution trailer), then `git push origin dev`.
8. **Report** in 3–5 lines: task, files changed, test counts, review
   rounds, anything the user should look at (spec changes, new questions).

Never run live Outlook or Claude tests, never send anything through
Outlook, and never push to `main` directly. `main` changes only by merging
a green `dev` → `main` PR in the review step above.
