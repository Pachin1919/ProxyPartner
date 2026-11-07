---
name: verify-lite
description: Verify ProxyPartner Lite correctness, process-tree memory and host non-interference at an exact commit. Not a general code-review checklist.
---
Read spec/ACCEPTANCE.md and only the current task gates. Execute checks in a fresh worktree. Report measured memory for all product processes, UI and worker included. Do not accept fixture-only evidence for Windows/live checks, sampled maxima as guaranteed peak, or modified thresholds as optimization. Write a verification receipt bound to current HEAD; never approve from implementation summary alone.
