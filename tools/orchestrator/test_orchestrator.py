"""Bootstrap checks using an isolated Git repo and synthetic file handoffs."""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path

from cli import run, writer_lock, write_json
from gates import GateError, git, head, read

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parents[1]
MODEL = "gpt-6.1-sol"


class Bootstrap(unittest.TestCase):
    def setUp(self):
        temp_root = Path(os.environ.get("ORCHESTRATOR_TEST_TMP", SOURCE / "runs"))
        temp_root.mkdir(exist_ok=True)
        temp = tempfile.TemporaryDirectory(prefix="pp-", dir=temp_root)
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name) / "repo"
        self.owner = self.root.parent / "owner"
        self.verifier = self.root.parent / "verifier"
        self.root.mkdir()
        for path in ("workflow/tasks.json", "workflow/state.json", "workflow/orchestrator.policy.json",
                     "workflow/module-ownership.json", "contracts/verification-receipt.schema.json"):
            dest = self.root / path
            dest.parent.mkdir(exist_ok=True)
            shutil.copyfile(SOURCE / path, dest)
        self.store = self.root / "runs/orchestrator"
        self.store.mkdir(parents=True)
        (self.root / ".gitignore").write_text("runs/\n", encoding="utf-8")
        (self.root / "spec").mkdir()
        (self.root / "spec/scope.txt").write_text("base", encoding="utf-8")
        git(self.root, "init", "--initial-branch=main")
        git(self.root, "config", "user.name", "Offline Fixture")
        git(self.root, "config", "user.email", "fixture@example.invalid")
        git(self.root, "config", "commit.gpgsign", "false")
        git(self.root, "add", ".")
        git(self.root, "commit", "-m", "base")
        self.base = head(self.root)
        git(self.root, "worktree", "add", "-b", "fixture-owner", str(self.owner))
        git(self.root, "worktree", "add", "--detach", str(self.verifier), self.base)
        policy = read(self.root / "workflow/orchestrator.policy.json")
        write_json(self.store / "bootstrap.json", {
            "head_sha": self.base, "model": MODEL,
            "checks": dict.fromkeys(policy["bootstrap_checks"], "PASS"),
        })
        self.manifest = self.store / "authorization.json"
        self.checks = read(self.root / "workflow/tasks.json")["tasks"][0]["required_checks"]
        write_json(self.manifest, {
            "task": "T00", "run": "offline", "base_sha": self.base,
            "owner_worktree": str(self.owner), "owner_branch": "fixture-owner",
            "coordinator_session": "coord", "bootstrap_report": "runs/orchestrator/bootstrap.json",
            "allowed_paths": ["spec/scope.txt"],
            "acceptance_commands": dict.fromkeys(self.checks, "offline fixture"),
        })
        self.event_file = self.store / "event.json"
        self.count = 0

    def cli(self, command, file=None):
        return run(Namespace(root=self.root, command=command, file=file, task="T00"))

    def process(self, command, *extra, ok=True):
        result = subprocess.run(
            [sys.executable, "-B", str(HERE / "cli.py"), "--root", str(self.root), command, *extra],
            text=True, capture_output=True, check=False,
        )
        self.assertEqual(result.returncode, 0 if ok else 2, result.stderr)
        return json.loads(result.stdout) if ok else result.stderr

    def start(self):
        return self.cli("start", self.manifest)

    def event(self, kind, **values):
        status = self.cli("status")
        write_json(self.event_file, {"type": kind, "revision": status["revision"],
                                    "dispatch_id": status["dispatch"]["id"], **values})
        return self.cli("resume", self.event_file)

    def attach(self, session):
        self.event("claim")
        return self.event("attach", session=session, model=MODEL)

    def candidate(self):
        self.attach("owner")
        self.count += 1
        (self.owner / "spec/scope.txt").write_text(str(self.count), encoding="utf-8")
        git(self.owner, "add", "spec/scope.txt")
        git(self.owner, "commit", "-m", f"candidate {self.count}")
        sha = head(self.owner)
        self.event("result", session="owner", sha=sha)
        return sha

    def verification(self, sha, verdict="PASS", session="verify", filename=None, **overrides):
        git(self.verifier, "checkout", "--detach", sha)
        failed = verdict == "FAIL"
        log = self.store / f"check-{self.count}-{session}.log"
        log.write_text("synthetic check", encoding="utf-8")
        data = {
            "schema_version": 1, "task_id": "T00", "head_sha": sha, "base_sha": self.base,
            "implementer_run": "owner", "verifier_run": session, "verdict": verdict,
            "checks": [
                {"id": check, "passed": not failed or i > 0, "exit_code": int(failed and i == 0),
                 "command": "offline fixture",
                 "evidence": [{"path": log.relative_to(self.root).as_posix(),
                               "sha256": hashlib.sha256(log.read_bytes()).hexdigest()}]}
                for i, check in enumerate(self.checks)
            ],
            **overrides,
        }
        path = self.store / (filename or f"receipt-{self.count}-{session}.json")
        write_json(path, data)
        return path.relative_to(self.root).as_posix(), log

    def verified(self, sha, verdict="PASS", cause="same"):
        self.attach("verify")
        rel, _ = self.verification(sha, verdict)
        failure = [self.checks[0], cause] if verdict == "FAIL" else None
        return self.event("result", session="verify", worktree=str(self.verifier),
                          receipt=rel, failure=failure)

    def integrate(self, sha):
        git(self.root, "merge", "--no-ff", "-m", "integrate fixture", sha)
        return head(self.root)

    def test_graph_routing_pass_requires_integration_verification(self):
        self.start()
        candidate = self.candidate()
        self.assertEqual(self.verified(candidate)["phase"], "integrate")
        self.cli("project")
        self.assertEqual(read(self.root / "workflow/state.json")["tasks"]["T00"]["status"], "merge_ready")
        self.attach("coord")
        integration = self.integrate(candidate)
        self.assertNotEqual(integration, candidate)
        self.assertEqual(self.event("result", session="coord", sha=integration)["phase"],
                         "verify_integration")
        self.attach("integration-verify")
        rel, _ = self.verification(integration, session="integration-verify")
        self.assertEqual(self.event("result", session="integration-verify", worktree=str(self.verifier),
                                    receipt=rel, failure=None)["phase"], "done")

    def test_graph_routing_blocked_stops_dependency(self):
        self.start()
        blocked = self.verified(self.candidate(), "BLOCKED")
        self.assertEqual(blocked["phase"], "blocked")
        self.cli("project")
        self.assertEqual(read(self.root / "workflow/state.json")["tasks"]["T00"]["status"], "blocked")
        self.assertEqual(self.start()["phase"], "waiting_input")
        self.assertEqual(self.event("unblock", confirmed_by="coord")["phase"], "verify_candidate")

    def test_graph_routing_integration_fail_returns_to_original_owner(self):
        self.start()
        candidate = self.candidate()
        self.verified(candidate)
        self.attach("coord")
        integration = self.integrate(candidate)
        self.event("result", session="coord", sha=integration)
        self.attach("integration-verify")
        rel, _ = self.verification(integration, "FAIL", session="integration-verify")
        failure = [self.checks[0], "integration regression"]
        failed = self.event("result", session="integration-verify", worktree=str(self.verifier),
                            receipt=rel, failure=failure)
        self.assertEqual(failed["phase"], "owner")
        self.assertEqual(failed["ledger"]["verification_rounds"], 2)
        self.event("claim")
        with self.assertRaisesRegex(GateError, "original owner"):
            self.event("attach", session="other", model=MODEL)
        self.event("attach", session="owner", model=MODEL)

    def test_repair_breakout_restarts_cycle_and_retains_root_cause(self):
        self.start()
        for phase in ("owner", "owner", "replan"):
            self.assertEqual(self.verified(self.candidate(), "FAIL")["phase"], phase)
        self.attach("coord")
        plan = self.store / "plan.txt"
        plan.write_text("Try a narrower implementation", encoding="utf-8")
        result = self.event("result", session="coord", plan=plan.relative_to(self.root).as_posix(),
                            within_scope=True)
        self.assertEqual(result["ledger"], {
            "cycle": 2, "cycle_candidates": 0, "verification_rounds": 3, "replans": 1,
            "failures": {f"{self.checks[0]}:same": 3},
        })
        self.assertEqual(self.verified(self.candidate(), "FAIL")["phase"], "waiting_input")

    def test_repair_breakout_verification_limits(self):
        self.start()
        for index in range(6):
            result = self.verified(self.candidate(), "FAIL", cause=f"root-{index}")
        self.assertEqual(result["phase"], "replan")
        self.assertEqual(result["ledger"]["cycle_candidates"], 6)
        self.attach("coord")
        plan = self.store / "plan.txt"
        plan.write_text("New plan", encoding="utf-8")
        self.event("result", session="coord", plan=plan.relative_to(self.root).as_posix(),
                   within_scope=True)
        for index in range(6):
            result = self.verified(self.candidate(), "FAIL", cause=f"new-root-{index}")
        self.assertEqual(result["phase"], "waiting_input")
        self.assertEqual(result["ledger"]["verification_rounds"], 12)
        self.assertFalse(result["dispatch"])

    def test_persistent_resume_real_subprocess(self):
        first = self.process("start", str(self.manifest))
        failed = self.verified(self.candidate(), "FAIL")
        recovered = self.process("status", "T00")
        self.assertEqual(recovered["thread"], first["thread"])
        self.assertEqual(recovered["dispatch"], failed["dispatch"])
        self.assertEqual(recovered["ledger"], failed["ledger"])
        self.assertTrue((self.store / "checkpoints.sqlite").is_file())

    def test_dispatch_reconcile_owner_and_integration(self):
        first = self.start()
        self.assertEqual(self.process("status", "T00")["dispatch"], first["dispatch"])
        reserved = self.event("claim")
        self.assertEqual(self.process("status", "T00")["dispatch"], reserved["dispatch"])
        self.assertEqual(reserved["dispatch"]["status"], "reconcile")
        with self.assertRaisesRegex(GateError, "stale"):
            self.cli("resume", self.event_file)
        self.event("not_started", confirmed_by="coord")
        candidate = self.candidate()
        self.verified(candidate)
        self.event("claim")
        integration = self.integrate(candidate)
        self.assertEqual(self.process("status", "T00")["dispatch"]["status"], "reconcile")
        with self.assertRaisesRegex(GateError, "integration HEAD changed"):
            self.event("not_started", confirmed_by="coord")
        self.event("attach", session="coord", model=MODEL)
        self.assertEqual(self.event("result", session="coord", sha=integration)["phase"],
                         "verify_integration")
        with self.assertRaisesRegex(GateError, "stale"):
            self.cli("resume", self.event_file)
        self.assertEqual(head(self.root), integration)

    def test_sha_evidence_gate(self):
        self.start()
        candidate = self.candidate()
        self.event("claim")
        with self.assertRaisesRegex(GateError, "independent"):
            self.event("attach", session="owner", model=MODEL)
        self.event("attach", session="verify", model=MODEL)
        for edits, reason in (({"head_sha": self.base}, "SHA mismatch"),
                              ({"checks": []}, "schema violation"),
                              ({"verifier_run": "owner"}, "independent verifier")):
            rel, _ = self.verification(candidate, **edits)
            with self.assertRaisesRegex(GateError, reason):
                self.event("result", session="verify", worktree=str(self.verifier), receipt=rel, failure=None)
        rel, log = self.verification(candidate)
        raw = read(self.root / rel)
        raw["checks"].pop()
        write_json(self.root / rel, raw)
        with self.assertRaisesRegex(GateError, "missing required checks"):
            self.event("result", session="verify", worktree=str(self.verifier), receipt=rel, failure=None)
        rel, log = self.verification(candidate)
        log.write_text("tampered", encoding="utf-8")
        with self.assertRaisesRegex(GateError, "evidence changed"):
            self.event("result", session="verify", worktree=str(self.verifier), receipt=rel, failure=None)
        self.assertEqual(self.cli("status")["phase"], "verify_candidate")

    def test_sha_evidence_gate_rejects_changes_outside_narrow_brief(self):
        self.start()
        self.attach("owner")
        (self.owner / "spec/outside.txt").write_text("outside", encoding="utf-8")
        git(self.owner, "add", "spec/outside.txt")
        git(self.owner, "commit", "-m", "outside the brief")
        with self.assertRaisesRegex(GateError, "outside the brief"):
            self.event("result", session="owner", sha=head(self.owner))
        self.assertEqual(self.cli("status")["phase"], "owner")

    def test_state_projection_rechecks_receipt_after_process_exit(self):
        self.start()
        candidate = self.candidate()
        self.verified(candidate)
        self.attach("coord")
        integration = self.integrate(candidate)
        self.event("result", session="coord", sha=integration)
        self.attach("integration-verify")
        rel, log = self.verification(integration, session="integration-verify")
        self.event("result", session="integration-verify", worktree=str(self.verifier),
                   receipt=rel, failure=None)
        state = self.root / "workflow/state.json"
        self.assertEqual(read(state)["tasks"]["T00"]["status"], "pending")
        with writer_lock(self.store):
            self.process("project", "T00", ok=False)
        self.assertEqual(read(state)["tasks"]["T00"]["status"], "pending")
        log.write_text("modified", encoding="utf-8")
        self.assertIn("evidence changed", self.process("project", "T00", ok=False))
        self.assertEqual(read(state)["tasks"]["T00"]["status"], "pending")
        log.write_text("synthetic check", encoding="utf-8")
        self.assertEqual(self.process("project", "T00")["phase"], "done")
        self.assertEqual(read(state)["tasks"]["T00"]["head_sha"], integration)
        self.assertEqual(self.process("project", "T00")["phase"], "done")

    def test_fast_forward_dependency_requires_matching_integration_projection(self):
        self.start()
        sha = self.candidate()
        self.verified(sha)
        self.cli("project")
        state_path = self.root / "workflow/state.json"
        candidate_entry = read(state_path)["tasks"]["T00"]
        self.attach("coord")
        git(self.root, "merge", "--ff-only", sha)
        self.assertEqual(head(self.root), sha)
        self.assertEqual(self.event("result", session="coord", sha=sha)["phase"], "verify_integration")

        owner2 = self.root.parent / "owner2"
        git(self.root, "worktree", "add", "-b", "fixture-owner2", str(owner2))
        task = read(self.root / "workflow/tasks.json")["tasks"][1]
        downstream = self.store / "t01.json"
        write_json(downstream, {
            **read(self.manifest), "task": "T01", "run": "downstream", "base_sha": sha,
            "owner_worktree": str(owner2), "owner_branch": "fixture-owner2",
            "allowed_paths": [task["scope"][0]],
            "acceptance_commands": dict.fromkeys(task["required_checks"], "offline fixture"),
        })
        stale = read(state_path)
        stale["tasks"]["T00"] = {**candidate_entry, "status": "done"}
        write_json(state_path, stale)
        self.assertIn("requires a terminal checkpoint", self.process("start", str(downstream), ok=False))
        self.process("project", "T00")
        self.assertEqual(read(state_path)["tasks"]["T00"]["status"], "verifying")

        self.attach("integration-verify")
        rel, log = self.verification(sha, session="integration-verify")
        self.assertEqual(self.event("result", session="integration-verify", worktree=str(self.verifier),
                                    receipt=rel, failure=None)["phase"], "done")
        write_json(state_path, stale)
        self.assertIn("projection does not match", self.process("start", str(downstream), ok=False))

        self.process("project", "T00")
        self.assertEqual(read(state_path)["tasks"]["T00"], {
            "status": "done", "owner_run": "owner", "head_sha": sha, "receipt_path": rel,
        })
        log.write_text("tampered after projection", encoding="utf-8")
        self.assertIn("evidence changed", self.process("start", str(downstream), ok=False))
        log.write_text("synthetic check", encoding="utf-8")
        result = self.process("start", str(downstream))
        self.assertEqual(result["phase"], "owner")
        self.assertEqual(result["dispatch"]["task"], "T01")

    def test_sol_model_policy_rejects_fallback_and_remote_operation(self):
        self.start()
        self.event("claim")
        with self.assertRaisesRegex(GateError, "actual Sol"):
            self.event("attach", session="owner", model="gpt-6-astra")
        with self.assertRaisesRegex(GateError, "attached dispatch"):
            self.event("push")
        self.assertEqual(self.event("attach", session="owner", model=MODEL)["dispatch"]["model"], MODEL)
        self.assertFalse(self.cli("status")["dispatch"]["remote_actions_authorized"])


if __name__ == "__main__":
    unittest.main()
