// flow is a local workflow consistency checker, not an agent executor.
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

type Task struct {
	ID             string   `json:"id"`
	Title          string   `json:"title"`
	DependsOn      []string `json:"depends_on"`
	Role           string   `json:"role"`
	Scope          []string `json:"scope"`
	Goal           string   `json:"goal"`
	RequiredChecks []string `json:"required_checks"`
	EvidenceRule   string   `json:"evidence_rule"`
}
type Plan struct {
	SchemaVersion      int    `json:"schema_version"`
	Name               string `json:"name"`
	MaxParallelWriters int    `json:"max_parallel_writers"`
	MaxRepairs         int    `json:"max_same_cause_repairs"`
	Tasks              []Task `json:"tasks"`
}
type Entry struct {
	Status      string `json:"status"`
	OwnerRun    string `json:"owner_run"`
	HeadSHA     string `json:"head_sha"`
	ReceiptPath string `json:"receipt_path"`
}
type State struct {
	SchemaVersion int              `json:"schema_version"`
	Tasks         map[string]Entry `json:"tasks"`
}
type Artifact struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
}
type Check struct {
	Command  string     `json:"command"`
	ID       string     `json:"id"`
	Passed   bool       `json:"passed"`
	ExitCode *int       `json:"exit_code"`
	Evidence []Artifact `json:"evidence"`
}
type Receipt struct {
	SchemaVersion  int     `json:"schema_version"`
	TaskID         string  `json:"task_id"`
	HeadSHA        string  `json:"head_sha"`
	BaseSHA        string  `json:"base_sha"`
	ImplementerRun string  `json:"implementer_run"`
	VerifierRun    string  `json:"verifier_run"`
	Verdict        string  `json:"verdict"`
	Checks         []Check `json:"checks"`
}

var gitSHA = regexp.MustCompile(`^([a-f0-9]{40}|[a-f0-9]{64})$`)
var digestSHA = regexp.MustCompile(`^[a-f0-9]{64}$`)

