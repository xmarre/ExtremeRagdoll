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

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type blowType = typeof(Blow);
            foreach (MethodInfo candidate in AccessTools.GetDeclaredMethods(typeof(Agent)))
            {
                if (candidate == null || candidate.Name != "Die")
                    continue;

                ParameterInfo[] parameters;
                try { parameters = candidate.GetParameters(); }
                catch { continue; }
                if (parameters.Length == 0)
                    continue;

                Type first = parameters[0].ParameterType;
                if (first == blowType || (first.IsByRef && first.GetElementType() == blowType))
                    yield return candidate;
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("TOR", "TOR_Core")]
        private static void Prefix(Agent __instance, [HarmonyArgument(0)] ref Blow blow)
        {
            if (__instance == null || ER_ActiveRagdollImpulse.HasAgentForceRoute)
                return;

            float originalMagnitude = blow.BaseMagnitude;
            if (float.IsNaN(originalMagnitude) || float.IsInfinity(originalMagnitude) || originalMagnitude < 0f)
                originalMagnitude = 0f;

            float damage = MathF.Max(0f, (float)blow.InflictedDamage);
            bool missile = IsMissile(in blow);

            float cap = missile ? MissileMagnitudeCap : MeleeMagnitudeCap;
            float configuredHardCap = ER_Config.CorpseImpulseHardCap;
            if (!float.IsNaN(configuredHardCap) &&
                !float.IsInfinity(configuredHardCap) &&
                configuredHardCap > 0f)
            {
                cap = MathF.Min(cap, configuredHardCap * HardCapToMagnitudeUnits);
            }
            if (cap <= 0f)
                return;

            float floor = missile ? MissileMagnitudeFloor : MeleeMagnitudeFloor;
            if (floor > cap)
                floor = cap;

            float multiplier = MathF.Max(1f, ER_Config.ExtraForceMultiplier) *
                               MathF.Max(1f, ER_Config.KnockbackMultiplier);
            multiplier = MathF.Min(multiplier, 4f);

            float damageScale = missile ? 12f : 20f;
            float sourceMagnitude = 1000f + damage * damageScale;
            float desiredMagnitude = sourceMagnitude * multiplier;
            if (desiredMagnitude < floor)
                desiredMagnitude = floor;
            if (desiredMagnitude > cap)
                desiredMagnitude = cap;

            // Preserve a stronger value supplied by another Die prefix. HandleBlow normally
            // reaches this point with exactly 1000 because of Bannerlord 1.2.12's managed clamp.
            if (desiredMagnitude > originalMagnitude)
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
                string flags = blow.BlowFlag.ToString();
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
