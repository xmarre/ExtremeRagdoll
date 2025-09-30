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
        public static float ExtraForceMultiplier      => MathF.Max(0f, Settings.Instance?.ExtraForceMultiplier ?? 1f);
        public static float DeathBlastRadius          => Settings.Instance?.DeathBlastRadius ?? 3.0f;
        public static float DeathBlastForceMultiplier => MathF.Max(0f, Settings.Instance?.DeathBlastForceMultiplier ?? 1f);
        public static bool  DebugLogging              => Settings.Instance?.DebugLogging ?? true;
        public static float LaunchDelay1              => Settings.Instance?.LaunchDelay1 ?? 0.02f;
        public static float LaunchDelay2              => Settings.Instance?.LaunchDelay2 ?? 0.07f;
        public static float LaunchPulse2Scale
        {
            get
            {
                float scale = Settings.Instance?.LaunchPulse2Scale ?? 0.60f;
                if (scale < 0f) scale = 0f;
                else if (scale > 2f) scale = 2f;
                return scale;
            }
        }
        public static float CorpseLaunchVelocityScaleThreshold => MathF.Max(1f, Settings.Instance?.CorpseLaunchVelocityScaleThreshold ?? 1.02f);
        public static float CorpseLaunchVelocityOffset         => Settings.Instance?.CorpseLaunchVelocityOffset ?? 0.01f;
        public static float CorpseLaunchVerticalDelta           => Settings.Instance?.CorpseLaunchVerticalDelta ?? 0.07f;
        public static float CorpseLaunchDisplacement            => Settings.Instance?.CorpseLaunchDisplacement ?? 0.005f;
        public static float MaxCorpseLaunchMagnitude            => Settings.Instance?.MaxCorpseLaunchMagnitude ?? 200_000_000f;
        public static float MaxAoEForce                         => Settings.Instance?.MaxAoEForce ?? 200_000_000f;
        public static float MaxNonLethalKnockback               => Settings.Instance?.MaxNonLethalKnockback ?? 0f;
        public static float CorpseImpulseMinimum                => MathF.Max(0f, Settings.Instance?.CorpseImpulseMinimum ?? 100_000f);
        public static float CorpseImpulseMaximum                => MathF.Max(0f, Settings.Instance?.CorpseImpulseMaximum ?? 400_000f);
        public static float CorpseLaunchXYJitter                => MathF.Max(0f, Settings.Instance?.CorpseLaunchXYJitter ?? 0.003f);
        public static float CorpseLaunchContactHeight           => MathF.Max(0f, Settings.Instance?.CorpseLaunchContactHeight ?? 0.35f);
        public static float CorpseLaunchRetryDelay              => MathF.Max(0f, Settings.Instance?.CorpseLaunchRetryDelay ?? 0.11f);
        public static float CorpseLaunchRetryJitter             => MathF.Max(0f, Settings.Instance?.CorpseLaunchRetryJitter ?? 0.005f);
        public static float CorpseLaunchZNudge                  => MathF.Max(0f, Settings.Instance?.CorpseLaunchZNudge ?? 0.05f);
        public static float CorpseLaunchZClampAbove             => MathF.Max(0f, Settings.Instance?.CorpseLaunchZClampAbove ?? 0.12f);
        public static int   CorpseLaunchQueueCap                => Math.Max(0, Settings.Instance?.CorpseLaunchQueueCap ?? 3);
    }

    [HarmonyPatch]
    internal static class ER_Amplify_RegisterBlowPatch
    {
        internal struct PendingLaunch
        {
            public Vec3 dir;
            public float mag;
            public Vec3 pos;
            public float time;
        }

        internal static readonly Dictionary<int, PendingLaunch> _pending =
            new Dictionary<int, PendingLaunch>();
        private static readonly Dictionary<int, float> _lastScheduled = new Dictionary<int, float>();
        private const float SCHEDULE_WINDOW = 0.08f; // tolerate engine timing jitter

        internal static void ClearPending()
        {
            _pending.Clear();
            _lastScheduled.Clear();
        }

        internal static bool TryTakePending(int agentId, out PendingLaunch pending)
        {
            if (_pending.TryGetValue(agentId, out pending))
            {
                _pending.Remove(agentId);
                return true;
            }
            pending = default;
            return false;
        }

        internal static void ForgetScheduled(int agentId)
        {
            _lastScheduled.Remove(agentId);
        }

        internal static bool TryMarkScheduled(int agentId, float now, float windowSec = SCHEDULE_WINDOW)
        {
            if (_lastScheduled.TryGetValue(agentId, out var last) && now - last < windowSec)
                return false;
            _lastScheduled[agentId] = now;
            return true;
        }

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
            flat = new Vec3(flat.x, flat.y, 0f);
            if (flat.LengthSquared < 1e-4f)
            {
                var look = __instance.LookDirection;
                flat = new Vec3(look.x, look.y, 0f);
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
            float clampMax = lethal ? ER_Config.MaxCorpseLaunchMagnitude : ER_Config.MaxNonLethalKnockback;
            if (clampMax > 0f && blow.BaseMagnitude > clampMax)
            {
                blow.BaseMagnitude = clampMax;
            }
            if (lethal)
            {
                const float lethalPreDeathScale = 0.15f;
                blow.BaseMagnitude *= lethalPreDeathScale;
            }
            blow.SwingDirection = dir;
            blow.BlowFlag |= BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound;

            if (lethal)
            {
                if (ER_Config.MaxCorpseLaunchMagnitude > 0f)
                {
                    float extraMult = ER_Config.ExtraForceMultiplier <= 0f ? 1f : ER_Config.ExtraForceMultiplier;
                    float mag = blow.BaseMagnitude * extraMult;
                    float maxMag = ER_Config.MaxCorpseLaunchMagnitude;
                    if (mag > maxMag)
                    {
                        mag = maxMag;
                    }
                    if (mag <= 0f || float.IsNaN(mag) || float.IsInfinity(mag))
                    {
                        _pending.Remove(__instance.Index);
                        return;
                    }
                    Vec3 contact = blow.GlobalPosition;
                    float recorded = __instance.Mission?.CurrentTime ?? 0f;
                    _pending[__instance.Index] = new PendingLaunch { dir = dir, mag = mag, pos = contact, time = recorded };
                    if (ER_Config.DebugLogging)
                    {
                        ER_Log.Info($"lethal pre-boost: hp={hp} dmg={blow.InflictedDamage} baseMag->{blow.BaseMagnitude} mag={mag}");
                    }
                }
                else
                {
                    _pending.Remove(__instance.Index);
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

}
