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
$partsRoot=Join-Path $payloadRoot 'parts'
$partHashes=[ordered]@{
    'part-00.b64'='4ee9e8ec9784c8a11aa3cfe773bc0e86f84852fcbf30165972220570a3d75814';
    'part-01.b64'='5013abc7665c41b23a8d6adf4809c8befa86ab91aa7510e6e59e12da03e145c5';
    'part-02.b64'='341be4e4bdf503a1936ca3644a52824d6dec7935e600b36028ebdf4b11cba09b';
    'part-03.b64'='a75dcc232f302aaaa736039fd5e3f1cb30e8872b0b51c61503e815ed50ab8282';
    'part-04.b64'='e514216753141c9a0c32b69ac852c6ba1225272ed8a3c682dd56e8ce5430f568';
    'part-05.b64'='d71e5e00d58f102d3014ea478e1406c0ef64467a6e6205c47b9d25966d60b4a4';
    'part-06.b64'='5bfb4bffca415088e0000dac0feeff73b001fa9cf5555df6dadd6d2d51079257'
}
$builder=[Text.StringBuilder]::new()
foreach($entry in $partHashes.GetEnumerator()){
    $part=Join-Path $partsRoot $entry.Key
    Assert-Hash $part $entry.Value "v1.1.17 payload $($entry.Key)"
    [void]$builder.Append([Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($part),'\s+',''))
}
$bytes=[Convert]::FromBase64String($builder.ToString())
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
