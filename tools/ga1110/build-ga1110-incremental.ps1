param(
    [Parameter(Mandatory=$true)][string]$BaseOutputRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Assert-Hash([string]$Path, [string]$Expected, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path)) { throw "$Label missing: $Path" }
    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected.ToLowerInvariant()) { throw "$Label hash mismatch: expected $Expected, observed $actual" }
}
function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $enc = [System.Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($Path, $Content, $enc)
}
function Decode-Base64File([string]$InputPath, [string]$OutputPath) {
    $raw = [IO.File]::ReadAllText($InputPath)
    $clean = [Text.RegularExpressions.Regex]::Replace($raw, '\s+', '')
    [IO.File]::WriteAllBytes($OutputPath, [Convert]::FromBase64String($clean))
}

$repoRoot = (Resolve-Path $env:GITHUB_WORKSPACE).Path
$payloadDir = Join-Path $repoRoot 'tools/ga1110'
$workspace = Join-Path $env:RUNNER_TEMP 'ga1110-work'
$patchPath = Join-Path $workspace 'ga119-to-ga1110.patch'
$testPath = Join-Path $workspace 'test-ga1110-recovery-gravity.py'
$moduleRoot = Join-Path $workspace 'GuidedArrow'
$diagnostics = Join-Path $OutputRoot 'diagnostics'
$artifact = Join-Path $OutputRoot 'artifact'
$baseModule = Join-Path $BaseOutputRoot 'artifact/GuidedArrow'

Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workspace,$diagnostics,$artifact -Force | Out-Null
if (!(Test-Path -LiteralPath $baseModule)) { throw "v1.1.9 base artifact module missing: $baseModule" }
Copy-Item -LiteralPath $baseModule -Destination $moduleRoot -Recurse -Force

$baseHashes = @{
    'Source/GuidedArrowBehavior.cs' = 'f7019ca71880c1da7666fc8409f4184df620f26d3bd1a50cfbb421c6ef5652d5'
    'Source/GuidedArrow.csproj' = '967f92c4812670889de24ae7520e4a721161fc6b867daf80a883110fe67bd7bb'
    'Source/Settings.cs' = '308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8'
    'Source/SubModule.cs' = '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae'
    'SubModule.xml' = '346b69068dc7086ddfab409b762972ce885c174ca73dd279f1814e3d071ba4c9'
    'GUI/Prefabs/GuidedArrowCrosshair.xml' = '318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach ($entry in $baseHashes.GetEnumerator()) {
    Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.9 $($entry.Key)"
}

Decode-Base64File (Join-Path $payloadDir 'ga119-to-ga1110.patch.b64') $patchPath
Assert-Hash $patchPath '73b680f961533c03de34293d2a5dcde4bc325a20e9eac9020047bc17f6b0b797' 'v1.1.9 to v1.1.10 patch'
Decode-Base64File (Join-Path $payloadDir 'test-ga1110-recovery-gravity.py.b64') $testPath
Assert-Hash $testPath '11eb204be44c153f339deb5f4c7239b920e42d5191756cb585be64d8d4a28c78' 'v1.1.10 deterministic test'

Push-Location $workspace
try {
    & git init -q
    if ($LASTEXITCODE -ne 0) { throw 'git init failed' }
    & git config core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw 'git core.autocrlf configuration failed' }
    & git apply --check --whitespace=error-all $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'v1.1.10 patch applicability check failed' }
    & git apply --whitespace=error-all $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'v1.1.10 patch application failed' }
}
finally { Pop-Location }

$finalHashes = @{
    'Source/GuidedArrowBehavior.cs' = 'b6dad903ddabbca715634ece63c8d3865ed589746b9315a338eee346187da1b5'
    'Source/GuidedArrow.csproj' = '5d0266ad4022647733fa25eb5f0e32e9c2b323ce5a621491c69646f51bd7521a'
    'Source/Settings.cs' = '308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8'
    'Source/SubModule.cs' = '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae'
    'SubModule.xml' = '13b0857a25403c95e91ef78dbff1727f34acc87469170f7766e64e0a8990608e'
    'GUI/Prefabs/GuidedArrowCrosshair.xml' = '318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach ($entry in $finalHashes.GetEnumerator()) {
    Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.10 $($entry.Key)"
}

Copy-Item -LiteralPath $testPath -Destination (Join-Path $moduleRoot 'test-ga1110-recovery-gravity.py')
$testLog = Join-Path $diagnostics 'RECOVERY_GRAVITY_TESTS.txt'
& python (Join-Path $moduleRoot 'test-ga1110-recovery-gravity.py') 2>&1 | Tee-Object -FilePath $testLog
if ($LASTEXITCODE -ne 0) { throw 'v1.1.10 recovery/gravity tests failed' }

