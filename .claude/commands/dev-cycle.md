---
description: Run one architect → coder → reviewer cycle on the next task in docs/PLAN.md, then commit and push to the dev branch.
---

Run one development cycle for Clippy's Revenge. Follow these steps exactly.

1. **Preflight.**
   - `git switch dev`. Never commit or push to `main`.
   - The working tree must be clean. If it isn't, stop and report.
   - `dotnet --version` must report a 10.x SDK. If not, stop and tell the
     user to install it.
2. **Pick a task.** Read `docs/PLAN.md`. Take the first `[ ]` task whose
   milestone dependencies are all `[x]`. If none is left and only `[!]`
   tasks remain, report the open questions in `docs/QUESTIONS.md` and stop
   (when running under /loop, end the loop). If everything is `[x]`, report
   that v0.0.1 is ready for a PR and end the loop. Mark the task `[~]`.
3. **Architect.** Launch the `architect` agent with the task id and text.
   It writes `docs/briefs/<id>.md`. If it reports a blocking question, mark
   the task `[!]`, commit `docs/` with message `<id>: blocked on Q<n>`, push,
   and go back to step 2 for a different task (at most once per cycle).
4. **Coder.** Launch the `coder` agent with the brief path. It must report
   a passing build and tests.
5. **Reviewer.** Launch the `reviewer` agent with the task id and brief
   path. If the verdict is `CHANGES_REQUESTED`, send the blocking findings
   back to the `coder` agent, then review again. Allow at most **2 fix
   rounds**. If it still isn't approved, revert the product code
   (`git restore` / `git clean` limited to `src/` and `tests/`), mark the
   task `[!]`, add the unresolved findings as a question in
   `docs/QUESTIONS.md`, commit `docs/`, push, and stop this cycle.
6. **Verify yourself.** Run `dotnet build -c Release` and
   `dotnet test -c Release --filter "live!=outlook"`. Both must pass.
7. **Commit and push.** Mark the task `[x]` in `docs/PLAN.md` and append a
   log line. Commit everything with message `<id>: <summary>` (plus the
   required attribution trailer), then `git push origin dev`.
8. **Report** in 3–5 lines: task, files changed, test counts, review
   rounds, anything the user should look at (spec changes, new questions).

Never run live Outlook tests, never send anything through Outlook, and
never merge or push to `main`.
