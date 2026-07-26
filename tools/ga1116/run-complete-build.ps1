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
Assert-Hash $zip '959b897af624f3f8928baf612f378571fd2e5a7c4b01c6aa9dc0230444dc4cfa' 'v1.1.16 runtime payload zip'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$payloadRoot,$true)
Assert-Hash (Join-Path $payloadRoot 'GuidedArrow-v1.1.15-to-v1.1.16-Single-Usage-Capture.patch') '2b34735164cf0a706faf9da7e7057dfe70fc4a1caf22e4cc52f3b6cbf59e4d2b' 'v1.1.16 patch'
Assert-Hash (Join-Path $payloadRoot 'InspectMissileCallGraph.cs') '3d54e93aae37f7078fef6983b4471dbe283568df01fbfc7ca38be8c01bbb26ef' 'v1.1.16 inspector'
Assert-Hash (Join-Path $payloadRoot 'build-ga1116-single-usage-capture.ps1') '2db067cd08ab888848401943bdd89c1e340793da071cc9514fd4e198869c436c' 'v1.1.16 build script'
Assert-Hash (Join-Path $payloadRoot 'test-ga1116-single-usage-capture.py') 'e419a685c2a08541f7bd2d5a7c4c3eb0b036bf41710cca23e5530abbdd637142' 'v1.1.16 test'
& (Join-Path $repo 'tools/ga1115/run-complete-build.ps1')
& (Join-Path $payloadRoot 'build-ga1116-single-usage-capture.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1115-output" -OutputRoot "$env:RUNNER_TEMP\ga1116-output"
Write-Host 'GA1116_COMPLETE_BUILD=SUCCESS'
