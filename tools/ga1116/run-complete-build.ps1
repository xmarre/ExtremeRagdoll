$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Assert-Hash([string]$Path,[string]$Expected,[string]$Label){
    if(!(Test-Path -LiteralPath $Path)){throw "${Label} missing: $Path"}
    $actual=Get-Sha256 $Path
    if($actual -ne $Expected){throw "${Label} hash mismatch: expected $Expected, observed $actual"}
}
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
$payloadRoot=Join-Path $repo 'tools/ga1116'
$encoded=Join-Path $payloadRoot 'ga1116-runtime-payload.zip.b64'
if(!(Test-Path -LiteralPath $encoded)){throw "v1.1.16 encoded payload missing: $encoded"}
$clean=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($encoded),'\s+','')
$bytes=[Convert]::FromBase64String($clean)
$zip=Join-Path $env:RUNNER_TEMP 'ga1116-runtime-payload.zip'
[IO.File]::WriteAllBytes($zip,$bytes)
Assert-Hash $zip '7763262fba960c5ce41fc959931273002aa8bebe1a4b7e2c505a8888e5f4ed04' 'v1.1.16 runtime payload zip'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$payloadRoot,$true)
Assert-Hash (Join-Path $payloadRoot 'GuidedArrow-v1.1.15-to-v1.1.16-Single-Usage-Capture.patch') '2d014481fdae2ce5b8a02d1fb13ab6139fa2bd75ec970aa66af4f730c9d8909a' 'v1.1.16 patch'
Assert-Hash (Join-Path $payloadRoot 'InspectMissileCallGraph.cs') '880af9b8bc15c378364c9866ec262b668e802807374cbe65c470e2185a3b0f04' 'v1.1.16 inspector'
Assert-Hash (Join-Path $payloadRoot 'build-ga1116-single-usage-capture.ps1') '0c3c44ddf505f475d698431b9df6d471c5a5679bfa9593b7638c40c2989fa2db' 'v1.1.16 build script'
Assert-Hash (Join-Path $payloadRoot 'test-ga1116-single-usage-capture.py') '921c77bc66353652cdf81dad4cd72e894fd446ef507c6fcb78031793607ac83e' 'v1.1.16 test'
& (Join-Path $repo 'tools/ga1115/run-complete-build.ps1')
& (Join-Path $payloadRoot 'build-ga1116-single-usage-capture.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1115-output" -OutputRoot "$env:RUNNER_TEMP\ga1116-output"
Write-Host 'GA1116_COMPLETE_BUILD=SUCCESS'
