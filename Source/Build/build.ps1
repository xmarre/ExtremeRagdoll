param([string]$OutDir = "")
$ErrorActionPreference = "Stop"
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"

$BuildDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SourceDir = Split-Path -Parent $BuildDir
if (-not $OutDir) { $OutDir = Join-Path $BuildDir "out" }
$StubDir = Join-Path $OutDir "stubs"
$BinDir = Join-Path $OutDir "bin"
$ToolDir = Join-Path $OutDir "tools"

if (-not $env:DOTNET_ROOT) { throw "Set DOTNET_ROOT to a .NET Core 3.1 SDK installation." }
$DotNet = if ($env:DOTNET) { $env:DOTNET } else { Join-Path $env:DOTNET_ROOT "dotnet.exe" }
$SdkDir = if ($env:DOTNET_SDK_DIR) { $env:DOTNET_SDK_DIR } else {
    (Get-ChildItem (Join-Path $env:DOTNET_ROOT "sdk") -Directory -Filter "3.1.*" | Sort-Object Name | Select-Object -Last 1).FullName
}
$Csc = Join-Path $SdkDir "Roslyn/bincore/csc.dll"
$NetStandard = Join-Path $SdkDir "ref/netstandard.dll"
$NetCoreRef = if ($env:NETCORE_REF_DIR) { $env:NETCORE_REF_DIR } else {
    (Get-ChildItem (Join-Path $env:DOTNET_ROOT "packs/Microsoft.NETCore.App.Ref") -Directory -Filter "3.1.*" | Sort-Object Name | Select-Object -First 1).FullName + "/ref/netcoreapp3.1"
}
$CecilProject = Join-Path $BuildDir "Tools/Mono.Cecil.csproj"
if (-not (Test-Path $CecilProject)) { throw "Required Mono.Cecil restore project not found: $CecilProject" }
& $DotNet restore $CecilProject --nologo
if ($LASTEXITCODE -ne 0) { throw "Mono.Cecil restore failed" }

$NuGetPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME ".nuget/packages" }
$CecilCandidates = Get-ChildItem (Join-Path $NuGetPackages "mono.cecil/0.10.1/lib") -Filter "Mono.Cecil.dll" -Recurse
$Cecil = ($CecilCandidates | Where-Object { $_.FullName -match "netstandard" } | Select-Object -First 1).FullName
if (-not $Cecil) { $Cecil = ($CecilCandidates | Select-Object -First 1).FullName }
if (-not $Cecil -or -not (Test-Path $Cecil)) { throw "Restored Mono.Cecil 0.10.1 assembly was not found under $NuGetPackages" }

New-Item -ItemType Directory -Force $StubDir, $BinDir, $ToolDir | Out-Null
$Common = @($Csc, "/nologo", "/noconfig", "/nostdlib", "/deterministic+", "/optimize+", "/debug-", "/langversion:7.3", "/target:library", "/r:$NetStandard")
$Stubs = Join-Path $BuildDir "ReferenceStubs"

function Compile([string[]]$Arguments) { & $DotNet @Arguments; if ($LASTEXITCODE -ne 0) { throw "C# compilation failed" } }

Compile ($Common + @("/out:$StubDir/TaleWorlds.Library.dll", "$Stubs/TaleWorlds.Library.Stub.cs"))
Compile ($Common + @("/out:$StubDir/TaleWorlds.Core.dll", "$Stubs/TaleWorlds.Core.Stub.cs"))
Compile ($Common + @("/out:$StubDir/TaleWorlds.Engine.dll", "/r:$StubDir/TaleWorlds.Library.dll", "$Stubs/TaleWorlds.Engine.Stub.cs"))
Compile ($Common + @("/out:$StubDir/MCMv5.dll", "$Stubs/MCMv5.Stub.cs"))
Compile ($Common + @("/out:$StubDir/TaleWorlds.MountAndBlade.dll", "/r:$StubDir/TaleWorlds.Library.dll", "/r:$StubDir/TaleWorlds.Core.dll", "/r:$StubDir/TaleWorlds.Engine.dll", "$Stubs/TaleWorlds.MountAndBlade.Stub.cs"))
Compile ($Common + @("/out:$BinDir/ExtremeRagdoll.ClothSync.dll", "/r:$StubDir/TaleWorlds.Library.dll", "/r:$StubDir/TaleWorlds.MountAndBlade.dll", "$SourceDir/ClothForceBridge.cs"))
Compile ($Common + @("/out:$BinDir/ExtremeRagdoll.raw.dll", "/r:$StubDir/TaleWorlds.Library.dll", "/r:$StubDir/TaleWorlds.Core.dll", "/r:$StubDir/TaleWorlds.Engine.dll", "/r:$StubDir/TaleWorlds.MountAndBlade.dll", "/r:$StubDir/MCMv5.dll", "/r:$BinDir/ExtremeRagdoll.ClothSync.dll", "$SourceDir/SafeSubModule.cs"))

$NetCoreRefs = Get-ChildItem $NetCoreRef -Filter "*.dll" | ForEach-Object { "/r:$($_.FullName)" }
$ToolCommon = @($Csc, "/nologo", "/noconfig", "/nostdlib", "/deterministic+", "/optimize+", "/debug-", "/langversion:7.3", "/target:exe", "/nowarn:1701") + $NetCoreRefs + @("/r:$Cecil")
Copy-Item $Cecil "$ToolDir/Mono.Cecil.dll" -Force
Copy-Item "$BuildDir/PatchOverride.runtimeconfig.json" "$ToolDir/PatchOverride.runtimeconfig.json" -Force
Copy-Item "$BuildDir/ValidateAssemblies.runtimeconfig.json" "$ToolDir/ValidateAssemblies.runtimeconfig.json" -Force
Compile ($ToolCommon + @("/out:$ToolDir/PatchOverride.dll", "$SourceDir/PatchOverride.cs"))
Compile ($ToolCommon + @("/out:$ToolDir/ValidateAssemblies.dll", "$BuildDir/ValidateAssemblies.cs"))

& $DotNet "$ToolDir/PatchOverride.dll" "$BinDir/ExtremeRagdoll.raw.dll" "$StubDir/TaleWorlds.MountAndBlade.dll" "$BinDir/ExtremeRagdoll.dll"
if ($LASTEXITCODE -ne 0) { throw "Override patch failed" }
& $DotNet "$ToolDir/ValidateAssemblies.dll" "$BinDir/ExtremeRagdoll.dll" "$BinDir/ExtremeRagdoll.ClothSync.dll"
if ($LASTEXITCODE -ne 0) { throw "Assembly validation failed" }
Write-Host "Build complete: $BinDir"
