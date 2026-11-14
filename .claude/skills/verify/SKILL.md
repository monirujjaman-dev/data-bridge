---
name: verify
description: Run DataBridge formatting, build and tests and produce the change report required by CLAUDE.md (changed files, commands run, test results, known limitations). Use before declaring a change done.
---

# Verify a DataBridge change

1. If `DataBridge.sln` does not exist yet, say so and stop: nothing to
   build until Phase 0 lands.
2. Run, in order, stopping to report on the first failure:
   - `dotnet format DataBridge.sln --verify-no-changes`
   - `dotnet build DataBridge.sln -warnaserror`
   - `dotnet test --solution DataBridge.sln --no-build`
   If integration tests need a SQL Server instance that is not
   available, run the other test projects, and report the integration
   tests as not run with the reason. Never point tests at a
   non-disposable database.
3. Check the change against the Definition of done in `CLAUDE.md`. If
   project or package references changed, use the
   `dependency-rule-checker` agent. If SQL, provider, comparison or
   synchronization code changed, use the `sql-safety-reviewer` agent.
4. Report:
   - Changed files (`git status --short`, `git diff --stat`).
   - Commands run and their outcome, with failure output.
   - Tests passed/failed/skipped per project.
   - Known limitations and anything not verified.
