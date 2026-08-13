# Changelog

## v1.3.18

- Added forward compatibility for Bannerlord 1.4.8 by removing the hard CLR override dependency on one exact `MissionBehavior.OnRegisterBlow` signature.
- Added a combat-only, reflection-based Harmony bridge that discovers compatible `OnRegisterBlow` callbacks at runtime and dispatches available hit context into the existing ragdoll behavior.
- Keeps existing health/state/removal death tracking active if a future callback shape cannot be bound or dispatched.
- Validates the compiled runtime and every direct TaleWorlds member reference against Bannerlord 1.4.7 reference assemblies and rejects any reintroduced hard `OnRegisterBlow` override.
- Added no ragdoll force, direction, corpse-finalization, campaign, or per-tick behavior changes.

## v1.3.17

- Fixed a Bannerlord 1.4.7 battle-start hard crash introduced by v1.3.16's deferred MCM localization installer.
- Removed localization patch installation from `AppDomain.AssemblyLoad` callbacks in the active submodule path.
- Prevented Harmony's dynamic patch assemblies from re-entering localization installation before completion.
- Retries localization integration only from bounded Bannerlord startup and mission-initialization lifecycle callbacks.
- Keeps complete Simplified Chinese MCM labels when the required MCM UI types are available.

## v1.3.16

- Fixed MCM retaining cached English mod titles, group headings, and setting names after Simplified Chinese became active.
- Replaced the unreliable tab-selection-only refresh with direct UI-binding getter patches for `SettingsPropertyVM.Name`, `SettingsPropertyGroupVM.GroupNameDisplay`, and `SettingsVM.DisplayName`.
- Resolves only Extreme Ragdoll's cached MCM labels through the currently active Bannerlord language dictionary whenever the UI reads them.
- Retained MCM's native `RefreshValues` path as a secondary optimisation, but localisation correctness no longer depends on that event firing.
- Retries patch installation before the initial module screen and when MCM/Harmony assemblies load.
- Added `ExtremeRagdoll.Localization.log` with one-time installation and first-translation diagnostics.
- Added no polling, mission tick, application tick, campaign scan, physics, or corpse-lifecycle work.

## v1.3.15

- Registered Extreme Ragdoll's language manifest in Bannerlord's live localization registry before MCM builds the menu.
- Reloaded the active non-English dictionary after registration so Simplified Chinese labels resolve during the same startup.
- Retained the registered path for later in-game language changes.
- Removed the 30-second Dismemberment Plus corpse-collision window.
- Starts forced paired `EndRagdollAsCorpse` finalization after two seconds for Active or temporarily skeleton-less corpses.
- Retries transient finalization failures only within the absolute three-second collision ceiling; three seconds is the total maximum, not an additional grace period.
- Logs each finalization retry exception in debug mode, including the underlying native invocation exception when available.
- Preserved death force, direction, pulse delivery, hit-bone routing, mount scaling, and a short Dismemberment Plus mesh-rebuild safety bound.

## v1.3.14

- Corrected both localization string files to Bannerlord's required `type="string"` XML format.
- Added the standard XML namespace declaration used by working Bannerlord module localization files.
- Retained the corrected Simplified Chinese language manifest and complete translation key set.
- Added package validation that rejects malformed base or Simplified Chinese string-table roots before release.
- No gameplay or physics behavior changed.

## v1.3.13

- Replaced the v1.3.12 direction guards with an authoritative-source direction pipeline.
- Stopped blending native `KillingBlow.RagdollImpulseAmount` result data into an already captured hit direction.
- Applied victim movement momentum once and prevented its opposing longitudinal component from reversing the killing blow.
- Corrected the Simplified Chinese `language_data.xml` registration path and language metadata so Bannerlord loads the existing translation file.
- Resolved the MCM display name through `TextObject` so the raw `{=ER_DisplayName}` token is no longer shown.

## v1.3.12

- Fixed reversed deathblow launches, including the first kill in a combat mission.
- Rejects oppositely signed `KillingBlow.RagdollImpulseAmount` vectors when an exact captured impact direction is available.
- Enforces a final horizontal source-away invariant after engine-impulse, victim-momentum, and upward-lift blending.
- Preserves force strength, pulse delivery, hit-bone routing, mount-collision scaling, ragdoll handoff, corpse finalization, and Dismemberment Plus safeguards.

## v1.3.11

- Rebuilt and republished the runtime DLLs from the maintained source after the earlier Nexus v1.3.10 package was found to contain stale binaries.
- Synchronized the module version, repository metadata, compiled runtime verification, package naming, and release assets.
- Retained the complete v1.3.10 Dismemberment Plus compatibility safeguards and Simplified Chinese localization.
- Added no further gameplay changes beyond the maintained v1.3.10 source.

## v1.3.10

- Added automatic Dismemberment Plus compatibility safeguards.
- Prevented Extreme Ragdoll from force-finalizing corpses while Dismemberment Plus may still be rebuilding body or armour meshes.
- Disabled only Extreme Ragdoll's nonlethal push and knockdown injection while Dismemberment Plus is loaded; lethal death-launch physics remain enabled.
- Added complete Simplified Chinese localization for the MCM menu, including groups, setting names, and hints.
- Preserved normal v1.3.9 behavior when Dismemberment Plus is not loaded.
