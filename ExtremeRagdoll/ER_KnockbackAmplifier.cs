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
        public static float KnockbackMultiplier => Settings.Instance?.KnockbackMultiplier ?? 6f;
        public static float MaxExtraMagnitude   => Settings.Instance?.MaxExtraMagnitude ?? 2500;
        public static bool  DebugLogging        => Settings.Instance?.DebugLogging ?? true;

        public static float ClampExtra(float magnitude)
        {
            if (magnitude <= 0f) return 0f;
            float max = MaxExtraMagnitude;
            return magnitude > max ? max : magnitude;
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
            var agent = typeof(Agent);
            var want = typeof(AttackCollisionData);
            var byRef = want.MakeByRefType();

            var method = AccessTools.Method(agent, nameof(Agent.RegisterBlow), new[] { typeof(Blow), byRef });
            if (method != null) return method;

            foreach (var candidate in AccessTools.GetDeclaredMethods(agent))
            {
                if (candidate.Name != nameof(Agent.RegisterBlow)) continue;

                var parameters = candidate.GetParameters();
                if (parameters.Length < 2) continue;
                if (parameters[0].ParameterType != typeof(Blow)) continue;

                var second = parameters[1].ParameterType;
                if (!second.IsByRef) continue;
                if (second.GetElementType() != want) continue;

                return candidate;
            }

            return null;
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

            if (ER_Config.DebugLogging)
            {
                Debug.Print($"[ExtremeRagdoll] death shove extra={extra:F1} dir={dir}");
            }
        }
    }
}
