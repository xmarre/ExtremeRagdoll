$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Assert-Hash([string]$Path,[string]$Expected,[string]$Label){
    if(!(Test-Path -LiteralPath $Path)){throw "${Label} missing: $Path"}
    $actual=Get-Sha256 $Path
    if($actual -ne $Expected.ToLowerInvariant()){throw "${Label} hash mismatch: expected $Expected, observed $actual"}
}
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
$payloadRoot=Join-Path $repo 'tools/ga1117'
$encoded=Join-Path $payloadRoot 'runtime-payload.zip.b64'
if(!(Test-Path -LiteralPath $encoded)){throw "v1.1.17 encoded payload missing: $encoded"}
$clean=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($encoded),'\s+','')
$bytes=[Convert]::FromBase64String($clean)
$zip=Join-Path $env:RUNNER_TEMP 'ga1117-runtime-payload.zip'
[IO.File]::WriteAllBytes($zip,$bytes)
Assert-Hash $zip '808be75420bae2df9d37fe2b336bdbbe77f39869940f436d2c363ebf09c78bd1' 'v1.1.17 runtime payload zip'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$payloadRoot,$true)
Assert-Hash (Join-Path $payloadRoot 'GuidedArrow-v1.1.16-to-v1.1.17-Collision-Entity.patch') '50da8631284c3de58a0538f5fbe4b9192e064efa506c1f05a0f583f0bfe72dce' 'v1.1.17 patch'
Assert-Hash (Join-Path $payloadRoot 'InspectMissilePatchContract.cs') 'f6685d1c0a2565ad2eb7290f784c3c496d480040e0d3e8e65668e59d8b07290b' 'v1.1.17 inspector'
Assert-Hash (Join-Path $payloadRoot 'build-ga1117-collision-entity.ps1') '2437ee1a8c4540eb57210b4cef858359e1b3868b8458d75cc7012d5067ea4495' 'v1.1.17 build script'
Assert-Hash (Join-Path $payloadRoot 'test-ga1117-collision-entity.py') 'd441d0ac9cb06095ba908e529b95e555f295074ade2f16ab1b71a2fff7399565' 'v1.1.17 test'
& (Join-Path $repo 'tools/ga1116/run-complete-build.ps1')
& (Join-Path $payloadRoot 'build-ga1117-collision-entity.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1116-output" -OutputRoot "$env:RUNNER_TEMP\ga1117-output"
Write-Host 'GA1117_COMPLETE_BUILD=SUCCESS'
