# Building Extreme Ragdoll v1.3.10

## Canonical inputs

The current runtime is built from:

- `Source/SafeSubModule.cs`
- `Source/ClothForceBridge.cs`
- `Source/PatchOverride.cs`
- `Source/Build/ValidateAssemblies.cs`
- `Source/Build/ReferenceStubs/*`
- `Source/Build/Tools/Mono.Cecil.csproj` (restores exact Mono.Cecil 0.10.1)

The stubs expose only the external signatures used by the mod. They are compile-time references and are not runtime files.

## Why the post-build patch exists

`SafeSubModule.cs` exposes the six-parameter mission callback as `OnRegisterBlowCompat` while compiling against the minimal stubs. `PatchOverride` renames it to `OnRegisterBlow`, imports the exact parameter metadata from the generated `TaleWorlds.MountAndBlade.dll` reference assembly, and records the explicit override.

`ValidateAssemblies` then checks the resulting main/helper assembly relationship and expected callback surface.

## Requirements

- .NET Core SDK 3.1
- Bash or PowerShell

## Commands

Windows:

```powershell
.\Source\Build\build.ps1 -OutDir .\Source\Build\out
```

Linux/macOS:

```bash
bash Source/Build/build.sh Source/Build/out
```

Expected outputs:

```text
Source/Build/out/bin/ExtremeRagdoll.dll
Source/Build/out/bin/ExtremeRagdoll.ClothSync.dll
```

Do not distribute the generated `stubs` or `tools` directories.
