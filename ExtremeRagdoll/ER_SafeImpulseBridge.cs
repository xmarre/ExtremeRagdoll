using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    // The legacy evaluator caches the first enum name containing "active". Bannerlord declares
    // ActiveFirstTick before Active, so the persistent Active state was incorrectly treated as inactive.
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
    /// Keeps the original knockback amplifier for living targets while preventing it from touching a lethal blow.
    /// The scope remains active until Harmony finalizers run, covering the old late-priority prefix.
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

            float damage = blow.InflictedDamage;
            if (float.IsNaN(damage) || float.IsInfinity(damage))
                damage = 0f;

            bool lethal = health <= 0f || (damage > 0f && health - damage <= 0f);
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
    /// Applies one impulse to a body that Bannerlord has already converted to ragdoll.
    /// No route in this class activates, wakes, ticks, freezes, or otherwise changes ragdoll state.
    /// </summary>
    internal static class ER_ActiveRagdollImpulse
    {
        private static readonly object BindLock = new object();
        private static int _bound;

        // Bannerlord 1.3.x: local contact + global force + ForceMode.Impulse.
        private static MethodInfo _globalForceAtLocalPos;
        private static object _impulseForceMode;

        // Compatibility routes used by older/newer engine builds.
        private static MethodInfo _worldImpulseInstance3;
        private static MethodInfo _worldImpulseExtension4;
        private static MethodInfo _worldImpulseInstance2;
        private static MethodInfo _localImpulseInstance2;
        private static MethodInfo _localImpulseExtension3;

        private static int _missingRouteLogged;

        internal static bool TryApply(GameEntity entity, Skeleton skeleton, in Vec3 worldImpulse, in Vec3 worldPosition)
        {
            if (entity == null || skeleton == null)
                return false;
            if (!ER_Math.IsFinite(in worldImpulse) || !ER_Math.IsFinite(in worldPosition))
                return false;
            if (worldImpulse.LengthSquared < ER_Math.ImpulseTinySq)
                return false;

            bool ragdollActive;
            try { ragdollActive = ER_DeathBlastBehavior.IsRagdollActiveFast(skeleton); }
            catch { ragdollActive = false; }
            if (!ragdollActive)
                return false;

            bool hasPhysicsBody;
            try { hasPhysicsBody = entity.HasPhysicsBody(); }
            catch { hasPhysicsBody = false; }
            if (!hasPhysicsBody)
                return false;

            Vec3 min;
            Vec3 max;
            try
            {
                min = entity.GetPhysicsBoundingBoxMin();
                max = entity.GetPhysicsBoundingBoxMax();
            }
            catch
            {
                return false;
            }
            if (!ER_Math.IsFinite(in min) || !ER_Math.IsFinite(in max))
                return false;
            Vec3 extent = max - min;
            if (!ER_Math.IsFinite(in extent) || extent.x <= 0f || extent.y <= 0f || extent.z <= 0f)
                return false;
            float maxExtent = ER_Config.MaxAabbExtent;
            if (float.IsNaN(maxExtent) || float.IsInfinity(maxExtent) || maxExtent <= 0f)
                maxExtent = 1024f;
            if (extent.x > maxExtent || extent.y > maxExtent || extent.z > maxExtent)
                return false;

            MatrixFrame frame;
            try { frame = entity.GetGlobalFrame(); }
            catch { return false; }

            Vec3 localPosition = WorldPointToLocal(in frame, in worldPosition);
            Vec3 localImpulse = WorldVectorToLocal(in frame, in worldImpulse);
            if (!ER_Math.IsFinite(in localPosition) || !ER_Math.IsFinite(in localImpulse))
                return false;

            EnsureBound();

            try
            {
                if (_globalForceAtLocalPos != null && _impulseForceMode != null)
                {
                    _globalForceAtLocalPos.Invoke(
                        null,
                        new[] { (object)entity, localPosition, worldImpulse, _impulseForceMode });
                    return true;
                }

                if (_worldImpulseInstance3 != null)
                {
                    _worldImpulseInstance3.Invoke(entity, new object[] { worldImpulse, worldPosition, false });
                    return true;
                }

                if (_worldImpulseExtension4 != null)
                {
                    _worldImpulseExtension4.Invoke(null, new object[] { entity, worldImpulse, worldPosition, false });
                    return true;
                }

                // Legacy GameEntity API: position first, impulse second.
                if (_worldImpulseInstance2 != null)
                {
                    _worldImpulseInstance2.Invoke(entity, new object[] { worldPosition, worldImpulse });
                    return true;
                }

                if (_localImpulseInstance2 != null)
                {
                    _localImpulseInstance2.Invoke(entity, new object[] { localPosition, localImpulse });
                    return true;
                }

                if (_localImpulseExtension3 != null)
                {
                    _localImpulseExtension3.Invoke(null, new object[] { entity, localPosition, localImpulse });
                    return true;
                }
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                if (ER_Config.DebugLogging)
                    ER_Log.Error("Safe active-ragdoll impulse failed", inner);
                return false;
            }
            catch (Exception ex)
            {
                if (ER_Config.DebugLogging)
                    ER_Log.Error("Safe active-ragdoll impulse failed", ex);
                return false;
            }

            if (Interlocked.Exchange(ref _missingRouteLogged, 1) == 0)
                ER_Log.Error("Safe active-ragdoll impulse disabled: no compatible GameEntity impulse API was found");
            return false;
        }

        private static Vec3 WorldPointToLocal(in MatrixFrame frame, in Vec3 worldPoint)
        {
            Vec3 delta = worldPoint - frame.origin;
            return WorldVectorToLocal(in frame, in delta);
        }

        private static Vec3 WorldVectorToLocal(in MatrixFrame frame, in Vec3 worldVector)
        {
            return new Vec3(
                frame.rotation.s.x * worldVector.x + frame.rotation.s.y * worldVector.y + frame.rotation.s.z * worldVector.z,
                frame.rotation.f.x * worldVector.x + frame.rotation.f.y * worldVector.y + frame.rotation.f.z * worldVector.z,
                frame.rotation.u.x * worldVector.x + frame.rotation.u.y * worldVector.y + frame.rotation.u.z * worldVector.z);
        }

        private static void EnsureBound()
        {
            if (Volatile.Read(ref _bound) != 0)
                return;

            lock (BindLock)
            {
                if (_bound != 0)
                    return;

                const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                Type gameEntityType = typeof(GameEntity);
                Type vec3Type = typeof(Vec3);
                Type extensions = gameEntityType.Assembly.GetType("TaleWorlds.Engine.GameEntityPhysicsExtensions");

                _worldImpulseInstance3 = gameEntityType.GetMethod(
                    "ApplyImpulseToDynamicBody",
                    InstanceFlags,
                    null,
                    new[] { vec3Type, vec3Type, typeof(bool) },
                    null);

                _worldImpulseInstance2 = gameEntityType.GetMethod(
                    "ApplyImpulseToDynamicBody",
                    InstanceFlags,
                    null,
                    new[] { vec3Type, vec3Type },
                    null);

                _localImpulseInstance2 = gameEntityType.GetMethod(
                    "ApplyLocalImpulseToDynamicBody",
                    InstanceFlags,
                    null,
                    new[] { vec3Type, vec3Type },
                    null);

                if (extensions != null)
                {
                    _worldImpulseExtension4 = extensions.GetMethod(
                        "ApplyImpulseToDynamicBody",
                        StaticFlags,
                        null,
                        new[] { gameEntityType, vec3Type, vec3Type, typeof(bool) },
                        null);

                    _localImpulseExtension3 = extensions.GetMethod(
                        "ApplyLocalImpulseToDynamicBody",
                        StaticFlags,
                        null,
                        new[] { gameEntityType, vec3Type, vec3Type },
                        null);

                    Type forceModeType = extensions.GetNestedType("ForceMode", BindingFlags.Public | BindingFlags.NonPublic);
                    if (forceModeType != null && forceModeType.IsEnum)
                    {
                        _globalForceAtLocalPos = extensions.GetMethod(
                            "ApplyGlobalForceAtLocalPosToDynamicBody",
                            StaticFlags,
                            null,
                            new[] { gameEntityType, vec3Type, vec3Type, forceModeType },
                            null);
                        if (_globalForceAtLocalPos != null)
                            _impulseForceMode = Enum.ToObject(forceModeType, 1);
                    }
                }

                Volatile.Write(ref _bound, 1);
            }
        }
    }
}
