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
    $clean=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($InputPath),'\s+','')
    $b64Bytes=[Text.UTF8Encoding]::new($false).GetBytes($clean)
    $b64Hash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b64Bytes)).Replace('-','').ToLowerInvariant()
    if($b64Hash -ne $ExpectedB64){throw "${Label} base64 hash mismatch: $b64Hash"}
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
$payload=Join-Path $repo 'tools/ga1116'
$workspace=Join-Path $env:RUNNER_TEMP 'ga1116-work'
$moduleRoot=Join-Path $workspace 'GuidedArrow'
$baseModule=Join-Path $BaseOutputRoot 'artifact/GuidedArrow'
$diagnostics=Join-Path $OutputRoot 'diagnostics'
$artifact=Join-Path $OutputRoot 'artifact'
$patch=Join-Path $workspace 'GuidedArrow-v1.1.15-to-v1.1.16-Native-Shot-Data.patch'
$hotfix=Join-Path $workspace 'GuidedArrow-v1.1.16-Direct-Resolved-Spawn.hotfix.patch'
$test=Join-Path $workspace 'test-ga1116-native-shot-data.py'
Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workspace,$diagnostics,$artifact -Force|Out-Null
if(!(Test-Path $baseModule)){throw "v1.1.15 base artifact missing: $baseModule"}
Copy-Item $baseModule $moduleRoot -Recurse -Force
$baseHashes=@{
    'Source/GuidedArrowBehavior.cs'='69a0cba342a232e7771a5fa46d2ee96ca66d7f8ec73c0380f07afbc008330954';
    'Source/MissileDamageBridge.cs'='a94116829b93cd617e3957af27ad7c9feba98c2f840ebd198235b6273e9f6223';
    'Source/Settings.cs'='2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
    'Source/GuidedArrow.csproj'='916f0940c60c341fa9f631b915ba82fee1d842ad1b2a725d52e09cf58592748e';
    'Source/SubModule.cs'='a3a14c334729ed0039f3e274d8557168344e72923d3c23394223f4f38f26309a';
    'SubModule.xml'='1260cfba8eac6bae76e608630b8fcb8888f2a805f64a99f23bf6db141d1dd6c9';
    'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($entry in $baseHashes.GetEnumerator()){Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.15 $($entry.Key)"}
Decode-GzipBase64 (Join-Path $payload 'patch.gz.b64') $patch '6591b50fa8d33444d5c49cc86865a1ca8049e69a389215bb2f0e63562e388001' '303e7c143cd4c478de02be38110dd34ac6b0c2c049606f7fa6bf9887d8a448ad' '72cf15e63be06796f55fc2ce6da6bd360b8d15c227ae7cf1d7c633cb45affe0b' 'v1.1.16 patch'
Decode-GzipBase64 (Join-Path $payload 'direct-spawn-hotfix.gz.b64') $hotfix '1c5fab2c47f1807d0467ae2a8e5df712b72689eab804b6665d73f7a4b3f0249b' '72ae959a135686e03255de7fff9d8eee508d6697f11bd17b12d233c4917195c8' '1eb09f5e45305eb7beac5a1a1f9861583777abe9ff20e5368a34a9272fb8564b' 'v1.1.16 direct-spawn hotfix'
Decode-GzipBase64 (Join-Path $payload 'test.gz.b64') $test 'f4fc3717ac150d2a49933220f3d568245f508bd93746fcd001f28fc52fa12f23' '489f8c28a4aa5527b8c025678a7ff36285cf5c1844d72ac45f00563fc2294c21' 'e0b404d77b7cadfbccd97ca0eef3bc2b7f77c0955a1f7c52045f9dfa05852513' 'v1.1.16 test'
Push-Location $workspace
try{
    & git init -q
    if($LASTEXITCODE -ne 0){throw 'git init failed'}
    & git config core.autocrlf false
    & git apply --check --whitespace=error-all $patch
    if($LASTEXITCODE -ne 0){throw 'v1.1.16 patch check failed'}
    & git apply --whitespace=error-all $patch
    if($LASTEXITCODE -ne 0){throw 'v1.1.16 patch apply failed'}
    & git apply --check --whitespace=error-all $hotfix
    if($LASTEXITCODE -ne 0){throw 'v1.1.16 direct-spawn hotfix check failed'}
    & git apply --whitespace=error-all $hotfix
    if($LASTEXITCODE -ne 0){throw 'v1.1.16 direct-spawn hotfix apply failed'}
}finally{Pop-Location}
$finalHashes=@{
    'Source/GuidedArrowBehavior.cs'='ed8343034b64c4f37af7f095a671a3ee30c9eabff63ca98d45b84218d33ea08a';
    'Source/MissileDamageBridge.cs'='fb911e8b983caacb6b9fdfa2b19aeae1155828168e16fa9da8704a0fa7c830ed';
    'Source/Settings.cs'='2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
    'Source/GuidedArrow.csproj'='b50657858c78e8c6c8b698337293b163a066e72fb8dfa468ab2bb545d3ca4624';
    'Source/SubModule.cs'='a3a14c334729ed0039f3e274d8557168344e72923d3c23394223f4f38f26309a';
    'SubModule.xml'='d6e753d8c576b5af2d3024855116b269ac6b0ccfaddd0d11383dadcffefaa9bc';
    'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($entry in $finalHashes.GetEnumerator()){Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.16 $($entry.Key)"}
$behaviorText=[IO.File]::ReadAllText((Join-Path $moduleRoot 'Source/GuidedArrowBehavior.cs'))
$bridgeText=[IO.File]::ReadAllText((Join-Path $moduleRoot 'Source/MissileDamageBridge.cs'))
$resolvedSpawnCount=([Text.RegularExpressions.Regex]::Matches($behaviorText,[Text.RegularExpressions.Regex]::Escape('MissileDamageBridge.AddResolvedCustomMissile('))).Count
if($resolvedSpawnCount -lt 2){throw "Expected at least two direct resolved synthetic spawn call sites, found $resolvedSpawnCount"}
if($behaviorText.Contains('MissileDamageBridge.OverrideNextSyntheticMissile(')){throw 'Synthetic behavior still depends on OverrideNextSyntheticMissile'}
foreach($required in @('_missilesList','_missilesDictionary','AddMissileSingleUsageAux','AddResolvedCustomMissile','new Mission.Missile','data.DamageBonus','data.BaseSpeed')){
    if(!$bridgeText.Contains($required)){throw "Direct resolved missile bridge missing required token: $required"}
}
$testLog=Join-Path $diagnostics 'NATIVE_SHOT_DATA_TESTS.txt'
$oldRoot=$env:GA1116_MODULE_ROOT
try{
    $env:GA1116_MODULE_ROOT=$moduleRoot
    & python $test 2>&1|Tee-Object -FilePath $testLog
    if($LASTEXITCODE -ne 0){throw 'v1.1.16 native shot data tests failed'}
}finally{$env:GA1116_MODULE_ROOT=$oldRoot}
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
    $projectText=[IO.File]::ReadAllText($project).Replace('1.3.15.110062',$version)
    Write-Utf8NoBom $project $projectText
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
Copy-Item $patch (Join-Path $runtime 'GuidedArrow-v1.1.15-to-v1.1.16-Native-Shot-Data.patch')
Copy-Item $hotfix (Join-Path $runtime 'GuidedArrow-v1.1.16-Direct-Resolved-Spawn.hotfix.patch')
Copy-Item $test $runtime
$dll=Join-Path $bin 'GuidedArrow.dll'
$pdb=Join-Path $bin 'GuidedArrow.pdb'
$dllHash=Get-Sha256 $dll
$pdbHash=Get-Sha256 $pdb
$vi=[Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
if($vi.FileVersion -ne '1.1.16.0'){throw "Unexpected DLL version $($vi.FileVersion)"}
Write-Utf8NoBom (Join-Path $diagnostics 'COMPATIBILITY_MATRIX.txt') (($matrix -join [Environment]::NewLine)+[Environment]::NewLine)
Write-Utf8NoBom (Join-Path $diagnostics 'SOURCE_VERIFICATION.txt') "BASE_V1_1_15_BEHAVIOR_SHA256=69a0cba342a232e7771a5fa46d2ee96ca66d7f8ec73c0380f07afbc008330954`nPATCH_SHA256=72cf15e63be06796f55fc2ce6da6bd360b8d15c227ae7cf1d7c633cb45affe0b`nDIRECT_SPAWN_HOTFIX_SHA256=1eb09f5e45305eb7beac5a1a1f9861583777abe9ff20e5368a34a9272fb8564b`nTEST_SHA256=e0b404d77b7cadfbccd97ca0eef3bc2b7f77c0955a1f7c52045f9dfa05852513`nFINAL_BEHAVIOR_SHA256=ed8343034b64c4f37af7f095a671a3ee30c9eabff63ca98d45b84218d33ea08a`nFINAL_DAMAGE_BRIDGE_SHA256=fb911e8b983caacb6b9fdfa2b19aeae1155828168e16fa9da8704a0fa7c830ed`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_METADATA.txt') "GUIDED_ARROW_VERSION=1.1.16`nPRODUCTION_REFERENCE=1.3.15.110062`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_STATUS.txt') "SUCCESS`n"
Write-Host 'GA1116_BUILD=SUCCESS'
Write-Host "DLL_SHA256=$dllHash"
Write-Host "PDB_SHA256=$pdbHash"
