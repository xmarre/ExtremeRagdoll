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
        public static float KnockbackMultiplier       => Settings.Instance?.KnockbackMultiplier ?? 1.8f;
        public static float ExtraForceMultiplier      => MathF.Max(0f, Settings.Instance?.ExtraForceMultiplier ?? 1f);
        public static float DeathBlastRadius          => Settings.Instance?.DeathBlastRadius ?? 3.0f;
        public static float DeathBlastForceMultiplier => MathF.Max(0f, Settings.Instance?.DeathBlastForceMultiplier ?? 1f);
        public static bool  DebugLogging              => Settings.Instance?.DebugLogging ?? true;
        public static bool  RespectEngineBlowFlags    => Settings.Instance?.RespectEngineBlowFlags ?? false;
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
        public static float CorpseLaunchVerticalDelta           => Settings.Instance?.CorpseLaunchVerticalDelta ?? 0.05f;
        public static float CorpseLaunchDisplacement            => Settings.Instance?.CorpseLaunchDisplacement ?? 0.03f;
        public static float MaxCorpseLaunchMagnitude            => Settings.Instance?.MaxCorpseLaunchMagnitude ?? 200_000_000f;
        public static float MaxAoEForce                         => Settings.Instance?.MaxAoEForce ?? 200_000_000f;
        public static float MaxNonLethalKnockback               => Settings.Instance?.MaxNonLethalKnockback ?? 0f;
        public static float CorpseImpulseMinimum                => MathF.Max(0f, Settings.Instance?.CorpseImpulseMinimum ?? 0.5f);
        public static float CorpseImpulseMaximum                => MathF.Max(0f, Settings.Instance?.CorpseImpulseMaximum ?? 1_500f);
        public static float CorpseLaunchXYJitter                => MathF.Max(0f, Settings.Instance?.CorpseLaunchXYJitter ?? 0.003f);
        public static float CorpseLaunchContactHeight           => MathF.Max(0f, Settings.Instance?.CorpseLaunchContactHeight ?? 0.35f);
        public static float CorpseLaunchRetryDelay              => MathF.Max(0f, Settings.Instance?.CorpseLaunchRetryDelay ?? 0.02f);
        public static float CorpseLaunchRetryJitter             => MathF.Max(0f, Settings.Instance?.CorpseLaunchRetryJitter ?? 0.005f);
        public static float CorpseLaunchScheduleWindow          => MathF.Max(0f, Settings.Instance?.CorpseLaunchScheduleWindow ?? 0.08f);
        public static float CorpseLaunchZNudge                  => MathF.Max(0f, Settings.Instance?.CorpseLaunchZNudge ?? 0.05f);
        public static float CorpseLaunchZClampAbove             => MathF.Max(0f, Settings.Instance?.CorpseLaunchZClampAbove ?? 0.18f);
        public static float DeathBlastTtl                       => MathF.Max(0f, Settings.Instance?.DeathBlastTtl ?? 0.75f);
        public static float CorpseLaunchMaxUpFraction
        {
            get
            {
                float frac = Settings.Instance?.CorpseLaunchMaxUpFraction ?? 0.22f;
                if (frac < 0f) return 0f;
                if (frac > 1f) return 1f;
                return frac;
            }
        }
        public static int   CorpseLaunchQueueCap                => Math.Max(0, Settings.Instance?.CorpseLaunchQueueCap ?? 3);
        public static int   CorpsePrelaunchTries
        {
            get
            {
                int value = Settings.Instance?.CorpsePrelaunchTries ?? 12;
                if (value < 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }
        public static int   CorpsePostDeathTries
        {
            get
            {
                int value = Settings.Instance?.CorpsePostDeathTries ?? 12;
                if (value < 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }
        public static int   CorpseLaunchesPerTickCap
        {
            get
            {
                int value = Settings.Instance?.CorpseLaunchesPerTick ?? 128;
                if (value < 0) return 0;
                if (value > 2048) return 2048;
                return value;
            }
        }
        public static int   KicksPerTickCap
        {
            get
            {
                int value = Settings.Instance?.KicksPerTick ?? 128;
                if (value < 0) return 0;
                if (value > 2048) return 2048;
                return value;
            }
        }
        public static int   AoEAgentsPerTickCap
        {
            get
            {
                int value = Settings.Instance?.AoEAgentsPerTick ?? 256;
                if (value < 0) return 0;
                if (value > 4096) return 4096;
                return value;
            }
        }
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
            _pending.Remove(agentId);
            _lastScheduled.Remove(agentId);
        }

        internal static bool TryMarkScheduled(int agentId, float now, float windowSec = -1f)
        {
            if (windowSec < 0f)
                windowSec = ER_Config.CorpseLaunchScheduleWindow;
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

        [HarmonyPrefix]
        [HarmonyAfter(new[] { "TOR", "TOR_Core" })]
        [HarmonyPriority(HarmonyLib.Priority.First)]
        private static void Prefix(
            Agent __instance,
            [HarmonyArgument(0)] ref Blow blow,
            [HarmonyArgument(1)] ref AttackCollisionData attackCollisionData)
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

            Vec3 dir = __instance.Position - blow.GlobalPosition;
            if (dir.LengthSquared < 1e-6f)
            {
                try { dir = __instance.LookDirection; }
                catch { dir = new Vec3(0f, 1f, 0.25f); }
            }
            dir = ER_DeathBlastBehavior.PrepDir(dir, 0.35f, 1.05f);

            bool respectBlow = ER_Config.RespectEngineBlowFlags;
            if (!respectBlow)
            {
                if (lethal)
                {
                    blow.SwingDirection = dir;
                    blow.BlowFlag |= BlowFlags.KnockBack | BlowFlags.KnockDown;
                }
                else
                {
                    Vec3 existing = blow.SwingDirection;
                    if (existing.LengthSquared < 1e-6f)
                    {
                        blow.SwingDirection = dir;
                    }
                    else
                    {
                        var clamped = ER_DeathBlastBehavior.PrepDir(existing, 1f, 0f);
                        blow.SwingDirection = clamped;
                    }
                }
            }

            if (lethal)
            {
                if (!respectBlow)
                {
                    // Let engine do the shove on lethal hits so missiles/spells always launch.
                    float missileSpeed = 0f;
                    try { missileSpeed = attackCollisionData.MissileSpeed; }
                    catch { /* not a missile */ }

                    float mult = MathF.Max(1f, ER_Config.ExtraForceMultiplier);
                    float target = 30000f + blow.InflictedDamage * 400f + missileSpeed * 200f;
                    float desired = target * mult;
                    if (desired > 0f && !float.IsNaN(desired) && !float.IsInfinity(desired) && blow.BaseMagnitude < desired)
                    {
                        blow.BaseMagnitude = desired;
                    }
                }

                if (ER_Config.MaxCorpseLaunchMagnitude > 0f)
                {
                    float extraMult = MathF.Max(1f, ER_Config.ExtraForceMultiplier);
                    float mag = (10000f + blow.InflictedDamage * 120f) * extraMult * 0.25f; // keep small; physics scaling happens later
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
                    if (float.IsNaN(contact.x) || float.IsNaN(contact.y) || float.IsNaN(contact.z) ||
                        float.IsInfinity(contact.x) || float.IsInfinity(contact.y) || float.IsInfinity(contact.z))
                    {
                        contact = __instance.Position;
                    }
                    float recorded = __instance.Mission?.CurrentTime ?? 0f;
                    _pending[__instance.Index] = new PendingLaunch
                    {
                        dir  = dir,
                        mag  = mag,
                        pos  = contact,
                        time = recorded,
                    };
                    var mission = __instance.Mission ?? Mission.Current;
                    mission?
                        .GetMissionBehavior<ER_DeathBlastBehavior>()
                        ?.QueuePreDeath(__instance, dir, mag, contact);
                }
                else
                {
                    _pending.Remove(__instance.Index);
                }
            }
            else
            {
                // Non-lethal: nudge via our behavior after the engine finishes (no damage side-effects).
                var beh = (__instance.Mission ?? Mission.Current)?.GetMissionBehavior<ER_DeathBlastBehavior>();
                float kickMag = (10000f + blow.InflictedDamage * 120f) * MathF.Max(1f, ER_Config.KnockbackMultiplier);
                beh?.EnqueueKick(__instance, dir, kickMag, 0.10f);
                _pending.Remove(__instance.Index);
            }
        }
    }

}
