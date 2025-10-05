# AGENTS.md — Repository Agent Guide (ExtremeRagdoll)

> Scope: guidance for a code‑editing agent working on this Bannerlord mod. Static edits only. Do not run the game.

---

## 1) What this repo is

A C# mod for Mount & Blade II: Bannerlord that amplifies ragdoll/knockback reactions. Core sources sit under `ExtremeRagdoll/` and patch engine methods via **Harmony**.

### Key classes (quick map)

- **`ER_DeathBlastBehavior`** (`ER_DeathBlast.cs`)  
  MissionBehavior that queues corpse launches and non‑lethal kicks, drives warm‑up, and applies AoE pushes:
  - Queues: `PreLaunch` (pre‑death warm), `Launch` (post‑death retries), `Kick` (alive agents).
  - Dedup + caps: per‑agent queue cap, per‑tick caps, schedule dedupe (dir/pos/mag).
  - Death scheduling: takes pending launches from `Agent.MakeDead` / `Agent.Die` and schedules two pulses (Pulse1 + optional Pulse2 via `LaunchPulse2Scale`).
  - AoE: records “death blasts” and applies radial non‑lethal knocks to nearby living agents.
  - Helpers: `PrepDir`, `ClampVertical`, `FinalizeImpulseDir`, jitter/nudge/clamps, `MarkLaunched`, `RecordBlast`.

- **`ER_Amplify_RegisterBlowPatch`** (`ER_KnockbackAmplifier.cs`)  
  Harmony patch on `Agent.RegisterBlow(...)` that computes a safe impulse, sets knockback flags when allowed, and:
  - On **lethal** hits: stores a `PendingLaunch` for the dying agent and fires a small immediate impulse to wake ragdoll safely.
  - On **non‑lethal** hits: enqueues a timed kick in the behavior.

- **`ER_ImpulseRouter`** + **`ER_ImpulsePrefs`** (`ER_ImpulseRouter.cs`)  
  Reflection‑bound delivery to engine impulse APIs. Picks entity‑space by default, falls back to skeleton if allowed; rejects invalid contacts/impulses; throttles noisy routes; guards non‑dynamic bodies. Resets state per mission.

- **`ER_RagdollPrep`** (`ER_RagdollPrep.cs`)  
  Minimal ragdoll activation and bone‑frame priming used during warm‑up and pre‑launch.

- **`ER_TOR_Adapter`** (`ER_TOR_Adapter.cs`)  
  At menu/game/mission start, flips `HasShockWave=true` on TOR `TriggeredEffectTemplate` entries that are damaging AOEs and affect hostiles, enabling shockwave handling without XML edits.

- **`SubModule`** (`SubModule.cs`)  
  Entrypoint. Forces debug logging at main menu when MCM is present, applies Harmony patches, injects `ER_DeathBlastBehavior`, and runs the TOR adapter once per session.

- **`Settings` + `ER_Config`** (`Settings.cs`, `ER_KnockbackAmplifier.cs`)  
  All tuning lives in MCM‑backed settings surfaced through readonly `ER_Config` accessors with range‑sanitizing.

- **`ER_Log`** (`ER_Log.cs`)  
  Lightweight file logger. Writes to Documents\Mount and Blade II Bannerlord\Logs\ExtremeRagdoll\`er_log.txt` with 5MB rotation and 3 backups.

- **`ER_Math`** (`ER_Math.cs`)  
  Tiny numeric guards and constants for zero/NaN/Inf handling.

---

## 2) How to operate (agent constraints)

- Perform **static** code edits only.
- Do not assume engine DLLs are present; keep changes compile‑plausible.
- Preserve public surface and runtime semantics unless a task says otherwise.
- Keep logging behind `ER_Config.DebugLogging` and never throw from logging.
- Prefer small, surgical diffs.

---

## 3) Hotspots & invariants