$versions = @(
    '1.3.15.110062',
    '1.4.0.112726-beta',
    '1.4.1.113228-beta',
    '1.4.2.113809-beta',
    '1.4.3.114169-beta',
    '1.4.4.114449-beta',
    '1.4.5.115026',
    '1.4.6.115628',
    '1.4.7.117484'
)
$matrix = New-Object System.Collections.Generic.List[string]
$productionOutput = $null
foreach ($version in $versions) {
    $safe = $version.Replace('.','_').Replace('-','_')
    $buildRoot = Join-Path $workspace "build-$safe"
    $sourceRoot = Join-Path $buildRoot 'Source'
    $output = Join-Path $buildRoot 'out'
    New-Item -ItemType Directory -Path $sourceRoot,$output -Force | Out-Null
    Copy-Item -Path (Join-Path $moduleRoot 'Source/*') -Destination $sourceRoot -Recurse -Force
    $project = Join-Path $sourceRoot 'GuidedArrow.csproj'
    $projectText = [IO.File]::ReadAllText($project)
    $projectText = $projectText.Replace('1.3.15.110062', $version)
    Write-Utf8NoBom $project $projectText

    $restoreLog = Join-Path $diagnostics "RESTORE_$safe.txt"
    $compileLog = Join-Path $diagnostics "COMPILER_$safe.txt"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet restore $project --nologo 2>&1 | Tee-Object -FilePath $restoreLog
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for Bannerlord $version" }
    & dotnet build $project -c Release --no-restore --nologo -p:ContinuousIntegrationBuild=true -p:TreatWarningsAsErrors=true -o $output 2>&1 | Tee-Object -FilePath $compileLog
    if ($LASTEXITCODE -ne 0) { throw "Compile failed for Bannerlord $version" }
    $sw.Stop()
    $dll = Join-Path $output 'GuidedArrow.dll'
    $pdb = Join-Path $output 'GuidedArrow.pdb'
    if (!(Test-Path $dll) -or !(Test-Path $pdb)) { throw "Expected build output missing for $version" }
    $matrix.Add("${version}: PASS, 0 warnings, 0 errors, $($sw.Elapsed)")
    if ($version -eq '1.3.15.110062') { $productionOutput = $output }
}
if ($null -eq $productionOutput) { throw 'Production output was not assigned' }

$runtimeRoot = Join-Path $artifact 'GuidedArrow'
$binRoot = Join-Path $runtimeRoot 'bin/Win64_Shipping_Client'
New-Item -ItemType Directory -Path $binRoot,(Join-Path $runtimeRoot 'GUI/Prefabs'),(Join-Path $runtimeRoot 'Source') -Force | Out-Null
Copy-Item (Join-Path $productionOutput 'GuidedArrow.dll') $binRoot
Copy-Item (Join-Path $productionOutput 'GuidedArrow.pdb') $binRoot
Copy-Item (Join-Path $moduleRoot 'SubModule.xml') $runtimeRoot
Copy-Item (Join-Path $moduleRoot 'GUI/Prefabs/GuidedArrowCrosshair.xml') (Join-Path $runtimeRoot 'GUI/Prefabs')
Copy-Item (Join-Path $moduleRoot 'Source/*') (Join-Path $runtimeRoot 'Source') -Recurse -Force
Copy-Item $patchPath (Join-Path $runtimeRoot 'GuidedArrow-v1.1.9-to-v1.1.10-Recovery-Plane-Gravity.patch')
Copy-Item $testPath (Join-Path $runtimeRoot 'test-ga1110-recovery-gravity.py')

$dllPath = Join-Path $binRoot 'GuidedArrow.dll'
$pdbPath = Join-Path $binRoot 'GuidedArrow.pdb'
$dllHash = Get-Sha256 $dllPath
$pdbHash = Get-Sha256 $pdbPath
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
if ($versionInfo.FileVersion -ne '1.1.10.0') { throw "Unexpected DLL FileVersion: $($versionInfo.FileVersion)" }

$matrixPath = Join-Path $diagnostics 'COMPATIBILITY_MATRIX.txt'
Write-Utf8NoBom $matrixPath (($matrix -join [Environment]::NewLine) + [Environment]::NewLine)
$sourceVerification = @(
    'BASE_V1_1_9_BEHAVIOR_SHA256=f7019ca71880c1da7666fc8409f4184df620f26d3bd1a50cfbb421c6ef5652d5',
    'PATCH_SHA256=73b680f961533c03de34293d2a5dcde4bc325a20e9eac9020047bc17f6b0b797',
    'TEST_SHA256=11eb204be44c153f339deb5f4c7239b920e42d5191756cb585be64d8d4a28c78',
    'FINAL_BEHAVIOR_SHA256=b6dad903ddabbca715634ece63c8d3865ed589746b9315a338eee346187da1b5',
    'FINAL_PROJECT_SHA256=5d0266ad4022647733fa25eb5f0e32e9c2b323ce5a621491c69646f51bd7521a',
    'FINAL_SUBMODULE_XML_SHA256=13b0857a25403c95e91ef78dbff1727f34acc87469170f7766e64e0a8990608e',
    "DLL_SHA256=$dllHash",
    "PDB_SHA256=$pdbHash"
)
Write-Utf8NoBom (Join-Path $diagnostics 'SOURCE_VERIFICATION.txt') (($sourceVerification -join [Environment]::NewLine) + [Environment]::NewLine)
$metadata = @(
    'GUIDED_ARROW_VERSION=1.1.10',
    'PRODUCTION_REFERENCE=1.3.15.110062',
    "ASSEMBLY_VERSION=$($versionInfo.FileVersion)",
    "PRODUCT_VERSION=$($versionInfo.ProductVersion)",
    "DLL_SHA256=$dllHash",
    "PDB_SHA256=$pdbHash"
)
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_METADATA.txt') (($metadata -join [Environment]::NewLine) + [Environment]::NewLine)
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_STATUS.txt') "SUCCESS`n"

Write-Host 'GA1110_BUILD=SUCCESS'
Write-Host "DLL_SHA256=$dllHash"
Write-Host "PDB_SHA256=$pdbHash"
