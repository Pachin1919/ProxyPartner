package main

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func fixture() (Plan, State) {
	return Plan{SchemaVersion: 1, Tasks: []Task{{ID: "T00", Title: "a", Goal: "g", Scope: []string{"a/"}, RequiredChecks: []string{"c"}}, {ID: "T01", Title: "b", Goal: "g", Scope: []string{"b/"}, RequiredChecks: []string{"d"}, DependsOn: []string{"T00"}}}}, State{SchemaVersion: 1, Tasks: map[string]Entry{"T00": {Status: "pending"}, "T01": {Status: "pending"}}}
}
func TestReady(t *testing.T) {
	p, s := fixture()
	if err := validate(p, s); err != nil {
		t.Fatal(err)
	}
	r := ready(p, s)
	if len(r) != 1 || r[0].ID != "T00" {
		t.Fatal(r)
	}
}
func TestCycle(t *testing.T) {
	p, s := fixture()
	p.Tasks[0].DependsOn = []string{"T01"}
	if validate(p, s) == nil {
		t.Fatal("cycle accepted")
	}
}
func TestUnknownDependency(t *testing.T) {
	p, s := fixture()
	p.Tasks[0].DependsOn = []string{"X"}
	if validate(p, s) == nil {
		t.Fatal("unknown dep accepted")
	}
}
func TestDoneNeedsEvidence(t *testing.T) {
	p, s := fixture()
	s.Tasks["T00"] = Entry{Status: "done"}
	if validate(p, s) == nil {
		t.Fatal("done without receipt accepted")
	}
}
func goodReceipt(t *testing.T) (string, Task, Receipt) {
	t.Helper()
	root := t.TempDir()
	os.Mkdir(filepath.Join(root, "runs"), 0700)
	b := []byte("fixture evidence only\n")
	os.WriteFile(filepath.Join(root, "runs/check.txt"), b, 0600)
	h := sha256.Sum256(b)
	task := Task{ID: "T00", RequiredChecks: []string{"c"}}
	r := Receipt{SchemaVersion: 1, TaskID: "T00", HeadSHA: strings.Repeat("a", 40), BaseSHA: strings.Repeat("b", 40), ImplementerRun: "impl", VerifierRun: "review", Verdict: "PASS", Checks: []Check{{ID: "c", Passed: true, ExitCode: intPtr(0), Command: "fixture-check (test only)", Evidence: []Artifact{{Path: "runs/check.txt", SHA256: hex.EncodeToString(h[:])}}}}}
	return root, task, r
}
func TestReceiptPass(t *testing.T) {
	root, task, r := goodReceipt(t)
	if err := checkReceipt(root, task, r, r.HeadSHA); err != nil {
		t.Fatal(err)
	}
}
func TestStaleSHA(t *testing.T) {
	root, task, r := goodReceipt(t)
	if checkReceipt(root, task, r, strings.Repeat("c", 40)) == nil {
		t.Fatal("stale accepted")
	}
}
func TestSameVerifier(t *testing.T) {
	root, task, r := goodReceipt(t)
	r.VerifierRun = r.ImplementerRun
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("self review accepted")
	}
}
func TestMissingCheck(t *testing.T) {
	root, task, r := goodReceipt(t)
	task.RequiredChecks = append(task.RequiredChecks, "runtime")
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("missing check accepted")
	}
}
func TestTamperedArtifact(t *testing.T) {
	root, task, r := goodReceipt(t)
	os.WriteFile(filepath.Join(root, "runs/check.txt"), []byte("changed"), 0600)
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("tampered accepted")
	}
}
func TestEscape(t *testing.T) {
	root, task, r := goodReceipt(t)
	r.Checks[0].Evidence[0].Path = "../secret.txt"
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("escape accepted")
	}
}
func TestFailedExit(t *testing.T) {
	root, task, r := goodReceipt(t)
	r.Checks[0].ExitCode = intPtr(1)
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("failed command accepted")
	}
}
func TestGraphEdges(t *testing.T) {
	p, _ := fixture()
	if !strings.Contains(graph(p), "T00 --> T01") {
		t.Fatal("missing dependency edge")
	}
}
func TestSymlinkEscape(t *testing.T) {
	root, task, r := goodReceipt(t)
	outside := t.TempDir()
	path := filepath.Join(outside, "foreign")
	os.WriteFile(path, []byte("foreign"), 0600)
	if err := os.Symlink(path, filepath.Join(root, "runs/link")); err != nil {
		t.Skip("symlinks unavailable")
	}
	r.Checks[0].Evidence[0].Path = "runs/link"
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("symlink escape accepted")
	}
}

func intPtr(v int) *int { return &v }
func TestMissingExitCode(t *testing.T) {
	root, task, r := goodReceipt(t)
	r.Checks[0].ExitCode = nil
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("missing exit_code accepted as zero")
	}
}
func TestMissingCommand(t *testing.T) {
	root, task, r := goodReceipt(t)
	r.Checks[0].Command = " "
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("missing command accepted")
	}
}
func TestBlockedCannotPass(t *testing.T) {
	root, task, r := goodReceipt(t)
	r.Verdict = "BLOCKED"
	if checkReceipt(root, task, r, r.HeadSHA) == nil {
		t.Fatal("blocked receipt accepted")
	}
}
func TestDoneWithIncompleteParent(t *testing.T) {
	p, s := fixture()
	s.Tasks["T01"] = Entry{Status: "done", HeadSHA: strings.Repeat("a", 40), ReceiptPath: "runs/test.json"}
	if validate(p, s) == nil {
		t.Fatal("downstream completed before parent")
	}
}