- Always normalize + clamp directions via `PrepDir` → `ClampVertical` before use. Avoid “rocket” Z: cap to `CorpseLaunchMaxUpFraction`.
- Clamp and sanitize magnitudes through config (`MaxCorpseLaunchMagnitude`, `CorpseImpulse*`, hard caps) before applying.
- Respect per‑tick caps: `CorpseLaunchesPerTick`, `KicksPerTick`, `AoEAgentsPerTick`.
- After a launch “takes”, call `MarkLaunched` and decrement the per‑agent queue.
- Reflection paths must be catch‑all safe; cache delegates and tolerate partial failure.
- Router chooses entity‑space unless disallowed, with skeleton fallback only when safe and configured.
- Scheduling must dedupe by direction/position/magnitude window to avoid spam.

---

## 4) Common edits that are safe

- Bug fixes: guard NaN/Inf, wrong clamp order, missing `MarkLaunched`, missed queue decrement.
- Refactors: replace ad‑hoc impulse math with `PrepDir`/`ClampVertical`/`FinalizeImpulseDir`; share helper methods.
- Config wiring: add MCM setting + `ER_Config` accessor with explicit bounds and defaults.
- Perf: trim per‑tick allocations; throttle logs in hot paths; reuse structs where possible.
- Docs: update this file and README/changes when settings or behavior change.

---

## 5) Runtime touchpoints

- Death scheduling: `Agent.MakeDead` + `Agent.Die` postfixes queue pulses into the behavior (Pulse2 scale optional).
- Immediate wake‑up: small impulse on lethal blows to flip ragdoll dynamic before queued pulses land.
- AoE from deaths: behavior stores recent blasts and applies distance‑scaled non‑lethal pushes each tick.
- TOR adaptation: one‑time reflection pass toggles `HasShockWave` on qualifying templates when TOR is present.

---

## 6) Settings quick reference (selected)

General:
- `KnockbackMultiplier`, `ExtraForceMultiplier`, `DeathBlastRadius`, `DeathBlastForceMultiplier`

Cadence & retries:
- `LaunchDelay1`, `LaunchDelay2`, `LaunchPulse2Scale`
- `CorpsePrelaunchTries`, `CorpsePostDeathTries`
- `CorpseLaunchRetryDelay`, `CorpseLaunchRetryJitter`, `CorpseLaunchScheduleWindow`
- `CorpseLaunchQueueCap`

Safety & clamps:
- `CorpseLaunchMaxUpFraction`
- `CorpseImpulseMinimum`, `CorpseImpulseMaximum`, `CorpseImpulseHardCap`
- `MaxCorpseLaunchMagnitude`, `MaxAoEForce`, `MaxBlowBaseMagnitude`, `MaxNonLethalKnockback`
- `CorpseLaunchVelocityScaleThreshold`, `CorpseLaunchVelocityOffset`, `CorpseLaunchVerticalDelta`, `CorpseLaunchDisplacement`

Contact/position shaping:
- `CorpseLaunchXYJitter`, `CorpseLaunchZNudge`, `CorpseLaunchZClampAbove`, `CorpseLaunchContactHeight`
- `ScheduleDirDuplicateSqThreshold`, `SchedulePosDuplicateSqThreshold`, `ScheduleMagDuplicateFraction`

Tick limits:
- `CorpseLaunchesPerTick`, `KicksPerTick`, `AoEAgentsPerTick`

Routing & engine behavior:
- `ForceEntityImpulse`, `AllowSkeletonFallbackForInvalidEntity`, `AllowEnt3World`
- `RespectEngineBlowFlags`, `MaxAabbExtent`, `ImmediateImpulseScale`
- `MinMissileSpeedForPush`, `BlockedMissilesCanPush`
- `WarmupBlowBaseMagnitude`, `HorseRamKnockDownThreshold`
- `DebugLogging`, `DeathBlastTtl`

---

## 7) Code style

- Early returns. `MathF` for float math. No‑throw outer catches around reflection/engine calls.
- `var` for obvious types. Explicit types for public members and tuple fields.
- English logs, with clear context prefixes.

---

## 8) Do not change without explicit instruction

- Harmony patch targets/signatures and order attributes.
- External/public APIs used by other mods.
- Player‑visible cadence defaults beyond bug‑fixing guardrails.
