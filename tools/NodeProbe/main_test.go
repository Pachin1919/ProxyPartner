package main

import (
	"os"
	"os/exec"
	"strings"
	"testing"
)

func TestWorkerProcess(t *testing.T) {
	if os.Getenv("PROXYPARTNER_TEST_NODE_PROBE") == "1" {
		main()
	}
}

func TestRejectedInput(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	for name, input := range map[string]string{
		"missing config":    `{}`,
		"unknown target":    `{"type":"anytls","server":"127.0.0.1","port":1,"password":"SECRET","targetUrl":"http://localhost/","timeoutMs":6000}`,
		"invalid transport": `{"type":"mieru","server":"127.0.0.1","port":1,"password":"SECRET","username":"user","transport":"tcp","targetUrl":"https://www.google.com/","timeoutMs":6000}`,
		"unknown field":     `{"password":"SECRET","unknown":true}`,
		"oversized input":   strings.Repeat("x", 65537),
	} {
		t.Run(name, func(t *testing.T) {
			command := exec.Command(executable, "-test.run=^TestWorkerProcess$")
			command.Env = append(os.Environ(), "PROXYPARTNER_TEST_NODE_PROBE=1")
			command.Stdin = strings.NewReader(input)
			output, err := command.CombinedOutput()
			exit, failed := err.(*exec.ExitError)
			if !failed || exit.ExitCode() != 2 {
				t.Fatalf("expected invalid-input exit 2, got %v", err)
			}
			if strings.Contains(string(output), "SECRET") || strings.Count(string(output), "\n") != 1 ||
				!strings.Contains(string(output), `"status"`) {
				t.Fatal("unsafe or missing terminal output")
			}
		})
	}
}
