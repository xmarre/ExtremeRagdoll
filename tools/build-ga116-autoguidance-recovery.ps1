param(
    [Parameter(Mandatory = $true)]
    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Get-Location).Path
$baseOutput = Join-Path $OutputRoot 'v1.1.5-baseline'
$diag = Join-Path $OutputRoot 'diagnostics'
$artifact = Join-Path $OutputRoot 'artifact'
New-Item -ItemType Directory $baseOutput,$diag,$artifact -Force | Out-Null

function Assert-FileHash([string] $Path, [string] $Expected, [string] $Label) {
    if (!(Test-Path -LiteralPath $Path)) { throw "$Label missing: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "$Label hash mismatch: expected $Expected, observed $actual" }
}

function Write-LfFile([string] $Source, [string] $Destination) {
    $text = [IO.File]::ReadAllText($Source).Replace("`r`n", "`n")
    [IO.File]::WriteAllText($Destination, $text, [Text.UTF8Encoding]::new($false))
}

try {
    & (Join-Path $repo 'tools/build-ga115-flight-profiles.ps1') -OutputRoot $baseOutput

    $sourceRoot = Join-Path $env:RUNNER_TEMP 'ga115-build/source'
    $gaSource = Join-Path $sourceRoot 'GuidedArrow'
    if (!(Test-Path -LiteralPath $gaSource)) { throw "Reconstructed v1.1.5 source missing: $gaSource" }

    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrow.csproj') 'b90bd0d3b93df6251a9820a6498371a47f99f17a98c0587e801bcbc942abdee1' 'v1.1.5 project'
    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrowBehavior.cs') '9ea60734372dbba1280ffec3c778689438cb6e6caaacee70e0f589f722f42da4' 'v1.1.5 behavior'
    Assert-FileHash (Join-Path $gaSource 'Source/Settings.cs') '308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8' 'v1.1.5 settings'
    Assert-FileHash (Join-Path $gaSource 'Source/SubModule.cs') '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae' 'v1.1.5 SubModule.cs'
    Assert-FileHash (Join-Path $gaSource 'SubModule.xml') 'c5fff278e1da74702318f5bdec942e11a1d99fc2216f1f3f25416276d08799e0' 'v1.1.5 SubModule.xml'

    $patch = Join-Path $env:RUNNER_TEMP 'ga116-autoguidance-recovery.patch'
    Write-LfFile (Join-Path $repo 'tools/ga116-autoguidance-recovery.patch') $patch
    Assert-FileHash $patch '11feb9e64e9a12307c91edda6fce119d0e016753a9eaf6772c62680c502d5d51' 'v1.1.6 patch'

    Push-Location $sourceRoot
    try {
        git apply --check --verbose --unsafe-paths $patch
        if ($LASTEXITCODE -ne 0) { throw 'v1.1.6 patch applicability check failed' }
        git apply --verbose --unsafe-paths $patch
        if ($LASTEXITCODE -ne 0) { throw 'v1.1.6 patch application failed' }
        git diff --check
        if ($LASTEXITCODE -ne 0) { throw 'v1.1.6 patch introduced whitespace errors' }
    }
    finally { Pop-Location }

    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrow.csproj') '128378a20b40d17aa6f25ce57ea08294f237e56ffad11f958b8edc70e2dab477' 'v1.1.6 project'
    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrowBehavior.cs') '7823433022369c96e7fe7f2ec8097e9d11cc1d311be93cd640a67aa76d020db0' 'v1.1.6 behavior'
    Assert-FileHash (Join-Path $gaSource 'Source/Settings.cs') '308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8' 'unchanged settings'
    Assert-FileHash (Join-Path $gaSource 'Source/SubModule.cs') '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae' 'unchanged SubModule.cs'
    Assert-FileHash (Join-Path $gaSource 'SubModule.xml') 'c68e927eede1060c46ebc826a7c96bb7db763a1287b34aa823ccdbc634b41fb7' 'v1.1.6 SubModule.xml'

    $behavior = [IO.File]::ReadAllText((Join-Path $gaSource 'Source/GuidedArrowBehavior.cs'))
    foreach ($marker in @(
        'GuidanceRecoveryActive',
        'GuidanceForceDirectIntercept',
        'GetAutoguidanceRecoveryReengageReserve',
        'ShouldForceDirectTerminalIntercept',
        'A decorative profile waypoint may become infeasible',
        'float demandedReserve = Math.Max(0f, reserve) * Clamp(turnDemand, 0f, 1f);'
    )) {
        if (!$behavior.Contains($marker)) { throw "Missing v1.1.6 behavior gate: $marker" }
    }
    if ($behavior.Contains('bestFallbackIndex')) { throw 'Unreachable-target fallback was reintroduced' }

    $settings = [IO.File]::ReadAllText((Join-Path $gaSource 'Source/Settings.cs'))
    if (!$settings.Contains('public string AutoguidanceHotkeyName { get; set; } = "Ctrl+G";')) { throw 'MCM hotkey identity changed' }
    if (!$settings.Contains('[SettingPropertyDropdown("Autoguidance Flight Profile", Order = 14')) { throw 'Flight profile setting changed' }

    python (Join-Path $repo 'tools/test-ga116-guidance.py') 2>&1 | Tee-Object -FilePath (Join-Path $diag 'GUIDANCE_GEOMETRY_TESTS.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Guidance geometry regressions failed' }

    @(
        'SOURCE_INTEGRITY=PASS',
        'PATCH_APPLICABILITY=PASS',
        'MCM_SCHEMA_PRESERVED=PASS',
        'UNREACHABLE_FALLBACK_REMOVED=PASS',
        'BEHAVIOR_SHA256=7823433022369c96e7fe7f2ec8097e9d11cc1d311be93cd640a67aa76d020db0',
        'PROJECT_SHA256=128378a20b40d17aa6f25ce57ea08294f237e56ffad11f958b8edc70e2dab477',
        'SETTINGS_SHA256=308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8'
    ) | Set-Content (Join-Path $diag 'SOURCE_VERIFICATION.txt') -Encoding utf8

    $references = @(
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
    $project = Join-Path $gaSource 'Source/GuidedArrow.csproj'
    $originalProject = [IO.File]::ReadAllText($project)
    $matrixResults = New-Object System.Collections.Generic.List[string]
    $productionDll = $null
    $productionPdb = $null

    try {
        foreach ($reference in $references) {
            $safeReference = $reference.Replace('.', '_').Replace('-', '_')
            $retargeted = $originalProject.Replace('1.3.15.110062', $reference)
            [IO.File]::WriteAllText($project, $retargeted, [Text.UTF8Encoding]::new($false))

            foreach ($folder in @('bin','obj')) {
                $path = Join-Path (Join-Path $gaSource 'Source') $folder
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
            }

            Push-Location (Join-Path $gaSource 'Source')
            try {
                dotnet restore GuidedArrow.csproj --nologo --force-evaluate 2>&1 | Tee-Object -FilePath (Join-Path $diag "RESTORE_$safeReference.txt")
                if ($LASTEXITCODE -ne 0) { throw "Restore failed for Bannerlord $reference" }
                dotnet build GuidedArrow.csproj -c Release --no-restore --nologo /p:ContinuousIntegrationBuild=true /p:Deterministic=true 2>&1 | Tee-Object -FilePath (Join-Path $diag "COMPILER_$safeReference.txt")
                if ($LASTEXITCODE -ne 0) { throw "Build failed for Bannerlord $reference" }
            }
            finally { Pop-Location }

            $compiled = Join-Path $gaSource 'Source/bin/Release/net472'
            $dll = Join-Path $compiled 'GuidedArrow.dll'
            $pdb = Join-Path $compiled 'GuidedArrow.pdb'
            $assembly = [Reflection.AssemblyName]::GetAssemblyName($dll)
            if ($assembly.Version.ToString() -ne '1.1.6.0') { throw "Unexpected assembly version for ${reference}: $($assembly.Version)" }
            $matrixResults.Add("$reference=PASS")

            if ($reference -eq '1.3.15.110062') {
                $productionDll = Join-Path $artifact 'GuidedArrow.dll'
                $productionPdb = Join-Path $artifact 'GuidedArrow.pdb'
                Copy-Item -LiteralPath $dll -Destination $productionDll -Force
                Copy-Item -LiteralPath $pdb -Destination $productionPdb -Force
            }
        }
    }
    finally {
        [IO.File]::WriteAllText($project, $originalProject, [Text.UTF8Encoding]::new($false))
    }

    if ($null -eq $productionDll -or !(Test-Path -LiteralPath $productionDll)) { throw 'Production DLL was not captured' }
    if ($null -eq $productionPdb -or !(Test-Path -LiteralPath $productionPdb)) { throw 'Production PDB was not captured' }
    $matrixResults | Set-Content (Join-Path $diag 'COMPATIBILITY_MATRIX.txt') -Encoding utf8

    $module = Join-Path $artifact 'GuidedArrow'
    $moduleSource = Join-Path $module 'Source'
    $moduleBin = Join-Path $module 'bin/Win64_Shipping_Client'
    New-Item -ItemType Directory $moduleSource,$moduleBin -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $gaSource 'SubModule.xml') -Destination $module -Force
    Copy-Item -LiteralPath (Join-Path $gaSource 'Source/GuidedArrow.csproj') -Destination $moduleSource -Force
    Copy-Item -LiteralPath (Join-Path $gaSource 'Source/GuidedArrowBehavior.cs') -Destination $moduleSource -Force
    Copy-Item -LiteralPath (Join-Path $gaSource 'Source/Settings.cs') -Destination $moduleSource -Force
    Copy-Item -LiteralPath (Join-Path $gaSource 'Source/SubModule.cs') -Destination $moduleSource -Force
    if (Test-Path -LiteralPath (Join-Path $gaSource 'GUI')) {
        Copy-Item -LiteralPath (Join-Path $gaSource 'GUI') -Destination $module -Recurse -Force
    }
    Copy-Item -LiteralPath $productionDll -Destination $moduleBin -Force
    Copy-Item -LiteralPath $productionPdb -Destination $moduleBin -Force

    Copy-Item -LiteralPath $patch -Destination (Join-Path $artifact 'GuidedArrow-v1.1.5-to-v1.1.6.patch') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'tools/test-ga116-guidance.py') -Destination $artifact -Force

    $assembly = [Reflection.AssemblyName]::GetAssemblyName($productionDll)
    $versionInfo = (Get-Item -LiteralPath $productionDll).VersionInfo
    @(
        "AssemblyVersion=$($assembly.Version)",
        "FileVersion=$($versionInfo.FileVersion)",
        "ProductVersion=$($versionInfo.ProductVersion)",
        "DLL_SHA256=$((Get-FileHash -LiteralPath $productionDll -Algorithm SHA256).Hash.ToLowerInvariant())",
        "PDB_SHA256=$((Get-FileHash -LiteralPath $productionPdb -Algorithm SHA256).Hash.ToLowerInvariant())",
        'PRODUCTION_REFERENCE=1.3.15.110062',
        'SUPPORTED_RANGE=1.3.15-1.4.7'
    ) | Set-Content (Join-Path $artifact 'BUILD_METADATA.txt') -Encoding utf8

    'BUILD_STATUS=SUCCESS' | Set-Content (Join-Path $diag 'BUILD_STATUS.txt') -Encoding utf8
    Copy-Item -Path (Join-Path $diag '*') -Destination $artifact -Force
}
catch {
    @(
        'BUILD_STATUS=FAILURE',
        "ERROR_TYPE=$($_.Exception.GetType().FullName)",
        "ERROR_MESSAGE=$($_.Exception.Message)",
        "STACK=$($_.ScriptStackTrace)"
    ) | Set-Content (Join-Path $diag 'BUILD_STATUS.txt') -Encoding utf8
    throw
}
