"""Disk and Git checks at the file adapter's trust boundary."""
import hashlib
import json
import re
import subprocess
from pathlib import Path

from jsonschema import Draft202012Validator


class GateError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise GateError(message)


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(worktree, *args):
    result = subprocess.run(
        ["git", "-C", str(worktree), *args],
        capture_output=True, encoding="utf-8", errors="replace",
    )
    require(result.returncode == 0, f"git {args[0]} failed in {worktree}: {result.stderr.strip()}")
    return result.stdout.strip()


def head(worktree):
    return git(worktree, "rev-parse", "HEAD")


def verify_worktree(root, worktree, expected, *, clean=True):
    require(
        Path(git(worktree, "rev-parse", "--show-toplevel")).resolve() == worktree,
        "expected the root of a Git worktree",
    )
    require(
        git(root, "rev-parse", "--path-format=absolute", "--git-common-dir")
        == git(worktree, "rev-parse", "--path-format=absolute", "--git-common-dir"),
        "worktree belongs to another repository",
    )
    require(head(worktree) == expected, f"unexpected HEAD in {worktree}")
    if clean:
        pathspec = ("--", ".", ":(exclude)workflow/state.json") if worktree == root else ()
        require(not git(worktree, "status", "--porcelain", "--untracked-files=normal", *pathspec),
                f"worktree is dirty: {worktree}")


def file_under(root, rel):
    require(isinstance(rel, str) and rel and ":" not in rel and "\\" not in rel
            and not Path(rel).is_absolute(), "expected a portable relative file path")
    path = (root / rel).resolve()
    require(path.is_relative_to(root) and path.is_file(), f"file unavailable inside root: {rel}")
    return path


def receipt(root, policy, task, rel, expected, owner, *, base=None, verifier=None):
    path = file_under(root, rel)
    # Hash and parse the same bytes so the stored digest binds the checked JSON.
    raw = path.read_bytes()
    data = json.loads(raw)
    schema = read(root / policy["receipt_schema"])
    error = next(Draft202012Validator(schema).iter_errors(data), None)
    require(error is None, f"receipt schema violation: {error}")
    require(data["task_id"] == task["id"] and data["head_sha"] == expected,
            "receipt task or SHA mismatch")
    require(base is None or data["base_sha"] == base, "receipt base mismatch")
    require(data["implementer_run"] == owner and data["verifier_run"] != owner,
            "receipt must identify the owner and an independent verifier")
    require(verifier is None or data["verifier_run"] == verifier, "receipt verifier mismatch")
    checks = {check["id"]: check for check in data["checks"]}
    require(len(checks) == len(data["checks"]) and all(checks), "duplicate or empty check ID")
    required = set(task["required_checks"])
    if data["verdict"] != "BLOCKED":
        require(required <= checks.keys(), "missing required checks")
    for check in checks.values():
        require(check["command"].strip(), "empty check command")
        if data["verdict"] == "PASS":
            require(check["passed"] and check["exit_code"] == 0, "PASS includes a failed check")
        for artifact in check["evidence"]:
            require(digest(file_under(root, artifact["path"])) == artifact["sha256"],
                    f"evidence changed: {artifact['path']}")
    return data, hashlib.sha256(raw).hexdigest()


def check_revision(root, worktree, base, candidate, scope):
    require(isinstance(candidate, str) and re.fullmatch(r"[a-f0-9]{40}|[a-f0-9]{64}", candidate)
            and base != candidate, "expected a new full candidate SHA")
    verify_worktree(root, worktree, candidate)
    git(worktree, "merge-base", "--is-ancestor", base, candidate)
    paths = git(worktree, "-c", "core.quotePath=false", "diff", "--name-only", "--no-renames",
                "-z", base, candidate).rstrip("\0").split("\0")
    require(paths != [""] and "workflow/state.json" not in paths and all(
        any(path.startswith(entry) if entry.endswith("/") else path == entry for entry in scope)
        for path in paths
    ), "candidate changes paths outside the brief or has no diff")


def authorized_manifest(root, task, policy, manifest, coordinator_branch):
    fields = ("task", "run", "base_sha", "owner_worktree", "owner_branch", "coordinator_session",
              "bootstrap_report")
    require(isinstance(manifest, dict)
            and set(manifest) == set(fields) | {"allowed_paths", "acceptance_commands"}
            and all(isinstance(manifest[key], str) and manifest[key].strip() for key in fields),
            "invalid coordinator authorization manifest")
    scope = manifest["allowed_paths"]
    require(isinstance(scope, list) and scope
            and all(isinstance(entry, str) and entry and ":" not in entry and "\\" not in entry
                    and not Path(entry).is_absolute() and ".." not in Path(entry).parts
                    and any(entry.startswith(limit) if limit.endswith("/") else entry == limit
                            for limit in task["scope"]) for entry in scope),
            "brief paths must be contained in task scope")
    commands = manifest["acceptance_commands"]
    require(isinstance(commands, dict) and commands.keys() == set(task["required_checks"])
            and all(isinstance(command, str) and command.strip() for command in commands.values()),
            "brief must supply a command for each required check")
    require(manifest["task"] == task["id"] and re.fullmatch(r"[A-Za-z0-9_-]+", manifest["run"]),
            "invalid task or run in authorization")
    require(manifest["base_sha"] == head(root), "authorization does not match coordinator HEAD")
    owner = Path(manifest["owner_worktree"]).resolve()
    require(owner != root and manifest["owner_branch"] != coordinator_branch,
            "owner requires a separate worktree and branch")
    verify_worktree(root, owner, manifest["base_sha"])
    require(git(owner, "branch", "--show-current") == manifest["owner_branch"], "wrong owner branch")
    bootstrap_path = file_under(root, manifest["bootstrap_report"])
    bootstrap = read(bootstrap_path)
    require(isinstance(bootstrap, dict)
            and bootstrap.get("checks") == dict.fromkeys(policy["bootstrap_checks"], "PASS")
            and bootstrap.get("model") == policy["model"]
            and re.fullmatch(r"[a-f0-9]{40}|[a-f0-9]{64}", str(bootstrap.get("head_sha"))),
            "coordinator bootstrap report must attest all checks at a full SHA")
    git(root, "merge-base", "--is-ancestor", bootstrap["head_sha"], manifest["base_sha"])
    return {**manifest, "owner_worktree": str(owner), "bootstrap_hash": digest(bootstrap_path)}
