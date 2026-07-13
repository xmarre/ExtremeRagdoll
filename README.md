# ExtremeRagdoll

ExtremeRagdoll adds bounded death impulses and radial death-blast propagation while preserving Bannerlord's own death-animation and ragdoll transition.

## Compatibility

Version **1.1.0** is built for:

- Mount & Blade II: Bannerlord **1.3.15**
- The Old Realms: War in the Mountains **1.16**
- Bannerlord.Harmony
- Mod Configuration Menu v5

The Old Realms integration is optional. When `TOR_Core` is loaded, the adapter enables shockwaves on damaging hostile AOE triggered effects at runtime.

## Bannerlord 1.3.x update

Bannerlord 1.3.x removed `GameEntity.GetPhysicsBoundingBoxMin()` and `GetPhysicsBoundingBoxMax()`. ExtremeRagdoll now reads `GlobalBoxMin` and `GlobalBoxMax`, with a reflection-only fallback for older game branches. The fatal-blow pipeline was validated against the 1.3.15 signatures for `Agent.RegisterBlow`, `Agent.Die`, `Agent.MakeDead`, and `Agent.HandleBlowAux`.

Bannerlord 1.3.15 also exposes the dedicated `Agent.ApplyForceOnRagdoll` route used by the safe impulse bridge. The legacy fatal-blow route remains available when that API cannot be bound.

## Installation

1. Delete any existing `Modules/ExtremeRagdoll` folder.
2. Extract the release archive into Bannerlord's `Modules` directory.
3. Enable **Extreme Ragdoll** in the launcher.
4. Load it after Bannerlord.Harmony, UIExtenderEx, ButterLib, MCM, and the TOR modules when TOR is installed.

## Configuration and diagnostics

Settings are available through MCM. Debug logging writes to:

`Documents/Mount and Blade II Bannerlord/Configs/ExtremeRagdoll.log`

For visible results, set the in-game corpse and ragdoll limits high enough that bodies remain active.

## Building

The project targets .NET Framework 4.7.2. Set `BannerlordGameDir`, `BannerlordBinPath`, or the corresponding environment variables before building the Release configuration.
