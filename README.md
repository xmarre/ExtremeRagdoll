# Extreme Ragdoll

Extreme Ragdoll is a Mount & Blade II: Bannerlord single-player mod that amplifies directional death physics while preserving Bannerlord's normal corpse and mission lifecycle.

Current repository version: **v1.3.17**  
Targeted Bannerlord range: **v1.3.15–v1.4.8**

## Repository layout

- `Source/` — current v1.3.17 C# source and the minimal deterministic build toolchain.
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

The canonical build compiles against minimal Bannerlord 1.3.15-compatible reference stubs, keeps `MissionBehavior.OnRegisterBlow` late-bound instead of encoding an exact CLR override, validates both assemblies, and resolves all direct TaleWorlds runtime references against the currently published Bannerlord 1.4.7 reference assemblies.

Requirements:

- .NET Core SDK 3.1
- NuGet access or cached `Mono.Cecil 0.10.1` and `Bannerlord.ReferenceAssemblies 1.4.7.117484` packages
- PowerShell on Windows, or Bash on Linux/macOS

Windows:

```powershell
.\Source\Build\build.ps1 -OutDir .\Source\Build\out
```

Linux/macOS:

```bash
bash Source/Build/build.sh Source/Build/out
```

Build output is written to `Source/Build/out/bin`. Reference stubs and metadata-only game assemblies are build inputs only and must never be copied into Bannerlord.

## Runtime/source parity

Pull-request CI rebuilds the current source and updates the checked-in runtime DLLs when their bytes differ. The default branch then rebuilds and fails if the committed binaries or `RUNTIME_SHA256.txt` do not match the source build.

## v1.3.17 scope

- Fixes the Bannerlord 1.4.7 battle-start hard crash introduced by v1.3.16's deferred MCM localization installer.
- Never enters Harmony patch installation from an `AppDomain.AssemblyLoad` callback in the active runtime path.
- Performs at most three bounded localization installation attempts from normal Bannerlord lifecycle callbacks: submodule load, initial-screen setup, and mission-behavior initialization.
- Installs the existing complete Chinese MCM getter patches only after Harmony and every required MCM UI type are already loaded.
- Treats localization compatibility as optional so it cannot prevent a battle from loading.
- Removes the hard CLR override dependency on one exact `MissionBehavior.OnRegisterBlow` signature and discovers compatible callback overloads at runtime from the combat-mission initialization path.
- Dispatches whatever compatible attacker/victim/blow/collision/weapon arguments the running Bannerlord exposes into the existing hit-context logic; unexpected callback shapes fail closed to the existing health/state/removal death fallbacks.
- Keeps the register-blow Harmony bridge reflection-only, with no hard Harmony assembly reference and no non-combat/tableau installation path.
- Validates the compiled runtime's direct TaleWorlds references against Bannerlord 1.4.7 metadata and explicitly rejects reintroducing a hard `OnRegisterBlow` override.
- Adds no mission tick, application tick, campaign scan, timer, persistent polling, physics, force, corpse-finalization, or Dismemberment Plus changes.

Bannerlord 1.4.8 native battle startup still requires in-game confirmation because no 1.4.8 crash log/runtime was supplied and the public reference-assembly validation package is currently available only through 1.4.7. The compatibility path is deliberately late-bound so an `OnRegisterBlow` signature/modifier change does not make `SafeRagdollBehavior` unloadable before managed fallback logic can run.
