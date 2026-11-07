// budget checks external process-tree samples. It does NOT prove lifetime peaks.
package main

import (
	"encoding/csv"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"math"
	"os"
	"sort"
	"strconv"
)

const mib = 1048576.0

type Sample struct {
	Elapsed, Private, Working, CPU float64
	Count, Root                    int
}
type Report struct {
	Status            string   `json:"status"`
	Phase             string   `json:"phase"`
	Samples           int      `json:"samples"`
	Duration          float64  `json:"duration_seconds"`
	Peak              float64  `json:"sampled_peak_private_mib"`
	P95               float64  `json:"p95_private_mib"`
	CPU               float64  `json:"mean_cpu_one_core_pct"`
	Errors            []string `json:"errors"`
	RequiresPeakProof bool     `json:"requires_lifetime_peak_proof"`
}

func load(r io.Reader) ([]Sample, error) {
	c := csv.NewReader(r)
	header, err := c.Read()
	if err != nil {
		return nil, err
	}
	want := []string{"elapsed_seconds", "private_bytes", "working_set_bytes", "cpu_total_seconds", "process_count", "root_alive"}
	if len(header) != len(want) {
		return nil, errors.New("wrong header")
	}
	for i, v := range want {
		if header[i] != v {
			return nil, errors.New("wrong header")
		}
	}
	var out []Sample
	for {
		row, e := c.Read()
		if e == io.EOF {
			break
		}
		if e != nil {
			return nil, e
		}
		if len(row) != 6 {
			return nil, errors.New("wrong column count")
		}
		v := make([]float64, 6)
		for i, s := range row {
			v[i], e = strconv.ParseFloat(s, 64)
			if e != nil || math.IsNaN(v[i]) || math.IsInf(v[i], 0) || v[i] < 0 {
				return nil, errors.New("invalid numeric sample")
			}
		}
		if v[4] != math.Trunc(v[4]) || v[5] != math.Trunc(v[5]) || v[5] > 1 {
			return nil, errors.New("invalid process fields")
		}
		s := Sample{v[0], v[1], v[2], v[3], int(v[4]), int(v[5])}
		if len(out) > 0 {
			p := out[len(out)-1]
			if s.Elapsed <= p.Elapsed || s.CPU < p.CPU {
				return nil, errors.New("time/CPU counters not monotonic")
			}
		}
		out = append(out, s)
	}
	if len(out) < 2 {
		return nil, errors.New("need at least two samples")
	}
	return out, nil
}
func quantile(v []float64, p float64) float64 {
	x := append([]float64(nil), v...)
	sort.Float64s(x)
	z := float64(len(x)-1) * p
	a := int(math.Floor(z))
	b := int(math.Ceil(z))
	return x[a] + (x[b]-x[a])*(z-float64(a))
}
func evaluate(s []Sample, phase string, minSeconds float64) (Report, error) {
	targets := map[string]float64{"tray": 25, "window": 35, "probe": 40}
	target, ok := targets[phase]
	if !ok {
		return Report{}, errors.New("phase must be tray, window or probe")
	}
	if minSeconds <= 0 || math.IsNaN(minSeconds) || math.IsInf(minSeconds, 0) {
		return Report{}, errors.New("invalid minimum duration")
	}
	if len(s) < 2 {
		return Report{}, errors.New("too few samples")
	}
	r := Report{Status: "SAMPLED_PASS_REQUIRES_PEAK_PROOF", Phase: phase, Samples: len(s), RequiresPeakProof: true, Errors: []string{}}
	r.Duration = s[len(s)-1].Elapsed - s[0].Elapsed
	if r.Duration <= 0 {
		return Report{}, errors.New("invalid duration")
	}
	var values []float64
	for i, x := range s {
		values = append(values, x.Private/mib)
		if x.Private/mib > r.Peak {
			r.Peak = x.Private / mib
		}
		if x.Root != 1 || x.Count < 1 {
			r.Errors = append(r.Errors, "root/owned-process coverage incomplete")
		}
		if i > 0 && x.Elapsed-s[i-1].Elapsed > 2.5 {
			r.Errors = append(r.Errors, "sampling gap exceeds 2.5 seconds")
		}
	}
	r.P95 = quantile(values, .95)
	r.CPU = (s[len(s)-1].CPU - s[0].CPU) / r.Duration * 100
	if r.Duration < minSeconds {
		r.Errors = append(r.Errors, "run shorter than requested scenario")
	}
	if r.Peak > 40 {
		r.Errors = append(r.Errors, "40 MiB hard sampled limit exceeded")
	}
	if r.P95 > target {
		r.Errors = append(r.Errors, fmt.Sprintf("%g MiB phase target exceeded", target))
	}
	if phase == "tray" && r.CPU > .5 {
		r.Errors = append(r.Errors, "tray CPU target exceeded")
	}
	if len(r.Errors) > 0 {
		r.Status = "FAIL"
	}
	return r, nil
}
func run(args []string) error {
	f := flag.NewFlagSet("budget", flag.ContinueOnError)
	file := f.String("csv", "", "process-tree sample CSV")
	phase := f.String("phase", "tray", "tray/window/probe")
	duration := f.Float64("min-seconds", 600, "required observed duration; final soak uses86400")
	if err := f.Parse(args); err != nil {
		return err
	}
	if *file == "" {
		return errors.New("--csv is required")
	}
	h, err := os.Open(*file)
	if err != nil {
		return err
	}
	defer h.Close()
	samples, err := load(h)
	if err != nil {
		return err
	}
	report, err := evaluate(samples, *phase, *duration)
	if err != nil {
		return err
	}
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	if err = enc.Encode(report); err != nil {
		return err
	}
	if report.Status == "FAIL" {
		return errors.New("sampled resource gate failed")
	}
	return nil
}
func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
}
