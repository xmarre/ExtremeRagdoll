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
    /// Bannerlord 1.2.12 clamps Blow.BaseMagnitude to 1000 inside Agent.HandleBlow before
    /// calling Agent.Die. Apply the bounded extreme value to Die's private Blow copy instead:
    /// damage and hit callbacks have already completed, while the native death transition has
    /// not started yet. This stays synchronous with the real fatal hit and never forces ragdoll.
    /// </summary>
    [HarmonyPatch]
    internal static class ER_SafeLegacyDieImpulsePatch
    {
        private const float MeleeMagnitudeCap = 6000f;
        private const float MissileMagnitudeCap = 3500f;
        private const float MeleeMagnitudeFloor = 3000f;
        private const float MissileMagnitudeFloor = 1800f;
        private const float HardCapToMagnitudeUnits = 1000f;

        private static readonly MethodBase[] Targets = FindTargets();

        [HarmonyPrepare]
        private static bool Prepare()
        {
            if (Targets.Length != 0)
                return true;

            if (ER_Config.DebugLogging)
                ER_Log.Info("Safe fatal Die impulse patch skipped: no compatible Agent.Die(Blow, ...) target");
            return false;
        }

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods() => Targets;

        private static MethodBase[] FindTargets()
        {
            var targets = new List<MethodBase>();
            var blowType = typeof(Blow);
            foreach (var candidate in AccessTools.GetDeclaredMethods(typeof(Agent)))
            {
                if (candidate == null || candidate.Name != "Die")
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

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("TOR", "TOR_Core")]
        private static void Prefix(Agent __instance, [HarmonyArgument(0)] ref Blow blow)
        {
            if (__instance == null || ER_ActiveRagdollImpulse.HasAgentForceRoute)
                return;

            var originalMagnitude = blow.BaseMagnitude;
            if (float.IsNaN(originalMagnitude) || float.IsInfinity(originalMagnitude) || originalMagnitude < 0f)
                originalMagnitude = 0f;

            var damage = MathF.Max(0f, (float)blow.InflictedDamage);
            var missile = IsMissile(in blow);

            var cap = missile ? MissileMagnitudeCap : MeleeMagnitudeCap;
            var configuredHardCap = ER_Config.CorpseImpulseHardCap;
            if (!float.IsNaN(configuredHardCap) &&
                !float.IsInfinity(configuredHardCap) &&
                configuredHardCap > 0f)
            {
                cap = MathF.Min(cap, configuredHardCap * HardCapToMagnitudeUnits);
            }
            if (cap <= 0f)
                return;

            var floor = missile ? MissileMagnitudeFloor : MeleeMagnitudeFloor;
            if (floor > cap)
                floor = cap;

            var multiplier = MathF.Max(1f, ER_Config.ExtraForceMultiplier) *
                             MathF.Max(1f, ER_Config.KnockbackMultiplier);
            multiplier = MathF.Min(multiplier, 4f);

            var damageScale = missile ? 12f : 20f;
            var sourceMagnitude = 1000f + damage * damageScale;
            var desiredMagnitude = sourceMagnitude * multiplier;
            if (desiredMagnitude < floor)
                desiredMagnitude = floor;
            if (desiredMagnitude > cap)
                desiredMagnitude = cap;

            // Preserve all values supplied by a stronger Die prefix. Only add our matching
            // KnockBack flag/direction when this patch actually supplies the winning magnitude.
            if (desiredMagnitude <= originalMagnitude)
            {
                if (ER_Config.DebugLogging)
                    ER_Log.Info($"Safe fatal Die impulse preserved stronger external magnitude={originalMagnitude:0}");
                return;
            }

            blow.BaseMagnitude = desiredMagnitude;

            // KnockBack is applied only to Die's by-value Blow copy. It cannot cause a living
            // hit reaction or alter Agent.HandleBlowAux after Die returns. Do not add KnockDown:
            // native death animation/ragdoll selection remains Bannerlord's responsibility.
            blow.BlowFlag |= BlowFlags.KnockBack;
            blow.SwingDirection = ER_SafeDeathPipeline.ResolveDirection(__instance, in blow);

            if (ER_Config.DebugLogging)
            {
                ER_Log.Info(
                    $"Safe fatal Die impulse original={originalMagnitude:0} applied={blow.BaseMagnitude:0} " +
                    $"missile={missile} knockBack=True");
            }
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
}
