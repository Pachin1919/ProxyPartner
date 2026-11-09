//go:build windows

// This fixture only starts copies of itself and commits private anonymous memory.
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"syscall"
	"time"
)

func must(err error) {
	if err != nil {
		panic(err)
	}
}

func waitFile(path string) {
	deadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(deadline) {
		if _, err := os.Stat(path); err == nil {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	panic("fixture signal timed out: " + path)
}

func main() {
	mode, dir := os.Args[1], os.Args[2]
	switch mode {
	case "child":
		size, err := strconv.Atoi(os.Args[3])
		must(err)
		pause, err := strconv.Atoi(os.Args[4])
		must(err)
		alloc := syscall.NewLazyDLL("kernel32.dll").NewProc("VirtualAlloc")
		address, _, winErr := alloc.Call(0, uintptr(size), 0x3000, 4) // MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE.
		if address == 0 {
			panic(winErr)
		}
		must(os.WriteFile(filepath.Join(dir, "allocated.txt"), []byte(fmt.Sprintf("%d,%d,%s\n", os.Getpid(), size,
			time.Now().UTC().Format(time.RFC3339Nano))), 0600))
		time.Sleep(time.Duration(pause) * time.Millisecond)
		must(os.WriteFile(filepath.Join(dir, "child-end.txt"), []byte(time.Now().UTC().Format(time.RFC3339Nano)), 0600))
	case "root":
		if os.Args[3] != "" {
			waitFile(filepath.Join(dir, os.Args[3]))
		}
		if os.Args[4] != "0" {
			exe, err := os.Executable()
			must(err)
			started := time.Now().UTC().Format(time.RFC3339Nano)
			child := exec.Command(exe, "child", dir, os.Args[4], os.Args[5])
			must(child.Start())
			must(os.WriteFile(filepath.Join(dir, "child-start.txt"), []byte(fmt.Sprintf("%d,%s\n", child.Process.Pid, started)), 0600))
			if os.Args[6] == "exit-first" {
				waitFile(filepath.Join(dir, "allocated.txt"))
				must(os.WriteFile(filepath.Join(dir, "root-end.txt"), []byte(time.Now().UTC().Format(time.RFC3339Nano)), 0600))
				return
			}
			must(child.Wait())
			must(os.WriteFile(filepath.Join(dir, "child-wait.txt"), []byte(time.Now().UTC().Format(time.RFC3339Nano)), 0600))
		}
		if os.Args[6] == "finish" {
			waitFile(filepath.Join(dir, "finish.txt"))
		}
	case "argv":
		if len(os.Args) != 6 || os.Args[3] != "" || os.Args[4] != "a \"quote\"" || os.Args[5] != "trailing slash\\" {
			panic("argv mismatch")
		}
	case "fail":
		os.Exit(7)
	default:
		panic("unknown fixture mode")
	}
}
