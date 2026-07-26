$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1113/run-complete-build.ps1')
$buildSource=Join-Path $repo 'tools/ga1114/build-ga1114-resolved-split-damage.ps1'
$buildText=[IO.File]::ReadAllText($buildSource)
$redundant="Copy-Item `$test (Join-Path `$workspace 'test-ga1114-resolved-split-damage.py')"
if(!$buildText.Contains($redundant)){throw 'Expected v1.1.14 redundant test copy was not found'}
$buildText=$buildText.Replace($redundant,"# Test payload is already decoded at the workspace test path.")
$fixedBuild=Join-Path $env:RUNNER_TEMP 'build-ga1114-resolved-split-damage-fixed.ps1'
[IO.File]::WriteAllText($fixedBuild,$buildText,[Text.UTF8Encoding]::new($false))
& $fixedBuild -BaseOutputRoot "$env:RUNNER_TEMP\ga1113-output" -OutputRoot "$env:RUNNER_TEMP\ga1114-output"
Write-Host 'GA1114_COMPLETE_BUILD=SUCCESS'
