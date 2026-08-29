# Extreme Ragdoll

Extreme Ragdoll is a Mount & Blade II: Bannerlord single-player mod that amplifies directional death physics while preserving Bannerlord's normal corpse and mission lifecycle.

Current repository version: **v1.3.19**  
Targeted Bannerlord range: **v1.3.15–v1.4.8**

## Repository layout

- `Source/` — current v1.3.19 C# source and the minimal deterministic build toolchain.
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

## External lethal-launch integration

Mods that implement special lethal launches can register the hit without taking over Extreme Ragdoll's corpse lifecycle:

```csharp
bool accepted = ExtremeRagdoll.ExtremeRagdollIntegration.TryRegisterLaunchIntent(
    attacker,
    victim,
    blow,
    launchDirection,
    forceMagnitude,
    "YourModId");
```

The API is implemented by `ExtremeRagdoll.ClothSync.dll`.

- `true` means the launch intent was accepted for matching. It does **not** predict that the hit is lethal.
- Extreme Ragdoll matches the intent to the same attacker, victim, blow owner, hit bone, missile state, and nearby hit position inside a short 0.75-second hit-context lifetime.
- If that exact hit is authoritatively confirmed lethal, Extreme Ragdoll owns `StartRagdollAsCorpse`, force delivery, and paired corpse finalization.
- If the hit remains nonlethal, the intent expires without changing live-agent behavior.
- `launchDirection` is treated as the external source's authoritative direction. Extreme Ragdoll does not add its normal upward lift, momentum carryover, or impact spin to that request.
- `forceMagnitude` is expressed in `ApplyForceOnRagdoll` force units and represents one logical launch pulse. The pulse may be split into bounded native force chunks and remains subject to Extreme Ragdoll's configured delivered-force and ragdoll-velocity safety limits.
- `sourceId` must be non-empty and at most 128 characters and may not contain control characters.
- `attacker` and `victim` must be different agents that both belong to `Mission.Current`; self-inflicted launch intents are rejected.
- A newer registration from the same `sourceId` for the same victim replaces that source's older still-pending intent.

For an **optional** integration, isolate the direct reference to `ExtremeRagdoll.ClothSync.dll` in a compatibility assembly that is loaded only when Extreme Ragdoll is present. This keeps Extreme Ragdoll optional for the base mod and avoids reflecting into its private implementation.

## v1.3.19 scope

- Adds the public `ExtremeRagdollIntegration.TryRegisterLaunchIntent` hook for external mods that need custom lethal-hit launch direction and force while leaving corpse ownership to Extreme Ragdoll.
- Matches each accepted intent to the same attacker, victim, and stable blow context inside a bounded 0.75-second lifetime.
- Applies external launch intent only after Extreme Ragdoll's existing authoritative death confirmation.
- Keeps `StartRagdollAsCorpse`, bounded ragdoll-force delivery, velocity/force safety limits, and paired corpse finalization under Extreme Ragdoll ownership.
- Preserves external direction and requested one-pulse force without reapplying Extreme Ragdoll's normal lift, momentum carryover, impact spin, or damage-derived scaling.
- Handles integration registration immediately before or after the normal `OnRegisterBlow` observation path, while rejecting late intent once force delivery or corpse-finalizer sentinel processing has begun.
- Cleans stale integration state on expiry, agent teardown, and mission teardown.
- Retains the v1.3.18 Bannerlord v1.3.15–v1.4.8 compatibility path and its late-bound `OnRegisterBlow` handling.

## Version history note

v1.3.19 adds the external lethal-launch integration API on top of the v1.3.18 Bannerlord compatibility runtime.
