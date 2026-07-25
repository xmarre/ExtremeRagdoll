param(
    [Parameter(Mandatory=$true)][string]$BaseOutputRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-Hash([string]$Path,[string]$Expected,[string]$Label) {
    if (!(Test-Path -LiteralPath $Path)) { throw "$Label missing: $Path" }
    $actual=Get-Sha256 $Path
    if ($actual -ne $Expected.ToLowerInvariant()) { throw "$Label hash mismatch: expected $Expected, observed $actual" }
}
function Write-Utf8NoBom([string]$Path,[string]$Content) { [IO.File]::WriteAllText($Path,$Content,[Text.UTF8Encoding]::new($false)) }
function Decode-GzipBase64([string]$InputPath,[string]$OutputPath,[string]$ExpectedGzipHash,[string]$ExpectedRawHash,[string]$Label) {
    $raw=[IO.File]::ReadAllText($InputPath)
    $clean=[Text.RegularExpressions.Regex]::Replace($raw,'\s+','')
    $compressed=[Convert]::FromBase64String($clean)
    $gzipHash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($compressed)).Replace('-','').ToLowerInvariant()
    if ($gzipHash -ne $ExpectedGzipHash) { throw "$Label gzip hash mismatch: $gzipHash" }
    $input=[IO.MemoryStream]::new($compressed)
    $gzip=[IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress)
    $output=[IO.File]::Create($OutputPath)
    try { $gzip.CopyTo($output) } finally { $output.Dispose(); $gzip.Dispose(); $input.Dispose() }
    Assert-Hash $OutputPath $ExpectedRawHash $Label
}
$repoRoot=(Resolve-Path $env:GITHUB_WORKSPACE).Path
$payloadDir=Join-Path $repoRoot 'tools/ga1112'
$workspace=Join-Path $env:RUNNER_TEMP 'ga1112-work'
$moduleRoot=Join-Path $workspace 'GuidedArrow'
$patchPath=Join-Path $workspace 'GuidedArrow-v1.1.11-to-v1.1.12-Stability-Standalone-Controls.patch'
$testPath=Join-Path $workspace 'test-ga1112-stability-standalone-controls.py'
$baseModule=Join-Path $BaseOutputRoot 'artifact/GuidedArrow'
$diagnostics=Join-Path $OutputRoot 'diagnostics'
$artifact=Join-Path $OutputRoot 'artifact'
Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workspace,$diagnostics,$artifact -Force | Out-Null
if (!(Test-Path $baseModule)) { throw "v1.1.11 base artifact missing: $baseModule" }
Copy-Item $baseModule $moduleRoot -Recurse -Force
$baseHashes=@{
 'Source/GuidedArrowBehavior.cs'='0eb798ada398f24e3c205eeea656142bb40e7474ab616009a1707a8012764022';
 'Source/Settings.cs'='76cd9c14b7bb59455b9da2abbda3ed92db5c6b41d761a9045b4ae172fabc0988';
 'Source/GuidedArrow.csproj'='362b3609d9fade89b1fc838ae55af77fe17bc8e322b87163878838a594ead55f';
 'Source/SubModule.cs'='31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae';
 'SubModule.xml'='ee06dc15654a9a61fe483f84bc468f7484b6b3125f012c666174dc1584065398';
 'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($entry in $baseHashes.GetEnumerator()){ Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.11 $($entry.Key)" }
Decode-GzipBase64 (Join-Path $payloadDir 'patch.gz.b64') $patchPath 'c2763c0ea449dd4dc9a10ccd717befabbc65590afc552c6e4599e23a2e20c920' '4614c683ff8fe589a121fa66e0f80113eade23d841a6347b5ca10bb80b9f6539' 'v1.1.12 patch'
Decode-GzipBase64 (Join-Path $payloadDir 'test.gz.b64') $testPath '934713cc784d4cdce350206f00979712118af1ea6e6128ae0e69db7756a52f74' 'cece60555868525f077f4d7e3f4125b3fcacc6bf71d9aa38ff59ab1c05c8e381' 'v1.1.12 test'
Push-Location $workspace
try {
 & git init -q; if($LASTEXITCODE -ne 0){throw 'git init failed'}
 & git config core.autocrlf false
 & git apply --check --whitespace=error-all $patchPath; if($LASTEXITCODE -ne 0){throw 'v1.1.12 patch check failed'}
 & git apply --whitespace=error-all $patchPath; if($LASTEXITCODE -ne 0){throw 'v1.1.12 patch apply failed'}
} finally { Pop-Location }
$finalHashes=@{
 'Source/GuidedArrowBehavior.cs'='dbf58a5e17eb0b461c90ef493c149641ce49022f1aa193279f691477c8a0c5b5';
 'Source/Settings.cs'='15c346246bc6ea267c4667b104743407b1fea8fd3d94452bd86286dd71e21443';
 'Source/GuidedArrow.csproj'='8aa1b4b17d815f4e404031813171af426da8744dd93209a834fdbe3ba0aad4bf';
 'Source/SubModule.cs'='31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae';
 'SubModule.xml'='6e208838ad68c61f7acf7e135ca2ce76635c7ac0b64cfcad2c1d2d0e44fe8153';
 'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($entry in $finalHashes.GetEnumerator()){ Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.12 $($entry.Key)" }
Copy-Item $testPath (Join-Path $moduleRoot 'test-ga1112-stability-standalone-controls.py')
$testLog=Join-Path $diagnostics 'STABILITY_STANDALONE_CONTROLS_TESTS.txt'
& python (Join-Path $moduleRoot 'test-ga1112-stability-standalone-controls.py') 2>&1 | Tee-Object -FilePath $testLog
if($LASTEXITCODE -ne 0){throw 'v1.1.12 deterministic tests failed'}
$versions=@('1.3.15.110062','1.4.0.112726-beta','1.4.1.113228-beta','1.4.2.113809-beta','1.4.3.114169-beta','1.4.4.114449-beta','1.4.5.115026','1.4.6.115628','1.4.7.117484')
$matrix=New-Object System.Collections.Generic.List[string]
$productionOutput=$null
foreach($version in $versions){
 $safe=$version.Replace('.','_').Replace('-','_'); $buildRoot=Join-Path $workspace "build-$safe"; $sourceRoot=Join-Path $buildRoot 'Source'; $output=Join-Path $buildRoot 'out'
 New-Item -ItemType Directory -Path $sourceRoot,$output -Force|Out-Null
 Copy-Item (Join-Path $moduleRoot 'Source/*') $sourceRoot -Recurse -Force
 $project=Join-Path $sourceRoot 'GuidedArrow.csproj'; $projectText=[IO.File]::ReadAllText($project).Replace('1.3.15.110062',$version); Write-Utf8NoBom $project $projectText
 $restoreLog=Join-Path $diagnostics "RESTORE_$safe.txt"; $compileLog=Join-Path $diagnostics "COMPILER_$safe.txt"; $sw=[Diagnostics.Stopwatch]::StartNew()
 & dotnet restore $project --nologo 2>&1|Tee-Object -FilePath $restoreLog; if($LASTEXITCODE -ne 0){throw "Restore failed for $version"}
 & dotnet build $project -c Release --no-restore --nologo -p:ContinuousIntegrationBuild=true -p:TreatWarningsAsErrors=true -o $output 2>&1|Tee-Object -FilePath $compileLog; if($LASTEXITCODE -ne 0){throw "Compile failed for $version"}
 $sw.Stop(); if(!(Test-Path (Join-Path $output 'GuidedArrow.dll'))){throw "DLL missing for $version"}; $matrix.Add("${version}: PASS, 0 warnings, 0 errors, $($sw.Elapsed)")
 if($version -eq '1.3.15.110062'){$productionOutput=$output}
}
$runtimeRoot=Join-Path $artifact 'GuidedArrow'; $binRoot=Join-Path $runtimeRoot 'bin/Win64_Shipping_Client'
New-Item -ItemType Directory -Path $binRoot,(Join-Path $runtimeRoot 'GUI/Prefabs'),(Join-Path $runtimeRoot 'Source') -Force|Out-Null
Copy-Item (Join-Path $productionOutput 'GuidedArrow.dll') $binRoot; Copy-Item (Join-Path $productionOutput 'GuidedArrow.pdb') $binRoot
Copy-Item (Join-Path $moduleRoot 'SubModule.xml') $runtimeRoot; Copy-Item (Join-Path $moduleRoot 'GUI/Prefabs/GuidedArrowCrosshair.xml') (Join-Path $runtimeRoot 'GUI/Prefabs')
Copy-Item (Join-Path $moduleRoot 'Source/*') (Join-Path $runtimeRoot 'Source') -Recurse -Force
Copy-Item $patchPath (Join-Path $runtimeRoot 'GuidedArrow-v1.1.11-to-v1.1.12-Stability-Standalone-Controls.patch'); Copy-Item $testPath $runtimeRoot
$dllPath=Join-Path $binRoot 'GuidedArrow.dll'; $pdbPath=Join-Path $binRoot 'GuidedArrow.pdb'; $dllHash=Get-Sha256 $dllPath; $pdbHash=Get-Sha256 $pdbPath
$vi=[Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath); if($vi.FileVersion -ne '1.1.12.0'){throw "Unexpected DLL version $($vi.FileVersion)"}
Write-Utf8NoBom (Join-Path $diagnostics 'COMPATIBILITY_MATRIX.txt') (($matrix -join [Environment]::NewLine)+[Environment]::NewLine)
Write-Utf8NoBom (Join-Path $diagnostics 'SOURCE_VERIFICATION.txt') "BASE_V1_1_11_BEHAVIOR_SHA256=0eb798ada398f24e3c205eeea656142bb40e7474ab616009a1707a8012764022`nPATCH_SHA256=4614c683ff8fe589a121fa66e0f80113eade23d841a6347b5ca10bb80b9f6539`nTEST_SHA256=cece60555868525f077f4d7e3f4125b3fcacc6bf71d9aa38ff59ab1c05c8e381`nFINAL_BEHAVIOR_SHA256=dbf58a5e17eb0b461c90ef493c149641ce49022f1aa193279f691477c8a0c5b5`nFINAL_SETTINGS_SHA256=15c346246bc6ea267c4667b104743407b1fea8fd3d94452bd86286dd71e21443`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_METADATA.txt') "GUIDED_ARROW_VERSION=1.1.12`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_STATUS.txt') "SUCCESS`n"
Write-Host 'GA1112_BUILD=SUCCESS'; Write-Host "DLL_SHA256=$dllHash"; Write-Host "PDB_SHA256=$pdbHash"
