$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Get-Sha256Text([string]$Text){
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes($Text)
    [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-','').ToLowerInvariant()
}
function Read-NormalizedChunk([string]$Path,[string]$Expected){
    if(!(Test-Path -LiteralPath $Path)){throw "Missing v1.1.15 test payload chunk: $Path"}
    $text=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($Path),'\s+','')
    $actual=Get-Sha256Text $text
    if($actual -ne $Expected){throw "v1.1.15 normalized test payload chunk hash mismatch for ${Path}: $actual"}
    return $text
}
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
& (Join-Path $repo 'tools/ga1114/run-complete-build.ps1')
$root=Join-Path $repo 'tools/ga1115'
$chunks=@(
    @{Path=(Join-Path $root 'testchunks/p00.b64');Hash='5f325bcac32fb164b3ebfe4f4ff5273376fab6096060bd5f651007a017fe8567'},
    @{Path=(Join-Path $root 'testchunks/p01.b64');Hash='1577a819a3accab0fa6f67e177ec83b8a1f0e0addccddb1fdc4e4f9f299553cd'},
    @{Path=(Join-Path $root 'testchunks/p02.b64');Hash='5fde082a7e9df2193b824fab88ddf5a2afa4b0b6a53eb8f5d215f48cd940ef80'},
    @{Path=(Join-Path $root 'testchunks/p03.b64');Hash='7c56f396b64214d76b9aa092f382cbe749de072fc8344f91e735bc301413fa66'}
)
$combined=($chunks|ForEach-Object{Read-NormalizedChunk $_.Path $_.Hash})-join''
$combinedHash=Get-Sha256Text $combined
if($combinedHash -ne '7c2fd6ad9a4dd483a46b88ad3345d25d74daab179a73f5b35c5e2314cdd249c4'){throw "Reconstructed v1.1.15 test payload hash mismatch: $combinedHash"}
$testPayload=Join-Path $root 'test.gz.b64'
[IO.File]::WriteAllText($testPayload,$combined,[Text.UTF8Encoding]::new($false))
& (Join-Path $root 'build-ga1115-split-spawn-recovery.ps1') -BaseOutputRoot "$env:RUNNER_TEMP\ga1114-output" -OutputRoot "$env:RUNNER_TEMP\ga1115-output"
Write-Host 'GA1115_COMPLETE_BUILD=SUCCESS'
