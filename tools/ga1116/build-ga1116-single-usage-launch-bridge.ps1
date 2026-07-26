param(
    [Parameter(Mandatory=$true)][string]$BaseOutputRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Hash([string]$Path, [string]$Expected, [string]$Label) {
    if (!(Test-Path -LiteralPath $Path)) { throw "${Label} missing: $Path" }
    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "${Label} hash mismatch: expected $Expected, observed $actual"
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Copy-NormalizedText([string]$Source, [string]$Destination, [string]$Expected, [string]$Label) {
    if (!(Test-Path -LiteralPath $Source)) { throw "${Label} source missing: $Source" }
    $text = [IO.File]::ReadAllText($Source).Replace("`r`n", "`n").Replace("`r", "`n")
    Write-Utf8NoBom $Destination $text
    Assert-Hash $Destination $Expected $Label
}

$repo = (Resolve-Path $env:GITHUB_WORKSPACE).Path
$payload = Join-Path $repo 'tools/ga1116'
$workspace = Join-Path $env:RUNNER_TEMP 'ga1116-work'
$moduleRoot = Join-Path $workspace 'GuidedArrow'
$baseModule = Join-Path $BaseOutputRoot 'artifact/GuidedArrow'
$diagnostics = Join-Path $OutputRoot 'diagnostics'
$artifact = Join-Path $OutputRoot 'artifact'
$patch = Join-Path $workspace 'GuidedArrow-v1.1.15-to-v1.1.16-Single-Usage-Launch-Bridge.patch'
$test = Join-Path $workspace 'test-ga1116-single-usage-launch-bridge.py'

Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workspace, $diagnostics, $artifact -Force | Out-Null
if (!(Test-Path -LiteralPath $baseModule)) { throw "v1.1.15 base artifact missing: $baseModule" }
Copy-Item $baseModule $moduleRoot -Recurse -Force

$baseHashes = @{
    'Source/GuidedArrowBehavior.cs' = '69a0cba342a232e7771a5fa46d2ee96ca66d7f8ec73c0380f07afbc008330954';
    'Source/MissileDamageBridge.cs' = 'a94116829b93cd617e3957af27ad7c9feba98c2f840ebd198235b6273e9f6223';
    'Source/Settings.cs' = '2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
    'Source/GuidedArrow.csproj' = '916f0940c60c341fa9f631b915ba82fee1d842ad1b2a725d52e09cf58592748e';
    'Source/SubModule.cs' = 'a3a14c334729ed0039f3e274d8557168344e72923d3c23394223f4f38f26309a';
    'SubModule.xml' = '1260cfba8eac6bae76e608630b8fcb8888f2a805f64a99f23bf6db141d1dd6c9';
    'GUI/Prefabs/GuidedArrowCrosshair.xml' = '318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach ($entry in $baseHashes.GetEnumerator()) {
    Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.15 $($entry.Key)"
}

Copy-NormalizedText (Join-Path $payload 'GuidedArrow-v1.1.15-to-v1.1.16-Single-Usage-Launch-Bridge.patch') $patch '26d5ed256a07e64cfbef1444fe82c839d302f38de37c578ffd5ab790f5cf2b9f' 'v1.1.16 patch'
Copy-NormalizedText (Join-Path $payload 'test-ga1116-single-usage-launch-bridge.py') $test 'ad761d4e26dc7bd1a65311c018a66f450537a4a1f8be634036c1c8a33c47949a' 'v1.1.16 test'

Push-Location $moduleRoot
try {
    & git init -q
    if ($LASTEXITCODE -ne 0) { throw 'git init failed' }
    & git config core.autocrlf false
    & git apply --check -p1 --whitespace=error-all $patch
    if ($LASTEXITCODE -ne 0) { throw 'v1.1.16 patch check failed' }
    & git apply -p1 --whitespace=error-all $patch
    if ($LASTEXITCODE -ne 0) { throw 'v1.1.16 patch apply failed' }
}
finally {
    Pop-Location
}

$finalHashes = @{
    'Source/GuidedArrowBehavior.cs' = '69a0cba342a232e7771a5fa46d2ee96ca66d7f8ec73c0380f07afbc008330954';
    'Source/MissileDamageBridge.cs' = '3cb8394d802a75cb23ffaa3ba8cde723462dca09118988dc8b6d0e73ee9b95f9';
    'Source/Settings.cs' = '2e13d219eec57f433c9776349881112b816a1084d4cb15686b0fe9a683493da7';
    'Source/GuidedArrow.csproj' = 'b50657858c78e8c6c8b698337293b163a066e72fb8dfa468ab2bb545d3ca4624';
    'Source/SubModule.cs' = 'a3a14c334729ed0039f3e274d8557168344e72923d3c23394223f4f38f26309a';
    'SubModule.xml' = 'd6e753d8c576b5af2d3024855116b269ac6b0ccfaddd0d11383dadcffefaa9bc';
    'GUI/Prefabs/GuidedArrowCrosshair.xml' = '318c41e4f52494dacf899da5497b6d0d89e4f67804363ecd8f82182ca194b372'
}
foreach ($entry in $finalHashes.GetEnumerator()) {
    Assert-Hash (Join-Path $moduleRoot $entry.Key) $entry.Value "v1.1.16 $($entry.Key)"
}

$testLog = Join-Path $diagnostics 'SINGLE_USAGE_LAUNCH_BRIDGE_TESTS.txt'
$oldModuleRoot = $env:GA1116_MODULE_ROOT
try {
    $env:GA1116_MODULE_ROOT = $moduleRoot
    & python $test 2>&1 | Tee-Object -FilePath $testLog
    if ($LASTEXITCODE -ne 0) { throw 'v1.1.16 single-usage launch bridge tests failed' }
}
finally {
    $env:GA1116_MODULE_ROOT = $oldModuleRoot
}

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
$production = $null
foreach ($version in $versions) {
    $safe = $version.Replace('.', '_').Replace('-', '_')
    $buildRoot = Join-Path $workspace "build-$safe"
    $sourceRoot = Join-Path $buildRoot 'Source'
    $out = Join-Path $buildRoot 'out'
    New-Item -ItemType Directory -Path $sourceRoot, $out -Force | Out-Null
    Copy-Item (Join-Path $moduleRoot 'Source/*') $sourceRoot -Recurse -Force
    $project = Join-Path $sourceRoot 'GuidedArrow.csproj'
    $projectText = [IO.File]::ReadAllText($project).Replace('1.3.15.110062', $version)
    Write-Utf8NoBom $project $projectText
    $restore = Join-Path $diagnostics "RESTORE_$safe.txt"
    $compile = Join-Path $diagnostics "COMPILER_$safe.txt"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & dotnet restore $project --nologo 2>&1 | Tee-Object -FilePath $restore
    if ($LASTEXITCODE -ne 0) { throw "Restore failed for $version" }
    & dotnet build $project -c Release --no-restore --nologo -p:ContinuousIntegrationBuild=true -p:TreatWarningsAsErrors=true -o $out 2>&1 | Tee-Object -FilePath $compile
    if ($LASTEXITCODE -ne 0) { throw "Compile failed for $version" }
    $sw.Stop()
    if (!(Test-Path (Join-Path $out 'GuidedArrow.dll')) -or !(Test-Path (Join-Path $out 'GuidedArrow.pdb'))) {
        throw "Build output missing for $version"
    }
    $matrix.Add("${version}: PASS, 0 warnings, 0 errors, $($sw.Elapsed)")
    if ($version -eq '1.3.15.110062') { $production = $out }
}
if ($null -eq $production) { throw 'Production output missing' }

$runtime = Join-Path $artifact 'GuidedArrow'
$bin = Join-Path $runtime 'bin/Win64_Shipping_Client'
New-Item -ItemType Directory -Path $bin, (Join-Path $runtime 'GUI/Prefabs'), (Join-Path $runtime 'Source') -Force | Out-Null
Copy-Item (Join-Path $production 'GuidedArrow.dll') $bin
Copy-Item (Join-Path $production 'GuidedArrow.pdb') $bin
Copy-Item (Join-Path $moduleRoot 'SubModule.xml') $runtime
Copy-Item (Join-Path $moduleRoot 'GUI/Prefabs/GuidedArrowCrosshair.xml') (Join-Path $runtime 'GUI/Prefabs')
Copy-Item (Join-Path $moduleRoot 'Source/*') (Join-Path $runtime 'Source') -Recurse -Force
Copy-Item $patch (Join-Path $runtime 'GuidedArrow-v1.1.15-to-v1.1.16-Single-Usage-Launch-Bridge.patch')
Copy-Item $test $runtime

$dll = Join-Path $bin 'GuidedArrow.dll'
$pdb = Join-Path $bin 'GuidedArrow.pdb'
$dllHash = Get-Sha256 $dll
$pdbHash = Get-Sha256 $pdb
$vi = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
if ($vi.FileVersion -ne '1.1.16.0') { throw "Unexpected DLL version $($vi.FileVersion)" }

Write-Utf8NoBom (Join-Path $diagnostics 'COMPATIBILITY_MATRIX.txt') (($matrix -join [Environment]::NewLine) + [Environment]::NewLine)
Write-Utf8NoBom (Join-Path $diagnostics 'SOURCE_VERIFICATION.txt') "BASE_V1_1_15_BRIDGE_SHA256=a94116829b93cd617e3957af27ad7c9feba98c2f840ebd198235b6273e9f6223`nPATCH_SHA256=26d5ed256a07e64cfbef1444fe82c839d302f38de37c578ffd5ab790f5cf2b9f`nTEST_SHA256=ad761d4e26dc7bd1a65311c018a66f450537a4a1f8be634036c1c8a33c47949a`nFINAL_BEHAVIOR_SHA256=69a0cba342a232e7771a5fa46d2ee96ca66d7f8ec73c0380f07afbc008330954`nFINAL_DAMAGE_BRIDGE_SHA256=3cb8394d802a75cb23ffaa3ba8cde723462dca09118988dc8b6d0e73ee9b95f9`nFINAL_PROJECT_SHA256=b50657858c78e8c6c8b698337293b163a066e72fb8dfa468ab2bb545d3ca4624`nFINAL_SUBMODULE_SHA256=d6e753d8c576b5af2d3024855116b269ac6b0ccfaddd0d11383dadcffefaa9bc`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_METADATA.txt') "GUIDED_ARROW_VERSION=1.1.16`nPRODUCTION_REFERENCE=1.3.15.110062`nDLL_SHA256=$dllHash`nPDB_SHA256=$pdbHash`n"
Write-Utf8NoBom (Join-Path $diagnostics 'BUILD_STATUS.txt') "SUCCESS`n"
Write-Host 'GA1116_BUILD=SUCCESS'
Write-Host "DLL_SHA256=$dllHash"
Write-Host "PDB_SHA256=$pdbHash"
