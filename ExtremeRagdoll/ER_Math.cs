using System.Runtime.CompilerServices;
using TaleWorlds.Library;

namespace ExtremeRagdoll
{
    internal static class ER_Math
    {
        internal const float DirectionTinySq = 1e-6f;
        internal const float PositionTinySq  = 1e-10f;
        internal const float ContactTinySq   = 1e-10f;
        internal const float ImpulseTinySq   = 1e-12f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsFinite(in Vec3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }
}
