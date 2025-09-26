# ExtremeRagdoll

ExtremeRagdoll injects an additional knockback blow when an agent dies so the corpse immediately hands off to the TaleWorlds ragdoll solver. A lightweight mission behaviour also forwards radial "death blast" data so nearby casualties inherit the same shove.

## Making the effect obvious

1. In **Options → Performance**, set **Number of Ragdolls** to **Unlimited**. In **Options → Gameplay**, set **Number of Corpses** to **Unlimited** so bodies stay around long enough to receive the extra push.
2. The default knockback multiplier is temporarily cranked to 6× vanilla inside `ER_Config`. This exaggerates the impulse so you can confirm the mod is active; drop it back toward 3× once you are satisfied.
3. Every time the postfix injects a shove it now prints a line like `[ExtremeRagdoll] death shove extra=...` into `rgl_log.txt`. Comment out `ER_Config.DebugLogging` once you no longer need the spam.

## Load order

Make sure Harmony (and Tale of Realms / TOR_Core if you use their shockwaves) load before ExtremeRagdoll. The mod attempts to auto-adapt TOR shockwaves on startup and falls back silently if the dependency is missing.

## Development

* `ER_KnockbackAmplifier` houses the Harmony postfix that replays the fatal blow with extra magnitude.
* `ER_DeathBlastBehavior` stores recent radial impulses so chained explosions can keep tossing corpses.
* `ER_TOR_Adapter` plumbs in TOR shockwave data when available.

Debug logging is controlled with `ER_Config.DebugLogging`; leave it enabled while testing and disable it for release builds.
