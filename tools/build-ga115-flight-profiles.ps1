param(
    [Parameter(Mandatory = $true)]
    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Get-Location).Path
$temp = Join-Path $env:RUNNER_TEMP 'ga115-build'
$diag = Join-Path $OutputRoot 'diagnostics'
$artifact = Join-Path $OutputRoot 'artifact'
New-Item -ItemType Directory $temp,$diag,$artifact -Force | Out-Null

function Assert-FileHash([string] $Path, [string] $Expected, [string] $Label) {
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw "$Label hash mismatch: $actual" }
}

function Write-LfPatch([string] $Source, [string] $Destination) {
    $text = [IO.File]::ReadAllText($Source).Replace("`r`n", "`n")
    [IO.File]::WriteAllText($Destination, $text, [Text.UTF8Encoding]::new($false))
}

function Apply-VerifiedPatch([string] $Root, [string] $Patch, [string] $ExpectedHash, [string] $Label) {
    Assert-FileHash $Patch $ExpectedHash $Label
    Push-Location $Root
    try {
        git apply --check --verbose --unsafe-paths $Patch
        if ($LASTEXITCODE -ne 0) { throw "$Label applicability check failed" }
        git apply --verbose --unsafe-paths $Patch
        if ($LASTEXITCODE -ne 0) { throw "$Label application failed" }
    }
    finally { Pop-Location }
}

