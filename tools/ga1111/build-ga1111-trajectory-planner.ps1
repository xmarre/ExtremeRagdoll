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
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Decode-GzipBase64([string]$InputPath, [string]$OutputPath, [string]$ExpectedGzipHash, [string]$Label) {
    if (!(Test-Path -LiteralPath $InputPath)) { throw "$Label payload missing: $InputPath" }
    $raw = [IO.File]::ReadAllText($InputPath)
    $clean = [Text.RegularExpressions.Regex]::Replace($raw, '\s+', '')
    $compressed = [Convert]::FromBase64String($clean)
    $gzipHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($compressed)).Replace('-','').ToLowerInvariant()
    if ($gzipHash -ne $ExpectedGzipHash.ToLowerInvariant()) { throw "$Label gzip hash mismatch: expected $ExpectedGzipHash, observed $gzipHash" }
    $input = [IO.MemoryStream]::new($compressed)
    $gzip = [IO.Compression.GZipStream]::new($input, [IO.Compression.CompressionMode]::Decompress)
    $output = [IO.File]::Create($OutputPath)
    try { $gzip.CopyTo($output) }
    finally { $output.Dispose(); $gzip.Dispose(); $input.Dispose() }
}

$repoRoot = (Resolve-Path $env:GITHUB_WORKSPACE).Path
$payloadDir = Join-Path $repoRoot 'tools/ga1111'
$workspace = Join-Path $env:RUNNER_TEMP 'ga1111-work'
$patchPayload = Join-Path $payloadDir 'patch.gz.b64'
$testPayload = Join-Path $payloadDir 'test.gz.b64'
$patchPath = Join-Path $workspace 'GuidedArrow-v1.1.10-to-v1.1.11-Trajectory-Planner.patch'
$testPath = Join-Path $workspace 'test-ga1111-trajectory-planner.py'
$moduleRoot = Join-Path $workspace 'GuidedArrow'
$diagnostics = Join-Path $OutputRoot 'diagnostics'
$artifact = Join-Path $OutputRoot 'artifact'
$baseModule = Join-Path $BaseOutputRoot 'artifact/GuidedArrow'

Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workspace,$diagnostics,$artifact -Force | Out-Null
if (!(Test-Path -LiteralPath $baseModule)) { throw "v1.1.10 base artifact module missing: $baseModule" }
Copy-Item -LiteralPath $baseModule -Destination $moduleRoot -Recurse -Force

$baseHashes = @{
    'Source/GuidedArrowBehavior.cs' = 'b6dad903ddabbca715634ece63c8d3865ed589746b9315a338eee346187da1b5'
    'Source/GuidedArrow.csproj' = '5d0266ad4022647733fa25eb5f0e32e9c2b323ce5a621491c69646f51bd7521a'
    'Source/Settings.cs' = '308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8'
    'Source/SubModule.cs' = '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae'
    'SubModule.xml' = '13b0857a25403c95e91ef78dbff1727f34acc87469170f7766e64e0a8990608e'
    'GUI/Prefabs/GuidedArrowCrosshair.xml' = '318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach ($entry in $baseHashes.GetEnumerator()) {
    Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.10 $($entry.Key)"
}

Decode-GzipBase64 $patchPayload $patchPath 'b227051aa7381d2c960b170bbf7a2a654daee5711f3bee3cf236e9c4ffeef6df' 'v1.1.11 patch'
Decode-GzipBase64 $testPayload $testPath 'ad89f5673fb080be50d8da2f1f0926e2d9a91e6618fc3ea2a80a99311b9c8350' 'v1.1.11 test'
Assert-Hash $patchPath '94df08d0836d6a741df9a4e6fe392a4c710ff52b6b28e4455f55b8bd65e7c9de' 'v1.1.10 to v1.1.11 patch'
Assert-Hash $testPath 'ffd7dc655268999a4a2cad46f4fa49e789153f490df58f05722a35f012c8df65' 'v1.1.11 deterministic test'

Push-Location $workspace
try {
    & git init -q
    if ($LASTEXITCODE -ne 0) { throw 'git init failed' }
    & git config core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw 'git core.autocrlf configuration failed' }
    & git apply --check --whitespace=error-all $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'v1.1.11 patch applicability check failed' }
    & git apply --whitespace=error-all $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'v1.1.11 patch application failed' }
}
finally { Pop-Location }

