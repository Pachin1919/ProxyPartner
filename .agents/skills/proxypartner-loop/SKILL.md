---
name: proxypartner-loop
description: Execute a ProxyPartner Lite task through implementation, independent verification and bounded repair. Use only within this repository.
---
Read WORKFLOW.zh-CN.md, workflow/HANDOFF.zh-CN.md, workflow/BREAKOUT.zh-CN.md and the assigned task. Use gpt-6.1-sol only, with no automatic model fallback. Development-only LangGraph must pass workflow/LANGGRAPH.zh-CN.md bootstrap checks before claiming an automated loop. Run tools/flow validate/ready. Assign one owner and a separate verifier, with exact SHA, paths and evidence predicates. Work the smallest useful unit; inspect actual diff and test/performance output. Failed check returns to owner, two identical failed repairs trigger replan. Integrate and verify the integration SHA before done. Record checkpoint before pause. No Node or background model scheduler.
