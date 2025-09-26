using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    internal static class ER_Config
    {
        public static float KnockbackMultiplier = 3.0f; // 1 = vanilla
        public static float MaxExtraMagnitude   = 2500f;

        public static float ClampExtra(float magnitude)
        {
            if (magnitude <= 0f) return 0f;
            return magnitude > MaxExtraMagnitude ? MaxExtraMagnitude : magnitude;
        }
    }

    [HarmonyPatch]
    internal static class ER_Amplify_RegisterBlowPatch
    {
        [System.ThreadStatic] private static bool _guard;
        [System.ThreadStatic] private static int _lastPushedId;
        [System.ThreadStatic] private static bool _lastPushedIdValid;

        static MethodBase TargetMethod()
        {
            var byRefACD = typeof(AttackCollisionData).MakeByRefType();
            return AccessTools.Method(typeof(Agent), nameof(Agent.RegisterBlow), new Type[] { typeof(Blow), byRefACD });
        }

        [HarmonyPostfix]
        private static void Postfix(Agent __instance, Blow blow)
        {
            if (_guard) return;

            if (_lastPushedIdValid && _lastPushedId == __instance.Index) return;

            if (__instance.Health > 0f)
            {
                _lastPushedIdValid = false;
                return;
            }

            var flags = blow.BlowFlag;
            bool hadKb = (flags & BlowFlags.KnockBack) != 0 || (flags & BlowFlags.KnockDown) != 0;

            float dmg = blow.InflictedDamage > 0 ? blow.InflictedDamage : 0f;
            float source = hadKb ? blow.BaseMagnitude : MathF.Max(150f, 4f * dmg);
            float extra = ER_Config.ClampExtra((ER_Config.KnockbackMultiplier - 1f) * source);
            if (extra <= 0f) return;

            Vec3 dir = (__instance.Position - blow.GlobalPosition).NormalizedCopy();
            if (dir.X == 0f && dir.Y == 0f && dir.Z == 0f) dir = __instance.LookDirection;
            dir = new Vec3(dir.X, dir.Y, dir.Z + 0.25f).NormalizedCopy();

            var push = new Blow(-1)
            {
                DamageType      = DamageTypes.Blunt,
                BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                BaseMagnitude   = extra,
                SwingDirection  = dir,
                GlobalPosition  = blow.GlobalPosition,
                InflictedDamage = 0
            };

            AttackCollisionData dummy = default;
            try
            {
                _guard = true;
                __instance.RegisterBlow(push, in dummy);
                _lastPushedId = __instance.Index;
                _lastPushedIdValid = true;
            }
            finally
            {
                _guard = false;
            }
        }
    }
}
