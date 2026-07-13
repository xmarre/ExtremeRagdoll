using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using MathF = TaleWorlds.Library.MathF;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    internal static class ER_SafeDeathPipeline
    {
        private const string LegacyPatchId = "extremeragdoll.patch";
        private const float ForceUnitsPerLegacyImpulseUnit = 2000f;
        private const float MinimumUnitsPerLegacyImpulseUnit = 500f;
        private const float AbsoluteForceCap = 16000f;
        private const float LegacyMeleeMagnitudeCap = 2200f;
        private const float LegacyMissileMagnitudeCap = 1400f;

        internal static void DisableLegacyDeathOverrides(Harmony harmony)
        {
            if (harmony == null)
                return;

            int removed = 0;
            removed += UnpatchAgentMethods(harmony, typeof(ER_Amplify_RegisterBlowPatch), "Prefix", nameof(Agent.RegisterBlow));
            removed += UnpatchAgentMethods(harmony, typeof(ER_Amplify_RegisterBlowPatch), "Postfix", nameof(Agent.RegisterBlow));
            removed += UnpatchAgentMethods(harmony, typeof(ER_Probe_MakeDead), "Pre", nameof(Agent.MakeDead));
            removed += UnpatchAgentMethods(harmony, typeof(ER_Probe_MakeDead), "Post", nameof(Agent.MakeDead));
            removed += UnpatchAgentMethods(harmony, typeof(ER_Probe_Die), "Pre", "Die");
            removed += UnpatchAgentMethods(harmony, typeof(ER_Probe_Die), "Post", "Die");

            ER_Log.Info($"Safe death pipeline enabled: removed {removed} legacy death override patch(es) from {LegacyPatchId}");
        }

        private static int UnpatchAgentMethods(Harmony harmony, Type patchType, string patchMethodName, string originalMethodName)
        {
            MethodInfo patch = AccessTools.Method(patchType, patchMethodName);
            if (patch == null)
            {
                ER_Log.Error($"Safe death pipeline: patch method not found: {patchType?.FullName}.{patchMethodName}");
                return 0;
            }

            int removed = 0;
            foreach (MethodInfo original in AccessTools.GetDeclaredMethods(typeof(Agent)))
            {
                if (original == null || original.Name != originalMethodName)
                    continue;

                try
                {
                    harmony.Unpatch(original, patch);
                    removed++;
                }
                catch (Exception ex)
                {
                    ER_Log.Error($"Safe death pipeline: failed to unpatch {patchType.Name}.{patchMethodName} from {original}", ex);
                }
            }
            return removed;
        }

        internal static float ComputeRagdollForceMagnitude(in Blow blow)
        {
            float baseMagnitude = blow.BaseMagnitude;
            if (float.IsNaN(baseMagnitude) || float.IsInfinity(baseMagnitude) || baseMagnitude < 0f)
                baseMagnitude = 0f;

            float damage = MathF.Max(0f, (float)blow.InflictedDamage);
            float sourceMagnitude = MathF.Max(MathF.Min(baseMagnitude, 1200f), 250f + damage * 8f);
            float multiplier = MathF.Max(0f, ER_Config.ExtraForceMultiplier) * MathF.Max(1f, ER_Config.KnockbackMultiplier);
            float force = sourceMagnitude * multiplier;

            float minimum = ER_Config.CorpseImpulseMinimum * MinimumUnitsPerLegacyImpulseUnit;
            float maximum = ER_Config.CorpseImpulseMaximum * MinimumUnitsPerLegacyImpulseUnit;
            if (maximum > 0f && minimum > maximum)
                minimum = maximum;
            if (minimum > 0f && force < minimum)
                force = minimum;
            if (maximum > 0f && force > maximum)
                force = maximum;

            float configuredHardCap = ER_Config.CorpseImpulseHardCap;
            if (!float.IsNaN(configuredHardCap) && !float.IsInfinity(configuredHardCap) && configuredHardCap > 0f)
                force = MathF.Min(force, configuredHardCap * ForceUnitsPerLegacyImpulseUnit);
            force = MathF.Min(force, AbsoluteForceCap);

            return float.IsNaN(force) || float.IsInfinity(force) || force <= 0f ? 0f : force;
        }

        internal static Vec3 ResolveDirection(Agent agent, in Blow blow)
        {
            Vec3 direction = blow.SwingDirection;
            if (!ER_Math.IsFinite(in direction) || direction.LengthSquared < ER_Math.DirectionTinySq)
                direction = blow.Direction;
            if (!ER_Math.IsFinite(in direction) || direction.LengthSquared < ER_Math.DirectionTinySq)
            {
                try { direction = agent.Position - blow.GlobalPosition; }
                catch { direction = Vec3.Zero; }
            }
            if (!ER_Math.IsFinite(in direction) || direction.LengthSquared < ER_Math.DirectionTinySq)
            {
                try { direction = agent.LookDirection; }
                catch { direction = new Vec3(0f, 1f, 0f); }
            }

            direction = ER_DeathBlastBehavior.PrepDir(direction, 0.98f, 0.02f);
            direction = ER_DeathBlastBehavior.FinalizeImpulseDir(direction, ER_Config.CorpseLaunchMaxUpFraction);
            return ER_Math.IsFinite(in direction) && direction.LengthSquared >= ER_Math.DirectionTinySq
                ? direction
                : new Vec3(0f, 1f, 0f);
        }

        internal static void ApplyLegacyFatalBlowFallback(Agent agent, ref Blow blow)
        {
            float originalMagnitude = blow.BaseMagnitude;
            if (float.IsNaN(originalMagnitude) || float.IsInfinity(originalMagnitude) || originalMagnitude < 0f)
                originalMagnitude = 0f;

            float damage = MathF.Max(0f, (float)blow.InflictedDamage);
            float multiplier = MathF.Max(1f, ER_Config.ExtraForceMultiplier) *
                               MathF.Max(1f, ER_Config.KnockbackMultiplier);
            multiplier = MathF.Min(multiplier, 3f);

            bool missile = false;
            try
            {
                string flags = blow.BlowFlag.ToString();
                missile = !string.IsNullOrEmpty(flags) &&
                          flags.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }

            float cap = missile ? LegacyMissileMagnitudeCap : LegacyMeleeMagnitudeCap;
            float configuredHardCap = ER_Config.CorpseImpulseHardCap;
            if (!float.IsNaN(configuredHardCap) && !float.IsInfinity(configuredHardCap) && configuredHardCap > 0f)
                cap = MathF.Min(cap, configuredHardCap * 350f);

            float floor = missile ? 900f : 1200f;
            if (floor > cap)
                floor = cap;

            float source = MathF.Max(originalMagnitude, 500f + damage * 10f);
            float desired = MathF.Min(source * multiplier, cap);
            if (desired < floor)
                desired = floor;

            if (desired > originalMagnitude)
                blow.BaseMagnitude = desired;

            Vec3 swing = blow.SwingDirection;
            if (!ER_Math.IsFinite(in swing) || swing.LengthSquared < ER_Math.DirectionTinySq || swing.z < 0f)
                blow.SwingDirection = ResolveDirection(agent, in blow);

            if (ER_Config.DebugLogging)
                ER_Log.Info($"Safe fatal-blow compatibility route magnitude={blow.BaseMagnitude:0} missile={missile}");
        }
    }

    [HarmonyPatch]
    internal static class ER_SafeRegisterBlowCapturePatch
    {
        internal struct CaptureState
        {
            internal bool Candidate;
            internal Vec3 Direction;
            internal float ForceMagnitude;
            internal float Time;
            internal sbyte BoneIndex;
        }

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type blowType = typeof(Blow);
            foreach (MethodInfo candidate in AccessTools.GetDeclaredMethods(typeof(Agent)))
            {
                if (candidate == null || candidate.Name != nameof(Agent.RegisterBlow))
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length == 0)
                    continue;

                Type first = parameters[0].ParameterType;
                if (first == blowType || (first.IsByRef && first.GetElementType() == blowType))
                    yield return candidate;
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        [HarmonyAfter("TOR", "TOR_Core")]
        private static void Prefix(Agent __instance, [HarmonyArgument(0)] ref Blow blow, out CaptureState __state)
        {
            __state = default;
            if (__instance == null)
                return;

            float health;
            try { health = __instance.Health; }
            catch { return; }
            if (float.IsNaN(health) || float.IsInfinity(health))
                return;

            int damage = blow.InflictedDamage;
            bool lethal = health <= 0f || (damage > 0 && health - damage <= 0f);
            if (!lethal)
                return;

            if (!ER_ActiveRagdollImpulse.HasAgentForceRoute)
            {
                ER_SafeDeathPipeline.ApplyLegacyFatalBlowFallback(__instance, ref blow);
                return;
            }

            float force = ER_SafeDeathPipeline.ComputeRagdollForceMagnitude(in blow);
            if (force <= 0f)
                return;

            __state = new CaptureState
            {
                Candidate = true,
                Direction = ER_SafeDeathPipeline.ResolveDirection(__instance, in blow),
                ForceMagnitude = force,
                Time = __instance.Mission?.CurrentTime ?? Mission.Current?.CurrentTime ?? 0f,
                BoneIndex = blow.BoneIndex,
            };
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Agent __instance, CaptureState __state)
        {
            if (!__state.Candidate || __instance == null)
                return;

            float health;
            try { health = __instance.Health; }
            catch { return; }
            if (float.IsNaN(health) || float.IsInfinity(health) || health > 0f)
                return;

            ER_SafeRagdollBehavior.Enqueue(
                __instance,
                __state.Direction,
                __state.ForceMagnitude,
                __state.Time,
                __state.BoneIndex,
                "RegisterBlow");
        }
    }

    [HarmonyPatch]
    internal static class ER_DisableLegacyPreDeathQueuePatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(ER_DeathBlastBehavior)))
                if (method != null && method.Name == "QueuePreDeath")
                    yield return method;
        }

        [HarmonyPrefix]
        private static bool Prefix() => false;
    }

    [HarmonyPatch]
    internal static class ER_DisableLegacyDeadSweepPatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(ER_DeathBlastBehavior)))
                if (method != null && method.Name == "SweepDeadRagdolls")
                    yield return method;
        }

        [HarmonyPrefix]
        private static bool Prefix() => false;
    }

    [HarmonyPatch]
    internal static class ER_DisableLegacyRemovedLaunchPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(ER_DeathBlastBehavior), nameof(ER_DeathBlastBehavior.OnAgentRemoved));

        [HarmonyPrefix]
        private static bool Prefix() => false;
    }

    public sealed class ER_SafeRagdollBehavior : MissionBehavior
    {
        private const float SolverSettleDelay = 0.033f;
        private const float RetryDelay = 0.05f;
        private const float PendingLifetime = 8f;
        private const int MaxApplyAttempts = 3;

        private sealed class PendingDeath
        {
            internal Agent Agent;
            internal int AgentIndex;
            internal Vec3 Direction;
            internal float ForceMagnitude;
            internal float CapturedAt;
            internal float RagdollSeenAt = -1f;
            internal float NextTryAt;
            internal int Attempts;
            internal sbyte BoneIndex;
            internal string Source;
        }

        private static ER_SafeRagdollBehavior _instance;
        private readonly object _gate = new object();
        private readonly List<PendingDeath> _pending = new List<PendingDeath>(64);
        private readonly HashSet<Agent> _completed = new HashSet<Agent>();

        internal static void Reset()
        {
            _instance = null;
        }

        internal static void Enqueue(
            Agent agent,
            Vec3 direction,
            float forceMagnitude,
            float capturedAt,
            sbyte boneIndex,
            string source)
        {
            _instance?.EnqueueInternal(agent, direction, forceMagnitude, capturedAt, boneIndex, source);
        }

        public override void OnBehaviorInitialize()
        {
            _instance = this;
            lock (_gate)
            {
                _pending.Clear();
                _completed.Clear();
            }
        }

        public override void OnRemoveBehavior()
        {
            lock (_gate)
            {
                _pending.Clear();
                _completed.Clear();
            }
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }

        private void EnqueueInternal(
            Agent agent,
            Vec3 direction,
            float forceMagnitude,
            float capturedAt,
            sbyte boneIndex,
            string source)
        {
            if (agent == null || forceMagnitude <= 0f || !ER_Math.IsFinite(in direction))
                return;

            int agentIndex;
            try { agentIndex = agent.Index; }
            catch { agentIndex = -1; }

            lock (_gate)
            {
                if (_completed.Contains(agent))
                    return;

                for (int i = 0; i < _pending.Count; i++)
                {
                    PendingDeath existing = _pending[i];
                    if (!ReferenceEquals(existing.Agent, agent))
                        continue;

                    if (forceMagnitude > existing.ForceMagnitude)
                    {
                        existing.Direction = direction;
                        existing.ForceMagnitude = forceMagnitude;
                        existing.BoneIndex = boneIndex;
                        existing.Source = source;
                    }
                    return;
                }

                _pending.Add(new PendingDeath
                {
                    Agent = agent,
                    AgentIndex = agentIndex,
                    Direction = direction,
                    ForceMagnitude = forceMagnitude,
                    CapturedAt = capturedAt,
                    NextTryAt = capturedAt,
                    BoneIndex = boneIndex,
                    Source = source ?? "unknown",
                });
            }
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null || state != AgentState.Killed)
                return;
            if (!ER_ActiveRagdollImpulse.HasAgentForceRoute)
                return;

            lock (_gate)
            {
                if (_completed.Contains(affected))
                    return;
                for (int i = 0; i < _pending.Count; i++)
                    if (ReferenceEquals(_pending[i].Agent, affected))
                        return;
            }

            Vec3 direction;
            try { direction = affected.LookDirection; }
            catch { direction = new Vec3(0f, 1f, 0f); }
            direction = ER_DeathBlastBehavior.FinalizeImpulseDir(
                ER_DeathBlastBehavior.PrepDir(direction, 0.98f, 0.02f),
                ER_Config.CorpseLaunchMaxUpFraction);

            float fallbackForce = 2500f * MathF.Max(1f, ER_Config.KnockbackMultiplier);
            float hardCap = ER_Config.CorpseImpulseHardCap;
            if (!float.IsNaN(hardCap) && !float.IsInfinity(hardCap) && hardCap > 0f)
                fallbackForce = MathF.Min(fallbackForce, hardCap * 2000f);
            fallbackForce = MathF.Min(fallbackForce, 16000f);

            float now = Mission?.CurrentTime ?? affected.Mission?.CurrentTime ?? 0f;
            EnqueueInternal(affected, direction, fallbackForce, now, -1, "OnAgentRemoved");
        }

        public override void OnMissionTick(float dt)
        {
            if (dt <= 0f || Mission == null || MBCommon.IsPaused)
                return;

            float now = Mission.CurrentTime;
            lock (_gate)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    PendingDeath pending = _pending[i];
                    Agent agent = pending.Agent;
                    if (agent == null || now - pending.CapturedAt > PendingLifetime)
                    {
                        _pending.RemoveAt(i);
                        continue;
                    }
                    if (now < pending.NextTryAt)
                        continue;

                    float health;
                    try { health = agent.Health; }
                    catch
                    {
                        _pending.RemoveAt(i);
                        continue;
                    }
                    if (float.IsNaN(health) || float.IsInfinity(health) || health > 0f)
                        continue;

                    Skeleton skeleton;
                    try { skeleton = agent.AgentVisuals?.GetSkeleton(); }
                    catch { skeleton = null; }
                    if (skeleton == null)
                    {
                        pending.NextTryAt = now + RetryDelay;
                        continue;
                    }

                    bool ragdollActive;
                    try { ragdollActive = ER_DeathBlastBehavior.IsRagdollActiveFast(skeleton); }
                    catch { ragdollActive = false; }
                    if (!ragdollActive)
                    {
                        pending.NextTryAt = now + RetryDelay;
                        continue;
                    }

                    if (pending.RagdollSeenAt < 0f)
                    {
                        pending.RagdollSeenAt = now;
                        pending.NextTryAt = now + SolverSettleDelay;
                        continue;
                    }
                    if (now - pending.RagdollSeenAt < SolverSettleDelay)
                        continue;

                    Vec3 direction = ER_DeathBlastBehavior.FinalizeImpulseDir(
                        pending.Direction,
                        ER_Config.CorpseLaunchMaxUpFraction);
                    Vec3 force = direction * pending.ForceMagnitude;

                    bool applied;
                    try
                    {
                        applied = ER_ActiveRagdollImpulse.TryApply(
                            agent,
                            skeleton,
                            pending.BoneIndex,
                            in force);
                    }
                    catch
                    {
                        applied = false;
                    }

                    if (applied)
                    {
                        _completed.Add(agent);
                        _pending.RemoveAt(i);
                        if (ER_Config.DebugLogging)
                            ER_Log.Info($"Safe corpse force applied Agent#{pending.AgentIndex} source={pending.Source} bone={pending.BoneIndex} force={pending.ForceMagnitude:0}");
                        continue;
                    }

                    pending.Attempts++;
                    if (pending.Attempts >= MaxApplyAttempts)
                    {
                        _pending.RemoveAt(i);
                        if (ER_Config.DebugLogging)
                            ER_Log.Info($"Safe corpse force dropped Agent#{pending.AgentIndex}: active ragdoll rejected {pending.Attempts} attempt(s)");
                    }
                    else
                    {
                        pending.NextTryAt = now + RetryDelay;
                    }
                }
            }
        }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    }
}
