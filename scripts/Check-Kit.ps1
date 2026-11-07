$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
foreach ($module in @('flow','budget')) {
    Push-Location (Join-Path $root "tools\$module")
    try {
        go test -count=1 ./...
        if ($LASTEXITCODE -ne 0) { throw "$module tests failed" }
        go vet ./...
        if ($LASTEXITCODE -ne 0) { throw "$module vet failed" }
    } finally { Pop-Location }
}
Push-Location (Join-Path $root 'tools\flow')
try {
    go run . validate --root $root
    if ($LASTEXITCODE -ne 0) { throw 'Workflow validation failed' }
    go run . ready --root $root
    if ($LASTEXITCODE -ne 0) { throw 'Ready-task query failed' }
} finally { Pop-Location }
Write-Host 'Kit checks passed. This does not verify a Windows product or its memory use.'
