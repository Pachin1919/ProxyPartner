# budget — sampled performance gate
Development-only Go standard-library tool. Not bundled with the product.

From this directory:
`go test ./...`
`go run . --csv ../../runs/perf/samples.csv --phase tray --min-seconds 600`

Consumes Measure-ProcessTree.ps1 CSV; validates numeric/time/process coverage and checks sampled total product-process PrivateBytes. Phases: tray25MiB p95; window35; probe40; all sampled peaks≤40. Tray CPU uses one-core-equivalent percentage. Exit2 indicates failure.

A zero exit is explicitly **SAMPLED_PASS_REQUIRES_PEAK_PROOF**. Polling can miss short child processes and spikes. Final acceptance requires Windows Job Object/ETW lifecycle evidence and 24h trend analysis; this tool cannot create such evidence. No fake fixture timings are shipped as real measurements.
