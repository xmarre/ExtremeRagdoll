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
    // The legacy evaluator cached ActiveFirstTick and then rejected the persistent Active state.
    [HarmonyPatch(typeof(ER_DeathBlastBehavior), "IsRagdollActiveFast")]
    internal static class ER_ExactRagdollStatePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Skeleton sk, ref bool __result)
        {
            if (sk == null)
            {
                __result = false;
                return false;
            }

            try
            {
                RagdollState state = sk.GetCurrentRagdollState();
                __result = state == RagdollState.ActiveFirstTick || state == RagdollState.Active;
            }
            catch
            {
                __result = false;
            }

            return false;
        }
    }

    internal static class ER_SafeLegacyRegisterBlow
    {
        internal static void RestoreNonLethalPrefix(Harmony harmony)
        {
            if (harmony == null)
                return;

            MethodInfo legacyPrefix = AccessTools.Method(typeof(ER_Amplify_RegisterBlowPatch), "Prefix");
            if (legacyPrefix == null)
            {
                ER_Log.Error("Safe death pipeline: legacy RegisterBlow prefix was not found");
                return;
            }

            int restored = 0;
            Type blowType = typeof(Blow);
            foreach (MethodInfo original in AccessTools.GetDeclaredMethods(typeof(Agent)))
            {
                if (original == null || original.Name != nameof(Agent.RegisterBlow))
                    continue;

                ParameterInfo[] parameters = original.GetParameters();
                if (parameters.Length == 0)
                    continue;

                Type first = parameters[0].ParameterType;
                if (first != blowType && !(first.IsByRef && first.GetElementType() == blowType))
                    continue;

                try
                {
                    var patch = new HarmonyMethod(legacyPrefix)
                    {
                        priority = Priority.Last,
                        after = new[] { "TOR", "TOR_Core" },
                    };
                    harmony.Patch(original, prefix: patch);
                    restored++;
                }
                catch (Exception ex)
                {
                    ER_Log.Error($"Safe death pipeline: failed to restore nonlethal RegisterBlow prefix on {original}", ex);
                }
            }

            ER_Log.Info($"Safe death pipeline: restored nonlethal RegisterBlow prefix on {restored} overload(s)");
        }
    }

    /// <summary>
    /// Keeps the old living-target knockback path while preventing it from mutating lethal blows.
    /// TOR runs first, capture observes the finalized blow, this scope then suppresses the old late prefix.
    /// </summary>
    [HarmonyPatch]
    internal static class ER_SuppressLegacyLethalRegisterBlowPatch
    {
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
        [HarmonyPriority(Priority.Low)]
        [HarmonyAfter("TOR", "TOR_Core")]
        private static void Prefix(
            Agent __instance,
            [HarmonyArgument(0)] ref Blow blow,
            out IDisposable __state)
        {
            __state = null;
            if (__instance == null)
                return;

            float health;
            try { health = __instance.Health; }
            catch { return; }
            if (float.IsNaN(health) || float.IsInfinity(health))
                return;

            int damage = blow.InflictedDamage;
            bool lethal = health <= 0f || (damage > 0 && health - damage <= 0f);
            if (lethal)
                __state = ER_Amplify_RegisterBlowPatch.SuppressPrefixScope();
        }

        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(IDisposable __state, Exception __exception)
        {
            try { __state?.Dispose(); }
            catch { }
            return __exception;
        }
    }

    /// <summary>
    /// Applies force through Bannerlord's dedicated Agent ragdoll API when that API exists.
    /// Bannerlord 1.2.12 does not expose these methods, so all binding is reflective and the
    /// safe death pipeline uses the original fatal blow as its compatibility route instead.
    /// </summary>
    internal static class ER_ActiveRagdollImpulse
    {
        private const float LinearVelocityLimit = 25f;
        private const float AngularVelocityLimit = 60f;

        private static readonly object BindLock = new object();
        private static bool _bound;
        private static MethodInfo _applyForceOnRagdoll;
        private static MethodInfo _setVelocityLimitsOnRagdoll;
        private static bool _missingRouteLogged;

        internal static bool HasAgentForceRoute
        {
            get
            {
                EnsureBound();
                return _applyForceOnRagdoll != null;
            }
        }

        internal static bool TryApply(Agent agent, Skeleton skeleton, sbyte requestedBoneIndex, in Vec3 worldForce)
        {
            if (agent == null || skeleton == null)
                return false;
            if (!ER_Math.IsFinite(in worldForce) || worldForce.LengthSquared < ER_Math.ImpulseTinySq)
                return false;

            RagdollState state;
            try { state = skeleton.GetCurrentRagdollState(); }
            catch { return false; }
            if (state != RagdollState.ActiveFirstTick && state != RagdollState.Active)
                return false;

            EnsureBound();
            if (_applyForceOnRagdoll == null)
            {
                LogMissingRouteOnce();
                return false;
            }

            sbyte boneIndex = ResolveBoneIndex(skeleton, requestedBoneIndex);
            try
            {
                if (_setVelocityLimitsOnRagdoll != null)
                {
                    _setVelocityLimitsOnRagdoll.Invoke(
                        agent,
                        new object[] { LinearVelocityLimit, AngularVelocityLimit });
                }

                ParameterInfo[] parameters = _applyForceOnRagdoll.GetParameters();
                object boneArgument = BoxInteger(parameters[0].ParameterType, boneIndex);
                _applyForceOnRagdoll.Invoke(agent, new object[] { boneArgument, worldForce });

                if (ER_Config.DebugLogging)
                {
                    string route = _applyForceOnRagdoll.DeclaringType?.Name + "." + _applyForceOnRagdoll.Name;
                    ER_Log.Info($"Safe ragdoll force route={route} bone={boneIndex} force={Math.Sqrt(worldForce.LengthSquared):0}");
                }
                return true;
            }
            catch (TargetInvocationException ex)
            {
                if (ER_Config.DebugLogging)
                    ER_Log.Error("Safe Agent ragdoll-force invocation failed", ex.InnerException ?? ex);
                return false;
            }
            catch (Exception ex)
            {
                if (ER_Config.DebugLogging)
                    ER_Log.Error("Safe Agent ragdoll-force invocation failed", ex);
                return false;
            }
        }

        private static void EnsureBound()
        {
            if (_bound)
                return;

            lock (BindLock)
            {
                if (_bound)
                    return;

                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo[] methods;
                try { methods = typeof(Agent).GetMethods(Flags); }
                catch { methods = Array.Empty<MethodInfo>(); }

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null)
                        continue;

                    ParameterInfo[] parameters;
                    try { parameters = method.GetParameters(); }
                    catch { continue; }

                    if (method.Name == "ApplyForceOnRagdoll" &&
                        parameters.Length == 2 &&
                        IsInteger(parameters[0].ParameterType) &&
                        IsVec3(parameters[1].ParameterType))
                    {
                        _applyForceOnRagdoll = method;
                        continue;
                    }

                    if (method.Name == "SetVelocityLimitsOnRagdoll" &&
                        parameters.Length == 2 &&
                        Unwrap(parameters[0].ParameterType) == typeof(float) &&
                        Unwrap(parameters[1].ParameterType) == typeof(float))
                    {
                        _setVelocityLimitsOnRagdoll = method;
                    }
                }

                _bound = true;
            }
        }

        private static void LogMissingRouteOnce()
        {
            lock (BindLock)
            {
                if (_missingRouteLogged)
                    return;
                _missingRouteLogged = true;
            }

            if (ER_Config.DebugLogging)
                ER_Log.Info("Dedicated Agent ragdoll-force API unavailable; using fatal-blow compatibility route");
        }

        private static Type Unwrap(Type type)
        {
            return type != null && type.IsByRef ? type.GetElementType() : type;
        }

        private static bool IsVec3(Type type)
        {
            return Unwrap(type) == typeof(Vec3);
        }

        private static bool IsInteger(Type type)
        {
            type = Unwrap(type);
            return type == typeof(sbyte) || type == typeof(byte) ||
                   type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint);
        }

        private static object BoxInteger(Type type, int value)
        {
            type = Unwrap(type);
            if (type == typeof(sbyte)) return (sbyte)Math.Max(sbyte.MinValue, Math.Min(sbyte.MaxValue, value));
            if (type == typeof(byte)) return (byte)Math.Max(byte.MinValue, Math.Min(byte.MaxValue, value));
            if (type == typeof(short)) return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
            if (type == typeof(ushort)) return (ushort)Math.Max(ushort.MinValue, Math.Min(ushort.MaxValue, value));
            if (type == typeof(uint)) return (uint)Math.Max(0, value);
            return value;
        }

        private static sbyte ResolveBoneIndex(Skeleton skeleton, sbyte requested)
        {
            int count;
            try { count = skeleton.GetBoneCount(); }
            catch { return requested >= 0 ? requested : (sbyte)0; }

            if (count <= 0)
                return 0;
            if (requested >= 0 && requested < count)
                return requested;

            string[] preferred = { "pelvis", "hips", "spine_0", "spine", "root" };
            for (int p = 0; p < preferred.Length; p++)
            {
                for (int i = 0; i < count && i <= sbyte.MaxValue; i++)
                {
                    try
                    {
                        string name = skeleton.GetBoneName((sbyte)i);
                        if (!string.IsNullOrEmpty(name) &&
                            name.IndexOf(preferred[p], StringComparison.OrdinalIgnoreCase) >= 0)
                            return (sbyte)i;
                    }
                    catch { }
                }
            }

            return 0;
        }
    }
}
