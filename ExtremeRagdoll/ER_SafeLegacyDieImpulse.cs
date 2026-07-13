using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    /// <summary>
    /// Carries one lethal RegisterBlow context to Agent.HandleBlowAux on the same thread.
    /// Bannerlord 1.2.12 calls HandleBlowAux only after damage callbacks and Agent.Die, so this
    /// is the last managed boundary before native hit physics without taking ownership of the
    /// animation-to-ragdoll transition.
    /// </summary>
    internal static class ER_LegacyFatalBlowContext
    {
        internal sealed class Frame
        {
            internal Agent Agent;
            internal int AgentIndex;
            internal Vec3 Direction;
            internal float Magnitude;
            internal bool Missile;
        }

        [ThreadStatic]
        private static Stack<Frame> _frames;

        internal static int Depth => _frames?.Count ?? 0;

        internal static void Arm(Agent agent, in Blow blow)
        {
            if (agent == null)
                return;

            var magnitude = ComputeMagnitude(in blow, out var missile);
            if (magnitude <= 0f)
                return;

            var direction = ER_SafeDeathPipeline.ResolveDirection(agent, in blow);
            if (!ER_Math.IsFinite(in direction) || direction.LengthSquared < ER_Math.DirectionTinySq)
                return;

            int agentIndex;
            try { agentIndex = agent.Index; }
            catch { agentIndex = -1; }

            if (_frames == null)
                _frames = new Stack<Frame>(4);

            _frames.Push(new Frame
            {
                Agent = agent,
                AgentIndex = agentIndex,
                Direction = direction,
                Magnitude = magnitude,
                Missile = missile,
            });
        }

        internal static bool TryConsume(Agent agent, out Frame frame)
        {
            frame = null;
            var frames = _frames;
            if (agent == null || frames == null || frames.Count == 0)
                return false;

            var top = frames.Peek();
            if (!ReferenceEquals(top.Agent, agent))
                return false;

            frame = frames.Pop();
            return true;
        }

        internal static void Trim(int depth)
        {
            var frames = _frames;
            if (frames == null)
                return;

            if (depth < 0)
                depth = 0;
            while (frames.Count > depth)
                frames.Pop();
        }

        private static float ComputeMagnitude(in Blow blow, out bool missile)
        {
            const float MeleeMagnitudeCap = 6000f;
            const float MissileMagnitudeCap = 3500f;
            const float MeleeMagnitudeFloor = 3000f;
            const float MissileMagnitudeFloor = 1800f;
            const float HardCapToMagnitudeUnits = 1000f;

            missile = IsMissile(in blow);
            var damage = MathF.Max(0f, (float)blow.InflictedDamage);
            var cap = missile ? MissileMagnitudeCap : MeleeMagnitudeCap;

            var configuredHardCap = ER_Config.CorpseImpulseHardCap;
            if (!float.IsNaN(configuredHardCap) &&
                !float.IsInfinity(configuredHardCap) &&
                configuredHardCap > 0f)
            {
                cap = MathF.Min(cap, configuredHardCap * HardCapToMagnitudeUnits);
            }
            if (cap <= 0f)
                return 0f;

            var floor = missile ? MissileMagnitudeFloor : MeleeMagnitudeFloor;
            if (floor > cap)
                floor = cap;

            var multiplier = MathF.Max(1f, ER_Config.ExtraForceMultiplier) *
                             MathF.Max(1f, ER_Config.KnockbackMultiplier);
            multiplier = MathF.Min(multiplier, 4f);

            var damageScale = missile ? 12f : 20f;
            var magnitude = (1000f + damage * damageScale) * multiplier;
            if (magnitude < floor)
                magnitude = floor;
            if (magnitude > cap)
                magnitude = cap;

            return float.IsNaN(magnitude) || float.IsInfinity(magnitude) ? 0f : magnitude;
        }

        private static bool IsMissile(in Blow blow)
        {
            try
            {
                var flags = blow.BlowFlag.ToString();
                return !string.IsNullOrEmpty(flags) &&
                       flags.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Replaces the obsolete RegisterBlow compatibility mutation with a thread-local handoff.
    /// The public Blow remains untouched; Bannerlord may perform its normal clamp and death work.
    /// </summary>
    [HarmonyPatch(typeof(ER_SafeDeathPipeline), nameof(ER_SafeDeathPipeline.ApplyLegacyFatalBlowFallback))]
    internal static class ER_ArmLegacyFatalBlowContextPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            [HarmonyArgument(0)] Agent agent,
            [HarmonyArgument(1)] ref Blow blow)
        {
            ER_LegacyFatalBlowContext.Arm(agent, in blow);
            return false;
        }
    }

    /// <summary>
    /// Bounds the lifetime of thread-local fatal-blow frames across nested/reentrant RegisterBlow
    /// calls. The consumed frame normally disappears in HandleBlowAux; the finalizer removes any
    /// unconsumed frame when Bannerlord exits early or throws.
    /// </summary>
    [HarmonyPatch]
    internal static class ER_LegacyRegisterBlowContextLifetimePatch
    {
        private static readonly MethodBase[] Targets = FindTargets(nameof(Agent.RegisterBlow));

        [HarmonyPrepare]
        private static bool Prepare() => Targets.Length != 0;

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods() => Targets;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(out int __state)
        {
            __state = ER_LegacyFatalBlowContext.Depth;
        }

        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(int __state, Exception __exception)
        {
            ER_LegacyFatalBlowContext.Trim(__state);
            return __exception;
        }

        internal static MethodBase[] FindTargets(string methodName)
        {
            var targets = new List<MethodBase>();
            var blowType = typeof(Blow);
            foreach (var candidate in AccessTools.GetDeclaredMethods(typeof(Agent)))
            {
                if (candidate == null || candidate.Name != methodName)
                    continue;

                ParameterInfo[] parameters;
                try { parameters = candidate.GetParameters(); }
                catch { continue; }
                if (parameters.Length == 0)
                    continue;

                var first = parameters[0].ParameterType;
                if (first == blowType || (first.IsByRef && first.GetElementType() == blowType))
                    targets.Add(candidate);
            }
            return targets.ToArray();
        }
    }

    /// <summary>
    /// Applies the bounded fatal force to the actual Blow passed to native HandleBlowAux.
    /// Agent.Die has already run, so the death animation/state transition remains engine-owned.
    /// </summary>
    [HarmonyPatch]
    internal static class ER_SafeLegacyHandleBlowAuxImpulsePatch
    {
        private static readonly MethodBase[] Targets =
            ER_LegacyRegisterBlowContextLifetimePatch.FindTargets("HandleBlowAux");

        [HarmonyPrepare]
        private static bool Prepare()
        {
            if (Targets.Length != 0)
                return true;

            if (ER_Config.DebugLogging)
                ER_Log.Info("Safe final blow impulse patch skipped: no compatible Agent.HandleBlowAux(ref Blow) target");
            return false;
        }

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods() => Targets;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("TOR", "TOR_Core")]
        private static void Prefix(Agent __instance, [HarmonyArgument(0)] ref Blow blow)
        {
            if (!ER_LegacyFatalBlowContext.TryConsume(__instance, out var frame))
                return;

            float health;
            try { health = __instance.Health; }
            catch { return; }
            if (float.IsNaN(health) || float.IsInfinity(health) || health > 0f)
                return;

            var originalMagnitude = blow.BaseMagnitude;
            if (float.IsNaN(originalMagnitude) || float.IsInfinity(originalMagnitude) || originalMagnitude < 0f)
                originalMagnitude = 0f;

            // Preserve all values supplied by a stronger native-boundary prefix.
            if (frame.Magnitude <= originalMagnitude)
            {
                if (ER_Config.DebugLogging)
                {
                    ER_Log.Info(
                        $"Safe final blow impulse preserved stronger external magnitude={originalMagnitude:0} " +
                        $"Agent#{frame.AgentIndex}");
                }
                return;
            }

            var direction = ER_DeathBlastBehavior.FinalizeImpulseDir(
                frame.Direction,
                ER_Config.CorpseLaunchMaxUpFraction);
            if (!ER_Math.IsFinite(in direction) || direction.LengthSquared < ER_Math.DirectionTinySq)
                direction = ER_SafeDeathPipeline.ResolveDirection(__instance, in blow);

            blow.BaseMagnitude = frame.Magnitude;
            blow.BlowFlag |= BlowFlags.KnockBack;
            blow.SwingDirection = direction;

            if (ER_Config.DebugLogging)
            {
                ER_Log.Info(
                    $"Safe final HandleBlowAux impulse Agent#{frame.AgentIndex} " +
                    $"original={originalMagnitude:0} applied={blow.BaseMagnitude:0} " +
                    $"missile={frame.Missile} knockBack=True");
            }
        }
    }
}
