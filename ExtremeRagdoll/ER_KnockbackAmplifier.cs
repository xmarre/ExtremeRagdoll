using System;
using System.Collections.Generic;
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
        internal static readonly Dictionary<int, (Vec3 dir, float mag, Vec3 pos)> _pending =
            new Dictionary<int, (Vec3 dir, float mag, Vec3 pos)>();

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

        [HarmonyPrefix, HarmonyPriority(HarmonyLib.Priority.First)]
        private static void Prefix(Agent __instance, [HarmonyArgument(0)] ref Blow blow)
        {
            if (__instance == null) return;

            float hp = __instance.Health;
            if (hp <= 0f)
            {
                _pending.Remove(__instance.Index);
                return;
            }
            if (blow.InflictedDamage <= 0f)
            {
                _pending.Remove(__instance.Index);
                return;
            }
            // optional: comment out for a quick A/B to confirm engine accepts huge impulses
            // if (hp > 0f && blow.InflictedDamage < hp * 0.7f) return;

            bool lethal = hp - blow.InflictedDamage <= 0f;

            Vec3 flat = __instance.Position - blow.GlobalPosition;
            flat = new Vec3(flat.X, flat.Y, 0f);
            if (flat.LengthSquared < 1e-4f)
            {
                var look = __instance.LookDirection;
                flat = new Vec3(look.X, look.Y, 0f);
            }
            if (flat.LengthSquared < 1e-6f)
            {
                flat = new Vec3(0f, 1f, 0f);
            }

            Vec3 dir = (flat.NormalizedCopy() * 0.35f + new Vec3(0f, 0f, 1.05f)).NormalizedCopy();

            float scale = MathF.Max(1f, ER_Config.KnockbackMultiplier);
            float target = (10000f + blow.InflictedDamage * 120f) * scale;
            if (blow.BaseMagnitude < target)
            {
                blow.BaseMagnitude = target;
            }
            blow.SwingDirection = dir;
            blow.BlowFlag |= BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound;

            if (lethal)
            {
                float extraMult = ER_Config.ExtraForceMultiplier <= 0f ? 1f : ER_Config.ExtraForceMultiplier;
                float mag = blow.BaseMagnitude * extraMult;
                Vec3 contact = blow.GlobalPosition;
                _pending[__instance.Index] = (dir, mag, contact);
                if (ER_Config.DebugLogging)
                {
                    ER_Log.Info($"lethal pre-boost: hp={hp} dmg={blow.InflictedDamage} baseMag->{blow.BaseMagnitude} mag={mag}");
                }
            }
            else
            {
                _pending.Remove(__instance.Index);
                if (ER_Config.DebugLogging)
                {
                    ER_Log.Info($"non-lethal boost: hp={hp} dmg={blow.InflictedDamage} baseMag->{blow.BaseMagnitude}");
                }
            }
        }
    }

    [HarmonyPatch(typeof(Agent), nameof(Agent.MakeDead))]
    internal static class ER_Probe_MakeDead
    {
        [HarmonyPostfix, HarmonyPriority(HarmonyLib.Priority.Last)]
        static void Post(Agent __instance)
        {
            if (__instance == null) return;
            if (!ER_Amplify_RegisterBlowPatch._pending.TryGetValue(__instance.Index, out var pending)) return;

            ER_Amplify_RegisterBlowPatch._pending.Remove(__instance.Index);

            var launch = new Blow(-1)
            {
                DamageType      = DamageTypes.Blunt,
                BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                BaseMagnitude   = pending.mag,
                SwingDirection  = pending.dir,
                GlobalPosition  = pending.pos,
                InflictedDamage = 0
            };
            AttackCollisionData acd = default;
            __instance.RegisterBlow(launch, in acd);

            ER_DeathBlastBehavior.Instance?.EnqueueKick(__instance, pending.dir, pending.mag, 1.0f);
            ER_DeathBlastBehavior.Instance?.RecordBlast(__instance.Position, ER_Config.DeathBlastRadius, pending.mag);

            if (ER_Config.DebugLogging)
            {
                ER_Log.Info($"ragdoll shove queued for Agent#{__instance.Index} mag={pending.mag}");
            }
        }
    }
}
