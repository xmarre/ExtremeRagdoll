# Extreme Ragdoll

Extreme Ragdoll is a Mount & Blade II: Bannerlord single-player mod that amplifies directional death physics while preserving Bannerlord's normal corpse and mission lifecycle.

Current repository version: **v1.3.16**  
Supported Bannerlord range: **v1.3.15–v1.4.7**

## Repository layout

- `Source/` — current v1.3.16 C# source and the minimal deterministic build toolchain.
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

## v1.3.16 scope

- Retains the v1.3.15 live localization registration and full English/Simplified Chinese translation tables.
- Corrects MCM v5.12.1's stale-language view-model state by calling MCM's own `RefreshValues` path whenever the Mod Options category becomes active.
- Refreshes setting labels, group headings, the mod-list entry, and the selected mod title after an in-game text-language change.
- Uses a guarded, reflection-based Harmony integration that becomes a no-op if the expected MCM UI type is unavailable.
- Adds no mission tick, campaign scan, persistent polling, physics, force, corpse-finalization, or Dismemberment Plus changes.

Runtime validation inside Bannerlord remains required for MCM rendering behaviour that cannot be executed in GitHub Actions.
