using System.Runtime.CompilerServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace ExtremeRagdoll
{
    internal static class ER_RagdollPrep
    {
        private const string PreparedTag = "ER_RAG_PREP";

        public static void PrepareIfNeeded(GameEntity ent, Skeleton skel)
        {
            // Avoid repeated heavy prep; but ONLY skip when ragdoll is actually active.
            bool hasTag = false;
            try
            {
                if (ent != null)
                {
                    hasTag = ent.HasTag(PreparedTag);
                }
            }
            catch { /* tag API not always available */ }

            if (hasTag)
            {
                bool ragActive = false;
                try { ragActive = (skel == null) || ER_DeathBlastBehavior.IsRagdollActiveFast(skel); }
                catch { ragActive = (skel == null); }
                if (ragActive)
                    return;
                // Tag exists but ragdoll still not active -> do heavy prep again.
            }

            Prep(ent, skel);

            // Only tag once ragdoll is confirmed active (prevents “tagged but frozen”).
            try
            {
                if (ent != null && !hasTag)
                {
                    bool ragActive2 = false;
                    try { ragActive2 = (skel == null) || ER_DeathBlastBehavior.IsRagdollActiveFast(skel); }
                    catch { ragActive2 = (skel == null); }
                    if (ragActive2)
                        ent.AddTag(PreparedTag);
                }
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Prep(GameEntity ent, Skeleton skel)
        {
            try { ent?.ActivateRagdoll(); } catch { }
            try { skel?.ActivateRagdoll(); } catch { }
            // Ensure the visual is in LOD0 for at least a couple frames so ragdoll updates don't get skipped.
            try { ent?.SetEnforcedMaximumLodLevel(0); } catch { }

            // force-update bone frames, otherwise sometimes ragdoll impulse gets "eaten"
            try { skel?.ForceUpdateBoneFrames(); } catch { }

            // tick a couple times
            try
            {
                MatrixFrame frame = default;
                try { frame = ent?.GetGlobalFrame() ?? default; }
                catch
                {
                    try { frame = ent?.GetFrame() ?? default; }
                    catch { frame = default; }
                }
                // Give physics a couple frames to settle before impulses.
                skel?.TickAnimationsAndForceUpdate(0.033f, frame, true);
                skel?.TickAnimationsAndForceUpdate(0.033f, frame, true);
                skel?.TickAnimationsAndForceUpdate(0.033f, frame, true);
            }
            catch { }
            finally
            {
                // Best-effort: release enforced LOD if supported (-1 typically means “not enforced”).
                try { ent?.SetEnforcedMaximumLodLevel(-1); } catch { }
            }
        }
    }
}
