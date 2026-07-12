using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    internal static class ER_SafeDeathPipeline
    {
        private const string LegacyPatchId = "extremeragdoll.patch";

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

        internal static float ComputeImpulseMagnitude(in Blow blow)
        {
            float baseMagnitude = blow.BaseMagnitude;
            if (float.IsNaN(baseMagnitude) || float.IsInfinity(baseMagnitude) || baseMagnitude < 0f)
                baseMagnitude = 0f;

            float damage = blow.InflictedDamage;
            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage < 0f)
                damage = 0f;

            // Use the real fatal blow only as an input signal. The engine blow itself remains untouched.
            // This range is strong enough to move an active ragdoll while staying below tunnelling speeds.
            float sourceMagnitude = MathF.Max(MathF.Min(baseMagnitude, 1200f), 250f + damage * 8f);
            float multiplier = MathF.Max(0f, ER_Config.ExtraForceMultiplier) * MathF.Max(1f, ER_Config.KnockbackMultiplier);
            float impulse = sourceMagnitude * 1.0e-3f * multiplier;

            float minimum = ER_Config.CorpseImpulseMinimum;
            if (float.IsNaN(minimum) || float.IsInfinity(minimum) || minimum < 0f)
                minimum = 0f;

            float maximum = ER_Config.CorpseImpulseMaximum;
            if (float.IsNaN(maximum) || float.IsInfinity(maximum) || maximum < 0f)
                maximum = 0f;

            if (maximum > 0f && minimum > maximum)
                minimum = maximum;
            if (minimum > 0f && impulse < minimum)
                impulse = minimum;
            if (maximum > 0f && impulse > maximum)
                impulse = maximum;

            float hardCap = ER_Config.CorpseImpulseHardCap;
            if (!float.IsNaN(hardCap) && !float.IsInfinity(hardCap) && hardCap > 0f && impulse > hardCap)
                impulse = hardCap;

            const float AbsoluteSafeCap = 1.25f;
            if (impulse > AbsoluteSafeCap)
                impulse = AbsoluteSafeCap;

            return float.IsNaN(impulse) || float.IsInfinity(impulse) || impulse <= 0f ? 0f : impulse;
        }

        internal static Vec3 ResolveDirection(Agent agent, in Blow blow)
        {
            Vec3 direction = blow.SwingDirection;
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
            if (!ER_Math.IsFinite(in direction) || direction.LengthSquared < ER_Math.DirectionTinySq)
                return new Vec3(0f, 1f, 0f);
            return direction;
        }

        internal static Vec3 ResolveContact(Agent agent)
        {
            Vec3 contact;
            try { contact = agent.Position; }
            catch { contact = Vec3.Zero; }
            contact.z += 0.9f;
            return contact;
        }
    }

    [HarmonyPatch]
    internal static class ER_SafeRegisterBlowCapturePatch
    {
        internal struct CaptureState
        {
            internal bool Candidate;
            internal Vec3 Direction;
            internal Vec3 Contact;
            internal float ImpulseMagnitude;
            internal float Time;
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
        [HarmonyPriority(Priority.First)]
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

            float damage = blow.InflictedDamage;
            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f)
                return;

            // Capture only a blow that can actually be fatal. The postfix confirms the final engine result.
            if (health > 0f && health - damage > 0f)
                return;

            float impulse = ER_SafeDeathPipeline.ComputeImpulseMagnitude(in blow);
            if (impulse <= 0f)
                return;

            __state = new CaptureState
            {
                Candidate = true,
                Direction = ER_SafeDeathPipeline.ResolveDirection(__instance, in blow),
                Contact = ER_SafeDeathPipeline.ResolveContact(__instance),
                ImpulseMagnitude = impulse,
                Time = __instance.Mission?.CurrentTime ?? Mission.Current?.CurrentTime ?? 0f,
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
                __state.Contact,
                __state.ImpulseMagnitude,
                __state.Time,
                "RegisterBlow");
        }
    }

    // These legacy paths activate physics before Bannerlord has completed its death transition.
    // Returning false leaves the engine animation/state machine in sole control of that transition.
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
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(ER_DeathBlastBehavior), nameof(ER_DeathBlastBehavior.OnAgentRemoved));

        [HarmonyPrefix]
        private static bool Prefix() => false;
    }

    public sealed class ER_SafeRagdollBehavior : MissionBehavior
    {
        private const float SolverSettleDelay = 0.033f;
        private const float RetryDelay = 0.05f;
        private const float PendingLifetime = 8.0f;
        private const int MaxApplyAttempts = 8;

        private sealed class PendingDeath
        {
            internal Agent Agent;
            internal int AgentIndex;
            internal Vec3 Direction;
            internal Vec3 Contact;
            internal float ImpulseMagnitude;
            internal float CapturedAt;
            internal float RagdollSeenAt = -1f;
            internal float NextTryAt;
            internal int Attempts;
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

        internal static void Enqueue(Agent agent, Vec3 direction, Vec3 contact, float impulseMagnitude, float capturedAt, string source)
        {
            _instance?.EnqueueInternal(agent, direction, contact, impulseMagnitude, capturedAt, source);
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

        private void EnqueueInternal(Agent agent, Vec3 direction, Vec3 contact, float impulseMagnitude, float capturedAt, string source)
        {
            if (agent == null || impulseMagnitude <= 0f || !ER_Math.IsFinite(in direction) || !ER_Math.IsFinite(in contact))
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

                    // Retain the strongest observed fatal hit without creating stacked pulses.
                    if (impulseMagnitude > existing.ImpulseMagnitude)
                    {
                        existing.Direction = direction;
                        existing.Contact = contact;
                        existing.ImpulseMagnitude = impulseMagnitude;
                        existing.Source = source;
                    }
                    return;
                }

                _pending.Add(new PendingDeath
                {
                    Agent = agent,
                    AgentIndex = agentIndex,
                    Direction = direction,
                    Contact = contact,
                    ImpulseMagnitude = impulseMagnitude,
                    CapturedAt = capturedAt,
                    NextTryAt = capturedAt,
                    Source = source ?? "unknown",
                });
            }
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null || state != AgentState.Killed)
                return;

            lock (_gate)
            {
                if (_completed.Contains(affected))
                    return;
                for (int i = 0; i < _pending.Count; i++)
                    if (ReferenceEquals(_pending[i].Agent, affected))
                        return;
            }

            // Scripted deaths may bypass RegisterBlow. Queue a conservative fallback and still wait for real ragdoll state.
            Vec3 direction;
            try { direction = affected.LookDirection; }
            catch { direction = new Vec3(0f, 1f, 0f); }
            direction = ER_DeathBlastBehavior.FinalizeImpulseDir(
                ER_DeathBlastBehavior.PrepDir(direction, 0.98f, 0.02f),
                ER_Config.CorpseLaunchMaxUpFraction);

            float fallbackImpulse = 0.60f * MathF.Max(1f, ER_Config.KnockbackMultiplier);
            float hardCap = ER_Config.CorpseImpulseHardCap;
            if (hardCap > 0f && fallbackImpulse > hardCap)
                fallbackImpulse = hardCap;
            if (fallbackImpulse > 1.25f)
                fallbackImpulse = 1.25f;

            float now = Mission?.CurrentTime ?? affected.Mission?.CurrentTime ?? 0f;
            EnqueueInternal(affected, direction, ER_SafeDeathPipeline.ResolveContact(affected), fallbackImpulse, now, "OnAgentRemoved");
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

                    GameEntity entity = null;
                    Skeleton skeleton = null;
                    try { entity = agent.AgentVisuals?.GetEntity(); } catch { entity = null; }
                    try { skeleton = agent.AgentVisuals?.GetSkeleton(); } catch { skeleton = null; }
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
                        // No activation, animation ticking, synthetic blow, or wake call here.
                        // Bannerlord owns the complete death-animation-to-ragdoll transition.
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

                    Vec3 contact = ER_DeathBlastBehavior.ClampHitToBody(agent, pending.Contact);
                    Vec3 impulse = pending.Direction * pending.ImpulseMagnitude;
                    if (impulse.z < 0f)
                        impulse.z = 0f;

                    bool applied = false;
                    try
                    {
                        applied = ER_DeathBlastBehavior.TryImpulseDirect(
                            entity,
                            skeleton,
                            in impulse,
                            in contact,
                            pending.AgentIndex,
                            now);
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
                            ER_Log.Info($"Safe corpse impulse applied Agent#{pending.AgentIndex} source={pending.Source} impulse={pending.ImpulseMagnitude:0.00}");
                        continue;
                    }

                    pending.Attempts++;
                    if (pending.Attempts >= MaxApplyAttempts)
                    {
                        _pending.RemoveAt(i);
                        if (ER_Config.DebugLogging)
                            ER_Log.Info($"Safe corpse impulse dropped Agent#{pending.AgentIndex}: active ragdoll rejected {pending.Attempts} attempt(s)");
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
