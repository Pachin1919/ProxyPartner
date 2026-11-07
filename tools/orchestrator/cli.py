"""Coordinator-only file handoff and state projection."""
import argparse
import hashlib
import json
import os
import sys
from contextlib import contextmanager
from pathlib import Path

# Disable SDK tracing and restrict checkpoint deserialization before importing LangGraph.
os.environ["LANGSMITH_TRACING"] = "false"
os.environ["LANGCHAIN_TRACING_V2"] = "false"
os.environ["LANGGRAPH_STRICT_MSGPACK"] = "true"

from langgraph.checkpoint.sqlite import SqliteSaver
from langgraph.types import Command

from gates import (GateError, authorized_manifest, digest, file_under, git, head, read,
                   receipt, require, verify_worktree)
from graph import TERMINAL, compile_graph


def write_json(path, value):
    temp = path.with_suffix(".tmp")
    with temp.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temp, path)


@contextmanager
def writer_lock(store):
    # The OS releases this lock if the CLI exits between a checkpoint and export.
    with (store / "writer.lock").open("a+b") as stream:
        if os.name == "nt":
            import msvcrt

            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl

            fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def completed_entry(root, policy, task, snapshot):
    state = snapshot.values
    require(state.get("phase") == "done" and not snapshot.next,
            f"reconcile {task['id']}: projected done requires a terminal checkpoint")
    auth = state["authorization"]
    require(auth["task"] == task["id"], f"reconcile {task['id']}: checkpoint task mismatch")
    rel, expected_hash = state["integration_evidence"]
    sha = state["integration"]
    owner = state["owner"]
    data, actual_hash = receipt(root, policy, task, rel, sha, owner, base=auth["base_sha"])
    require(data["verdict"] == "PASS" and actual_hash == expected_hash
            and data["verifier_run"] != state["candidate_verifier"],
            f"reconcile {task['id']}: changed integration receipt")
    return {"status": "done", "owner_run": owner, "head_sha": sha, "receipt_path": rel}


def run(args):
    root = args.root.resolve()
    require(Path(git(root, "rev-parse", "--show-toplevel")).resolve() == root,
            "--root must identify the coordinator Git worktree")
    require(not (root / "runs/orchestrator/STOP").exists(), "coordinator stop marker is present")
    policy_file = root / "workflow/orchestrator.policy.json"
    policy = read(policy_file)
    require(policy["schema_version"] == 1 and policy["model"] == "gpt-6.1-sol"
            and not policy["automatic_model_fallback"] and not policy["background_model_calls"]
            and policy["remote_publication_requires_authorization"], "unsupported workflow policy")
    database = root / policy["checkpoint_database"]
    store = database.parent.resolve()
    require(store == root / "runs/orchestrator" and database.name == "checkpoints.sqlite",
            "checkpoint store must be inside runs")
    store.mkdir(parents=True, exist_ok=True)
    with writer_lock(store):
        return run_locked(args, root, policy, policy_file, store, database)


