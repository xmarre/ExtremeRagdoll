$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Assert-Chunk([string]$Path,[string]$Expected){
 if(!(Test-Path -LiteralPath $Path)){throw "Missing payload chunk: $Path"}
 $actual=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -ne $Expected){throw "Payload chunk hash mismatch for ${Path}: $actual"}
}
function Join-Chunks([array]$Specs,[string]$Output,[string]$ExpectedCombined){
 foreach($spec in $Specs){Assert-Chunk $spec.Path $spec.Hash}
 $payload=($Specs|ForEach-Object{([IO.File]::ReadAllText($_.Path)).Trim()})-join''
 [IO.File]::WriteAllText($Output,$payload,[Text.UTF8Encoding]::new($false))
 $actual=(Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -ne $ExpectedCombined){throw "Reconstructed payload hash mismatch for ${Output}: $actual"}
}
function Expand-GzipBase64([string]$InputPath,[string]$OutputPath,[string]$ExpectedGzip,[string]$ExpectedRaw){
 $clean=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($InputPath),'\s+','')
 $compressed=[Convert]::FromBase64String($clean)
 $gzipHash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($compressed)).Replace('-','').ToLowerInvariant()
 if($gzipHash -ne $ExpectedGzip){throw "Build script gzip hash mismatch: $gzipHash"}
 $input=[IO.MemoryStream]::new($compressed);$gzip=[IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress);$output=[IO.File]::Create($OutputPath)
 try{$gzip.CopyTo($output)}finally{$output.Dispose();$gzip.Dispose();$input.Dispose()}
 $raw=(Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
 if($raw -ne $ExpectedRaw){throw "Build script raw hash mismatch: $raw"}
}
$root=(Resolve-Path $PSScriptRoot).Path
$patch=@(
 @{Path=(Join-Path $root 'patchchunks/p00.b64');Hash='045b610cceb2db529846c33f651d63c4c480b7d6c4ee8d0192c2e173b239cdf2'},
 @{Path=(Join-Path $root 'patchchunks/p01.b64');Hash='00a5ae611485b49c3c09f8f1f7b616dcdba9d0a332c2e8a4282fcd6e1454c4b6'},
 @{Path=(Join-Path $root 'patchchunks/p02.b64');Hash='19ecfcfa5ddc2ad76595b6feb1b2c76eb350e76acc6af78494a529edb8fbca75'}
)
$test=@(@{Path=(Join-Path $root 'testchunks/p00.b64');Hash='6751ab4acf8caa861fc66e9d17993dda8e9c998288b82bfebe253feb49634a34'})
$build=@(@{Path=(Join-Path $root 'buildchunks/p00.b64');Hash='38e501ac5ebb28332cd9693ce3c6bfff69d6145d1817a8bab23931efdd8a02eb'})
Join-Chunks $patch (Join-Path $root 'patch.gz.b64') '197522f6418b2f27bf7ef048bd4948362c2cfe5f3434c286b8ad681322f24b2a'
Join-Chunks $test (Join-Path $root 'test.gz.b64') '6751ab4acf8caa861fc66e9d17993dda8e9c998288b82bfebe253feb49634a34'
Join-Chunks $build (Join-Path $root 'build.gz.b64') '38e501ac5ebb28332cd9693ce3c6bfff69d6145d1817a8bab23931efdd8a02eb'
Expand-GzipBase64 (Join-Path $root 'build.gz.b64') (Join-Path $root 'build-ga1113-camera-split-crash.ps1') '9a2be50339ffad203974503394e378e2a934c07b750d16b644893ddd0c0e208d' '12d0e9afa4a400117819a34671f6a2313ab54b8fa1babf4f1665e44e9f074549'
Write-Host 'GA1113_PAYLOAD_RECONSTRUCTION=SUCCESS'