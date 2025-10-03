using System.Runtime.CompilerServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace ExtremeRagdoll
{
    internal static class ER_RagdollPrep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Prep(GameEntity ent, Skeleton skel)
        {
            try { ent?.ActivateRagdoll(); } catch { }
            try { skel?.ActivateRagdoll(); } catch { }
            try { skel?.ForceUpdateBoneFrames(); } catch { }
            try
            {
                MatrixFrame frame = default;
                try { frame = ent?.GetGlobalFrame() ?? default; }
                catch
                {
                    try { frame = ent?.GetFrame() ?? default; }
                    catch { frame = default; }
                }
                // Give physics a full frame (twice) to settle before impulses.
                skel?.TickAnimationsAndForceUpdate(0.016f, frame, true);
                skel?.TickAnimationsAndForceUpdate(0.016f, frame, true);
            }
            catch { }
        }
    }
}
