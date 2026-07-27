# Extreme Ragdoll

Extreme Ragdoll is a Mount & Blade II: Bannerlord single-player mod that amplifies directional death physics while preserving Bannerlord's normal corpse and mission lifecycle.

Current repository version: **v1.3.14**  
Supported Bannerlord range: **v1.3.15–v1.4.7**

## Repository layout

- `Source/` — current v1.3.14 C# source and the minimal deterministic build toolchain.
- `bin/Win64_Shipping_Client/` — compiled runtime DLLs loaded by Bannerlord.
- `ModuleData/Languages/` — English and Simplified Chinese MCM localization.
- `SubModule.xml` — Bannerlord module manifest.
- `RUNTIME_SHA256.txt` — hashes for the checked-in runtime DLLs.

Historical patch chains, intermediate binaries, reconstruction payloads, old build reports, and superseded source snapshots are intentionally excluded.

## Installation

1. Download the installable ZIP from the latest GitHub release.
2. Extract the included `ExtremeRagdoll` folder into Bannerlord's `Modules` directory.
3. Ensure Harmony and Mod Configuration Menu v5 are installed.
4. Enable **Extreme Ragdoll** in the Bannerlord launcher or BLSE.

The runtime module requires only these paths:

```text
ExtremeRagdoll/
├── SubModule.xml
├── ModuleData/
└── bin/Win64_Shipping_Client/
    ├── ExtremeRagdoll.dll
    └── ExtremeRagdoll.ClothSync.dll
```

## Building

The canonical build compiles against minimal Bannerlord 1.3.15-compatible reference stubs, then patches the exact `MissionBehavior.OnRegisterBlow` override metadata and validates both assemblies.

Requirements:

- .NET Core SDK 3.1
- NuGet access or a cached `Mono.Cecil` 0.10.1 package
- PowerShell on Windows, or Bash on Linux/macOS

Windows:

```powershell
.\Source\Build\build.ps1 -OutDir .\Source\Build\out
```

Linux/macOS:

```bash
bash Source/Build/build.sh Source/Build/out
```

Build output is written to `Source/Build/out/bin`. The reference stubs are compile-time inputs only and must never be copied into Bannerlord.

## Runtime/source parity

Pull-request CI rebuilds the current source and updates the checked-in runtime DLLs when their bytes differ. The default branch then rebuilds and fails if the committed binaries or `RUNTIME_SHA256.txt` do not match the source build.

## v1.3.14 scope

- Corrects the English and Simplified Chinese string tables to Bannerlord's required `type="string"` localization XML shape.
- Retains the corrected `CNs/language_data.xml` registration path and complete Chinese translation set.
- Adds package checks for the string-table root type, language tags, registered file path, and matching localization IDs.
- Makes no gameplay, force, ragdoll, corpse lifecycle, compatibility, or performance changes.

Runtime validation inside Bannerlord remains required for MCM rendering behavior that cannot be executed in GitHub Actions.
