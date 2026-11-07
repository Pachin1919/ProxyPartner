# ProxyPartner Lite
Build a Windows node-quality test panel, not a proxy client. Start with CODEX_START.md. This v3 specification supersedes previous ProxyBench/ProxyBoard bundles.

## Non-negotiable
- No Node/npm, JS/HTML UI, Electron/WebView, local UI/API/SOCKS server, new TUN/driver, or administrator requirement.
- Never modify the host proxy's mode, selection, profiles, subscriptions, DNS, routes, caches or connections. No remote-control features. Live delay/healthcheck routes are excluded from default read-only access.
- Discover the user's existing local Verge/Mihomo. Do not request a subscription URL. Do not read arbitrary profile directories or publish credentials.
- C# collector + SQLite; Go probe only when needed. Budget: tray target25 MiB, window35 MiB, total product-process peak gate40 MiB. Read spec/PERFORMANCE.md. These are goals, not measured claims.
- Native AOT and a Win32 virtual list are the first low-memory candidate. Avalonia is conditional on the same measured budget, not a mandatory dependency.
- Product runtime has no LLM, Codex, Python, agent framework or telemetry service.
- Store typed facts with provenance, not whole API snapshots. Missing ≠0; idle/sleep/observer stopped ≠ node outage. Destination IP ≠ exit IP.
- Reuse verified source selectively. Read relevant functions and licenses; record the upstream ref/blob and copied/adapted scope. README claims are not implementation evidence.

## Delivery loop
Use gpt-6.1-sol for coordination, implementation and verification; no automatic Astra/model fallback. Development-only LangGraph is the authorized orchestration direction; read workflow/LANGGRAPH.zh-CN.md and its bootstrap gates before claiming a running loop. This supersedes the older no-LangGraph statement.
Read spec/DESIGN.zh-CN.md, workflow/HANDOFF.zh-CN.md and workflow/BREAKOUT.zh-CN.md. Machine policy lives in workflow/orchestrator.policy.json; tasks.json remains the product DAG. Independent Sol sessions are not cross-model verification.
Use workflow/tasks.json and WORKFLOW.zh-CN.md. One writer per worktree/task; separate verifier run on the exact candidate SHA. First vertical slice serial; later max2 nonoverlapping writers. Same-cause retry maximum2 before replan. No invented `/loop` command.
A change is not done until behavior AND applicable resource/no-interference checks pass. Do not edit fixtures, budgets or forbidden-route tests merely to make code pass. Store actual command, exit code, SHA and artifact hashes. New SHA invalidates old verdict.
Continue to the next ready task inside the current authorized session; do not ask “continue?” after every small step. Pause only for genuine permission/product decisions, unavailable environment, user stop or exhausted session budget; write checkpoint. No automatic API-consuming background process.
No remote push/PR/release without user authorization. Never reset/clean user work. Changes outside assigned files return to coordinator. Do not preload all research into every agent.
