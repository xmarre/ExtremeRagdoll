from pathlib import Path

path = Path("GuidedArrow/Source/GuidedArrowBehavior.cs")
text = path.read_text(encoding="utf-8")


def replace_once(old: str, new: str, label: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one match, found {count}")
    text = text.replace(old, new, 1)

replace_once(
    "        private float _settledElapsed;\n",
    "        private float _settledElapsed;\n        private bool _cinematicSawActiveRagdoll;\n",
    "ragdoll observation field",
)
replace_once(
    "            _settledElapsed = 0f;\n            if (!IsFinite(_impactDirection)",
    "            _settledElapsed = 0f;\n            _cinematicSawActiveRagdoll = false;\n            if (!IsFinite(_impactDirection)",
    "cinematic ragdoll reset",
)
replace_once(
    "            try { center = victim.Position + WorldUp * 0.95f; }",
    "            try { center = GetVisualPosition(victim) + WorldUp * 0.95f; }",
    "cinematic visual center",
)
replace_once(
    "            try { position = victim.Position; }",
    "            try { position = GetVisualPosition(victim); }",
    "settled visual position",
)
replace_once(
    '''                RagdollState state = skeleton.GetCurrentRagdollState();
                // Extreme Ragdoll v1.3.6 uses the same boundary before EndRagdollAsCorpse().
                // We only observe it; we never finalize or mutate corpse physics ourselves.
                if (state == RagdollState.NeedsDeactivation)
                    BeginReturn("NativeCorpseFinalizationBoundary");
''',
    '''                RagdollState state = skeleton.GetCurrentRagdollState();
                if (state == RagdollState.ActiveFirstTick || state == RagdollState.Active)
                {
                    _cinematicSawActiveRagdoll = true;
                    return;
                }

                // Extreme Ragdoll v1.3.6 uses NeedsDeactivation immediately before
                // EndRagdollAsCorpse(). Observe the same boundary without mutating the corpse.
                if (state == RagdollState.NeedsDeactivation)
                {
                    BeginReturn("NativeCorpseFinalizationBoundary");
                    return;
                }

                // Extreme Ragdoll and Bannerlord may finalize between our bounded 10 Hz samples.
                // Disabled after an observed active ragdoll is the completed side of that crossing.
                if (state == RagdollState.Disabled &&
                    (_cinematicSawActiveRagdoll || _cinematicElapsed >= 0.75f))
                {
                    BeginReturn("NativeCorpseFinalizedBetweenSamples");
                }
''',
    "full finalization crossing",
)
replace_once(
    "            _settledElapsed = 0f;\n\n            if (behaviorRemoving)",
    "            _settledElapsed = 0f;\n            _cinematicSawActiveRagdoll = false;\n\n            if (behaviorRemoving)",
    "global ragdoll reset",
)
replace_once(
    "        private static bool IsArrowOrBolt(Mission.Missile missile)\n",
    '''        private static Vec3 GetVisualPosition(Agent agent)
        {
            if (agent == null)
                return Vec3.Zero;

            try
            {
                Vec3 position = agent.GetVisualPosition();
                if (IsFinite(position))
                    return position;
            }
            catch { }

            try { return agent.Position; }
            catch { return Vec3.Zero; }
        }

        private static bool IsArrowOrBolt(Mission.Missile missile)
''',
    "visual position helper",
)

path.write_text(text, encoding="utf-8", newline="\n")
