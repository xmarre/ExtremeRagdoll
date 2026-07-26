$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1114/run-complete-build.ps1')
& (Join-Path $repo 'tools/ga1115/build-ga1115-split-spawn-recovery.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1114-output" -OutputRoot "$env:RUNNER_TEMP\ga1115-output"
Write-Host 'GA1115_COMPLETE_BUILD=SUCCESS'
