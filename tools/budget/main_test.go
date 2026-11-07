package main

import (
	"math"
	"strings"
	"testing"
)

const header = "elapsed_seconds,private_bytes,working_set_bytes,cpu_total_seconds,process_count,root_alive\n"

func ss(m float64) []Sample {
	return []Sample{{0, m * mib, 2 * mib, 0, 1, 1}, {1, m * mib, 2 * mib, .001, 1, 1}, {2, m * mib, 2 * mib, .002, 1, 1}}
}
func TestValidCSV(t *testing.T) {
	s, e := load(strings.NewReader(header + "0,100,100,0,1,1\n1,110,100,0.001,1,1\n"))
	if e != nil || len(s) != 2 {
		t.Fatal(e)
	}
}
func TestBadHeader(t *testing.T) {
	_, e := load(strings.NewReader("bad\n1\n"))
	if e == nil {
		t.Fatal("accepted")
	}
}
func TestNaNRejected(t *testing.T) {
	_, e := load(strings.NewReader(header + "0,NaN,100,0,1,1\n1,110,100,0,1,1\n"))
	if e == nil {
		t.Fatal("accepted")
	}
}
func TestNonmonotonic(t *testing.T) {
	_, e := load(strings.NewReader(header + "1,100,100,0,1,1\n0,100,100,0,1,1\n"))
	if e == nil {
		t.Fatal("accepted")
	}
}
func TestTrayPassNotLifetimeClaim(t *testing.T) {
	r, e := evaluate(ss(20), "tray", 2)
	if e != nil || r.Status != "SAMPLED_PASS_REQUIRES_PEAK_PROOF" || !r.RequiresPeakProof {
		t.Fatal(r, e)
	}
}
func TestTrayTarget(t *testing.T) {
	r, _ := evaluate(ss(26), "tray", 2)
	if r.Status != "FAIL" {
		t.Fatal(r)
	}
}
func TestWorkerTotalOverHardGate(t *testing.T) {
	s := ss(20)
	s[1].Private = 41 * mib
	s[1].Count = 2
	r, _ := evaluate(s, "probe", 2)
	if r.Status != "FAIL" {
		t.Fatal(r)
	}
}
func TestRootExited(t *testing.T) {
	s := ss(20)
	s[2].Root = 0
	r, _ := evaluate(s, "tray", 2)
	if r.Status != "FAIL" {
		t.Fatal(r)
	}
}
func TestCPU(t *testing.T) {
	s := ss(20)
	s[2].CPU = 1
	r, _ := evaluate(s, "tray", 2)
	if r.Status != "FAIL" {
		t.Fatal(r)
	}
}
func TestShortRun(t *testing.T) {
	r, _ := evaluate(ss(20), "tray", 600)
	if r.Status != "FAIL" {
		t.Fatal(r)
	}
}
func TestSamplingGap(t *testing.T) {
	s := ss(20)
	s[2].Elapsed = 5
	r, _ := evaluate(s, "tray", 2)
	if r.Status != "FAIL" {
		t.Fatal(r)
	}
}
func TestBadPhaseAndDuration(t *testing.T) {
	_, e := evaluate(ss(20), "anything", 2)
	if e == nil {
		t.Fatal("accepted")
	}
	_, e = evaluate(ss(20), "tray", math.NaN())
	if e == nil {
		t.Fatal("accepted")
	}
}
func TestQuantile(t *testing.T) {
	if math.Abs(quantile([]float64{20, 10, 30}, .95)-29) > 1e-9 {
		t.Fatal("R7 wrong")
	}
}
