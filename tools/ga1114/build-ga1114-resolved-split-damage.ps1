param(
    [Parameter(Mandatory=$true)][string]$BaseOutputRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Get-Sha256([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Assert-Hash([string]$Path,[string]$Expected,[string]$Label){
 if(!(Test-Path -LiteralPath $Path)){throw "$Label missing: $Path"}
 $actual=Get-Sha256 $Path
 if($actual -ne $Expected.ToLowerInvariant()){throw "$Label hash mismatch: expected $Expected, observed $actual"}
}
function Write-Utf8NoBom([string]$Path,[string]$Content){[IO.File]::WriteAllText($Path,$Content,[Text.UTF8Encoding]::new($false))}
function Decode-GzipBase64([string]$InputPath,[string]$OutputPath,[string]$ExpectedGzip,[string]$ExpectedRaw,[string]$Label){
 $clean=[Text.RegularExpressions.Regex]::Replace([IO.File]::ReadAllText($InputPath),'\s+','')
 $compressed=[Convert]::FromBase64String($clean)
 $gzipHash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($compressed)).Replace('-','').ToLowerInvariant()
 if($gzipHash -ne $ExpectedGzip){throw "$Label gzip hash mismatch: $gzipHash"}
 $input=[IO.MemoryStream]::new($compressed);$gzip=[IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress);$output=[IO.File]::Create($OutputPath)
 try{$gzip.CopyTo($output)}finally{$output.Dispose();$gzip.Dispose();$input.Dispose()}
 Assert-Hash $OutputPath $ExpectedRaw $Label
}
$repo=(Resolve-Path $env:GITHUB_WORKSPACE).Path
$payload=Join-Path $repo 'tools/ga1114'
$workspace=Join-Path $env:RUNNER_TEMP 'ga1114-work'
$moduleRoot=Join-Path $workspace 'GuidedArrow'
$baseModule=Join-Path $BaseOutputRoot 'artifact/GuidedArrow'
$diagnostics=Join-Path $OutputRoot 'diagnostics'
$artifact=Join-Path $OutputRoot 'artifact'
$patch=Join-Path $workspace 'GuidedArrow-v1.1.13-to-v1.1.14-Resolved-Split-Damage.patch'
$test=Join-Path $workspace 'test-ga1114-resolved-split-damage.py'
Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workspace,$diagnostics,$artifact -Force|Out-Null
if(!(Test-Path $baseModule)){throw "v1.1.13 base artifact missing: $baseModule"}
Copy-Item $baseModule $moduleRoot -Recurse -Force
$baseHashes=@{
 'Source/GuidedArrowBehavior.cs'='69c04ae90a9f27579a283798340c8a0b1a189974d3f35d2cd0442da24f8c8e7d';
 'Source/Settings.cs'='2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
 'Source/GuidedArrow.csproj'='2229ec00106523093b88149fe724358d887bc3931d4345e18c4a26fec171f99f';
 'Source/SubModule.cs'='31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae';
 'SubModule.xml'='2ecb61b19ff00e5c4f25e9b0664b507fd4176c84e07913e33c9360e61262c4dd';
 'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($e in $baseHashes.GetEnumerator()){Assert-Hash (Join-Path $moduleRoot $e.Key) $e.Value "v1.1.13 $($e.Key)"}
Decode-GzipBase64 (Join-Path $payload 'patch.gz.b64') $patch 'c45799c9c91104c0fb3ff29dd48dd46daa28fb65b0f0b0554fa4e580d742438a' 'f0b34578928b07de68029d4dff8f93757e6ccd4839d9bddd4df9a3d688ff6c50' 'v1.1.14 patch'
Decode-GzipBase64 (Join-Path $payload 'test.gz.b64') $test '3d524fdef9da3ce0bc0466f133ed112c9a78c21f0cbfc6e3d41814748859e9ac' '1f31300365700d82adcdb748951c37bdfcc8c27ef5f257106f7960bddaca0d9d' 'v1.1.14 test'
Push-Location $workspace
try{
 & git init -q; if($LASTEXITCODE -ne 0){throw 'git init failed'}
 & git config core.autocrlf false
 & git apply --check --whitespace=error-all $patch; if($LASTEXITCODE -ne 0){throw 'v1.1.14 patch check failed'}
 & git apply --whitespace=error-all $patch; if($LASTEXITCODE -ne 0){throw 'v1.1.14 patch apply failed'}
}finally{Pop-Location}
$finalHashes=@{
 'Source/GuidedArrowBehavior.cs'='b88516aab934c9383edfe80c7cdaa0b303e8d36bfd8697d3485cccbeb8fbad59';
 'Source/MissileDamageBridge.cs'='66ded32e5f594025cf12ca88902818f9c9c65d1366bcbf4ac849caf70e2f544d';
 'Source/Settings.cs'='2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
 'Source/GuidedArrow.csproj'='d881ce7c9a2e65f8f3897fa5be5bd0022890b1cc32e78ff6c959297c552dd821';
 'Source/SubModule.cs'='a3a14c334729ed0039f3e274d8557168344e72923d3c23394223f4f38f26309a';
 'SubModule.xml'='2b4ff271c4e6046728105988afb23da83fc6fbd8084a90a0e550d1c65d3301d0';
 'GUI/Prefabs/GuidedArrowCrosshair.xml'='318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach($e in $finalHashes.GetEnumerator()){Assert-Hash (Join-Path $moduleRoot $e.Key) $e.Value "v1.1.14 $($e.Key)"}
Copy-Item $test (Join-Path $workspace 'test-ga1114-resolved-split-damage.py')
$testLog=Join-Path $diagnostics 'RESOLVED_SPLIT_DAMAGE_TESTS.txt'
& python (Join-Path $workspace 'test-ga1114-resolved-split-damage.py') 2>&1|Tee-Object -FilePath $testLog
if($LASTEXITCODE -ne 0){throw 'v1.1.14 resolved split damage tests failed'}
$versions=@('1.3.15.110062','1.4.0.112726-beta','1.4.1.113228-beta','1.4.2.113809-beta','1.4.3.114169-beta','1.4.4.114449-beta','1.4.5.115026','1.4.6.115628','1.4.7.117484')
$matrix=New-Object System.Collections.Generic.List[string]
$production=$null
foreach($version in $versions){
 $safe=$version.Replace('.','_').Replace('-','_');$buildRoot=Join-Path $workspace "build-$safe";$sourceRoot=Join-Path $buildRoot 'Source';$out=Join-Path $buildRoot 'out'
 New-Item -ItemType Directory -Path $sourceRoot,$out -Force|Out-Null
 Copy-Item (Join-Path $moduleRoot 'Source/*') $sourceRoot -Recurse -Force
 $project=Join-Path $sourceRoot 'GuidedArrow.csproj';$text=[IO.File]::ReadAllText($project).Replace('1.3.15.110062',$version);Write-Utf8NoBom $project $text
 $restore=Join-Path $diagnostics "RESTORE_$safe.txt";$compile=Join-Path $diagnostics "COMPILER_$safe.txt";$sw=[Diagnostics.Stopwatch]::StartNew()
 & dotnet restore $project --nologo 2>&1|Tee-Object -FilePath $restore;if($LASTEXITCODE -ne 0){throw "Restore failed for $version"}
 & dotnet build $project -c Release --no-restore --nologo -p:ContinuousIntegrationBuild=true -p:TreatWarningsAsErrors=true -o $out 2>&1|Tee-Object -FilePath $compile;if($LASTEXITCODE -ne 0){throw "Compile failed for $version"}
 $sw.Stop();if(!(Test-Path (Join-Path $out 'GuidedArrow.dll')) -or !(Test-Path (Join-Path $out 'GuidedArrow.pdb'))){throw "Build output missing for $version"}
 $matrix.Add("${version}: PASS, 0 warnings, 0 errors, $($sw.Elapsed)")
 if($version -eq '1.3.15.110062'){$production=$out}
}
if($null -eq $production){throw 'Production output missing'}
$runtime=Join-Path $artifact 'GuidedArrow';$bin=Join-Path $runtime 'bin/Win64_Shipping_Client'
New-Item -ItemType Directory -Path $bin,(Join-Path $runtime 'GUI/Prefabs'),(Join-Path $runtime 'Source') -Force|Out-Null
Copy-Item (Join-Path $production 'GuidedArrow.dll') $bin;Copy-Item (Join-Path $production 'GuidedArrow.pdb') $bin
Copy-Item (Join-Path $moduleRoot 'SubModule.xml') $runtime;Copy-Item (Join-Path $moduleRoot 'GUI/Prefabs/GuidedArrowCrosshair.xml') (Join-Path $runtime 'GUI/Prefabs')
Copy-Item (Join-Path $moduleRoot 'Source/*') (Join-Path $runtime 'Source') -Recurse -Force
Copy-Item $patch (Join-Path $runtime 'GuidedArrow-v1.1.13-to-v1.1.14-Resolved-Split-Damage.patch');Copy-Item $test $runtime
$dll=Join-Path $bin 'GuidedArrow.dll';$pdb=Join-Path $bin 'GuidedArrow.pdb';$dllHash=Get-Sha256 $dll;$pdbHash=Get-Sha256 $pdb
$vi=[Diagnostics.FileVersionInfo]::GetVersionInfo($dll);if($vi.FileVersion -ne '1.1.14.0'){throw "Unexpected DLL version $($vi.FileVersion)"}
Write-Utf8NoBom (Join-Path $diagnostics 'COMPATIBILITY_MATRIX.txt') (($matrix -join [Environment]::NewLine)+[Environment]::NewLine)
Write-Utf8NoBom (Join-Path $diagnostics 'SOURCE_VERIFICATION.txt') "BASE_V1_1_13_BEHAVIOR_SHA256=69c04ae90a9f27579a283798340c8a0b1a189974d3f35d2cd0442da24f8c8e7d`nPATCH_SHA256=f0b34578928b07de68029d4dff8f93757e6ccd4839d9bddd4df9a3d688ff6c50`nTEST_SHA256=1f31300365700d82adcdb748951c37bdfcc8c27ef5f257106f7960bddaca0d9d`nFINAL_BEHAVIOR_SHA256=b88516aab934c9383edfe80c7cdaa0b303e8d36bfd8697d3485cccbeb8fbad59`nFINAL_DAMAGE_BRIDGE_SHA256=66ded32e5f594025cf12ca88902818f9c9c65d1366bcbf4ac849caf70e2f544d`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_METADATA.txt') "GUIDED_ARROW_VERSION=1.1.14`nPRODUCTION_REFERENCE=1.3.15.110062`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_STATUS.txt') "SUCCESS`n"
Write-Host 'GA1114_BUILD=SUCCESS';Write-Host "DLL_SHA256=$dllHash";Write-Host "PDB_SHA256=$pdbHash"