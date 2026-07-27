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

- Retains the v1.3.15 live localization registration and complete English/Simplified Chinese translation tables.
- Patches MCM's visible cached-label getters directly for Extreme Ragdoll: setting names, group headings, and the mod display name are resolved through the current Bannerlord language dictionary whenever the UI reads them.
- Retains MCM's native `RefreshValues` route only as a secondary optimisation; correct localization no longer depends on a tab-selection event firing.
- Retries installation before the initial module screen and when the required MCM/Harmony assemblies load.
- Writes one-time patch-installation and first-translation evidence to `ExtremeRagdoll.Localization.log` for native verification.
- Adds no mission tick, application tick, campaign scan, timer, persistent polling, physics, force, corpse-finalization, or Dismemberment Plus changes.

Runtime validation inside Bannerlord remains required for MCM rendering behaviour that cannot be executed in GitHub Actions.
