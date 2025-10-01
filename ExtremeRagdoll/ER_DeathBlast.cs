using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;           // for GameEntity, Skeleton
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using System.Reflection;           // reflection fallback for impulse API
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExtremeRagdoll
{
    public sealed class ER_DeathBlastBehavior : MissionBehavior
    {
        // Cache possible impulse methods across TW versions
        private static MethodInfo _extEntImp3, _extEntImp2, _extEntImp1;
        private static int _extImpulseScannedInt;
        private static bool _deepFieldLog;
        private static bool _scanLogged;
        private static bool _extImpulseLogged;
        private static readonly object _fallbackWarnLock = new object();
        private static readonly HashSet<string> _fallbackWarnedSet = new HashSet<string>();
        private static readonly Queue<string> _fallbackWarnOrder = new Queue<string>();
        private const int FallbackWarnLimit = 32;
        private sealed class EntImpulseEntry
        {
            public MethodInfo M3;
            public MethodInfo M2;
            public MethodInfo M1;
            public Action<GameEntity, Vec3, Vec3, bool> D3;
            public Action<GameEntity, Vec3, Vec3> D2;
            public Action<GameEntity, Vec3> D1;
            public bool D3Failed;
            public bool D2Failed;
            public bool D1Failed;
        }

        private sealed class SkelImpulseEntry
        {
            public MethodInfo M2;
            public MethodInfo M1;
            public Action<Skeleton, Vec3, Vec3> D2;
            public Action<Skeleton, Vec3> D1;
            public bool D2Failed;
            public bool D1Failed;
        }

        private static readonly Dictionary<Type, EntImpulseEntry> _entImpCache = new Dictionary<Type, EntImpulseEntry>();
        private static readonly object _entImpLock = new object();
        private static readonly Dictionary<Type, SkelImpulseEntry> _skelImpCache = new Dictionary<Type, SkelImpulseEntry>();
        private static readonly object _skelImpLock = new object();
        // Caches for deep fallback
        private static readonly Dictionary<Type, (MethodInfo m2, MethodInfo m1)> _forceMethodCache = new Dictionary<Type, (MethodInfo m2, MethodInfo m1)>();
        private static readonly object _forceMethodLock = new object();
        private static readonly Dictionary<Type, MemberInfo[]> _physChildCache = new Dictionary<Type, MemberInfo[]>();
        private static readonly object _physChildLock = new object();
        private static readonly Dictionary<Type, Func<object, bool>> _ragdollStateCache = new Dictionary<Type, Func<object, bool>>();
        private static readonly object _ragdollStateLock = new object();
        private static readonly List<object> _childScratch = new List<object>(64);
        private static readonly List<object> _grandChildScratch = new List<object>(64);
        private static readonly Dictionary<Type, Func<GameEntity, string>> _entityIdAccessorCache = new Dictionary<Type, Func<GameEntity, string>>();
        private static readonly object _entityIdAccessorLock = new object();
        private static Action<GameEntity, Vec3, Vec3, bool> _extEntImp3Delegate;
        private static Action<GameEntity, Vec3, Vec3> _extEntImp2Delegate;
        private static Action<GameEntity, Vec3> _extEntImp1Delegate;
        private static readonly ConditionalWeakTable<GameEntity, object> _preparedEntities = new ConditionalWeakTable<GameEntity, object>();
        private static readonly object _preparedMarker = new object();
        private static Func<GameEntity, bool> _isDynamicBodyAccessor;
        private static bool _dynamicBodyChecked;
        private static float CorpseLaunchMaxUpFrac => ER_Config.CorpseLaunchMaxUpFraction;
        // circuit breakers for crashy extension routes
        private static volatile bool _extEnt1Unsafe, _extEnt2Unsafe, _extEnt3Unsafe;
        // tiny, cheap per-frame guard
        private float _lastTickT;

        private static bool LooksImpulseName(string n, bool allowVelocityFallback, out bool velocityOnly)
        {
            velocityOnly = false;
            if (string.IsNullOrEmpty(n))
                return false;
            n = n.ToLowerInvariant();
            bool primary = n.Contains("impulse") || n.Contains("force") || n.Contains("apply") ||
                           n.Contains("addforce") || n.Contains("addimpulse") || n.Contains("atposition") ||
                           n.Contains("atpos") || n.Contains("atpoint") || n.Contains("angular");
            if (primary)
                return true;
            if (!allowVelocityFallback)
                return false;
            if (n.Contains("velocity"))
            {
                velocityOnly = true;
                return true;
            }
            return false;
        }

        private static bool LooksPhysicsName(string s)
        {
            s = (s ?? string.Empty).ToLowerInvariant();
            return s.Contains("body") || s.Contains("phys") || s.Contains("rigid") ||
                   s.Contains("actor") || s.Contains("ragdoll") || s.Contains("collid") ||
                   s.Contains("capsule") || s.Contains("shape");
        }
        private static bool IsVec3Like(Type t) =>
            t == typeof(Vec3) || t == typeof(Vec3).MakeByRefType();

        private static bool MethodRequiresLocalSpace(MethodInfo method)
        {
            if (method == null)
                return false;
            var name = method.Name ?? string.Empty;
            return name.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryConvertWorldToLocal(GameEntity ent, Vec3 impulse, Vec3 pos,
            out Vec3 localImpulse, out Vec3 localPos)
        {
            localImpulse = impulse;
            localPos = pos;
            if (ent == null)
                return false;
            try
            {
                MatrixFrame frame;
                try
                {
                    frame = ent.GetGlobalFrame();
                }
                catch
                {
                    frame = ent.GetFrame();
                }
                localPos = frame.TransformToLocal(pos);
                var tipWorld = pos + impulse;
                var tipLocal = frame.TransformToLocal(tipWorld);
                localImpulse = tipLocal - localPos;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static ER_DeathBlastBehavior()
        {
            try { EnsureExtensionImpulseMethods(); }
            catch { }
        }

        internal static Vec3 ClampVertical(Vec3 dir)
        {
            if (dir.LengthSquared < 1e-6f)
                return dir;
            bool clamped = false;
            if (dir.z > CorpseLaunchMaxUpFrac)
            {
                dir.z = CorpseLaunchMaxUpFrac;
                clamped = true;
            }
            if (dir.z < 0f)
            {
                dir.z = 0f;
                clamped = true;
            }
            if (!clamped)
                return dir;

            float lenSq = dir.LengthSquared;
            if (lenSq < 1e-6f)
                return new Vec3(0f, 1f, 0f);

            return dir.NormalizedCopy();
        }

        internal static Vec3 PrepDir(Vec3 dir, float planarScale = 0.90f, float upBias = 0.10f)
        {
            if (!Vec3IsFinite(dir) || dir.LengthSquared < 1e-6f)
                dir = new Vec3(0f, 1f, 0f);
            else
                dir = dir.NormalizedCopy();

            var biased = dir * planarScale + new Vec3(0f, 0f, upBias);
            biased = ClampVertical(biased);

            float lenSq = biased.LengthSquared;
            if (lenSq < 1e-6f || float.IsNaN(lenSq) || float.IsInfinity(lenSq))
                return new Vec3(0f, 1f, 0f);

            return biased.NormalizedCopy();
        }

        private static void EnsureExtensionImpulseMethods()
        {
            if (Interlocked.Exchange(ref _extImpulseScannedInt, 1) == 1)
                return;

            Assembly[] assemblies;
            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                return;
            }

            foreach (var asm in assemblies)
            {
                if (asm == null || asm.IsDynamic)
                    continue;
                bool reflectionOnly = false;
                try { reflectionOnly = asm.ReflectionOnly; }
                catch { }
                if (reflectionOnly)
                    continue;
                string an = null;
                try { an = asm.GetName().Name; }
                catch { }
                if (an == null)
                    continue;
                if (an.IndexOf("TaleWorlds.Engine", StringComparison.OrdinalIgnoreCase) < 0 &&
                    an.IndexOf("TaleWorlds.Core", StringComparison.OrdinalIgnoreCase) < 0 &&
                    an.IndexOf("TaleWorlds.MountAndBlade", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle)
                {
                    types = rtle.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                    continue;

                foreach (var t in types)
                {
                    if (t == null)
                        continue;
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        if (m == null || !LooksImpulseName(m.Name, false, out _))
                            continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 0)
                            continue;
                        if (!typeof(GameEntity).IsAssignableFrom(ps[0].ParameterType))
                            continue;

                        if (ps.Length == 4 && IsVec3Like(ps[1].ParameterType) && IsVec3Like(ps[2].ParameterType) &&
                            ps[3].ParameterType == typeof(bool))
                        {
                            if (_extEntImp3 == null)
                                _extEntImp3 = m;
                            if (!ps[1].ParameterType.IsByRef && !ps[2].ParameterType.IsByRef)
                            {
                                if (_extEntImp3Delegate == null)
                                    _extEntImp3Delegate = TryCreateExtDelegate<Action<GameEntity, Vec3, Vec3, bool>>(m);
                            }
                        }
                        else if (ps.Length == 3 && IsVec3Like(ps[1].ParameterType) && IsVec3Like(ps[2].ParameterType))
                        {
                            if (_extEntImp2 == null)
                                _extEntImp2 = m;
                            if (!ps[1].ParameterType.IsByRef && !ps[2].ParameterType.IsByRef)
                            {
                                if (_extEntImp2Delegate == null)
                                    _extEntImp2Delegate = TryCreateExtDelegate<Action<GameEntity, Vec3, Vec3>>(m);
                            }
                        }
                        else if (ps.Length == 2 && IsVec3Like(ps[1].ParameterType))
                        {
                            if (_extEntImp1 == null)
                                _extEntImp1 = m;
                            if (!ps[1].ParameterType.IsByRef)
                            {
                                if (_extEntImp1Delegate == null)
                                    _extEntImp1Delegate = TryCreateExtDelegate<Action<GameEntity, Vec3>>(m);
                            }
                        }

                        if (_extEntImp3 != null && _extEntImp2 != null && _extEntImp1 != null)
                            break;
                    }

                    if (_extEntImp3 != null && _extEntImp2 != null && _extEntImp1 != null)
                        break;
                }

                if (_extEntImp3 != null && _extEntImp2 != null && _extEntImp1 != null)
                    break;
            }

            if (ER_Config.DebugLogging && !Volatile.Read(ref _extImpulseLogged))
            {
                Volatile.Write(ref _extImpulseLogged, true);
                void Log(MethodInfo mi, string label)
                {
                    if (mi == null) return;
                    var ps = mi.GetParameters();
                    ER_Log.Info($"IMPULSE_EXT {label} {mi.DeclaringType?.FullName}::{mi.Name}({string.Join(",", ps.Select(p => p.ParameterType.Name))})");
                }

                Log(_extEntImp3, "ent3");
                Log(_extEntImp2, "ent2");
                Log(_extEntImp1, "ent1");
            }
        }

        private static TDelegate TryCreateExtDelegate<TDelegate>(MethodInfo method) where TDelegate : class
        {
            if (method == null || !method.IsStatic)
                return null;
            var expected = typeof(TDelegate);
            var invoke = expected.GetMethod("Invoke");
            var parameters = method.GetParameters();
            var invokeParameters = invoke?.GetParameters();
            if (invokeParameters == null)
                return null;
            if (parameters.Length != invokeParameters.Length)
                return null;
            // Ensure first parameter types match expected GameEntity usage
            if (!typeof(GameEntity).IsAssignableFrom(invokeParameters[0].ParameterType))
                return null;
            if (parameters.Length == 0 || parameters[0].ParameterType.IsByRef)
                return null;
            if (!typeof(GameEntity).IsAssignableFrom(parameters[0].ParameterType))
                return null;
            if (parameters.Skip(1).Any(p => p.ParameterType.IsByRef))
                return null;
            if (invokeParameters.Skip(1).Select(p => p.ParameterType)
                .SequenceEqual(parameters.Skip(1).Select(p => p.ParameterType)))
            {
                try
                {
                    return method.CreateDelegate(expected) as TDelegate;
                }
                catch { }
            }
            return null;
        }

        private static TDelegate TryCreateInstanceDelegate<TDelegate>(MethodInfo method) where TDelegate : class
        {
            if (method == null || method.IsStatic)
                return null;
            try
            {
                return method.CreateDelegate(typeof(TDelegate)) as TDelegate;
            }
            catch
            {
                return null;
            }
        }

        private static bool Vec3IsFinite(Vec3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
            !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);

        private static bool NearZero(Vec3 v)
        {
            float s = v.x * v.x + v.y * v.y + v.z * v.z;
            return s < 1e-8f || float.IsNaN(s) || float.IsInfinity(s);
        }

        private static void MarkRagdollPrepared(GameEntity ent)
        {
            if (ent == null)
                return;
            try
            {
                _preparedEntities.Add(ent, _preparedMarker);
            }
            catch (ArgumentException)
            {
                // already tracked
            }
        }

        private static bool WasRagdollPrepared(GameEntity ent)
        {
            if (ent == null)
                return false;
            return _preparedEntities.TryGetValue(ent, out _);
        }

        private static bool EntIsDynamic(GameEntity ent)
        {
            if (ent == null)
                return false;
            if (!_dynamicBodyChecked)
            {
                _dynamicBodyChecked = true;
                try
                {
                    var method = typeof(GameEntity).GetMethod("IsDynamicBody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (method != null)
                        _isDynamicBodyAccessor = TryCreateInstanceDelegate<Func<GameEntity, bool>>(method);
                }
                catch
                {
                    _isDynamicBodyAccessor = null;
                }
            }
            if (_isDynamicBodyAccessor == null)
                return false;
            try
            {
                return _isDynamicBodyAccessor(ent);
            }
            catch
            {
                return false;
            }
        }

        private static EntImpulseEntry GetEntImpulseEntry(Type type)
        {
            if (type == null)
                return null;
            if (_entImpCache.TryGetValue(type, out var entry))
                return entry;

            lock (_entImpLock)
            {
                if (_entImpCache.TryGetValue(type, out entry))
                    return entry;

                entry = ResolveEntImpulseMethods(type);
                _entImpCache[type] = entry;
                return entry;
            }
        }

        private static SkelImpulseEntry GetSkelImpulseEntry(Type type)
        {
            if (type == null)
                return null;
            if (_skelImpCache.TryGetValue(type, out var entry))
                return entry;

            lock (_skelImpLock)
            {
                if (_skelImpCache.TryGetValue(type, out entry))
                    return entry;

                entry = ResolveSkelImpulseMethods(type);
                _skelImpCache[type] = entry;
                return entry;
            }
        }

        private static void CaptureEntImpulseMethods(Type type, EntImpulseEntry entry)
        {
            if (type == null || entry == null)
                return;
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return;
            }
            foreach (var m in methods)
            {
                if (m == null)
                    continue;
                if (!LooksImpulseName(m.Name, allowVelocityFallback: false, out _))
                    continue;
                var ps = m.GetParameters();
                if (entry.M3 == null && ps.Length == 3 && IsVec3Like(ps[0].ParameterType) && IsVec3Like(ps[1].ParameterType) && ps[2].ParameterType == typeof(bool))
                {
                    entry.M3 = m;
                    continue;
                }
                if (entry.M2 == null && ps.Length == 2 && IsVec3Like(ps[0].ParameterType) && IsVec3Like(ps[1].ParameterType))
                {
                    entry.M2 = m;
                    continue;
                }
                if (entry.M1 == null && ps.Length == 1 && IsVec3Like(ps[0].ParameterType))
                {
                    entry.M1 = m;
                }
                if (entry.M3 != null && entry.M2 != null && entry.M1 != null)
                    return;
            }
        }

        private static EntImpulseEntry ResolveEntImpulseMethods(Type type)
        {
            var entry = new EntImpulseEntry();
            for (var t = type; t != null && typeof(GameEntity).IsAssignableFrom(t); t = t.BaseType)
            {
                CaptureEntImpulseMethods(t, entry);
                if (entry.M3 != null && entry.M2 != null && entry.M1 != null)
                    break;
            }
            if (entry.M3 == null || entry.M2 == null || entry.M1 == null)
            {
                CaptureEntImpulseMethods(typeof(GameEntity), entry);
            }
            if (entry.M3 == null)
            {
                try
                {
                    entry.M3 = typeof(GameEntity).GetMethod("ApplyImpulseToDynamicBody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3), typeof(bool) }, null);
                }
                catch
                {
                    entry.M3 = null;
                }
            }
            return entry;
        }

        private static void CaptureSkelImpulseMethods(Type type, SkelImpulseEntry entry)
        {
            if (type == null || entry == null)
                return;
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                return;
            }
            foreach (var m in methods)
            {
                if (m == null)
                    continue;
                if (!LooksImpulseName(m.Name, allowVelocityFallback: false, out _))
                    continue;
                var ps = m.GetParameters();
                if (entry.M2 == null && ps.Length == 2 && IsVec3Like(ps[0].ParameterType) && IsVec3Like(ps[1].ParameterType))
                {
                    entry.M2 = m;
                    continue;
                }
                if (entry.M1 == null && ps.Length == 1 && IsVec3Like(ps[0].ParameterType))
                {
                    entry.M1 = m;
                }
                if (entry.M2 != null && entry.M1 != null)
                    return;
            }
        }

        private static SkelImpulseEntry ResolveSkelImpulseMethods(Type type)
        {
            var entry = new SkelImpulseEntry();
            for (var t = type; t != null && typeof(Skeleton).IsAssignableFrom(t); t = t.BaseType)
            {
                CaptureSkelImpulseMethods(t, entry);
                if (entry.M2 != null && entry.M1 != null)
                    break;
            }
            if (entry.M2 == null || entry.M1 == null)
            {
                CaptureSkelImpulseMethods(typeof(Skeleton), entry);
            }
            return entry;
        }

        private static Func<GameEntity, string> CreateEntityIdAccessor(Type type)
        {
            if (type == null)
                return _ => null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            PropertyInfo prop = null;
            try { prop = type.GetProperty("Id", flags); }
            catch { prop = null; }
            if (prop == null || !prop.CanRead)
            {
                try { prop = type.GetProperty("StringId", flags) ?? type.GetProperty("Name", flags); }
                catch { prop = null; }
            }
            if (prop != null && prop.CanRead)
            {
                return ent =>
                {
                    if (ent == null)
                        return null;
                    try
                    {
                        var value = prop.GetValue(ent, null);
                        return value?.ToString();
                    }
                    catch
                    {
                        return null;
                    }
                };
            }

            FieldInfo field = null;
            try { field = type.GetField("Id", flags); }
            catch { field = null; }
            if (field != null)
            {
                return ent =>
                {
                    if (ent == null)
                        return null;
                    try
                    {
                        var value = field.GetValue(ent);
                        return value?.ToString();
                    }
                    catch
                    {
                        return null;
                    }
                };
            }

            MethodInfo getName = null;
            try { getName = type.GetMethod("GetName", flags, null, Type.EmptyTypes, null); }
            catch { getName = null; }
            if (getName != null)
            {
                return ent =>
                {
                    if (ent == null)
                        return null;
                    try
                    {
                        var value = getName.Invoke(ent, Array.Empty<object>());
                        return value?.ToString();
                    }
                    catch
                    {
                        return null;
                    }
                };
            }

            return _ => null;
        }

        private static string TryGetGameEntityId(GameEntity ent)
        {
            if (ent == null)
                return null;
            var type = ent.GetType();
            if (type == null)
                return null;
            if (!_entityIdAccessorCache.TryGetValue(type, out var accessor))
            {
                lock (_entityIdAccessorLock)
                {
                    if (!_entityIdAccessorCache.TryGetValue(type, out accessor))
                    {
                        accessor = CreateEntityIdAccessor(type);
                        _entityIdAccessorCache[type] = accessor;
                    }
                }
            }
            if (accessor == null)
                return null;
            try
            {
                return accessor(ent);
            }
            catch
            {
                return null;
            }
        }

        private static bool ShouldLogFallback(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            lock (_fallbackWarnLock)
            {
                if (_fallbackWarnedSet.Contains(key))
                    return false;
                _fallbackWarnedSet.Add(key);
                _fallbackWarnOrder.Enqueue(key);
                while (_fallbackWarnOrder.Count > FallbackWarnLimit)
                {
                    var removed = _fallbackWarnOrder.Dequeue();
                    _fallbackWarnedSet.Remove(removed);
                }
                return true;
            }
        }

        private static string BuildFallbackContextId(int agentId, GameEntity ent, Skeleton skel)
        {
            if (agentId >= 0)
                return $"agent#{agentId}";
            if (ent != null)
            {
                var stable = TryGetGameEntityId(ent);
                if (!string.IsNullOrEmpty(stable))
                    return $"ent#{stable}";
                return $"ent#{ent.GetHashCode():X}";
            }
            if (skel != null)
                return $"skel#{skel.GetHashCode():X}";
            return "agent#?";
        }

        private static bool EntityLooksDynamic(GameEntity ent)
        {
            if (ent == null)
                return false;
            try
            {
                var bf = ent.BodyFlag.ToString();
                if (!string.IsNullOrEmpty(bf) && (bf.Contains("Dynamic") || bf.Contains("DynamicBody")))
                    return true;
            }
            catch
            {
            }
            try
            {
                var pdf = ent.PhysicsDescBodyFlag.ToString();
                if (!string.IsNullOrEmpty(pdf) && pdf.Contains("Dynamic"))
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private static bool EntityAabbSane(GameEntity ent)
        {
            if (ent == null)
                return false;
            try
            {
                var mn = ent.GetPhysicsBoundingBoxMin();
                var mx = ent.GetPhysicsBoundingBoxMax();
                if (!Vec3IsFinite(mn) || !Vec3IsFinite(mx))
                    return false;
                // reject wild/zero volumes
                if (MathF.Abs(mx.x) > 1e5f || MathF.Abs(mx.y) > 1e5f || MathF.Abs(mx.z) > 1e5f)
                    return false;
                if (mx.x <= mn.x || mx.y <= mn.y || mx.z <= mn.z)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CanUseEntityExt(GameEntity ent)
            => ent != null && EntityLooksDynamic(ent) && EntityAabbSane(ent);

        private static void MarkExtUnsafe(int which, Exception ex)
        {
            if (ex is AccessViolationException)
            {
                if (which == 1)
                    _extEnt1Unsafe = true;
                else if (which == 2)
                    _extEnt2Unsafe = true;
                else if (which == 3)
                    _extEnt3Unsafe = true;
                ER_Log.Info($"IMPULSE_DISABLE ext ent{which} after {ex.GetType().Name}");
            }
        }

        private static void DumpPhysicsMembers(GameEntity ent, string tag)
        {
            if (ent == null) return;
            try { var v = ent.BodyFlag; ER_Log.Info($"IMPULSE_VAL {tag}.BodyFlag={v}"); } catch { }
            try { var v = ent.PhysicsDescBodyFlag; ER_Log.Info($"IMPULSE_VAL {tag}.PhysicsDescBodyFlag={v}"); } catch { }
            try
            {
                var mn = ent.GetPhysicsBoundingBoxMin();
                var mx = ent.GetPhysicsBoundingBoxMax();
                ER_Log.Info($"IMPULSE_VAL {tag}.AABB=({mn.x:F3},{mn.y:F3},{mn.z:F3})..({mx.x:F3},{mx.y:F3},{mx.z:F3})");
            }
            catch { }
            try
            {
                var f = ent.GetGlobalFrame();
                ER_Log.Info($"IMPULSE_VAL {tag}.Pos=({f.origin.x:F3},{f.origin.y:F3},{f.origin.z:F3})");
            }
            catch { }
        }

        private static void DumpPhysicsMembers(Skeleton skel, string tag)
        {
            if (skel == null) return;
            try { var s = skel.GetCurrentRagdollState(); ER_Log.Info($"IMPULSE_VAL {tag}.RagdollState={s}"); } catch { }
        }

        private static bool TryApplyImpulse(GameEntity ent, Skeleton skel, Vec3 impulse, Vec3 pos, int agentId = -1)
        {
            if (!Vec3IsFinite(impulse) || !Vec3IsFinite(pos) || NearZero(impulse))
                return false;
            bool ok = false;
            string fallbackContextId = BuildFallbackContextId(agentId, ent, skel);
            if (!_scanLogged && ER_Config.DebugLogging)
            {
                _scanLogged = true;
                void Dump(string who, Type t)
                {
                    if (t == null) return;
                    foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var n = m.Name;
                        if (n.IndexOf("Impulse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Velocity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Ragdoll", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var ps = m.GetParameters();
                            ER_Log.Info($"IMPULSE_SCAN {who}.{t.FullName}::{n}({string.Join(",", ps.Select(p => p.ParameterType.Name))})");
                        }
                    }
                }
                Dump("ent", ent?.GetType());
                Dump("GameEntity", typeof(GameEntity));
                Dump("skel", skel?.GetType());
            }

            bool prepared = false;
            void EnsureRagdollReady()
            {
                if (prepared || ok)
                    return;
                if (ent == null && skel == null)
                {
                    prepared = true;
                    return;
                }
                if (ent != null && WasRagdollPrepared(ent))
                {
                    prepared = true;
                    return;
                }
                if (skel != null && SkeletonAlreadyRagdolled(skel))
                {
                    prepared = true;
                    MarkRagdollPrepared(ent);
                    return;
                }
                if (EntIsDynamic(ent))
                {
                    prepared = true;
                    MarkRagdollPrepared(ent);
                    return;
                }
                prepared = true;
                try { ent?.ActivateRagdoll(); } catch { }
                try { skel?.ActivateRagdoll(); } catch { }
                try { skel?.ForceUpdateBoneFrames(); } catch { }
                try
                {
                    MatrixFrame f;
                    try { f = ent?.GetGlobalFrame() ?? default; }
                    catch { f = default; }
                    skel?.TickAnimationsAndForceUpdate(0.001f, f, true);
                }
                catch { }
                MarkRagdollPrepared(ent);
            }

            // --- GameEntity route ---
            if (ent != null)
            {
                EnsureRagdollReady();
                var entEntry = GetEntImpulseEntry(ent.GetType());
                if (entEntry != null)
                {
                    try
                    {
                        if (entEntry.M3 != null)
                        {
                            var ps = entEntry.M3.GetParameters();
                            if (entEntry.D3 == null && !entEntry.D3Failed && ps.Length == 3 && !ps[0].ParameterType.IsByRef && !ps[1].ParameterType.IsByRef)
                            {
                                var del = TryCreateInstanceDelegate<Action<GameEntity, Vec3, Vec3, bool>>(entEntry.M3);
                                if (del != null) entEntry.D3 = del; else entEntry.D3Failed = true;
                            }
                            try
                            {
                                if (entEntry.D3 != null)
                                    entEntry.D3(ent, impulse, pos, false);
                                else
                                    entEntry.M3.Invoke(ent, new object[] { impulse, pos, false });
                                if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ent3(false)");
                                ok = true;
                            }
                            catch
                            {
                                try
                                {
                                    if (entEntry.D3 != null)
                                        entEntry.D3(ent, impulse, pos, true);
                                    else
                                        entEntry.M3.Invoke(ent, new object[] { impulse, pos, true });
                                    if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ent3(true)");
                                    ok = true;
                                }
                                catch
                                {
                                    // ignore and allow fallthrough
                                }
                            }
                        }
                        if (!ok && entEntry.M2 != null)
                        {
                            var ps = entEntry.M2.GetParameters();
                            if (entEntry.D2 == null && !entEntry.D2Failed && ps.Length == 2 && !ps[0].ParameterType.IsByRef && !ps[1].ParameterType.IsByRef)
                            {
                                var del = TryCreateInstanceDelegate<Action<GameEntity, Vec3, Vec3>>(entEntry.M2);
                                if (del != null) entEntry.D2 = del; else entEntry.D2Failed = true;
                            }
                            Vec3 callImpulse = impulse;
                            Vec3 callPos = pos;
                            if (MethodRequiresLocalSpace(entEntry.M2))
                                TryConvertWorldToLocal(ent, impulse, pos, out callImpulse, out callPos);
                            if (entEntry.D2 != null)
                                entEntry.D2(ent, callImpulse, callPos);
                            else
                                entEntry.M2.Invoke(ent, new object[] { callImpulse, callPos });
                            if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ent2");
                            ok = true;
                        }
                        if (!ok && entEntry.M1 != null)
                        {
                            var ps = entEntry.M1.GetParameters();
                            if (entEntry.D1 == null && !entEntry.D1Failed && ps.Length == 1 && !ps[0].ParameterType.IsByRef)
                            {
                                var del = TryCreateInstanceDelegate<Action<GameEntity, Vec3>>(entEntry.M1);
                                if (del != null) entEntry.D1 = del; else entEntry.D1Failed = true;
                            }
                            if (entEntry.D1 != null)
                                entEntry.D1(ent, impulse);
                            else
                                entEntry.M1.Invoke(ent, new object[] { impulse });
                            if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ent1");
                            ok = true;
                        }
                    }
                    catch
                    {
                        // keep ok as-is
                    }
                }
            }
            if (!ok && ent != null)
            {
                EnsureRagdollReady();
                EnsureExtensionImpulseMethods();
                bool canUse = CanUseEntityExt(ent);
                if (_extEntImp3 != null && !ok)
                {
                    try
                    {
                        if (!_extEnt3Unsafe && canUse)
                        {
                            bool local = MethodRequiresLocalSpace(_extEntImp3);
                            var vImp = impulse; var vPos = pos;
                            if (!local || TryConvertWorldToLocal(ent, impulse, pos, out vImp, out vPos))
                            {
                                if (_extEntImp3Delegate != null) _extEntImp3Delegate(ent, vImp, vPos, local);
                                else _extEntImp3.Invoke(null, new object[] { ent, vImp, vPos, local });
                                ok = true;
                                if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent3 ApplyImpulseToDynamicBody(entity,Vec3,Vec3)");
                            }
                            else
                            {
                                // ext ent3 fallback to world-space
                                if (ER_Config.DebugLogging)
                                    ER_Log.Info("IMPULSE_SKIP ext ent3: local convert failed");
                                try
                                {
                                    if (_extEntImp3Delegate != null) _extEntImp3Delegate(ent, impulse, pos, false);
                                    else _extEntImp3.Invoke(null, new object[] { ent, impulse, pos, false });
                                    ok = true;
                                    if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent3 (world-space fallback)");
                                }
                                catch (Exception ex)
                                {
                                    if (ER_Config.DebugLogging) ER_Log.Info($"IMPULSE_FAIL ext ent3 world: {ex.GetType().Name}: {ex.Message}");
                                    MarkExtUnsafe(3, ex);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ER_Config.DebugLogging) ER_Log.Info($"IMPULSE_FAIL ext ent3: {ex.GetType().Name}: {ex.Message}");
                        MarkExtUnsafe(3, ex);
                    }
                }
                if (_extEntImp2 != null && !ok)
                {
                    try
                    {
                        if (!_extEnt2Unsafe && canUse)
                        {
                            bool local = MethodRequiresLocalSpace(_extEntImp2);
                            var vImp = impulse; var vPos = pos;
                            if (!local || TryConvertWorldToLocal(ent, impulse, pos, out vImp, out vPos))
                            {
                                if (_extEntImp2Delegate != null) _extEntImp2Delegate(ent, vImp, vPos);
                                else _extEntImp2.Invoke(null, new object[] { ent, vImp, vPos });
                                ok = true;
                                if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent2 ApplyLocalImpulseToDynamicBody(entity,Vec3,Vec3)");
                            }
                            else
                            {
                                // ext ent2 fallback to world-space
                                if (ER_Config.DebugLogging)
                                    ER_Log.Info("IMPULSE_SKIP ext ent2: local convert failed");
                                try
                                {
                                    if (_extEntImp2Delegate != null) _extEntImp2Delegate(ent, impulse, pos);
                                    else _extEntImp2.Invoke(null, new object[] { ent, impulse, pos });
                                    ok = true;
                                    if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent2 (world-space fallback)");
                                }
                                catch (Exception ex)
                                {
                                    if (ER_Config.DebugLogging) ER_Log.Info($"IMPULSE_FAIL ext ent2 world: {ex.GetType().Name}: {ex.Message}");
                                    MarkExtUnsafe(2, ex);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ER_Config.DebugLogging) ER_Log.Info($"IMPULSE_FAIL ext ent2: {ex.GetType().Name}: {ex.Message}");
                        MarkExtUnsafe(2, ex);
                    }
                }
                if (_extEntImp1 != null && !ok)
                {
                    try
                    {
                        if (!_extEnt1Unsafe && canUse)
                        {
                            if (_extEntImp1Delegate != null) _extEntImp1Delegate(ent, impulse);
                            else _extEntImp1.Invoke(null, new object[] { ent, impulse });
                            ok = true;
                            if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent1 ApplyForceToDynamicBody(entity,Vec3)");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ER_Config.DebugLogging) ER_Log.Info($"IMPULSE_FAIL ext ent1: {ex.GetType().Name}: {ex.Message}");
                        MarkExtUnsafe(1, ex);
                    }
                }
            }
            // --- Skeleton route (ragdoll bones) ---
            if (!ok && skel != null)
            {
                EnsureRagdollReady();
                var skelEntry = GetSkelImpulseEntry(skel.GetType());
                if (skelEntry != null)
                {
                    try
                    {
                        if (skelEntry.M2 != null)
                        {
                            var ps = skelEntry.M2.GetParameters();
                            if (skelEntry.D2 == null && !skelEntry.D2Failed && ps.Length == 2 && !ps[0].ParameterType.IsByRef && !ps[1].ParameterType.IsByRef)
                            {
                                var del = TryCreateInstanceDelegate<Action<Skeleton, Vec3, Vec3>>(skelEntry.M2);
                                if (del != null) skelEntry.D2 = del; else skelEntry.D2Failed = true;
                            }
                            if (skelEntry.D2 != null)
                                skelEntry.D2(skel, impulse, pos);
                            else
                                skelEntry.M2.Invoke(skel, new object[] { impulse, pos });
                            if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE skel2");
                            ok = true;
                        }
                        if (!ok && skelEntry.M1 != null)
                        {
                            var ps = skelEntry.M1.GetParameters();
                            if (skelEntry.D1 == null && !skelEntry.D1Failed && ps.Length == 1 && !ps[0].ParameterType.IsByRef)
                            {
                                var del = TryCreateInstanceDelegate<Action<Skeleton, Vec3>>(skelEntry.M1);
                                if (del != null) skelEntry.D1 = del; else skelEntry.D1Failed = true;
                            }
                            if (skelEntry.D1 != null)
                                skelEntry.D1(skel, impulse);
                            else
                                skelEntry.M1.Invoke(skel, new object[] { impulse });
                            if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE skel1");
                            ok = true;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            // ---- DEEP FALLBACK: hunt physics-y subobjects and call any force-like API ----
            if (!ok)
            {
                EnsureRagdollReady();
                try { skel?.ForceUpdateBoneFrames(); } catch { }

                (MethodInfo m2, MethodInfo m1) BuildForceMethodPair(Type type)
                {
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    MethodInfo[] methods;
                    try { methods = type.GetMethods(flags); }
                    catch { methods = Array.Empty<MethodInfo>(); }
                    MethodInfo m2 = null, m1 = null;
                    MethodInfo m2Velocity = null, m1Velocity = null;
                    foreach (var m in methods)
                    {
                        if (m == null)
                            continue;
                        var ps = m.GetParameters();
                        if (ps.Any(p => p.ParameterType.IsByRef))
                            continue;
                        bool velocityOnly;
                        if (!LooksImpulseName(m.Name, allowVelocityFallback: true, out velocityOnly))
                            continue;
                        if (ps.Length == 2 && IsVec3Like(ps[0].ParameterType) && IsVec3Like(ps[1].ParameterType))
                        {
                            if (velocityOnly)
                            {
                                if (m2Velocity == null)
                                    m2Velocity = m;
                            }
                            else if (m2 == null)
                                m2 = m;
                        }
                        else if (ps.Length == 1 && IsVec3Like(ps[0].ParameterType))
                        {
                            if (velocityOnly)
                            {
                                if (m1Velocity == null)
                                    m1Velocity = m;
                            }
                            else if (m1 == null)
                                m1 = m;
                        }
                    }
                    if (m2 == null) m2 = m2Velocity;
                    if (m1 == null) m1 = m1Velocity;
                    return (m2, m1);
                }

                bool TryInvokeForceLike(object target, Vec3 imp, Vec3 atPos, string tag)
                {
                    if (target == null)
                        return false;
                    var t = target.GetType();
                    if (t == null)
                        return false;
                    if (!_forceMethodCache.TryGetValue(t, out var pair))
                    {
                        pair = BuildForceMethodPair(t);
                        lock (_forceMethodLock)
                        {
                            if (!_forceMethodCache.TryGetValue(t, out var existing))
                            {
                                _forceMethodCache[t] = pair;
                            }
                            else
                            {
                                pair = existing;
                            }
                        }
                    }
                    var typeName = t?.FullName ?? t?.Name ?? "<unknown>";
                    try
                    {
                        if (pair.m2 != null)
                        {
                            pair.m2.Invoke(target, new object[] { imp, atPos });
                            int token = 0;
                            try { token = pair.m2.MetadataToken; }
                            catch { token = 0; }
                            var warnKey = $"{fallbackContextId}:{tag}:{typeName}:{token}";
                            if (ShouldLogFallback(warnKey))
                                ER_Log.Info($"IMPULSE_FALLBACK_USED {fallbackContextId} {tag} {typeName}::{pair.m2.Name}");
                            if (ER_Config.DebugLogging)
                                ER_Log.Info($"IMPULSE_USE {tag}.m2 {typeName}::{pair.m2.Name}");
                            return true;
                        }
                        if (pair.m1 != null)
                        {
                            pair.m1.Invoke(target, new object[] { imp });
                            int token = 0;
                            try { token = pair.m1.MetadataToken; }
                            catch { token = 0; }
                            var warnKey = $"{fallbackContextId}:{tag}:{typeName}:{token}";
                            if (ShouldLogFallback(warnKey))
                                ER_Log.Info($"IMPULSE_FALLBACK_USED {fallbackContextId} {tag} {typeName}::{pair.m1.Name}");
                            if (ER_Config.DebugLogging)
                                ER_Log.Info($"IMPULSE_USE {tag}.m1 {typeName}::{pair.m1.Name}");
                            return true;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                    return false;
                }

                MemberInfo[] GetPhysicsMembers(Type type)
                {
                    if (type == null)
                        return Array.Empty<MemberInfo>();
                    if (_physChildCache.TryGetValue(type, out var cached))
                        return cached;

                    var list = new List<MemberInfo>();
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                    MemberInfo[] CacheResult()
                    {
                        var result = list.ToArray();
                        lock (_physChildLock)
                        {
                            if (!_physChildCache.TryGetValue(type, out var existing))
                            {
                                _physChildCache[type] = result;
                                return result;
                            }
                            return existing;
                        }
                    }

                    FieldInfo[] fields;
                    try { fields = type.GetFields(flags); }
                    catch { fields = Array.Empty<FieldInfo>(); }
                    foreach (var f in fields)
                    {
                        if (f == null)
                            continue;
                        if (!LooksPhysicsName(f.Name) && !LooksPhysicsName(f.FieldType?.Name))
                            continue;
                        list.Add(f);
                        if (list.Count >= 64)
                            return CacheResult();
                    }
                    if (list.Count < 64)
                    {
                        PropertyInfo[] props;
                        try { props = type.GetProperties(flags); }
                        catch { props = Array.Empty<PropertyInfo>(); }
                        foreach (var p in props)
                        {
                            if (p == null || !p.CanRead)
                                continue;
                            if (!LooksPhysicsName(p.Name) && !LooksPhysicsName(p.PropertyType?.Name))
                                continue;
                            list.Add(p);
                            if (list.Count >= 64)
                                return CacheResult();
                        }
                    }
                    if (list.Count < 64)
                    {
                        MethodInfo[] methods;
                        try { methods = type.GetMethods(flags); }
                        catch { methods = Array.Empty<MethodInfo>(); }
                        foreach (var m in methods)
                        {
                            if (m == null || m.IsStatic)
                                continue;
                            if (m.GetParameters().Length != 0)
                                continue;
                            var name = m.Name ?? string.Empty;
                            if (!name.StartsWith("get", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!LooksPhysicsName(name) && !LooksPhysicsName(m.ReturnType?.Name))
                                continue;
                            list.Add(m);
                            if (list.Count >= 64)
                                return CacheResult();
                        }
                    }

                    return CacheResult();
                }

                int CollectPhysicsChildren(object owner, List<object> buffer)
                {
                    buffer.Clear();
                    if (owner == null)
                        return 0;
                    var type = owner.GetType();
                    if (type == null)
                        return 0;
                    var members = GetPhysicsMembers(type);
                    if (members == null || members.Length == 0)
                        return 0;
                    foreach (var member in members)
                    {
                        if (member == null)
                            continue;
                        object val = null;
                        try
                        {
                            switch (member)
                            {
                                case FieldInfo field:
                                    val = field.GetValue(owner);
                                    break;
                                case PropertyInfo prop:
                                    val = prop.GetValue(owner, null);
                                    break;
                                case MethodInfo method:
                                    val = method.Invoke(owner, Array.Empty<object>());
                                    break;
                            }
                        }
                        catch
                        {
                            val = null;
                        }
                        if (val != null)
                        {
                            buffer.Add(val);
                            if (buffer.Count >= 64)
                                break;
                        }
                    }
                    return buffer.Count;
                }

                bool TryDescend(object owner, string tag)
                {
                    if (owner == null)
                        return false;
                    if (TryInvokeForceLike(owner, impulse, pos, tag))
                        return true;

                    lock (_childScratch)
                    {
                        var childCount = CollectPhysicsChildren(owner, _childScratch);
                        for (int i = 0; i < childCount; i++)
                        {
                            var child = _childScratch[i];
                            var childName = child?.GetType()?.Name ?? "?";
                            if (TryInvokeForceLike(child, impulse, pos, $"{tag}->{childName}"))
                            {
                                _childScratch.Clear();
                                return true;
                            }
                        }
                        if (childCount > 12)
                        {
                            _childScratch.Clear();
                            return false;
                        }
                        lock (_grandChildScratch)
                        {
                            for (int i = 0; i < childCount; i++)
                            {
                                var child = _childScratch[i];
                                var childName = child?.GetType()?.Name ?? "?";
                                var grandCount = CollectPhysicsChildren(child, _grandChildScratch);
                                for (int g = 0; g < grandCount; g++)
                                {
                                    var grand = _grandChildScratch[g];
                                    var grandName = grand?.GetType()?.Name ?? "?";
                                    if (TryInvokeForceLike(grand, impulse, pos, $"{tag}->{childName}->{grandName}"))
                                    {
                                        _grandChildScratch.Clear();
                                        _childScratch.Clear();
                                        return true;
                                    }
                                }
                                _grandChildScratch.Clear();
                            }
                        }
                        _childScratch.Clear();
                    }
                    return false;
                }

                if (!ok)
                    ok = TryDescend(ent, "ent");
                if (!ok)
                    ok = TryDescend(skel, "skel");

                if (!ok && ER_Config.DebugLogging && !_deepFieldLog)
                {
                    _deepFieldLog = true;
                    void DumpPhysicsMemberListing(object owner, string tag)
                    {
                        if (owner == null)
                            return;
                        var type = owner.GetType();
                        if (type == null)
                            return;
                        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        FieldInfo[] fields;
                        try { fields = type.GetFields(flags); }
                        catch { fields = Array.Empty<FieldInfo>(); }
                        foreach (var f in fields)
                        {
                            if (f == null)
                                continue;
                            if (!LooksPhysicsName(f.Name) && !LooksPhysicsName(f.FieldType?.Name))
                                continue;
                            var typeName = type.FullName ?? type.Name ?? "<unknown>";
                            ER_Log.Info($"IMPULSE_SCAN_FIELD {tag}.{typeName}::{f.Name}:{f.FieldType?.FullName}");
                        }

                        PropertyInfo[] props;
                        try { props = type.GetProperties(flags); }
                        catch { props = Array.Empty<PropertyInfo>(); }
                        foreach (var p in props)
                        {
                            if (p == null || !p.CanRead)
                                continue;
                            if (!LooksPhysicsName(p.Name) && !LooksPhysicsName(p.PropertyType?.Name))
                                continue;
                            var typeName = type.FullName ?? type.Name ?? "<unknown>";
                            ER_Log.Info($"IMPULSE_SCAN_PROP {tag}.{typeName}::{p.Name}:{p.PropertyType?.FullName}");
                        }

                        MethodInfo[] methods;
                        try { methods = type.GetMethods(flags); }
                        catch { methods = Array.Empty<MethodInfo>(); }
                        foreach (var m in methods)
                        {
                            if (m == null || m.IsStatic)
                                continue;
                            if (m.GetParameters().Length != 0)
                                continue;
                            var name = m.Name ?? string.Empty;
                            if (!name.StartsWith("get", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!LooksPhysicsName(name) && !LooksPhysicsName(m.ReturnType?.Name))
                                continue;
                            var typeName = type.FullName ?? type.Name ?? "<unknown>";
                            ER_Log.Info($"IMPULSE_SCAN_METHOD {tag}.{typeName}::{name}:{m.ReturnType?.FullName}");
                        }
                    }

                    DumpPhysicsMemberListing(ent, "ent");
                    DumpPhysicsMemberListing(skel, "skel");
                    DumpPhysicsMembers(ent, "ent");
                    DumpPhysicsMembers(skel, "skel");
                }
            }
            if (!ok && ER_Config.DebugLogging)
                ER_Log.Info("IMPULSE_END: no entity/skeleton path succeeded");
            return ok;
        }

        private static Func<object, bool> CreateRagdollStateEvaluator(Type type)
        {
            if (type == null)
                return _ => false;
            MethodInfo getter = null;
            try
            {
                getter = type.GetMethod("GetCurrentRagdollState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            }
            catch { }
            if (getter == null)
                return _ => false;

            object activeValue = null;
            bool activeValueResolved = false;
            return target =>
            {
                try
                {
                    var state = getter.Invoke(target, Array.Empty<object>());
                    if (state == null)
                        return false;
                    if (state is bool b)
                        return b;
                    var stType = state.GetType();
                    if (stType.IsEnum)
                    {
                        if (!activeValueResolved)
                        {
                            foreach (var name in Enum.GetNames(stType))
                            {
                                if (name.IndexOf("ragdoll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    try { activeValue = Enum.Parse(stType, name); }
                                    catch { activeValue = null; }
                                    break;
                                }
                            }
                            activeValueResolved = true;
                        }
                        if (activeValue != null)
                            return Equals(state, activeValue);
                        var enumText = state.ToString();
                        return enumText.IndexOf("ragdoll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               enumText.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               enumText.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    var text = state.ToString();
                    return text.IndexOf("ragdoll", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           text.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           text.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch
                {
                    return false;
                }
            };
        }

        private static bool SkeletonAlreadyRagdolled(Skeleton skel)
        {
            if (skel == null)
                return false;
            var type = skel.GetType();
            if (!_ragdollStateCache.TryGetValue(type, out var eval))
            {
                lock (_ragdollStateLock)
                {
                    if (!_ragdollStateCache.TryGetValue(type, out eval))
                    {
                        eval = CreateRagdollStateEvaluator(type);
                        _ragdollStateCache[type] = eval;
                    }
                }
            }
            try
            {
                return eval?.Invoke(skel) ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static float ToPhysicsImpulse(float mag)
        {
            // Convert RegisterBlow magnitude to a reasonable physics impulse scale.
            // Make corpse nudges visible. Old: 25k → 6.3. New: 25k → 25.
            float imp = mag * 1.0e-3f;

            float minImpulse = ER_Config.CorpseImpulseMinimum;
            if (float.IsNaN(minImpulse) || float.IsInfinity(minImpulse))
                minImpulse = 0f;
            minImpulse = MathF.Max(0f, minImpulse);

            float maxImpulse = ER_Config.CorpseImpulseMaximum;
            if (float.IsNaN(maxImpulse) || float.IsInfinity(maxImpulse))
                maxImpulse = 0f;
            if (maxImpulse < 0f)
                maxImpulse = 0f;

            if (maxImpulse > 0f)
            {
                if (minImpulse > maxImpulse)
                    minImpulse = maxImpulse;
            }

            if (imp < minImpulse)
                imp = minImpulse;
            if (maxImpulse > 0f && imp > maxImpulse)
                imp = maxImpulse;

            if (imp < 0f || float.IsNaN(imp) || float.IsInfinity(imp))
                return 0f;
            return imp;
        }

        private struct Blast { public Vec3 Pos; public float Radius; public float Force; public float T; }
        private struct Kick  { public Agent A; public Vec3 Dir; public float Force; public float T0; public float Dur; }
        private struct Launch { public Agent A; public GameEntity Ent; public Vec3 Dir; public float Mag; public Vec3 Pos; public float T; public int Tries; public int AgentId; public bool Warmed; public Vec3 P0; public Vec3 V0; }
        private struct PreLaunch { public Agent Agent; public Vec3 Dir; public float Mag; public Vec3 Pos; public float NextTry; public int Tries; public int AgentId; public bool Warmed; }
        private readonly List<Blast> _recent = new List<Blast>();
        private readonly List<Kick>  _kicks  = new List<Kick>();
        private readonly List<PreLaunch> _preLaunches = new List<PreLaunch>();
        private readonly List<Launch> _launches = new List<Launch>();
        private readonly HashSet<int> _launchFailLogged = new HashSet<int>();
        private readonly HashSet<int> _launchedOnce = new HashSet<int>();

        private void MarkLaunched(int agentId)
        {
            if (agentId < 0)
                return;

            _launchedOnce.Add(agentId);
            ER_Amplify_RegisterBlowPatch.ForgetScheduled(agentId);
        }
        private readonly Dictionary<int, int> _queuedPerAgent = new Dictionary<int, int>();
        private static float RecentBlastTtl => MathF.Max(0f, ER_Config.DeathBlastTtl);

        // --- API shims for TaleWorlds versions lacking these helpers ---
        // Nur wirklich 'weg', wenn Mission fehlt oder Agent nicht mehr aktiv ist.
        private static bool AgentRemoved(Agent a)
        {
            if (a == null) return true;
            try { if (a.Mission == null) return true; } catch { return true; }
            try { if (!a.IsActive()) return true; } catch { /* ältere Builds: IsActive fehlt */ }
            return false;
        }
        private static PropertyInfo _ragdollProp;
        private static bool RagdollActive(Agent a, bool warmed)
        {
            if (a == null || a.Health > 0f) return false;
            try
            {
                var visuals = a.AgentVisuals;
                var skeleton = visuals?.GetSkeleton();
                if (skeleton != null)
                {
                    var skelType = skeleton.GetType();
                    if (_ragdollProp == null || _ragdollProp.DeclaringType == null || !_ragdollProp.DeclaringType.IsAssignableFrom(skelType))
                    {
                        _ragdollProp = skelType.GetProperty("IsRagdollActive")
                                       ?? skelType.GetProperty("IsRagdollEnabled")
                                       ?? skelType.GetProperty("IsRagdolled");
                        if (_ragdollProp != null && _ragdollProp.PropertyType != typeof(bool))
                            _ragdollProp = null;
                    }
                    if (_ragdollProp != null)
                        return (bool)_ragdollProp.GetValue(skeleton);
                }
            }
            catch
            {
                // ignored - fall back below
            }
            // Fallback: auf manchen Builds gibt's das Flag nicht → trotzdem fortfahren.
            return true;
        }

        private void IncQueue(int agentId)
        {
            if (agentId < 0) return;
            if (_queuedPerAgent.TryGetValue(agentId, out var count))
                _queuedPerAgent[agentId] = count + 1;
            else
                _queuedPerAgent[agentId] = 1;
        }

        private void DecQueue(int agentId)
        {
            if (agentId < 0) return;
            if (_queuedPerAgent.TryGetValue(agentId, out var count))
            {
                if (count <= 1)
                    _queuedPerAgent.Remove(agentId);
                else
                    _queuedPerAgent[agentId] = count - 1;
            }
        }

        private static Vec3 XYJitter(Vec3 p)
        {
            float jitter = ER_Config.CorpseLaunchXYJitter;
            if (jitter <= 0f)
                return p;
            return new Vec3(
                p.x + MBRandom.RandomFloatRanged(-jitter, jitter),
                p.y + MBRandom.RandomFloatRanged(-jitter, jitter),
                p.z);
        }

        internal static float ApplyDelayJitter(float baseDelay)
        {
            float jitter = ER_Config.CorpseLaunchRetryJitter;
            if (jitter <= 0f)
                return baseDelay;
            float value = baseDelay + MBRandom.RandomFloatRanged(-jitter, jitter);
            return value < 0f ? 0f : value;
        }

        public static ER_DeathBlastBehavior Instance;

        public override void OnBehaviorInitialize() => Instance = this;

        public override void OnRemoveBehavior()
        {
            Instance = null;
            _recent.Clear();
            _kicks.Clear();
            _preLaunches.Clear();
            _launches.Clear();
            _launchFailLogged.Clear();
            _launchedOnce.Clear();
            _queuedPerAgent.Clear();
            // also clear any cross-behavior pending impulses
            ER_Amplify_RegisterBlowPatch.ClearPending();
        }

        public void QueuePreDeath(Agent agent, Vec3 dir, float mag, Vec3 pos)
        {
            if (agent == null)
                return;
            var mission = Mission;
            if (mission == null)
                return;
            if (agent.Mission != null && agent.Mission != mission)
                return;
            if (ER_Config.MaxCorpseLaunchMagnitude <= 0f)
                return;
            if (mag <= 0f || float.IsNaN(mag) || float.IsInfinity(mag))
                return;

            int agentId = agent.Index;
            if (_launchedOnce.Contains(agentId))
                return;

            float maxMag = ER_Config.MaxCorpseLaunchMagnitude;
            if (maxMag > 0f && mag > maxMag)
                mag = maxMag;
            if (mag <= 0f)
                return;

            Vec3 safeDir = PrepDir(dir);
            if (!Vec3IsFinite(safeDir) || safeDir.LengthSquared < 1e-6f)
                return;

            Vec3 contact = pos;
            if (!Vec3IsFinite(contact) || contact.LengthSquared < 1e-10f)
            {
                try { contact = agent.Position; }
                catch { contact = Vec3.Zero; }
            }
            float lift = MathF.Max(ER_Config.CorpseLaunchZNudge, 0.04f);
            contact.z += lift;

            float now = mission.CurrentTime;
            var entry = new PreLaunch
            {
                Agent   = agent,
                AgentId = agentId,
                Dir     = safeDir,
                Mag     = mag,
                Pos     = contact,
                NextTry = now + ApplyDelayJitter(0.01f),
                Tries   = Math.Max(0, ER_Config.CorpsePrelaunchTries),
                Warmed  = false,
            };

            for (int i = 0; i < _preLaunches.Count; i++)
            {
                if (_preLaunches[i].AgentId == agentId)
                {
                    _preLaunches[i] = entry;
                    return;
                }
            }
            _preLaunches.Add(entry);
        }

        public void RecordBlast(Vec3 center, float radius, float force)
        {
            if (radius <= 0f || force <= 0f) return;
            _recent.Add(new Blast { Pos = center, Radius = radius, Force = force, T = Mission.CurrentTime });
        }

        public void EnqueueKick(Agent a, Vec3 dir, float force, float duration)
        {
            if (a == null) return;
            Vec3 safeDir = PrepDir(dir, 1f, 0f);
            _kicks.Add(new Kick { A = a, Dir = safeDir, Force = force, T0 = Mission.CurrentTime, Dur = duration });
        }

        public void EnqueueLaunch(Agent a, Vec3 dir, float mag, Vec3 pos, float delaySec = 0.03f, int retries = 8)
        {
            if (a == null) return;
            var mission = Mission;
            if (mission == null) return;
            if (a.Mission != null && a.Mission != mission) return;
            if (ER_Config.MaxCorpseLaunchMagnitude <= 0f) return;
            if (mag <= 0f) return;
            if (a.Health > 0f) return;
            if (delaySec < 0f) delaySec = 0f;
            delaySec = ApplyDelayJitter(delaySec);
            if (retries < 0) retries = 0;
            int maxRetries = Math.Max(0, ER_Config.CorpsePostDeathTries);
            if (retries > maxRetries) retries = maxRetries;
            int agentIndex = a.Index;
            if (_launchedOnce.Contains(agentIndex))
                return;
            int queueCap = ER_Config.CorpseLaunchQueueCap;
            if (queueCap > 0)
            {
                _queuedPerAgent.TryGetValue(agentIndex, out var queued);
                if (queued >= queueCap) return;
            }

            float maxMag = ER_Config.MaxCorpseLaunchMagnitude;
            if (maxMag > 0f && mag > maxMag)
            {
                mag = maxMag;
            }
            if (mag <= 0f || float.IsNaN(mag) || float.IsInfinity(mag))
                return;
            Vec3 safeDir = PrepDir(dir);
            if (!Vec3IsFinite(safeDir) || safeDir.LengthSquared < 1e-6f)
                return;
            Vec3 nudgedPos = pos;
            float zNudge = ER_Config.CorpseLaunchZNudge;
            float zClamp = ER_Config.CorpseLaunchZClampAbove;
            float agentZ = nudgedPos.z;
            try { agentZ = a.Position.z; } catch { }
            nudgedPos.z = MathF.Min(nudgedPos.z + zNudge, agentZ + zClamp);

            GameEntity ent = null;
            try { ent = a.AgentVisuals?.GetEntity(); } catch { }
            _launches.Add(new Launch { A = a, Ent = ent, Dir = safeDir, Mag = mag, Pos = nudgedPos, T = mission.CurrentTime + delaySec, Tries = retries, AgentId = agentIndex, Warmed = false });
            IncQueue(agentIndex);
        }

        private static void WarmRagdoll(GameEntity ent, Skeleton skel)
        {
            try { ent?.ActivateRagdoll(); } catch { }
            try { skel?.ActivateRagdoll(); } catch { }
            // For perf, don't force LOD=0 globally; it’s heavy on crowds.
            // try { ent?.SetEnforcedMaximumLodLevel(0); } catch { }
            try { skel?.ForceUpdateBoneFrames(); } catch { }
            // Do NOT heavy-tick here; just ensure bones exist and are dynamic.
        }

        public override void OnMissionTick(float dt)
        {
            var mission = Mission;
            // Stop freezes when returning from Options (and avoid zero-dt spins).
            if (IsPausedSafe(mission, dt)) return;
            if (mission == null || mission.Agents == null || dt <= 1e-6f) return;
            float now = mission.CurrentTime;
            // Gentle ramp after UI resume
            if (_lastTickT == 0f) _lastTickT = now;
            bool recentlyResumed = (now - _lastTickT) < 1.0f;
            _lastTickT = now;
            int maxLaunchesPerTick = ER_Config.CorpseLaunchesPerTickCap;
            if (recentlyResumed && maxLaunchesPerTick > 16) maxLaunchesPerTick = 16;
            bool limitLaunches = maxLaunchesPerTick > 0;
            int launchesWorked = 0;
            float tookScale = ER_Config.CorpseLaunchVelocityScaleThreshold;
            float tookOffset = ER_Config.CorpseLaunchVelocityOffset;
            float tookVertical = ER_Config.CorpseLaunchVerticalDelta;
            float tookDisplacement = ER_Config.CorpseLaunchDisplacement;
            float contactHeight = ER_Config.CorpseLaunchContactHeight;
            float retryDelay = ER_Config.CorpseLaunchRetryDelay;
            float preRetryDelay = MathF.Max(0.02f, ER_Config.CorpseLaunchRetryDelay);
            int queueCap = ER_Config.CorpseLaunchQueueCap;
            float zNudge = ER_Config.CorpseLaunchZNudge;
            float zClamp = ER_Config.CorpseLaunchZClampAbove;
            float tickMaxSetting = ER_Config.MaxCorpseLaunchMagnitude;
            bool clampMag = tickMaxSetting > 0f; // <=0 bedeutet: nicht kappen
            float blastTtl = RecentBlastTtl;
            for (int i = _recent.Count - 1; i >= 0; i--)
                if (now - _recent[i].T > blastTtl) _recent.RemoveAt(i);
            for (int i = _preLaunches.Count - 1; i >= 0; i--)
            {
                var entry = _preLaunches[i];
                if (entry.AgentId >= 0 && _launchedOnce.Contains(entry.AgentId))
                {
                    _preLaunches.RemoveAt(i);
                    continue;
                }
                if (now < entry.NextTry)
                    continue;
                var agent = entry.Agent;
                if (!entry.Warmed)
                {
                    GameEntity warmEnt = null;
                    Skeleton warmSkel = null;
                    try { warmEnt = agent?.AgentVisuals?.GetEntity(); } catch { }
                    try { warmSkel = agent?.AgentVisuals?.GetSkeleton(); } catch { }
                    WarmRagdoll(warmEnt, warmSkel);
                    entry.Warmed = true;
                    entry.NextTry = now + preRetryDelay;
                    _preLaunches[i] = entry;
                    if (ER_Config.DebugLogging)
                        ER_Log.Info($"RAGDOLL_WARM id#{entry.AgentId} ent={(warmEnt != null)} skel={(warmSkel != null)}");
                    continue;
                }
                bool remove = false;
                if (agent == null || agent.Mission == null || agent.Mission != mission)
                {
                    remove = true;
                }
                else if (entry.Tries <= 0)
                {
                    remove = true;
                }
                else
                {
                    GameEntity ent = null;
                    Skeleton skel = null;
                    try { ent = agent.AgentVisuals?.GetEntity(); } catch { }
                    try { skel = agent.AgentVisuals?.GetSkeleton(); } catch { }
                    if (ent == null && skel == null)
                    {
                        if (entry.Tries <= 0)
                        {
                            remove = true;
                        }
                        else
                        {
                            entry.Tries--;
                            entry.NextTry = now + preRetryDelay;
                            _preLaunches[i] = entry;
                            continue;
                        }
                    }
                    else
                    {
                        float impulseMag = ToPhysicsImpulse(entry.Mag);
                        if (impulseMag <= 0f)
                        {
                            remove = true;
                        }
                        else
                        {
                            Vec3 contact = entry.Pos;
                            if (!Vec3IsFinite(contact))
                            {
                                try { contact = agent.Position; }
                                catch { contact = entry.Pos; }
                            }
                            else
                            {
                                try
                                {
                                    contact.z = MathF.Min(contact.z, agent.Position.z + zClamp);
                                }
                                catch
                                {
                                }
                            }
                            Vec3 dir = entry.Dir;
                            try
                            {
                                // Pre-death warm: push while agent is still alive so ragdoll becomes dynamic.
                                if (dir.LengthSquared < 1e-6f)
                                {
                                    try { dir = agent.LookDirection; } catch { dir = new Vec3(0f, 1f, 0.25f); }
                                }
                                // Bias upward a bit; avoid vertical-only
                                dir = ER_DeathBlastBehavior.PrepDir(dir, 0.35f, 1.05f);

                                float warmMag = ER_Config.MaxNonLethalKnockback > 0f
                                    ? MathF.Min(impulseMag, ER_Config.MaxNonLethalKnockback)
                                    : impulseMag;
                                var kb = new Blow(-1)
                                {
                                    DamageType      = DamageTypes.Blunt,
                                    BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                                    BaseMagnitude   = warmMag,
                                    SwingDirection  = dir,
                                    GlobalPosition  = contact,
                                    InflictedDamage = 0
                                };
                                AttackCollisionData acd = default;
                                agent.RegisterBlow(kb, in acd);

                                // Re-try shortly so the post-death launch lands on a dynamic body.
                                if (AgentRemoved(agent))
                                {
                                    remove = true;
                                }
                                else if (entry.Tries > 0)
                                {
                                    entry.Tries--;
                                    entry.NextTry = now + preRetryDelay;
                                    _preLaunches[i] = entry;
                                    continue;
                                }
                                else
                                {
                                    remove = true;
                                }
                            }
                            catch
                            {
                                // If anything fails here, drop back to post-death path only.
                                remove = true;
                            }
                        }
                    }
                }
                if (remove)
                {
                    _preLaunches.RemoveAt(i);
                }
            }
            int maxKicksPerTick = ER_Config.KicksPerTickCap;
            bool limitKicks = maxKicksPerTick > 0;
            int kicksWorked = 0;
            for (int i = 0; i < _kicks.Count;)
            {
                var k = _kicks[i];
                if (k.A == null || AgentRemoved(k.A))
                {
                    _kicks.RemoveAt(i);
                    continue;
                }

                float age = now - k.T0;
                if (age > k.Dur)
                {
                    _kicks.RemoveAt(i);
                    continue;
                }

                if (limitKicks && kicksWorked >= maxKicksPerTick)
                {
                    i++;
                    continue;
                }

                float gain = 1f - (age / k.Dur);
                float mag = k.Force * gain * 0.30f;
                float maxNonLethal = ER_Config.MaxNonLethalKnockback;
                if (k.A.Health > 0f && maxNonLethal > 0f && mag > maxNonLethal)
                {
                    mag = maxNonLethal;
                }

                if (mag <= 0f)
                {
                    _kicks.RemoveAt(i);
                    continue;
                }

                Vec3 dir = PrepDir(k.Dir, 1f, 0f);

                var kb = new Blow(-1)
                {
                    DamageType      = DamageTypes.Blunt,
                    BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                    BaseMagnitude   = mag,
                    SwingDirection  = dir,
                    GlobalPosition  = k.A.Position,
                    InflictedDamage = 0
                };
                AttackCollisionData acd = default;
                k.A.RegisterBlow(kb, in acd);
                if (limitKicks)
                    kicksWorked++;

                i++;
            }

            // delayed corpse launches (run AFTER ragdoll is active)
            for (int i = _launches.Count - 1; i >= 0; i--)
            {
                var L = _launches[i];
                if (now < L.T) continue;
                if (limitLaunches)
                {
                    if (launchesWorked >= maxLaunchesPerTick)
                        break;
                    launchesWorked++;
                }
                var agent = L.A;
                int agentIndex = L.AgentId >= 0 ? L.AgentId : agent?.Index ?? -1;
                if (agentIndex >= 0 && _launchedOnce.Contains(agentIndex))
                {
                    _launches.RemoveAt(i);
                    _launchFailLogged.Remove(agentIndex);
                    DecQueue(agentIndex);
                    continue;
                }
                bool nudged = false;
                bool queueDecremented = false;
                void DecOnce()
                {
                    if (!queueDecremented)
                    {
                        DecQueue(agentIndex);
                        queueDecremented = true;
                    }
                }
                _launches.RemoveAt(i);
                Vec3 dir = L.Dir;
                if (!Vec3IsFinite(dir) || dir.LengthSquared < 1e-6f)
                {
                    dir = new Vec3(0f, 1f, 0f);
                }
                else
                {
                    dir = ClampVertical(dir);
                    float lenSq = dir.LengthSquared;
                    if (lenSq < 1e-6f || float.IsNaN(lenSq) || float.IsInfinity(lenSq))
                        dir = new Vec3(0f, 1f, 0f);
                    else
                        dir = dir.NormalizedCopy();
                }
                L.Dir = dir;
                GameEntity ent = L.Ent;
                Skeleton skel = null;
                if (ent == null)
                {
                    try { ent = agent?.AgentVisuals?.GetEntity(); } catch { }
                }
                try { skel = agent?.AgentVisuals?.GetSkeleton(); } catch { }
                bool agentMissing = agent == null || agent.Mission == null || agent.Mission != mission;
                if (agentMissing)
                {
                    if (ent != null || skel != null)
                    {
                        float impMag = ToPhysicsImpulse(L.Mag);
                        if (impMag > 0f)
                        {
                            var contactMiss = XYJitter(L.Pos); contactMiss.z += contactHeight;
                            bool ok = TryApplyImpulse(ent, skel, dir * impMag, contactMiss, agentIndex);
                            nudged |= ok;
                            if (ok)
                                MarkLaunched(agentIndex);
                            if (ER_Config.DebugLogging)
                                ER_Log.Info($"corpse entity impulse (no agent) id#{agentIndex} impMag={impMag:F1} ok={ok}");
                        }
                    }
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue;
                }
                if (agent.Health > 0f)
                {
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue; // only launch ragdolls still in mission
                }

                float dirSq = dir.LengthSquared;
                if (dirSq < 1e-8f || float.IsNaN(dirSq) || float.IsInfinity(dirSq))
                {
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue;
                }
                float mag = L.Mag;
                if (clampMag && mag > tickMaxSetting)
                {
                    mag = tickMaxSetting;
                }
                if (mag <= 0f || float.IsNaN(mag) || float.IsInfinity(mag))
                {
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue;
                }
                Vec3 hit = XYJitter(L.Pos);
                Vec3 contactPoint = hit;
                contactPoint.z += contactHeight;
                contactPoint.z = MathF.Min(contactPoint.z, agent.Position.z + zClamp);

                if (!L.Warmed)
                {
                    L.P0 = agent.Position;
                    L.V0 = agent.Velocity;
                    var blow = new Blow(-1)
                    {
                        DamageType      = DamageTypes.Blunt,
                        BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                        BaseMagnitude   = mag,
                        SwingDirection  = dir,
                        GlobalPosition  = contactPoint,
                        InflictedDamage = 0
                    };
                    AttackCollisionData acd = default;
                    agent.RegisterBlow(blow, in acd);
                    L.Warmed = true;
                    L.T = now + MathF.Max(0.05f, retryDelay); // small settle time
                    L.Pos = agent.Position;
                    L.Ent = ent;
                    _launches.Add(L);
                    continue; // measure on next tick
                }
                if (!RagdollActive(agent, L.Warmed))
                {
                    // too early: requeue same launch shortly, keep queue counts unchanged
                    L.Warmed = false;
                    L.T   = now + ApplyDelayJitter(MathF.Max(0.04f, retryDelay)); // mehr Luft bis Ragdoll aktiv
                    L.Pos = agent.Position;
                    L.Ent = ent;
                    _launches.Add(L);
                    continue;
                }
                // Ragdoll/Visuals kommen oft 1–2 Ticks verspätet. Requeue statt Drop.
                if (agent.AgentVisuals == null)
                {
                    if (!AgentRemoved(agent) && L.Tries > 0)
                    {
                        L.Tries--;
                        L.Warmed = false;                 // neu messen, wenn Visuals dran sind
                        L.T      = now + MathF.Max(0.05f, retryDelay);
                        L.Pos    = agent.Position;
                        L.Ent    = ent;
                        _launches.Add(L);
                        IncQueue(agentIndex);
                        if (ER_Config.DebugLogging && agentIndex >= 0)
                            ER_Log.Info($"corpse launch re-queued (no visuals yet) Agent#{agentIndex} tries={L.Tries}");
                        DecOnce(); // alten Eintrag sauber abbuchen
                        continue;
                    }
                    DecOnce();
                    continue;
                }
                if (agent.AgentVisuals != null)
                {
                    try
                    {
                        var ent2 = agent.AgentVisuals.GetEntity();
                        skel = agent.AgentVisuals.GetSkeleton();
                        if (ent2 != null || skel != null)
                        {
                            float impMag2 = ToPhysicsImpulse(mag);
                            if (impMag2 > 0f)
                            {
                                try { skel?.ActivateRagdoll(); } catch { }
                                try { skel?.ForceUpdateBoneFrames(); } catch { }
                                try
                                {
                                    MatrixFrame f2;
                                    try { f2 = ent2?.GetGlobalFrame() ?? ent?.GetGlobalFrame() ?? default; }
                                    catch { f2 = default; }
                                    skel?.TickAnimationsAndForceUpdate(0.001f, f2, true);
                                }
                                catch { }
                                bool ok2 = TryApplyImpulse(ent2, skel, dir * impMag2, contactPoint, agentIndex);
                                nudged |= ok2;
                                if (ok2)
                                    MarkLaunched(agentIndex);
                                if (ER_Config.DebugLogging)
                                    ER_Log.Info($"corpse nudge Agent#{agentIndex} impMag={impMag2:F1} ok={ok2}");
                            }
                            ent = ent2;
                        }
                    }
                    catch { }
                }
                if (AgentRemoved(agent))
                {
                    if (ent != null || skel != null)
                    {
                        float impMag = ToPhysicsImpulse(L.Mag);
                        if (impMag > 0f)
                        {
                            var contactEntity = XYJitter(L.Pos); contactEntity.z += contactHeight;
                            bool ok = TryApplyImpulse(ent, skel, dir * impMag, contactEntity, agentIndex);
                            nudged |= ok;
                            if (ok)
                                MarkLaunched(agentIndex);
                            if (ER_Config.DebugLogging)
                                ER_Log.Info($"corpse entity impulse (no agent) id#{agentIndex} impMag={impMag:F1} ok={ok}");
                        }
                    }
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue;
                }
                Vec3 velAfter = agent.Velocity;
                float vAfter2 = velAfter.LengthSquared;
                float vBefore2 = L.V0.LengthSquared;
                float moved = L.P0.Distance(agent.Position);
                bool took = nudged
                    || (vAfter2 > vBefore2 * tookScale + tookOffset)
                    || (velAfter.z > L.V0.z + tookVertical)
                    || (moved > tookDisplacement);

                if (!took)
                {
                    // Fallback: directly impulse ragdoll physics if RegisterBlow had no effect
                    try
                    {
                        var entLocal = (agent?.AgentVisuals != null) ? agent.AgentVisuals.GetEntity() : null;
                        var skelLocal = (agent?.AgentVisuals != null) ? agent.AgentVisuals.GetSkeleton() : null;
                        float impMag = ToPhysicsImpulse(mag);
                        if ((entLocal != null || skelLocal != null) && impMag > 0f)
                        {
                            var impulse = dir * impMag;
                            try { skelLocal?.ActivateRagdoll(); } catch { }
                            try { skelLocal?.ForceUpdateBoneFrames(); } catch { }
                            try
                            {
                                MatrixFrame fLocal;
                                try { fLocal = entLocal?.GetGlobalFrame() ?? ent?.GetGlobalFrame() ?? default; }
                                catch { fLocal = default; }
                                skelLocal?.TickAnimationsAndForceUpdate(0.001f, fLocal, true);
                            }
                            catch { }
                            bool ok = TryApplyImpulse(entLocal, skelLocal, impulse, contactPoint, agentIndex);
                            nudged |= ok;
                            if (ok)
                                MarkLaunched(agentIndex);
                            if (ok && ER_Config.DebugLogging)
                                ER_Log.Info($"corpse physics impulse attempted Agent#{agentIndex} impMag={impMag:F1}");
                        }
                    }
                    catch { /* never throw here */ }

                    if (nudged)
                    {
                        _launchFailLogged.Remove(agentIndex);
                        DecOnce();
                        continue;
                    }

                    if (ER_Config.DebugLogging && _launchFailLogged.Add(agentIndex))
                    {
                        float deltaZ = velAfter.z - L.V0.z;
                        ER_Log.Info($"corpse launch miss Agent#{agentIndex} vBefore2={vBefore2:F4} vAfter2={vAfter2:F4} deltaZ={deltaZ:F4}");
                    }

                    if (L.Tries > 0)
                    {
                        float nextTime = now + ApplyDelayJitter(retryDelay);
                        Vec3 retryPos = XYJitter(agent.Position);
                        retryPos.z = MathF.Min(retryPos.z + zNudge, agent.Position.z + zClamp);

                        if (clampMag && L.Mag > tickMaxSetting)
                        {
                            L.Mag = tickMaxSetting;
                        }

                        bool canQueue = true;
                        int existingQueued = 0;
                        if (agentIndex >= 0)
                        {
                            _queuedPerAgent.TryGetValue(agentIndex, out existingQueued);
                        }
                        if (AgentRemoved(agent) || agentMissing)
                        {
                            canQueue = false;
                        }
                        if (queueCap > 0 && existingQueued >= queueCap)
                        {
                            canQueue = false;
                        }

                        if (canQueue)
                        {
                            L.Tries--;
                            L.T = nextTime;
                            L.Pos = retryPos;
                            L.AgentId = agentIndex;
                            L.Warmed = false;
                            L.Ent = ent;
                            _launches.Add(L);
                            IncQueue(agentIndex);
                            if (ER_Config.DebugLogging && agentIndex >= 0 && (L.Tries % 3 == 0))
                            {
                                _queuedPerAgent.TryGetValue(agentIndex, out var newQueuedCount);
                                ER_Log.Info($"corpse launch re-queued for Agent#{agentIndex} tries={L.Tries} queued={newQueuedCount}");
                            }
                        }
                        else if (ER_Config.DebugLogging && agentIndex >= 0)
                        {
                            ER_Log.Info($"corpse launch queue full for Agent#{agentIndex} tries={L.Tries} queued={existingQueued} cap={queueCap}");
                        }
                    }
                    else
                    {
                        _launchFailLogged.Remove(agentIndex);
                    }
                    DecOnce();
                    if (nudged)
                        MarkLaunched(agentIndex);
                }
                else
                {
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    MarkLaunched(agentIndex);
                    if (ER_Config.DebugLogging)
                    {
                        ER_Log.Info($"corpse launch took Agent#{agentIndex} moved={moved:F4} v↑Δ={velAfter.z - L.V0.z:F4}");
                        _queuedPerAgent.TryGetValue(agentIndex, out var queued);
                        ER_Log.Info($"death shove applied to Agent#{agentIndex} took={took} mag={mag} tries={L.Tries} queued={queued}");
                    }
                }
            }
            if (_recent.Count == 0) return;

            int maxAoEAgentsPerTick = ER_Config.AoEAgentsPerTickCap;
            bool limitAoE = maxAoEAgentsPerTick > 0;
            int worked = 0;
            foreach (var a in mission.Agents)
            {
                if (a == null || a.Health <= 0f) continue; // ragdoll corpse already handled on kill
                bool affected = false;
                Vec3 pos = a.Position;
                for (int i = _recent.Count - 1; i >= 0; i--)
                {
                    var b = _recent[i];
                    float d = pos.Distance(b.Pos);
                    if (d > b.Radius) continue;
                    float force = (b.Force * ER_Config.DeathBlastForceMultiplier) * (1f / (1f + d));
                    if (force <= 0f) continue;
                    Vec3 flat = pos - b.Pos; flat = new Vec3(flat.x, flat.y, 0f);
                    Vec3 dir = PrepDir(flat, 0.70f, 0.72f);
                    float maxForce = ER_Config.MaxAoEForce;
                    if (maxForce > 0f && force > maxForce)
                    {
                        force = maxForce;
                    }
                    float maxNonLethal = ER_Config.MaxNonLethalKnockback;
                    if (a.Health > 0f && maxNonLethal > 0f && force > maxNonLethal)
                    {
                        force = maxNonLethal;
                    }

                    var aoe = new Blow(-1)
                    {
                        DamageType      = DamageTypes.Blunt,
                        BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                        BaseMagnitude   = force,
                        SwingDirection  = dir,
                        GlobalPosition  = b.Pos,
                        InflictedDamage = 0
                    };
                    AttackCollisionData aoeAcd = default;
                    a.RegisterBlow(aoe, in aoeAcd);
                    affected = true;
                    if (d < b.Radius * 0.55f && a.Health > 0f)
                    {
                        var kb = new Blow(-1)
                        {
                            DamageType      = DamageTypes.Blunt,
                            BlowFlag        = BlowFlags.KnockDown | BlowFlags.NoSound,
                            BaseMagnitude   = MathF.Min(1f, force * 0.00005f),
                            SwingDirection  = dir,
                            GlobalPosition  = b.Pos,
                            InflictedDamage = 0
                        };
                        AttackCollisionData kbAcd = default;
                        a.RegisterBlow(kb, in kbAcd);
                    }
                    break; // pro Agent nur ein Blast pro Tick
                }
                if (affected)
                {
                    worked++;
                    if (limitAoE && worked >= maxAoEAgentsPerTick)
                        break;
                }
            }
        }

        private static bool IsPausedSafe(Mission mission, float dt)
        {
            if (dt <= 1e-6f)
                return true;

            try
            {
                var pauseType = AccessTools.TypeByName("TaleWorlds.Engine.MBCommon");
                if (pauseType != null)
                {
                    var pauseProperty = pauseType.GetProperty("IsPaused", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pauseProperty != null && pauseProperty.PropertyType == typeof(bool))
                    {
                        var isPaused = pauseProperty.GetValue(null);
                        if (isPaused is bool paused)
                            return paused;
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (mission != null)
                {
                    var missionType = mission.GetType();
                    var pauseProperty = missionType.GetProperty("IsPaused", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                         ?? missionType.GetProperty("Paused", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pauseProperty != null && pauseProperty.PropertyType == typeof(bool))
                    {
                        var isPaused = pauseProperty.GetValue(mission);
                        if (isPaused is bool paused)
                            return paused;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        // Fire post-death fallback if MakeDead scheduling failed to consume the pending entry
        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null) return;
            _launchFailLogged.Remove(affected.Index);
            if (state != AgentState.Killed) return;
            if (affected.Mission != null && affected.Mission != Mission) return;
            if (ER_Config.MaxCorpseLaunchMagnitude <= 0f) return;

            for (int i = _launches.Count - 1; i >= 0; i--)
            {
                var queued = _launches[i];
                if (queued.AgentId == affected.Index || queued.A == affected)
                {
                    _launches.RemoveAt(i);
                    DecQueue(affected.Index);
                    _launchFailLogged.Remove(affected.Index);
                }
            }

            if (_launchedOnce.Remove(affected.Index))
                return;

            // Kontaktpunkt: konservativ das Agent-Center verwenden (kein KillingBlow.GlobalPosition vorhanden)
            Vec3 hitPos = affected.Position;
            Vec3 flat   = new Vec3(affected.LookDirection.x, affected.LookDirection.y, 0f);
            Vec3 fallbackDir = PrepDir(flat, 0.35f, 1.05f);

            // Fallback only if MakeDead didn’t consume the pending entry
            if (!ER_Amplify_RegisterBlowPatch.TryTakePending(affected.Index, out var p))
            {
                ER_Amplify_RegisterBlowPatch.ForgetScheduled(affected.Index);
                return;
            }
            ER_Amplify_RegisterBlowPatch.ForgetScheduled(affected.Index);

            float mag = p.mag;
            Vec3 dir = p.dir;
            if (!Vec3IsFinite(dir) || dir.LengthSquared < 1e-6f)
            {
                dir = fallbackDir;
            }
            else
            {
                dir = ClampVertical(dir);
                float lenSq = dir.LengthSquared;
                dir = (lenSq < 1e-6f || float.IsNaN(lenSq) || float.IsInfinity(lenSq))
                    ? fallbackDir
                    : dir.NormalizedCopy();
            }
            if (p.pos.LengthSquared > 1e-6f) hitPos = p.pos;

            try
            {
                var ent  = affected.AgentVisuals?.GetEntity();
                var skel = affected.AgentVisuals?.GetSkeleton();
                if (ent != null || skel != null)
                {
                    var contactImmediate = hitPos;
                    contactImmediate.z += ER_Config.CorpseLaunchContactHeight;
                    float imp = ToPhysicsImpulse(mag);
                    if (imp > 0f)
                    {
                        bool ok = TryApplyImpulse(ent, skel, dir * imp, contactImmediate, affected.Index);
                        if (ok)
                            MarkLaunched(affected.Index);
                    }
                }
            }
            catch { }

            // Schedule with retries (safety net in case MakeDead timing was late)
            int postTries = ER_Config.CorpsePostDeathTries;
            int pulse2Tries = Math.Max(0, (int)MathF.Round(postTries * MathF.Max(0f, ER_Config.LaunchPulse2Scale)));
            EnqueueLaunch(affected, dir, mag,                         hitPos, ER_Config.LaunchDelay1, retries: postTries);
            EnqueueLaunch(affected, dir, mag * ER_Config.LaunchPulse2Scale, hitPos, ER_Config.LaunchDelay2, retries: pulse2Tries);
            EnqueueKick  (affected, dir, mag, 1.2f);
            RecordBlast(affected.Position, ER_Config.DeathBlastRadius, mag);
            if (ER_Config.DebugLogging)
                ER_Log.Info($"OnAgentRemoved fallback: scheduled corpse launch Agent#{affected.Index} mag={mag}");
        }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    }

    // Schedule corpse launch right after death (ragdoll just activated)
    [HarmonyPatch(typeof(Agent))]
    internal static class ER_Probe_MakeDead
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(Agent)))
                if (m.Name == nameof(Agent.MakeDead)) yield return m;
        }

        [HarmonyPostfix, HarmonyAfter(new[] { "TOR", "TOR_Core" }), HarmonyPriority(HarmonyLib.Priority.Last)]
        static void Post(Agent __instance)
        {
            if (__instance == null) return;
            if (!ER_Amplify_RegisterBlowPatch.TryTakePending(__instance.Index, out var p)) return;
            float now = __instance.Mission?.CurrentTime ?? 0f;
            if (!ER_Amplify_RegisterBlowPatch.TryMarkScheduled(__instance.Index, now)) return;
            ER_DeathScheduler.Schedule(__instance, p, tag: "MakeDead");
        }
    }

    // Also patch Agent.Die for game versions that use it instead of MakeDead
    [HarmonyPatch(typeof(Agent))]
    internal static class ER_Probe_Die
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(Agent)))
                if (m.Name == "Die") yield return m;
        }

        [HarmonyPostfix, HarmonyAfter(new[] { "TOR", "TOR_Core" }), HarmonyPriority(HarmonyLib.Priority.Last)]
        static void Post(Agent __instance)
        {
            if (__instance == null) return;
            if (!ER_Amplify_RegisterBlowPatch.TryTakePending(__instance.Index, out var p)) return;
            float now = __instance.Mission?.CurrentTime ?? 0f;
            if (!ER_Amplify_RegisterBlowPatch.TryMarkScheduled(__instance.Index, now)) return;
            ER_DeathScheduler.Schedule(__instance, p, tag: "Die");
        }
    }

    internal static class ER_DeathScheduler
    {
        internal static void Schedule(Agent a, ER_Amplify_RegisterBlowPatch.PendingLaunch p, string tag)
        {
            if (a == null) return;
            if (ER_DeathBlastBehavior.Instance == null || a.Mission == null) return;
            if (ER_Config.MaxCorpseLaunchMagnitude <= 0f) return;
            var dir = ER_DeathBlastBehavior.PrepDir(p.dir, 1f, 0f);
            int postTries = ER_Config.CorpsePostDeathTries;
            int pulse2Tries = Math.Max(0, (int)MathF.Round(postTries * MathF.Max(0f, ER_Config.LaunchPulse2Scale)));
            ER_DeathBlastBehavior.Instance.EnqueueLaunch(a, dir, p.mag,                           p.pos, ER_Config.LaunchDelay1, retries: postTries);
            ER_DeathBlastBehavior.Instance.EnqueueLaunch(a, dir, p.mag * ER_Config.LaunchPulse2Scale, p.pos, ER_Config.LaunchDelay2, retries: pulse2Tries);
            ER_DeathBlastBehavior.Instance.EnqueueKick  (a, dir, p.mag, 1.2f);
            ER_DeathBlastBehavior.Instance.RecordBlast(a.Position, ER_Config.DeathBlastRadius, p.mag);
            if (ER_Config.DebugLogging) ER_Log.Info($"{tag}: scheduled corpse launch Agent#{a.Index} mag={p.mag}");
        }
    }
}
