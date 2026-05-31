---
description: Run full review against project standards (correctness, architecture, perf)
---

# Full review

Run a comprehensive review of changes on the current branch.

## Steps

1. **Determine scope**
   - On a feature branch: `git diff $(git merge-base HEAD main)..HEAD --name-only`
   - On main / master: `git diff --staged --name-only` then fall back to `git diff HEAD~1 --name-only` if nothing staged
   - Print the file list before proceeding. If empty, stop and tell the user.

2. **Static checks** (run in parallel via Bash):
   - `dotnet format --verify-no-changes`
   - `dotnet build --no-restore /warnaserror`
   - `dotnet test --filter Category=Unit --no-build --verbosity quiet`

3. **Spawn subagents in parallel** (single message, multiple Task tool calls):
   - `code-reviewer` — correctness and standards
   - `architecture-analyzer` — layer integrity and DDD
   - `perf-analyzer` — only if changes touch known perf-sensitive paths (data access, hot endpoints, background jobs, parallel processing)

4. **Consolidate findings** into a single report:

```
# Review: <branch-name>

## Static checks
- format: ✅ / ❌
- build:  ✅ / ❌  (N warnings)
- tests:  ✅ / ❌  (N passed, M failed)

## Code review
<summary from code-reviewer, top findings only>

## Architecture
<summary from architecture-analyzer>

## Performance
<summary from perf-analyzer, if run; otherwise "skipped — no perf-sensitive changes">

## Verdict
- Blockers: N
- Should-fix: N
- Nits: N

Recommendation: <merge | fix blockers | discuss>
```

5. **If blockers exist**, list the top 3 with exact `file:line` and proposed fix. Offer to apply fixes, but do not apply without confirmation.

## Constraints
- Read-only during review. Do not modify files.
- If `git` commands fail (not a repo, no commits), tell the user and stop.
- Final report under 200 lines. Subagent details available on follow-up request.
