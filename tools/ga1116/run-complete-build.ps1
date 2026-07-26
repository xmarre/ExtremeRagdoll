$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1115/run-complete-build.ps1')
& (Join-Path $repo 'tools/ga1116/build-ga1116-single-usage-launch-bridge.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1115-output" -OutputRoot "$env:RUNNER_TEMP\ga1116-output"
Write-Host 'GA1116_COMPLETE_BUILD=SUCCESS'
