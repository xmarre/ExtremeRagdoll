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
        public static float KnockbackMultiplier       => Settings.Instance?.KnockbackMultiplier ?? 6f;
        public static float ExtraForceMultiplier      => Settings.Instance?.ExtraForceMultiplier ?? 1f;
        public static float DeathBlastRadius          => Settings.Instance?.DeathBlastRadius ?? 3.0f;
        public static float DeathBlastForceMultiplier => Settings.Instance?.DeathBlastForceMultiplier ?? 1f;
        public static bool  DebugLogging              => Settings.Instance?.DebugLogging ?? true;
    }

    [HarmonyPatch]
    internal static class ER_Amplify_RegisterBlowPatch
    {
        [HarmonyPrepare]
        static bool Prepare()
        {
            var method = TargetMethod();
            if (method == null)
            {
                foreach (var cand in AccessTools.GetDeclaredMethods(typeof(Agent)))
                {
                    if (cand.Name != nameof(Agent.RegisterBlow)) continue;
                    var sig = string.Join(", ", Array.ConvertAll(cand.GetParameters(), p => p.ParameterType.FullName));
                    ER_Log.Info("RegisterBlow overload: (" + sig + ")");
                }
                ER_Log.Error("Harmony target not found: Agent.RegisterBlow with byref AttackCollisionData");
                return false;
            }

            ER_Log.Info("Patching: " + method);
            return true;
        }

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
        private static void Postfix(Agent __instance, [HarmonyArgument(0)] Blow blow)
        {
            if (_guard) return;
            if (__instance == null) return;

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
            float extra = MathF.Max(0f, (ER_Config.KnockbackMultiplier - 1f) * source);
            if (extra <= 0f) return;
            ER_Log.Info($"death shove: hadKb={hadKb} dmg={dmg} baseMag={blow.BaseMagnitude} extra={extra}");

            Vec3 flat = __instance.Position - blow.GlobalPosition;
            flat = new Vec3(flat.X, flat.Y, 0f);
            if (flat.LengthSquared < 1e-4f)
            {
                var look = __instance.LookDirection;
                flat = new Vec3(look.X, look.Y, 0f);
            }
            Vec3 dir = (flat.NormalizedCopy() * 0.70f + new Vec3(0f, 0f, 0.72f)).NormalizedCopy();

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
                __instance.ApplyExternalForceToBones(dir * (extra * ER_Config.ExtraForceMultiplier),
                    MBActionSet.BoneUsage.Movement, 0.45f);
                ER_DeathBlastBehavior.Instance?.EnqueueKick(__instance, dir, extra * ER_Config.ExtraForceMultiplier, 0.90f);
                _lastPushedId = __instance.Index;
                _lastPushedIdValid = true;
            }
            finally
            {
                _guard = false;
            }

            ER_Log.Info($"death shove applied to Agent#{__instance.Index} dir={dir}");

            // fire local AOE blast (independent of TOR); tick sweep applies scaling
            try
            {
                ER_DeathBlastBehavior.Instance?.RecordBlast(
                    __instance.Position,
                    ER_Config.DeathBlastRadius,
                    extra);
            }
            catch
            {
                // ignore
            }
        }
    }

    [HarmonyPatch(typeof(Agent), nameof(Agent.MakeDead))]
    internal static class ER_Probe_MakeDead
    {
        [HarmonyPostfix]
        static void Post(Agent __instance)
        {
            ER_Log.Info($"MakeDead: Agent#{__instance?.Index} dead");
        }
    }
}
