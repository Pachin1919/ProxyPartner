"""Bounded, persisted development workflow. All external actions are manual."""
from pathlib import Path
from typing import TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.types import interrupt

from gates import (GateError, check_revision, digest, file_under, git, head, read,
                   receipt, require, verify_worktree)


class Cycle(TypedDict, total=False):
    authorization: dict
    workflow_hash: str
    coordinator_branch: str
    coordinator_head: str
    phase: str
    revision: int
    dispatch: dict
    event: dict
    error: str
    cycle: int
    cycle_candidates: int
    verification_rounds: int
    replans: int
    failures: dict
    owner: str
    candidate: str
    candidate_evidence: list
    candidate_verifier: str
    integration: str
    integration_evidence: list
    blocked_phase: str
    replans_evidence: list


TERMINAL = ("done", "waiting_input")
VERIFICATION = ("verify_candidate", "verify_integration")


def compile_graph(root, task, lead, policy, checkpointer):
    model = policy["model"]

    def prepare(state: Cycle):
        phase = state["phase"]
        if phase in TERMINAL:
            return {"dispatch": {}}
        if phase in VERIFICATION and state["verification_rounds"] >= policy["max_verification_rounds_per_task"]:
            return {"phase": "waiting_input", "dispatch": {},
                    "error": "task verification limit reached"}
        if phase == "verify_candidate" and state["cycle_candidates"] >= policy["max_candidate_verifications_per_cycle"]:
            phase = "replan"
        if phase == "replan" and (
            state["replans"] >= policy["max_replans_per_task"]
            or state["verification_rounds"] >= policy["max_verification_rounds_per_task"]
        ):
            return {"phase": "waiting_input", "dispatch": {},
                    "error": "replan requires coordinator decision"}
        revision = state["revision"] + 1
        auth = state["authorization"]
        dispatch = {
            "id": f"{task['id']}.{auth['run']}.{state['cycle']}.{revision}",
            "phase": phase, "status": "ready", "session": "",
            "model": model, "cycle": state["cycle"], "task": task["id"],
            "role": {"owner": lead, "verify_candidate": "testing",
                     "verify_integration": "testing"}.get(phase, "coordinator"),
            "goal": task["goal"], "base_sha": auth["base_sha"],
            "candidate_sha": state.get("candidate", ""),
            "integration_sha": state.get("integration", ""),
            "owner_run": state.get("owner", ""),
            "owner_worktree": auth["owner_worktree"], "owner_branch": auth["owner_branch"],
            "allowed_paths": auth["allowed_paths"], "required_checks": task["required_checks"],
            "acceptance_commands": auth["acceptance_commands"],
            "evidence_rule": task["evidence_rule"], "evidence_root": str(root),
            "failure_counts": state["failures"],
            "remote_actions_authorized": False,
        }
        return {
            "phase": phase, "revision": revision, "dispatch": dispatch,
            "verification_rounds": state["verification_rounds"] + (phase in VERIFICATION),
            "cycle_candidates": state["cycle_candidates"] + (phase == "verify_candidate"),
        }

    def wait(state: Cycle):
        return {"event": interrupt({
            **state["dispatch"], "revision": state["revision"], "error": state.get("error", ""),
        })}

    def apply(state: Cycle):
        event = state["event"]
        dispatch = state["dispatch"]
        phase = state["phase"]
        auth = state["authorization"]
        common = {"dispatch_id", "revision", "type"}
        keep = {"revision": state["revision"] + 1, "event": {}, "error": ""}

        def fields(*extras):
            require(set(event) == common | set(extras), "unexpected or missing event fields")

        try:
            require(isinstance(event, dict) and common <= event.keys(), "invalid event")
            require(event["dispatch_id"] == dispatch["id"]
                    and type(event["revision"]) is int and event["revision"] == state["revision"],
                    "event belongs to another dispatch or revision")
            action = event["type"]
            status = dispatch["status"]
            if action == "claim":
                fields()
                require(status == "ready" and phase != "blocked", "dispatch is not ready to claim")
                return {**keep, "dispatch": {**dispatch, "status": "reconcile"}}
            if action == "not_started":
                fields("confirmed_by")
                require(status == "reconcile" and event["confirmed_by"] == auth["coordinator_session"],
                        "coordinator must confirm that no action was started")
                require(phase != "integrate" or head(root) == state["coordinator_head"],
                        "integration HEAD changed, reconcile the existing result")
                return {**keep, "dispatch": {**dispatch, "status": "ready"}}
            if action == "attach":
                fields("session", "model")
                session = event["session"]
                require(status == "reconcile" and isinstance(session, str) and session.strip()
                        and event["model"] == model, "expected the actual Sol session for the claim")
                if phase in ("integrate", "replan"):
                    require(session == auth["coordinator_session"], "wrong coordinator session")
                elif phase in VERIFICATION:
                    require(session not in (auth["coordinator_session"], state["owner"]),
                            "independent verifier session required")
                    if phase == "verify_integration":
                        require(session != state["candidate_verifier"],
                                "integration requires a fresh verification run")
                else:
                    require(session != auth["coordinator_session"], "owner session must be separate")
                    require(not state.get("owner") or state["owner"] == session,
                            "repair must return to the original owner session")
                return {**keep, "dispatch": {**dispatch, "status": "waiting_result", "session": session}}
            if action == "unblock":
                fields("confirmed_by")
                require(phase == "blocked" and event["confirmed_by"] == auth["coordinator_session"],
                        "coordinator must confirm the missing prerequisite is available")
                return {**keep, "phase": state["blocked_phase"], "dispatch": {}}

            require(action == "result" and status == "waiting_result",
                    "expected an attached dispatch result")
            base = auth["base_sha"]
            session = dispatch["session"]
            if phase == "owner":
                fields("session", "sha")
                require(event["session"] == session, "result does not match the attached session")
                owner_tree = Path(auth["owner_worktree"])
                require(git(owner_tree, "branch", "--show-current") == auth["owner_branch"],
                        "wrong owner branch")
                require(event["sha"] != state.get("candidate"), "repair must produce a new candidate SHA")
                check_revision(root, owner_tree, base, event["sha"], auth["allowed_paths"])
                return {**keep, "phase": "verify_candidate", "dispatch": {},
                        "owner": session, "candidate": event["sha"], "candidate_evidence": [],
                        "integration": ""}

            if phase in VERIFICATION:
                fields("session", "worktree", "receipt", "failure")
                require(event["session"] == session, "result does not match the attached session")
                tested = state["candidate" if phase == "verify_candidate" else "integration"]
                verifier_tree = Path(event["worktree"]).resolve()
                require(verifier_tree not in (root, Path(auth["owner_worktree"])),
                        "independent verifier worktree required")
                verify_worktree(root, verifier_tree, tested)
                result, receipt_hash = receipt(root, policy, task, event["receipt"], tested,
                                               state["owner"], base=base, verifier=session)
                if result["verdict"] == "BLOCKED":
                    return {**keep, "phase": "blocked", "dispatch": {}, "blocked_phase": phase,
                            "error": f"blocked by receipt {event['receipt']} ({receipt_hash})"}
                if result["verdict"] == "PASS":
                    require(event["failure"] is None, "PASS cannot carry a failure signature")
                    evidence = [event["receipt"], receipt_hash]
                    if phase == "verify_integration":
                        return {**keep, "phase": "done", "dispatch": {}, "integration_evidence": evidence}
                    return {**keep, "phase": "integrate", "dispatch": {},
                            "candidate_evidence": evidence, "candidate_verifier": session}
                failure = event["failure"]
                failed = {c["id"] for c in result["checks"] if not c["passed"] or c["exit_code"] != 0}
                require(isinstance(failure, list) and len(failure) == 2
                        and isinstance(failure[0], str) and failure[0] in failed
                        and isinstance(failure[1], str) and failure[1].strip(),
                        "FAIL needs a failed check ID and a root-cause classification")
                key = f"{failure[0]}:{failure[1]}"
                failures = {**state["failures"], key: state["failures"].get(key, 0) + 1}
                capped = failures[key] > policy["max_same_cause_repairs"]
                out_of_candidates = state["cycle_candidates"] >= policy["max_candidate_verifications_per_cycle"]
                next_phase = "replan" if capped or out_of_candidates else "owner"
                if state["verification_rounds"] >= policy["max_verification_rounds_per_task"]:
                    next_phase = "waiting_input"
                return {**keep, "phase": next_phase, "dispatch": {}, "failures": failures,
                        "error": f"{task['id']}:{key}; receipt {event['receipt']} ({receipt_hash})"}

            if phase == "integrate":
                fields("session", "sha")
                require(event["session"] == session, "result does not match the attached session")
                check_revision(root, root, base, event["sha"], auth["allowed_paths"])
                git(root, "merge-base", "--is-ancestor", state["coordinator_head"], event["sha"])
                git(root, "merge-base", "--is-ancestor", state["candidate"], event["sha"])
                require(git(root, "rev-parse", f"{state['candidate']}^{{tree}}")
                        == git(root, "rev-parse", f"{event['sha']}^{{tree}}"),
                        "integration tree differs from the verified candidate")
                return {**keep, "phase": "verify_integration", "dispatch": {},
                        "integration": event["sha"], "coordinator_head": event["sha"]}

            require(phase == "replan", "unexpected result phase")
            fields("session", "plan", "within_scope")
            require(event["session"] == session and type(event["within_scope"]) is bool,
                    "invalid replan result")
            plan = file_under(root, event["plan"])
            require(plan.stat().st_size > 0, "replan evidence is empty")
            evidence = [*state.get("replans_evidence", []), [event["plan"], digest(plan)]]
            if not event["within_scope"]:
                return {**keep, "phase": "waiting_input", "dispatch": {}, "replans_evidence": evidence}
            return {**keep, "phase": "owner", "dispatch": {}, "cycle": state["cycle"] + 1,
                    "cycle_candidates": 0, "replans": state["replans"] + 1, "replans_evidence": evidence}
        except (GateError, OSError, ValueError) as error:
            return {**keep, "error": str(error)}

    graph = StateGraph(Cycle)
    graph.add_node("prepare", prepare)
    graph.add_node("wait", wait)
    graph.add_node("apply", apply)
    graph.add_edge(START, "prepare")
    graph.add_conditional_edges("prepare", lambda state: END if state["phase"] in TERMINAL else "wait")
    graph.add_edge("wait", "apply")
    graph.add_conditional_edges("apply", lambda state: "wait" if state["dispatch"] else "prepare")
    return graph.compile(checkpointer=checkpointer)