try {
    # Exact v1.1.0 source archive.
    $baselineParts = Get-ChildItem (Join-Path $repo 'tools/ga110/p*.b64') | Sort-Object Name
    if ($baselineParts.Count -ne 7) { throw "Expected 7 v1.1.0 chunks, found $($baselineParts.Count)" }
    $baselineB64 = ($baselineParts | ForEach-Object { (Get-Content $_.FullName -Raw).Trim() }) -join ''
    $baselineZip = Join-Path $temp 'ga110.zip'
    [IO.File]::WriteAllBytes($baselineZip, [Convert]::FromBase64String($baselineB64))
    Assert-FileHash $baselineZip '02d485f72bd1eb2da35f16d2e379a480b29fbd3a5717e2b8bfd1c174fb4fd194' 'v1.1.0 source archive'

    $sourceRoot = Join-Path $temp 'source'
    Expand-Archive -LiteralPath $baselineZip -DestinationPath $sourceRoot -Force
    $gaRoot = $sourceRoot
    $gaSource = Join-Path $sourceRoot 'GuidedArrow'

    Push-Location $gaRoot
    try {
        git init | Out-Null
        git config core.autocrlf false
    }
    finally { Pop-Location }

    # Exact v1.1.2 main source patch.
    $ga112Specs = @(
        @{ Path='tools/ga112source/p00.b64'; Hash='68c6d304935d86a253aa3c09c5d0d09e308bbc3534aaa1dee4b590fd24bd80d6' },
        @{ Path='tools/ga112source/p01.b64'; Hash='cdf69e9a9d405f3dc84f41d65aa6932821f75697d591c97804965259f4a5e367' },
        @{ Path='tools/ga112source/p02a.b64'; Hash='cdb7332d17731e63fba24c635fb980803fbe8410c285e2d4f3d4e2b67dbcd636' },
        @{ Path='tools/ga112source/p02b.b64'; Hash='2296f4d3226231aa3416c02407d9e0ff7a36743391afe5dffd064d77c92a1777' }
    )
    foreach ($spec in $ga112Specs) { Assert-FileHash (Join-Path $repo $spec.Path) $spec.Hash $spec.Path }
    $ga112B64 = ($ga112Specs | ForEach-Object { (Get-Content (Join-Path $repo $_.Path) -Raw).Trim() }) -join ''
    $ga112Compressed = [Convert]::FromBase64String($ga112B64)
    $ga112Input = [IO.MemoryStream]::new($ga112Compressed)
    $ga112Gzip = [IO.Compression.GZipStream]::new($ga112Input, [IO.Compression.CompressionMode]::Decompress)
    $ga112Patch = Join-Path $temp 'ga112-main.patch'
    $ga112Output = [IO.File]::Create($ga112Patch)
    try { $ga112Gzip.CopyTo($ga112Output) }
    finally { $ga112Output.Dispose(); $ga112Gzip.Dispose(); $ga112Input.Dispose() }
    Apply-VerifiedPatch $gaRoot $ga112Patch '4f668b3502a402ea4cc1baf2c2747dc44f6da5e3906ee2b0df18ac013a68e37d' 'v1.1.2 main patch'

    # Exact v1.1.2 endpoint/review fixes.
    foreach ($fix in @(
        @{ Path='tools/ga112-compilefix.patch'; Hash='44ec2c59ac832c6b737ae31328d661b3515d0454b3b96bddfc7b8613dd9d639d'; Label='v1.1.2 compatibility fix' },
        @{ Path='tools/ga112-reviewfix.patch'; Hash='23e680ceecdeff42ecd87b5a772d349ae562d92893e78d38b8758a2f62a90287'; Label='v1.1.2 review fix' },
        @{ Path='tools/ga113-startup-hotfix.patch'; Hash='76199e8e2300aea2a8e88b8bba4e1420c242d6c15a9fb5116acdc5cc4b1ededf'; Label='v1.1.3 startup fix' },
        @{ Path='tools/ga113-to-ga114-source.patch'; Hash='5e7f7f0da0e28ea80796a4e30f65a0353f4e53953aea635e9e58e72e2b17f1ba'; Label='v1.1.4 mission-mode patch' }
    )) {
        $normalised = Join-Path $temp ([IO.Path]::GetFileName($fix.Path) + '.lf')
        Write-LfPatch (Join-Path $repo $fix.Path) $normalised
        Apply-VerifiedPatch $gaRoot $normalised $fix.Hash $fix.Label
    }

    # Verify exact v1.1.4 baseline before applying the new feature.
    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrow.csproj') '330678c4655995d36a75c41e636d7175a4f0b54dcf7de11f80fc3017b8c304f4' 'v1.1.4 project'
    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrowBehavior.cs') '2eb112344add51d39c1d2db2b1e7ddf11fad63d728cc8a9b160bd7ac8b46d5fa' 'v1.1.4 behavior'
    Assert-FileHash (Join-Path $gaSource 'Source/Settings.cs') 'ee4d4989f23b5fc2c4bbf11cba6db48b4cab6f0d6621fe894825bda002351d62' 'v1.1.4 settings'
    Assert-FileHash (Join-Path $gaSource 'Source/SubModule.cs') '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae' 'v1.1.4 SubModule.cs'
    Assert-FileHash (Join-Path $gaSource 'SubModule.xml') '38ced9fd1bb40aac34ed076eed78be233db081eb99715b5f48aa3d38c1a3fcc3' 'v1.1.4 SubModule.xml'

    # Exact v1.1.5 flight-profile patch, transported in independently hashed chunks.
    $ga115Specs = @(
        @{ Path='tools/ga115source/p00.b64'; Hash='f6e19bc20953392446a7b7acbe454fde3b9e6ae31ae645c3fd66f2d9539ae939' },
        @{ Path='tools/ga115source/p01.b64'; Hash='584eaaebf1a10f4087313b903a343c4d29e0751f0e9497fafd4b207ce36e194a' },
        @{ Path='tools/ga115source/p02.b64'; Hash='313ef7cf16b83869346c37a258d4c21c20b49e92162965ff2aa256082a7bdd2b' },
        @{ Path='tools/ga115source/p03.b64'; Hash='072190a33425472bb3d6651630d36f870ef4e5d8c78071a2867600667452cf3e' }
    )
    foreach ($spec in $ga115Specs) { Assert-FileHash (Join-Path $repo $spec.Path) $spec.Hash $spec.Path }
    $ga115B64 = ($ga115Specs | ForEach-Object { (Get-Content (Join-Path $repo $_.Path) -Raw).Trim() }) -join ''
    $ga115PayloadHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($ga115B64))).Replace('-','').ToLowerInvariant()
    if ($ga115PayloadHash -ne '9c06fdf651e4c9f342bb9e5cfd0d3d4182a097a9c1a3086a65e9b68f7c2c5ab7') { throw "v1.1.5 payload mismatch: $ga115PayloadHash" }
    $ga115Compressed = [Convert]::FromBase64String($ga115B64)
    $ga115CompressedPath = Join-Path $temp 'ga115-source.patch.gz'
    [IO.File]::WriteAllBytes($ga115CompressedPath, $ga115Compressed)
    Assert-FileHash $ga115CompressedPath 'ee44825d2bf245e50245b83f3a9338f23471dd5a7bec3e8fc7fa37c20bfb69cf' 'v1.1.5 compressed patch'
    $ga115Input = [IO.MemoryStream]::new($ga115Compressed)
    $ga115Gzip = [IO.Compression.GZipStream]::new($ga115Input, [IO.Compression.CompressionMode]::Decompress)
    $ga115Patch = Join-Path $temp 'ga115-source.patch'
    $ga115Output = [IO.File]::Create($ga115Patch)
    try { $ga115Gzip.CopyTo($ga115Output) }
    finally { $ga115Output.Dispose(); $ga115Gzip.Dispose(); $ga115Input.Dispose() }
    Apply-VerifiedPatch $gaRoot $ga115Patch 'df70bcf4ba0919ec2165d7e31e60974f88ded0ea508478b8f6ab897350e30321' 'v1.1.5 flight-profile patch'

    # Exact compiler correction: capture the primary candidate position together with velocity.
    $ga115CompileFix = Join-Path $temp 'ga115-compilefix.patch.lf'
    Write-LfPatch (Join-Path $repo 'tools/ga115-compilefix.patch') $ga115CompileFix
    Apply-VerifiedPatch $gaRoot $ga115CompileFix 'ec351f1887c9af015c8533c21383e6a5fbb7d4152503bca4d15d1e83214ea2de' 'v1.1.5 compiler fix'

    # Exact final source and startup-safe MCM schema.
    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrow.csproj') 'b90bd0d3b93df6251a9820a6498371a47f99f17a98c0587e801bcbc942abdee1' 'v1.1.5 project'
    Assert-FileHash (Join-Path $gaSource 'Source/GuidedArrowBehavior.cs') '9ea60734372dbba1280ffec3c778689438cb6e6caaacee70e0f589f722f42da4' 'v1.1.5 behavior'
    Assert-FileHash (Join-Path $gaSource 'Source/Settings.cs') '308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8' 'v1.1.5 settings'
    Assert-FileHash (Join-Path $gaSource 'Source/SubModule.cs') '31103cc6fc3d192e5164ef3ca0d2128c9026ab32c81cc45d8e9c3ffe73601bae' 'v1.1.5 SubModule.cs'
    Assert-FileHash (Join-Path $gaSource 'SubModule.xml') 'c5fff278e1da74702318f5bdec942e11a1d99fc2216f1f3f25416276d08799e0' 'v1.1.5 SubModule.xml'

    $settings = [IO.File]::ReadAllText((Join-Path $gaSource 'Source/Settings.cs'))
    if (!$settings.Contains('public string AutoguidanceHotkeyName { get; set; } = "Ctrl+G";')) { throw 'Preserved MCM hotkey property missing' }
    if ($settings.Contains('public string AutoguidanceHotkeyChord')) { throw 'Regressed replacement hotkey property exists' }
    if (!$settings.Contains('[SettingPropertyDropdown("Autoguidance Flight Profile", Order = 14')) { throw 'Flight-profile dropdown attribute missing' }
    if (!$settings.Contains('public Dropdown<string> AutoguidanceFlightProfile')) { throw 'Flight-profile dropdown property missing' }
    foreach ($name in @('Low Strike','Natural Ballistic','Direct Hunter','Lofted Arc','Banking Flank','Serpentine','Adaptive Mix')) {
        if (!$settings.Contains('"' + $name + '"')) { throw "Flight-profile option missing: $name" }
    }

    @(
        'SOURCE_INTEGRITY=PASS',
        'MCM_SCHEMA=PASS',
        'PROJECT_SHA256=b90bd0d3b93df6251a9820a6498371a47f99f17a98c0587e801bcbc942abdee1',
        'BEHAVIOR_SHA256=9ea60734372dbba1280ffec3c778689438cb6e6caaacee70e0f589f722f42da4',
        'SETTINGS_SHA256=308079e86f24e845385ed29033c54f8d5b5bebe55f1b77d108cc441d148b36f8'
    ) | Set-Content (Join-Path $diag 'SOURCE_VERIFICATION.txt') -Encoding utf8

    # Production endpoint: Bannerlord 1.3.15.
    Push-Location (Join-Path $gaSource 'Source')
    try {
        dotnet restore GuidedArrow.csproj --nologo 2>&1 | Tee-Object -FilePath (Join-Path $diag 'RESTORE_1.3.15.txt')
        if ($LASTEXITCODE -ne 0) { throw '1.3.15 restore failed' }
        dotnet build GuidedArrow.csproj -c Release --no-restore --nologo /p:ContinuousIntegrationBuild=true /p:Deterministic=true 2>&1 | Tee-Object -FilePath (Join-Path $diag 'COMPILER_1.3.15.txt')
        if ($LASTEXITCODE -ne 0) { throw '1.3.15 build failed' }
    }
    finally { Pop-Location }

    $compiled = Join-Path $gaSource 'Source/bin/Release/net472'
    $dll = Join-Path $compiled 'GuidedArrow.dll'
    $pdb = Join-Path $compiled 'GuidedArrow.pdb'
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($dll)
    if ($assembly.Version.ToString() -ne '1.1.5.0') { throw "Unexpected assembly version: $($assembly.Version)" }
    $versionInfo = (Get-Item $dll).VersionInfo
    @(
        "AssemblyVersion=$($assembly.Version)",
        "FileVersion=$($versionInfo.FileVersion)",
        "ProductVersion=$($versionInfo.ProductVersion)",
        "DLL_SHA256=$((Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant())",
        "PDB_SHA256=$((Get-FileHash $pdb -Algorithm SHA256).Hash.ToLowerInvariant())"
    ) | Set-Content (Join-Path $artifact 'BUILD_METADATA.txt') -Encoding utf8
    Copy-Item $dll,$pdb -Destination $artifact

    # Same exact source, newest supported endpoint: Bannerlord 1.4.7.
    $project = Join-Path $gaSource 'Source/GuidedArrow.csproj'
    $originalProject = [IO.File]::ReadAllText($project)
    try {
        [IO.File]::WriteAllText($project, $originalProject.Replace('1.3.15.110062','1.4.7.117484'), [Text.UTF8Encoding]::new($false))
        Push-Location (Join-Path $gaSource 'Source')
        try {
            dotnet restore GuidedArrow.csproj --nologo 2>&1 | Tee-Object -FilePath (Join-Path $diag 'RESTORE_1.4.7.txt')
            if ($LASTEXITCODE -ne 0) { throw '1.4.7 restore failed' }
            dotnet build GuidedArrow.csproj -c Release --no-restore --nologo /p:ContinuousIntegrationBuild=true /p:Deterministic=true 2>&1 | Tee-Object -FilePath (Join-Path $diag 'COMPILER_1.4.7.txt')
            if ($LASTEXITCODE -ne 0) { throw '1.4.7 build failed' }
        }
        finally { Pop-Location }
    }
    finally { [IO.File]::WriteAllText($project, $originalProject, [Text.UTF8Encoding]::new($false)) }

    Copy-Item (Join-Path $diag '*') -Destination $artifact
    'BUILD_STATUS=SUCCESS' | Set-Content (Join-Path $diag 'BUILD_STATUS.txt') -Encoding utf8
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