func readJSON(path string, dst any) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	if len(b) > 8<<20 {
		return errors.New("JSON document exceeds 8 MiB")
	}
	d := json.NewDecoder(strings.NewReader(string(b)))
	d.DisallowUnknownFields()
	if err = d.Decode(dst); err != nil {
		return fmt.Errorf("%s: %w", path, err)
	}
	var extra any
	if err = d.Decode(&extra); err != io.EOF {
		return errors.New("expected exactly one JSON document")
	}
	return nil
}
func validate(p Plan, s State) error {
	if p.SchemaVersion != 1 || s.SchemaVersion != 1 {
		return errors.New("unsupported schema version")
	}
	if len(p.Tasks) == 0 {
		return errors.New("empty graph")
	}
	ids := map[string]Task{}
	for _, t := range p.Tasks {
		if t.ID == "" || t.Title == "" || t.Goal == "" || len(t.Scope) == 0 || len(t.RequiredChecks) == 0 {
			return fmt.Errorf("incomplete task %q", t.ID)
		}
		if _, ok := ids[t.ID]; ok {
			return fmt.Errorf("duplicate task %s", t.ID)
		}
		c := map[string]bool{}
		for _, id := range t.RequiredChecks {
			if id == "" || c[id] {
				return fmt.Errorf("empty/duplicate check in %s", t.ID)
			}
			c[id] = true
		}
		ids[t.ID] = t
	}
	for _, t := range p.Tasks {
		for _, d := range t.DependsOn {
			if _, ok := ids[d]; !ok {
				return fmt.Errorf("unknown dependency %s", d)
			}
		}
	}
	color := map[string]int{}
	var visit func(string) error
	visit = func(id string) error {
		if color[id] == 1 {
			return fmt.Errorf("dependency cycle at %s", id)
		}
		if color[id] == 2 {
			return nil
		}
		color[id] = 1
		for _, d := range ids[id].DependsOn {
			if err := visit(d); err != nil {
				return err
			}
		}
		color[id] = 2
		return nil
	}
	for id := range ids {
		if err := visit(id); err != nil {
			return err
		}
	}
	allowed := map[string]bool{"pending": true, "running": true, "implemented": true, "verifying": true, "needs_fix": true, "verified": true, "merge_ready": true, "done": true, "blocked": true, "paused": true}
	for id := range s.Tasks {
		if _, ok := ids[id]; !ok {
			return fmt.Errorf("state has unknown task %s", id)
		}
	}
	for id := range ids {
		e, ok := s.Tasks[id]
		if !ok {
			return fmt.Errorf("state missing task %s", id)
		}
		if !allowed[e.Status] {
			return fmt.Errorf("invalid state %q for %s", e.Status, id)
		}
		if e.Status == "done" || e.Status == "verified" || e.Status == "merge_ready" {
			if !gitSHA.MatchString(e.HeadSHA) || e.ReceiptPath == "" {
				return fmt.Errorf("%s lacks SHA/receipt", id)
			}
		}
		if e.Status == "done" {
			for _, d := range ids[id].DependsOn {
				if s.Tasks[d].Status != "done" {
					return fmt.Errorf("done task %s has incomplete dependency %s", id, d)
				}
			}
		}
	}
	return nil
}
func ready(p Plan, s State) []Task {
	out := []Task{}
	for _, t := range p.Tasks {
		status := s.Tasks[t.ID].Status
		if status != "pending" && status != "needs_fix" {
			continue
		}
		ok := true
		for _, d := range t.DependsOn {
			if s.Tasks[d].Status != "done" {
				ok = false
			}
		}
		if ok {
			out = append(out, t)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

// Resolve symlinks and reject paths escaping the root, including traversal.
func safeFile(root, rel string) (string, error) {
	if rel == "" || filepath.IsAbs(rel) {
		return "", errors.New("evidence path must be relative")
	}
	// A receipt produced on Windows must not sneak an absolute path into a Unix check.
	if strings.Contains(rel, ":") || strings.Contains(rel, "\\") {
		return "", errors.New("use portable relative slash paths")
	}
	r, err := filepath.Abs(root)
	if err != nil {
		return "", err
	}
	r, err = filepath.EvalSymlinks(r)
	if err != nil {
		return "", err
	}
	p, err := filepath.EvalSymlinks(filepath.Join(r, filepath.FromSlash(rel)))
	if err != nil {
		return "", err
	}
	sub, err := filepath.Rel(r, p)
	if err != nil {
		return "", err
	}
	if sub == ".." || strings.HasPrefix(sub, ".."+string(filepath.Separator)) {
		return "", errors.New("evidence escapes repository")
	}
	info, err := os.Stat(p)
	if err != nil {
		return "", err
	}
	if !info.Mode().IsRegular() {
		return "", errors.New("evidence is not a regular file")
	}
	return p, nil
}
func checkReceipt(root string, t Task, r Receipt, expected string) error {
	if r.SchemaVersion != 1 || r.TaskID != t.ID {
		return errors.New("receipt schema/task mismatch")
	}
	if !gitSHA.MatchString(expected) || r.HeadSHA != expected || !gitSHA.MatchString(r.BaseSHA) {
		return errors.New("stale or invalid SHA")
	}
	if r.ImplementerRun == "" || r.VerifierRun == "" || r.ImplementerRun == r.VerifierRun {
		return errors.New("independent verifier run required")
	}
	if r.Verdict != "PASS" {
		return fmt.Errorf("verdict %s is not PASS", r.Verdict)
	}
	seen := map[string]bool{}
	for _, c := range r.Checks {
		if c.ID == "" || seen[c.ID] {
			return errors.New("duplicate/empty receipt check")
		}
		seen[c.ID] = true
		if !c.Passed || c.ExitCode == nil || *c.ExitCode != 0 || strings.TrimSpace(c.Command) == "" || len(c.Evidence) == 0 {
			return fmt.Errorf("check %s lacks passing evidence", c.ID)
		}
		for _, a := range c.Evidence {
			if !digestSHA.MatchString(a.SHA256) {
				return errors.New("invalid artifact digest")
			}
			p, err := safeFile(root, a.Path)
			if err != nil {
				return err
			}
			f, err := os.Open(p)
			if err != nil {
				return err
			}
			h := sha256.New()
			_, copyErr := io.Copy(h, f)
			closeErr := f.Close()
			if copyErr != nil {
				return copyErr
			}
			if closeErr != nil {
				return closeErr
			}
			if hex.EncodeToString(h.Sum(nil)) != a.SHA256 {
				return fmt.Errorf("artifact changed: %s", a.Path)
			}
		}
	}
	for _, id := range t.RequiredChecks {
		if !seen[id] {
			return fmt.Errorf("missing required check %s", id)
		}
	}
	return nil
}
func graph(p Plan) string {
	var b strings.Builder
	b.WriteString("flowchart TD\n")
	for _, t := range p.Tasks {
		label := strings.ReplaceAll(t.ID+" "+t.Title, "\"", "'")
		fmt.Fprintf(&b, "  %s[\"%s\"]\n", t.ID, label)
	}
	for _, t := range p.Tasks {
		for _, d := range t.DependsOn {
			fmt.Fprintf(&b, "  %s --> %s\n", d, t.ID)
		}
	}
	return b.String()
}
func run(args []string) error {
	if len(args) == 0 {
		return errors.New("usage: flow validate|ready|graph|check --root PATH [--task ID --receipt RELPATH --head SHA]")
	}
	cmd := args[0]
	fs := flag.NewFlagSet(cmd, flag.ContinueOnError)
	root := fs.String("root", "../..", "repository root")
	task := fs.String("task", "", "task id")
	receipt := fs.String("receipt", "", "receipt relative path")
	head := fs.String("head", "", "actual head SHA")
	if err := fs.Parse(args[1:]); err != nil {
		return err
	}
	if fs.NArg() != 0 {
		return errors.New("unexpected positional argument")
	}
	var p Plan
	var s State
	if err := readJSON(filepath.Join(*root, "workflow/tasks.json"), &p); err != nil {
		return err
	}
	if err := readJSON(filepath.Join(*root, "workflow/state.json"), &s); err != nil {
		return err
	}
	if err := validate(p, s); err != nil {
		return err
	}
	// Final states require an existing receipt at the state head, not a checkbox alone.
	for _, t := range p.Tasks {
		e := s.Tasks[t.ID]
		if e.Status == "done" || e.Status == "verified" || e.Status == "merge_ready" {
			path, err := safeFile(*root, e.ReceiptPath)
			if err != nil {
				return err
			}
			var r Receipt
			if err = readJSON(path, &r); err != nil {
				return err
			}
			if err = checkReceipt(*root, t, r, e.HeadSHA); err != nil {
				return fmt.Errorf("state %s: %w", t.ID, err)
			}
		}
	}
	switch cmd {
	case "validate":
		fmt.Printf("PASS: %d tasks; graph/state consistency only, not application verification.\n", len(p.Tasks))
	case "ready":
		b, _ := json.MarshalIndent(ready(p, s), "", "  ")
		fmt.Println(string(b))
	case "graph":
		fmt.Print(graph(p))
	case "check":
		path, err := safeFile(*root, *receipt)
		if err != nil {
			return err
		}
		var r Receipt
		if err = readJSON(path, &r); err != nil {
			return err
		}
		found := false
		for _, t := range p.Tasks {
			if t.ID == *task {
				found = true
				if err = checkReceipt(*root, t, r, *head); err != nil {
					return err
				}
			}
		}
		if !found {
			return errors.New("unknown task")
		}
		fmt.Println("PASS: receipt consistency; execution truth still requires independent verification.")
	default:
		return errors.New("unknown command")
	}
	return nil
}
func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "FAIL:", err)
		os.Exit(2)
	}
}
