# flow — no-network workflow consistency helper

Only Go standard library. No agent SDK, Node, Python, server, database or model call.

Run from this folder:

```powershell
go test ./...
go run . validate --root ../..
go run . ready --root ../..
go run . graph --root ../..
go run . check --root ../.. --task T00 --receipt runs/T00/receipt.json --head <actual-git-head>
```

The last line is a usage template, not an executable receipt. `check` returns exit 2 for stale SHA, non-independent run IDs, missing checks, failed exit codes, absent/tampered artifacts, or paths outside the repository. JSON schema validation should additionally run in the eventual CI; this tool implements the safety-relevant checks without an external schema library.

`ready` determines dependency readiness only. The coordinator must also enforce path ownership, permissions, active worker limits and budgets. `validate` verifies receipts for final state entries; it does not prove a merge occurred or logs were truthfully produced. It never auto-sets states, merges, invokes shell commands, or calls Codex.

Go 1.22 in go.mod is a language floor for this tiny helper, NOT a recommendation to install an old unsupported Go toolchain or the version chosen for the eventual Mihomo worker. This delivery was tested with the locally available version recorded in VALIDATION.md.