$finalHashes = @{
    'Source/GuidedArrowBehavior.cs' = '0eb798ada398f24e3c205eeea656142bb40e7474ab616009a1707a8012764022'
    'Source/GuidedArrow.csproj' = '362b3609d9fade89b1fc838ae55af77fe17bc8e322b87163878838a594ead55f'
    'Source/Settings.cs' = '76cd9c14b7bb59455b9da2abbda3ed92db5c6b41d761a9045b4ae172fabc0988'
    'Source/SubModule.cs' = '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae'
    'SubModule.xml' = 'ee06dc15654a9a61fe483f84bc468f7484b6b3125f012c666174dc1584065398'
    'GUI/Prefabs/GuidedArrowCrosshair.xml' = '318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach ($entry in $finalHashes.GetEnumerator()) {
    Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.11 $($entry.Key)"
}

Copy-Item -LiteralPath $testPath -Destination (Join-Path $moduleRoot 'test-ga1111-trajectory-planner.py')
$testLog = Join-Path $diagnostics 'TRAJECTORY_PLANNER_TESTS.txt'
& python (Join-Path $moduleRoot 'test-ga1111-trajectory-planner.py') 2>&1 | Tee-Object -FilePath $testLog
if ($LASTEXITCODE -ne 0) { throw 'v1.1.11 trajectory planner tests failed' }

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
Copy-Item $patchPath (Join-Path $runtimeRoot 'GuidedArrow-v1.1.10-to-v1.1.11-Trajectory-Planner.patch')
Copy-Item $testPath (Join-Path $runtimeRoot 'test-ga1111-trajectory-planner.py')

$dllPath = Join-Path $binRoot 'GuidedArrow.dll'
$pdbPath = Join-Path $binRoot 'GuidedArrow.pdb'
$dllHash = Get-Sha256 $dllPath
$pdbHash = Get-Sha256 $pdbPath
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
if ($versionInfo.FileVersion -ne '1.1.11.0') { throw "Unexpected DLL FileVersion: $($versionInfo.FileVersion)" }

Write-Utf8NoBom (Join-Path $diagnostics 'COMPATIBILITY_MATRIX.txt') (($matrix -join [Environment]::NewLine) + [Environment]::NewLine)
$sourceVerification = @(
    'BASE_V1_1_10_BEHAVIOR_SHA256=b6dad903ddabbca715634ece63c8d3865ed589746b9315a338eee346187da1b5',
    'PATCH_SHA256=94df08d0836d6a741df9a4e6fe392a4c710ff52b6b28e4455f55b8bd65e7c9de',
    'TEST_SHA256=ffd7dc655268999a4a2cad46f4fa49e789153f490df58f05722a35f012c8df65',
    'FINAL_BEHAVIOR_SHA256=0eb798ada398f24e3c205eeea656142bb40e7474ab616009a1707a8012764022',
    'FINAL_SETTINGS_SHA256=76cd9c14b7bb59455b9da2abbda3ed92db5c6b41d761a9045b4ae172fabc0988',
    'FINAL_PROJECT_SHA256=362b3609d9fade89b1fc838ae55af77fe17bc8e322b87163878838a594ead55f',
    'FINAL_SUBMODULE_XML_SHA256=ee06dc15654a9a61fe483f84bc468f7484b6b3125f012c666174dc1584065398',
    "DLL_SHA256=$dllHash",
    "PDB_SHA256=$pdbHash"
)
Write-Utf8NoBom (Join-Path $diagnostics 'SOURCE_VERIFICATION.txt') (($sourceVerification -join [Environment]::NewLine) + [Environment]::NewLine)
$metadata = @(
    'GUIDED_ARROW_VERSION=1.1.11',
    'PRODUCTION_REFERENCE=1.3.15.110062',
    "ASSEMBLY_VERSION=$($versionInfo.FileVersion)",
    "PRODUCT_VERSION=$($versionInfo.ProductVersion)",
    "DLL_SHA256=$dllHash",
    "PDB_SHA256=$pdbHash"
)
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_METADATA.txt') (($metadata -join [Environment]::NewLine) + [Environment]::NewLine)
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_STATUS.txt') "SUCCESS`n"

Write-Host 'GA1111_BUILD=SUCCESS'
Write-Host "DLL_SHA256=$dllHash"
Write-Host "PDB_SHA256=$pdbHash"
