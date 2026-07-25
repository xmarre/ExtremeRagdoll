$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1113/run-complete-build.ps1')
& (Join-Path $repo 'tools/ga1114/build-ga1114-resolved-split-damage.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1113-output" -OutputRoot "$env:RUNNER_TEMP\ga1114-output"
Write-Host 'GA1114_COMPLETE_BUILD=SUCCESS'
