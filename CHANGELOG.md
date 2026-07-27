# Changelog

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
