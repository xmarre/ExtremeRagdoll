$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1113/reconstruct-payloads.ps1')
& (Join-Path $repo 'tools/ga1112/run-complete-build.ps1')
& (Join-Path $repo 'tools/ga1113/build-ga1113-camera-split-crash.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1112-output" -OutputRoot "$env:RUNNER_TEMP\ga1113-output"
Write-Host 'GA1113_COMPLETE_BUILD=SUCCESS'