# AGENTS.md — Repository Agent Guide (ExtremeRagdoll)

> **Scope:** Guidance for a code-editing agent working on this Bannerlord mod repository.  

---

## 1) What this repo is

This is a C# mod for Mount & Blade II: Bannerlord focused on amplified ragdolls/knockback. Core code lives under `ExtremeRagdoll/` and patches engine methods via **Harmony**.

### Key classes (quick map)

- `ER_DeathBlastBehavior` (`ER_DeathBlast.cs`)
  - Applies post-hit impulses and queued corpse launches.
  - Maintains caches for reflection-based physics calls.
  - Tracks recent blasts/kicks/launches and per-tick processing caps.
  - Adds helpers:
    - `ClampVertical(Vec3)`: clamps Z component to `[0, MaxUpFraction]` then renormalizes.
    - `PrepDir(Vec3, planarScale, upBias)`: normalizes input, applies up-bias, clamps via `ClampVertical`.
    - `ToPhysicsImpulse(float)`: converts damage/blow magnitudes to physics impulse with min/max clamps.
  - Queues:
    - **PreLaunch**: attempts before full ragdoll warmup.
    - **Launch**: retries after death until “took” conditions are met.
    - **Kick**: non-lethal nudges via `RegisterBlow` (no damage side-effects).

- `ER_Amplify_RegisterBlowPatch` (`ER_KnockbackAmplifier.cs`)
  - Harmony postfix on `Agent.RegisterBlow(...)`.
  - Computes a robust, clamped direction (`PrepDir`) and derives a **pending launch** for lethal blows.
  - Optional: respects engine blow flags if `RespectEngineBlowFlags` is enabled; otherwise sets knockback flags and direction.

- `Settings` / `ER_Config` (`Settings.cs`, `ER_KnockbackAmplifier.cs`)
  - Strongly-typed accessors that sanitize ranges and expose **tuning**.
  - New/important knobs:
    - Scalar thresholds: `CorpseLaunchVelocityScaleThreshold`, `CorpseLaunchVelocityOffset`, `CorpseLaunchVerticalDelta`, `CorpseLaunchDisplacement`.
    - Magnitude/impulse: `MaxCorpseLaunchMagnitude`, `CorpseImpulseMinimum`, `CorpseImpulseMaximum`.
    - Direction clamp: `CorpseLaunchMaxUpFraction` (used by `ClampVertical`).
    - Retry/cadence: `CorpsePrelaunchTries`, `CorpsePostDeathTries`, `CorpseLaunchRetryDelay`, `CorpseLaunchRetryJitter`, `CorpseLaunchScheduleWindow`.
    - Per‑tick caps: `CorpseLaunchesPerTick`, `KicksPerTick`, `AoEAgentsPerTick`.
    - Misc: `DeathBlastTtl`, `CorpseLaunchXYJitter`, `CorpseLaunchZNudge`, `CorpseLaunchZClampAbove`.
    - Behavior: `RespectEngineBlowFlags`, `DebugLogging`.

---

## 2) How **you** should operate (agent constraints)

- ✅ Perform **static** code analysis and edits only.
- ✅ You may adjust C# sources, comments, and markdown docs.
- ✅ You may suggest unit-testable refactors (even if tests aren’t present).
- ✅ You may add small internal helpers where they reduce duplication (e.g., more uses of `PrepDir`).
- ❌ **Never** attempt to run Bannerlord or require it to be installed.
- ❌ Don’t assume engine DLLs are present at edit time. If a build step needs external references, just keep changes compile‑plausible and explain missing refs in PR notes.

**Preferred change style**
- Keep diffs **minimal and surgical**.
- Preserve existing public API surface and runtime behavior unless the task says otherwise.
- Keep logging guarded by `ER_Config.DebugLogging`.
- Maintain exception safety around reflection and physics calls (no-throw outer catches are intentional).

---

## 3) Hotspots & invariants

- Direction handling must go through `PrepDir` / `ClampVertical` before use.
- All impulse magnitudes go through `ToPhysicsImpulse` then clamped by config min/max.
- **No vertical “rocket” launches**: Z is clamped to `[0, CorpseLaunchMaxUpFraction]`.
- Respect per-tick caps to avoid O(N) spikes: `CorpseLaunchesPerTick`, `KicksPerTick`, `AoEAgentsPerTick`.
- When a corpse launch **takes**, call `MarkLaunched` so we don’t double-impulse the same agent.
- Always null/NaN/Infinity-guard vectors and magnitudes; prefer early returns.
- Reflection paths are intentionally defensive: cache lookups, allow partial failures, avoid throwing.

---

## 4) Common tasks you can safely do

- **Bug fixes**: e.g., missed NaN guard, forgotten `MarkLaunched`, wrong clamp order, forgotten queue decrement.
- **Refactors**: replace ad‑hoc direction math with `PrepDir`; remove duplicate normalization/clamp snippets.
- **Config wiring**: expose new tuning as properties in `Settings` + `ER_Config` with sane bounds.
- **Docs**: update README/CHANGELOG comments explaining new settings and defaults.
- **Performance**: reduce allocations in per-tick loops; widen scratch buffers; limit logging in hot paths.

When in doubt, prefer readability + guardrails over micro-optimizations.

---

## 5) Testing strategy

- Treat this repository as a library:
  - Ensure **compiles-in-theory** changes (no new external deps, no API breaks).
  - Add lightweight guards (null/NaN checks) and unit-testable pure helpers (e.g., small methods for math/clamp) where feasible.
- Provide PR notes that describe:
  - Functional intent and safety checks.
  - Any behavior-affecting config defaults you touched.
  - How you validated logic (reasoned test cases, small examples).

---

## 6) Code style & conventions

- Use `MathF`, early returns, and `try { } catch { }` **only** where we must not throw (reflection/engine calls).
- Prefer `var` for obvious types; explicit types for public members and tuple fields.
- Keep English logs; prefix with context (`corpse launch`, `kick`, `miss`, etc.).
- Preserve `#region`/file organization if present; otherwise group helpers → queues → ticks → patches.

---

## 7) Safe knowledge of settings (quick reference)

```
CorpseLaunchMaxUpFraction    [0..1]    // vertical clamp for directions
CorpseImpulseMinimum         >= 0      // min physics impulse after scaling
CorpseImpulseMaximum         >= 0      // max physics impulse after scaling (0=unbounded)
CorpsePrelaunchTries         0..100
CorpsePostDeathTries         0..100
CorpseLaunchesPerTick        0..2048   // 0 disables cap (not recommended)
KicksPerTick                 0..2048
AoEAgentsPerTick             0..4096
CorpseLaunchRetryDelay       >= 0
CorpseLaunchRetryJitter      >= 0
CorpseLaunchScheduleWindow   >= 0
DeathBlastTtl                >= 0
RespectEngineBlowFlags       bool
DebugLogging                 bool
```

---

## 8) PR/commit hygiene

- **Commit message:** short imperative subject + one bullet list describing *why*, *what*, *risk*.
- **Do not** bump versions or change packaging paths unless explicitly asked.
- If you touch public config defaults, call it out in the PR description.

---

## 9) Things you must not change without explicit instruction

- External public API used by other mods (if any).
- Harmony patch targets/signatures.
- Runtime behaviors that players rely on (e.g., launch cadence) beyond bug fixes or guardrails.
