# Optional developer evidence fetcher. Never runs upstream source and never needs Node.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Id)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$manifest=Get-Content -LiteralPath (Join-Path $root 'research\sources.json') -Raw | ConvertFrom-Json
$matches=@($manifest.sources | Where-Object { $_.id -eq $Id })
if ($matches.Count -ne 1) { throw 'Unknown/ambiguous source ID' }
$entry=$matches[0]
$dir=Join-Path $root ".source-cache\$Id"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$file=Join-Path $dir ([IO.Path]::GetFileName($entry.path))
$temp="$file.partial"
curl.exe --fail --location --retry 2 --connect-timeout 15 --max-time 60 --output $temp $entry.raw_url
if ($LASTEXITCODE -ne 0) { Remove-Item $temp -ErrorAction SilentlyContinue; throw 'Source download failed' }
$bytes=[IO.File]::ReadAllBytes($temp)
$header=[Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
$stream=[IO.MemoryStream]::new()
try {
    $stream.Write($header,0,$header.Length); $stream.Write($bytes,0,$bytes.Length); $stream.Position=0
    $sha=[Security.Cryptography.SHA1]::Create()
    try { $actual=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
} finally { $stream.Dispose() }
if ($null -ne $entry.git_blob_sha1 -and $actual -ne $entry.git_blob_sha1) {
    throw "Source changed: expected $($entry.git_blob_sha1), got $actual. Re-audit; partial file retained as evidence."
}
Move-Item -LiteralPath $temp -Destination $file -Force
Write-Host "Saved $file; Git blob SHA1=$actual. Read relevant symbols and LICENSE before reuse."
if ($null -eq $entry.git_blob_sha1) { Write-Warning 'Prior-turn entry: no pinned blob was available. This fetch is NOT a completed re-audit.' }
