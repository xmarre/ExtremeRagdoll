using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    internal static class ER_Config
    {
        public static float KnockbackMultiplier = 3.0f; // 1 = Vanilla
        public static float MaxExtraMagnitude = 2000f;
    }

    [HarmonyPatch(typeof(Agent))]
    internal static class ER_Amplify_RegisterBlowPatch
    {
        [System.ThreadStatic] private static bool _guard;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Agent.RegisterBlow))]
        private static void Postfix(Agent __instance, Blow blow)
        {
            if (_guard) return;

            var flags = blow.BlowFlag;
            bool isKb = (flags & BlowFlags.KnockBack) != 0 || (flags & BlowFlags.KnockDown) != 0;
            if (!isKb) return;

            float extra = (ER_Config.KnockbackMultiplier - 1f) * blow.BaseMagnitude;
            if (extra <= 0f) return;
            if (extra > ER_Config.MaxExtraMagnitude) extra = ER_Config.MaxExtraMagnitude;

            Vec3 dir = blow.SwingDirection;
            if (dir.X == 0f && dir.Y == 0f && dir.Z == 0f) dir = __instance.LookDirection;

            var push = new Blow(-1)
            {
                DamageType = DamageTypes.Blunt,
                BlowFlag = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                BaseMagnitude = extra,
                SwingDirection = dir,
                GlobalPosition = blow.GlobalPosition,
                InflictedDamage = 0
            };

            AttackCollisionData dummy = default;
            _guard = true;
            __instance.RegisterBlow(push, in dummy);
            _guard = false;
        }
    }
}