def run_locked(args, root, policy, policy_file, store, database):
    tasks_file = root / policy["task_source"]
    ownership_file = root / "workflow/module-ownership.json"
    projection_file = root / policy["state_projection"]
    plan = read(tasks_file)
    ownership = read(ownership_file)
    tasks = {task["id"]: task for task in plan["tasks"]}
    projection = read(projection_file)
    require(plan["schema_version"] == projection["schema_version"] == ownership["schema_version"] == 1
            and len(tasks) == len(plan["tasks"]) and tasks.keys() == projection["tasks"].keys(),
            "invalid task plan or state projection")
    entries = projection["tasks"]
    for task_id, entry in entries.items():
        if entry["status"] in ("verified", "merge_ready"):
            data, _ = receipt(root, policy, tasks[task_id], entry["receipt_path"],
                              entry["head_sha"], entry["owner_run"])
            require(data["verdict"] == "PASS", f"reconcile invalid receipt for {task_id}")

    version = hashlib.sha256("".join(digest(path) for path in (
        policy_file, tasks_file, ownership_file, root / policy["receipt_schema"],
        Path(__file__), Path(__file__).with_name("graph.py"), Path(__file__).with_name("gates.py"),
        Path(__file__).with_name("requirements.lock"),
    )).encode()).hexdigest()
    registry = store / "active.json"
    coordinator_branch = git(root, "branch", "--show-current")
    require(coordinator_branch, "coordinator must be on a branch")
    with SqliteSaver.from_conn_string(str(database)) as saver:
        runs = read(registry) if registry.exists() else {}
        if args.command == "start":
            completed = set()
            for tid, entry in entries.items():
                if entry["status"] != "done":
                    continue
                require(tid in runs, f"reconcile {tid}: projected done has no registered checkpoint")
                prior_graph = compile_graph(root, tasks[tid], ownership["task_leads"][tid], policy, saver)
                prior_snapshot = prior_graph.get_state({"configurable": {"thread_id": runs[tid]["thread"]}})
                require(entry == completed_entry(root, policy, tasks[tid], prior_snapshot),
                        f"reconcile {tid}: projection does not match the integration checkpoint")
                git(root, "merge-base", "--is-ancestor", entry["head_sha"], head(root))
                completed.add(tid)
            require(all(set(tasks[tid]["depends_on"]) <= completed for tid in completed),
                    "reconcile incomplete dependency of a projected done task")
            ready = sorted(tid for tid, item in tasks.items()
                           if entries[tid]["status"] in ("pending", "needs_fix")
                           and set(item["depends_on"]) <= completed)
            if not ready:
                remaining = {tid: {"status": item["status"], "dependencies": tasks[tid]["depends_on"]}
                             for tid, item in entries.items() if tid not in completed}
                return {"phase": "waiting_input" if remaining else "complete", "remaining": remaining}
            auth = read(args.file)
            task_id = auth.get("task") if isinstance(auth, dict) else None
            require(isinstance(task_id, str) and task_id in ready, f"authorize a ready task: {ready}")
            require(task_id not in runs, "task already has a checkpoint; use status or resume")
            require(runs.keys() <= completed, "coordinator already has an unfinished task")
            task = tasks[task_id]
            verify_worktree(root, root, head(root))
            auth = authorized_manifest(root, task, policy, auth, coordinator_branch)
            thread = f"{root.as_posix()}:{task_id}:1:{auth['run']}"
            initial = {
                "authorization": auth, "workflow_hash": version,
                "coordinator_branch": coordinator_branch, "coordinator_head": auth["base_sha"],
                "phase": "owner", "revision": 0, "cycle": 1, "cycle_candidates": 0,
                "verification_rounds": 0, "replans": 0, "failures": {},
            }
            runs[task_id] = {"thread": thread, "initial": initial}
            write_json(registry, runs)
        else:
            task_id = args.task
            require(task_id in tasks, "unknown task")
        task = tasks[task_id]
        graph = compile_graph(root, task, ownership["task_leads"][task_id], policy, saver)
        require(task_id in runs, "task has no checkpoint")
        record = runs[task_id]
        initial = record["initial"]
        require(initial["workflow_hash"] == version and initial["coordinator_branch"] == coordinator_branch,
                "reconcile changed workflow version or coordinator branch before resuming")
        auth = initial["authorization"]
        require(digest(file_under(root, auth["bootstrap_report"])) == auth["bootstrap_hash"],
                "reconcile changed bootstrap evidence")
        config = {"configurable": {"thread_id": record["thread"]},
                  "recursion_limit": policy["recursion_limit_per_cycle"]}
        snapshot = graph.get_state(config)
        state = snapshot.values or initial
        expected = state["coordinator_head"]
        actual = head(root)
        dispatch = state.get("dispatch", {})
        expected_change = (state["phase"] == "integrate"
                           and dispatch.get("status") in ("reconcile", "waiting_result"))
        require(actual == expected or expected_change,
                "reconcile unexpected coordinator HEAD")
        if state["phase"] in ("verify_candidate", "integrate"):
            owner_tree = Path(auth["owner_worktree"])
            verify_worktree(root, owner_tree, state["candidate"])
            require(git(owner_tree, "branch", "--show-current") == auth["owner_branch"],
                    "reconcile unexpected owner branch")
        if state.get("candidate_evidence") and state["phase"] in ("integrate", "verify_integration", "done"):
            rel, sha = state["candidate_evidence"]
            data, actual_hash = receipt(root, policy, task, rel, state["candidate"], state["owner"],
                                        base=auth["base_sha"])
            require(data["verdict"] == "PASS" and actual_hash == sha,
                    "reconcile candidate receipt before continuing")

        rejected = False
        if not snapshot.values or (snapshot.next and not any(t.interrupts for t in snapshot.tasks)):
            require(args.command != "resume", "recover the pending checkpoint with status first")
            graph.invoke(initial if not snapshot.values else None, config, durability="sync")
        elif args.command == "resume":
            require(state["phase"] not in TERMINAL, "cycle is closed")
            event = read(args.file)
            require(isinstance(event, dict) and event.get("dispatch_id") == dispatch["id"]
                    and event.get("revision") == state["revision"], "stale dispatch event")
            result = graph.invoke(Command(resume=event), config, durability="sync")
            rejected = bool(result.get("error") and result["dispatch"].get("id") == dispatch["id"]
                            and result["dispatch"].get("status") == dispatch["status"])
        snapshot = graph.get_state(config)
        state = snapshot.values
        dispatch = state.get("dispatch", {})
        output = {
            "thread": record["thread"], "phase": state["phase"], "dispatch": dispatch,
            "revision": state["revision"], "error": state.get("error", ""),
            "ledger": {key: state[key] for key in (
                "cycle", "cycle_candidates", "verification_rounds", "replans", "failures")},
        }
        if args.command == "project":
            phase = state["phase"]
            status = {"owner": "needs_fix" if state["failures"] else "running",
                      "verify_candidate": "verifying", "integrate": "merge_ready",
                      "verify_integration": "verifying", "blocked": "blocked",
                      "replan": "needs_fix", "waiting_input": "paused", "done": "done"}[phase]
            sha = state.get("integration") or state.get("candidate", "")
            rel = ""
            if status == "merge_ready":
                rel, expected_hash = state["candidate_evidence"]
                data, actual_hash = receipt(root, policy, task, rel, sha, state["owner"], base=auth["base_sha"])
                require(data["verdict"] == "PASS" and actual_hash == expected_hash,
                        "reconcile changed receipt before projecting")
            entries[task_id] = (completed_entry(root, policy, task, snapshot) if status == "done"
                                else {"status": status, "owner_run": state.get("owner", ""),
                                      "head_sha": sha, "receipt_path": rel})
            write_json(projection_file, projection)
        write_json(store / f"{task_id}.handoff.json", output)
        if rejected:
            raise GateError(output["error"])
        return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, type=Path)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("start").add_argument("file", type=Path)
    commands.add_parser("status").add_argument("task")
    commands.add_parser("project").add_argument("task")
    resume = commands.add_parser("resume")
    resume.add_argument("task")
    resume.add_argument("file", type=Path)
    try:
        print(json.dumps(run(parser.parse_args()), ensure_ascii=True))
    except (ValueError, OSError, KeyError, TypeError) as error:
        print(str(error), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
