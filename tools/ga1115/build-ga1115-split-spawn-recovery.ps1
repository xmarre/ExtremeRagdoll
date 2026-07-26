param(
    [Parameter(Mandatory=$true)][string]$BaseOutputRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Assert-Hash([string]$Path,[string]$Expected,[string]$Label){
    if(!(Test-Path -LiteralPath $Path)){throw "${Label} missing: $Path"}
    $actual=Get-Sha256 $Path
    if($actual -ne $Expected.ToLowerInvariant()){throw "${Label} hash mismatch: expected $Expected, observed $actual"}
}
function Write-Utf8NoBom([string]$Path,[string]$Content){[IO.File]::WriteAllText($Path,$Content,[Text.UTF8Encoding]::new($false))}
function Decode-GzipBase64([string]$InputPath,[string]$OutputPath,[string]$ExpectedB64,[string]$ExpectedGzip,[string]$ExpectedRaw,[string]$Label){
    Assert-Hash $InputPath $ExpectedB64 "${Label} base64"
    $clean=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($InputPath),'\s+','')
    $compressed=[Convert]::FromBase64String($clean)
    $gzipHash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($compressed)).Replace('-','').ToLowerInvariant()
    if($gzipHash -ne $ExpectedGzip){throw "${Label} gzip hash mismatch: $gzipHash"}
    $input=[IO.MemoryStream]::new($compressed)
    $gzip=[IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress)
    $output=[IO.File]::Create($OutputPath)
    try{$gzip.CopyTo($output)}finally{$output.Dispose();$gzip.Dispose();$input.Dispose()}
    Assert-Hash $OutputPath $ExpectedRaw $Label
}
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
$payload=Join-Path $repo 'tools/ga1115'
$workspace=Join-Path $env:RUNNER_TEMP 'ga1115-work'
$moduleRoot=Join-Path $workspace 'GuidedArrow'
$baseModule=Join-Path $BaseOutputRoot 'artifact/GuidedArrow'
$diagnostics=Join-Path $OutputRoot 'diagnostics'
$artifact=Join-Path $OutputRoot 'artifact'
$patch=Join-Path $workspace 'GuidedArrow-v1.1.14-to-v1.1.15-Split-Capture-Timing.patch'
$test=Join-Path $workspace 'test-ga1115-split-spawn-recovery.py'
Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workspace,$diagnostics,$artifact -Force|Out-Null
if(!(Test-Path $baseModule)){throw "v1.1.14 base artifact missing: $baseModule"}
Copy-Item $baseModule $moduleRoot -Recurse -Force
$baseHashes=@{
    'Source/GuidedArrowBehavior.cs'='b88516aab934c9383edfe80c7cdaa0b303e8d36bfd8697d3485cccbeb8fbad59';
    'Source/MissileDamageBridge.cs'='66ded32e5f594025cf12ca88902818f9c9c65d1366bcbf4ac849caf70e2f544d';
    'Source/Settings.cs'='2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
    'Source/GuidedArrow.csproj'='d881ce7c9a2e65f8f3897fa5be5bd0022890b1cc32e78ff6c959297c552dd821';
    'Source/SubModule.cs'='a3a14c334729ed0039f3e274d8557168344e72923d3c23394223f4f38f26309a';
    'SubModule.xml'='2b4ff271c4e6046728105988afb23da83fc6fbd8084a90a0e550d1c65d3301d0';
    'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($entry in $baseHashes.GetEnumerator()){Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.14 $($entry.Key)"}
Decode-GzipBase64 (Join-Path $payload 'patch.gz.b64') $patch 'e69c29ea87f9533e5aa9f9a594be60b9ab722934888a4e2bc0c9169a8a30d408' '5f0b6be01fcb18c2d38b1955692b394ad9b1de890f3fe07c2729b5c854c96689' '28ef5da0911824b9d82d7fb55624a88a0f06e72dbbef9f30779af22fa7fa8662' 'v1.1.15 patch'
Decode-GzipBase64 (Join-Path $payload 'test.gz.b64') $test '7c2fd6ad9a4dd483a46b88ad3345d25d74daab179a73f5b35c5e2314cdd249c4' '9de6c860673d16d3c4667534b1d31f016733cc41e48595184c0520b24d2baecc' 'a35c17e83c7c71b493d74b2a7d0848d1ecfcf41c56984e74bcef8905503dca70' 'v1.1.15 test'
Push-Location $moduleRoot
try{
    & git init -q
    if($LASTEXITCODE -ne 0){throw 'git init failed'}
    & git config core.autocrlf false
    & git apply --check -p1 --whitespace=error-all $patch
    if($LASTEXITCODE -ne 0){throw 'v1.1.15 patch check failed'}
    & git apply -p1 --whitespace=error-all $patch
    if($LASTEXITCODE -ne 0){throw 'v1.1.15 patch apply failed'}
}finally{Pop-Location}
$finalHashes=@{
    'Source/GuidedArrowBehavior.cs'='69a0cba342a232e7771a5fa46d2ee96ca66d7f8ec73c0380f07afbc008330954';
    'Source/MissileDamageBridge.cs'='a94116829b93cd617e3957af27ad7c9feba98c2f840ebd198235b6273e9f6223';
    'Source/Settings.cs'='2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
    'Source/GuidedArrow.csproj'='916f0940c60c341fa9f631b915ba82fee1d842ad1b2a725d52e09cf58592748e';
    'Source/SubModule.cs'='a3a14c334729ed0039f3e274d8557168344e72923d3c23394223f4f38f26309a';
    'SubModule.xml'='1260cfba8eac6bae76e608630b8fcb8888f2a805f64a99f23bf6db141d1dd6c9';
    'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($entry in $finalHashes.GetEnumerator()){Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.15 $($entry.Key)"}
$testLog=Join-Path $diagnostics 'SPLIT_SPAWN_RECOVERY_TESTS.txt'
$oldModuleRoot=$env:GA1115_MODULE_ROOT
try{
    $env:GA1115_MODULE_ROOT=$moduleRoot
    & python $test 2>&1|Tee-Object -FilePath $testLog
    if($LASTEXITCODE -ne 0){throw 'v1.1.15 split spawn recovery tests failed'}
}finally{$env:GA1115_MODULE_ROOT=$oldModuleRoot}
$versions=@('1.3.15.110062','1.4.0.112726-beta','1.4.1.113228-beta','1.4.2.113809-beta','1.4.3.114169-beta','1.4.4.114449-beta','1.4.5.115026','1.4.6.115628','1.4.7.117484')
$matrix=New-Object System.Collections.Generic.List[string]
$production=$null
foreach($version in $versions){
    $safe=$version.Replace('.','_').Replace('-','_')
    $buildRoot=Join-Path $workspace "build-$safe"
    $sourceRoot=Join-Path $buildRoot 'Source'
    $out=Join-Path $buildRoot 'out'
    New-Item -ItemType Directory -Path $sourceRoot,$out -Force|Out-Null
    Copy-Item (Join-Path $moduleRoot 'Source/*') $sourceRoot -Recurse -Force
    $project=Join-Path $sourceRoot 'GuidedArrow.csproj'
    $text=[IO.File]::ReadAllText($project).Replace('1.3.15.110062',$version)
    Write-Utf8NoBom $project $text
    $restore=Join-Path $diagnostics "RESTORE_$safe.txt"
    $compile=Join-Path $diagnostics "COMPILER_$safe.txt"
    $sw=[Diagnostics.Stopwatch]::StartNew()
    & dotnet restore $project --nologo 2>&1|Tee-Object -FilePath $restore
    if($LASTEXITCODE -ne 0){throw "Restore failed for $version"}
    & dotnet build $project -c Release --no-restore --nologo -p:ContinuousIntegrationBuild=true -p:TreatWarningsAsErrors=true -o $out 2>&1|Tee-Object -FilePath $compile
    if($LASTEXITCODE -ne 0){throw "Compile failed for $version"}
    $sw.Stop()
    if(!(Test-Path (Join-Path $out 'GuidedArrow.dll')) -or !(Test-Path (Join-Path $out 'GuidedArrow.pdb'))){throw "Build output missing for $version"}
    $matrix.Add("${version}: PASS, 0 warnings, 0 errors, $($sw.Elapsed)")
    if($version -eq '1.3.15.110062'){$production=$out}
}
if($null -eq $production){throw 'Production output missing'}
$runtime=Join-Path $artifact 'GuidedArrow'
$bin=Join-Path $runtime 'bin/Win64_Shipping_Client'
New-Item -ItemType Directory -Path $bin,(Join-Path $runtime 'GUI/Prefabs'),(Join-Path $runtime 'Source') -Force|Out-Null
Copy-Item (Join-Path $production 'GuidedArrow.dll') $bin
Copy-Item (Join-Path $production 'GuidedArrow.pdb') $bin
Copy-Item (Join-Path $moduleRoot 'SubModule.xml') $runtime
Copy-Item (Join-Path $moduleRoot 'GUI/Prefabs/GuidedArrowCrosshair.xml') (Join-Path $runtime 'GUI/Prefabs')
Copy-Item (Join-Path $moduleRoot 'Source/*') (Join-Path $runtime 'Source') -Recurse -Force
Copy-Item $patch (Join-Path $runtime 'GuidedArrow-v1.1.14-to-v1.1.15-Split-Capture-Timing.patch')
Copy-Item $test $runtime
$dll=Join-Path $bin 'GuidedArrow.dll'
$pdb=Join-Path $bin 'GuidedArrow.pdb'
$dllHash=Get-Sha256 $dll
$pdbHash=Get-Sha256 $pdb
$vi=[Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
if($vi.FileVersion -ne '1.1.15.0'){throw "Unexpected DLL version $($vi.FileVersion)"}
Write-Utf8NoBom (Join-Path $diagnostics 'COMPATIBILITY_MATRIX.txt') (($matrix -join [Environment]::NewLine)+[Environment]::NewLine)
Write-Utf8NoBom (Join-Path $diagnostics 'SOURCE_VERIFICATION.txt') "BASE_V1_1_14_BEHAVIOR_SHA256=b88516aab934c9383edfe80c7cdaa0b303e8d36bfd8697d3485cccbeb8fbad59`nPATCH_SHA256=28ef5da0911824b9d82d7fb55624a88a0f06e72dbbef9f30779af22fa7fa8662`nTEST_SHA256=a35c17e83c7c71b493d74b2a7d0848d1ecfcf41c56984e74bcef8905503dca70`nFINAL_BEHAVIOR_SHA256=69a0cba342a232e7771a5fa46d2ee96ca66d7f8ec73c0380f07afbc008330954`nFINAL_DAMAGE_BRIDGE_SHA256=a94116829b93cd617e3957af27ad7c9feba98c2f840ebd198235b6273e9f6223`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_METADATA.txt') "GUIDED_ARROW_VERSION=1.1.15`nPRODUCTION_REFERENCE=1.3.15.110062`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_STATUS.txt') "SUCCESS`n"
Write-Host 'GA1115_BUILD=SUCCESS'
Write-Host "DLL_SHA256=$dllHash"
Write-Host "PDB_SHA256=$pdbHash"
