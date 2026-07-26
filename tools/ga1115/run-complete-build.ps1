$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1114/run-complete-build.ps1')
$sourceBuild=Join-Path $repo 'tools/ga1115/build-ga1115-split-spawn-recovery.ps1'
$runtimeBuild=Join-Path $env:RUNNER_TEMP 'build-ga1115-split-spawn-recovery.ps1'
$text=[IO.File]::ReadAllText($sourceBuild).Replace('7c2fd6ad9a4dd483a46b88ad3345d25d74daab179a73f5b35c5e2314cdd249c4','f747f3bbd64f1ed45f3ee4615c0f5cca5e655eac1bf2faf874d7c3c5f02fd200')
[IO.File]::WriteAllText($runtimeBuild,$text,[Text.UTF8Encoding]::new($false))
& $runtimeBuild -BaseOutputRoot "$env:RUNNER_TEMP\ga1114-output" -OutputRoot "$env:RUNNER_TEMP\ga1115-output"
Write-Host 'GA1115_COMPLETE_BUILD=SUCCESS'
