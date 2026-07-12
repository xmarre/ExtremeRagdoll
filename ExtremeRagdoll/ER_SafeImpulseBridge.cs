using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace ExtremeRagdoll
{
    internal static class ER_SafeImpulseScope
    {
        [ThreadStatic]
        internal static int Depth;
    }

    [HarmonyPatch(typeof(ER_SafeRagdollBehavior), nameof(ER_SafeRagdollBehavior.OnMissionTick))]
    internal static class ER_SafeImpulseScopePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(out int __state)
        {
            __state = ER_SafeImpulseScope.Depth;
            ER_SafeImpulseScope.Depth = __state + 1;
        }

        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(int __state, Exception __exception)
        {
            ER_SafeImpulseScope.Depth = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ER_DeathBlastBehavior), "TryImpulseDirect")]
    internal static class ER_SafeImpulseBridgePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            GameEntity ent,
            Skeleton skel,
            ref Vec3 worldImpulse,
            ref Vec3 worldPos,
            ref bool __result)
        {
            if (ER_SafeImpulseScope.Depth <= 0)
                return true;

            __result = ER_ActiveRagdollImpulse.TryApply(ent, skel, in worldImpulse, in worldPos);
            return false;
        }
    }

    /// <summary>
    /// Applies one world-space impulse to a body that Bannerlord has already converted to ragdoll.
    /// This class deliberately has no activation, wake-up, animation-tick, synthetic-blow, or retry logic.
    /// </summary>
    internal static class ER_ActiveRagdollImpulse
    {
        private static readonly object BindLock = new object();
        private static int _bound;
        private static Action<GameEntity, Vec3, Vec3, bool> _instanceApplyWorld;
        private static Action<GameEntity, Vec3, Vec3, bool> _extensionApplyWorld;
        private static MethodInfo _instanceApplyWorldMethod;
        private static MethodInfo _extensionApplyWorldMethod;
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
            if (extent.x > maxExtent || extent.y > maxExtent || extent.z > maxExtent)
                return false;

            EnsureBound();

            try
            {
                if (_instanceApplyWorld != null)
                {
                    _instanceApplyWorld(entity, worldImpulse, worldPosition, false);
                    return true;
                }
                if (_instanceApplyWorldMethod != null)
                {
                    _instanceApplyWorldMethod.Invoke(entity, new object[] { worldImpulse, worldPosition, false });
                    return true;
                }
                if (_extensionApplyWorld != null)
                {
                    _extensionApplyWorld(entity, worldImpulse, worldPosition, false);
                    return true;
                }
                if (_extensionApplyWorldMethod != null)
                {
                    _extensionApplyWorldMethod.Invoke(null, new object[] { entity, worldImpulse, worldPosition, false });
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
                ER_Log.Error("Safe active-ragdoll impulse disabled: no world-space GameEntity impulse API was found");
            return false;
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
                _instanceApplyWorldMethod = typeof(GameEntity).GetMethod(
                    "ApplyImpulseToDynamicBody",
                    InstanceFlags,
                    null,
                    new[] { typeof(Vec3), typeof(Vec3), typeof(bool) },
                    null);
                if (_instanceApplyWorldMethod != null)
                {
                    try
                    {
                        _instanceApplyWorld = (Action<GameEntity, Vec3, Vec3, bool>)_instanceApplyWorldMethod.CreateDelegate(
                            typeof(Action<GameEntity, Vec3, Vec3, bool>));
                    }
                    catch
                    {
                        _instanceApplyWorld = null;
                    }
                }

                Type extensions = typeof(GameEntity).Assembly.GetType("TaleWorlds.Engine.GameEntityPhysicsExtensions");
                if (extensions != null)
                {
                    const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    _extensionApplyWorldMethod = extensions.GetMethod(
                        "ApplyImpulseToDynamicBody",
                        StaticFlags,
                        null,
                        new[] { typeof(GameEntity), typeof(Vec3), typeof(Vec3), typeof(bool) },
                        null);
                    if (_extensionApplyWorldMethod != null)
                    {
                        try
                        {
                            _extensionApplyWorld = (Action<GameEntity, Vec3, Vec3, bool>)_extensionApplyWorldMethod.CreateDelegate(
                                typeof(Action<GameEntity, Vec3, Vec3, bool>));
                        }
                        catch
                        {
                            _extensionApplyWorld = null;
                        }
                    }
                }

                Volatile.Write(ref _bound, 1);
            }
        }
    }
}
