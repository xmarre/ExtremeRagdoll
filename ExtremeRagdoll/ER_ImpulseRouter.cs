using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    // Bone-space impulses can misalign on missile kills. Prefer entity-space.
    internal static class ER_ImpulsePrefs
    {
        internal static bool ForceEntityImpulse => ER_Config.ForceEntityImpulse;
        internal static bool AllowSkeletonFallbackForInvalidEntity => ER_Config.AllowSkeletonFallbackForInvalidEntity;
    }

    internal static class ER_ImpulseRouter
    {
        private const float ContactTinySqThreshold = ER_Math.ContactTinySq;
        private const float ImpulseTinySqThreshold = ER_Math.ImpulseTinySq;
        // absolute safety caps to stop “teleport” speeds even if config is wild
        private static float MaxWorldImpulse => MathF.Min(1800f, MathF.Max(200f, ER_Config.WorldImpulseCap));
        private static float MaxLocalImpulse => MathF.Min(1800f, MathF.Max(200f, ER_Config.LocalImpulseCap));
        private const float Ent2WarmupSeconds = 0.12f;
        // drip-feed window for delayed ent2 when the body is still kinematic
        private const float PendingLife = 0.40f;
        private const int PendingBursts = 3;
        private const float BurstScale = 1f / PendingBursts;
        private static bool _ensured;
        private static readonly object _ensureLock = new object();
        private static bool _ent1Unsafe, _ent2Unsafe, _ent3Unsafe, _sk1Unsafe, _sk2Unsafe;
        private static MethodInfo _ent3, _ent2, _ent1;
        private static MethodInfo _ent3Inst, _ent2Inst, _ent1Inst;
        private static MethodInfo _sk2, _sk1;
        private static MethodInfo _wake;
        // skeleton -> entity resolver (built once)
        private static Func<Skeleton, GameEntity> _skToEnt;
        private static Action<GameEntity, Vec3, Vec3, bool> _dEnt3;
        private static Action<GameEntity, Vec3, Vec3> _dEnt2;
        private static Action<GameEntity, Vec3> _dEnt1;
        private static Action<GameEntity, Vec3, Vec3, bool> _dEnt3Inst;
        private static Action<GameEntity, Vec3, Vec3> _dEnt2Inst;
        private static Action<GameEntity, Vec3> _dEnt1Inst;
        private static Action<Skeleton, Vec3, Vec3> _dSk2;
        private static Action<Skeleton, Vec3> _dSk1;
        private static float _lastNoSkNote = float.NegativeInfinity;
        private static bool _skEntLogged;
        private static Action<GameEntity> _dWake;
        private static Func<GameEntity, bool> _isDyn;
        private static float _lastImpulseLog = float.NegativeInfinity; // keep
        private static float _lastNoiseLog = float.NegativeInfinity;
        private static string _ent1Name, _ent2Name, _ent3Name, _sk1Name, _sk2Name; // debug
        private static bool _bindLogged;
        private static int _skCandLogCount;
        private static float _lastAvLog = float.NegativeInfinity;
        // AV throttling state: indexes 1..5 map to routes.
        private static readonly float[] _disableUntil = new float[6];
        private static readonly int[] _avCount = new int[6];
        private sealed class Rag
        {
            public float t;
            public int root = -1;
            public bool warmStarted;
        }

        private struct Pending
        {
            public GameEntity ent;
            public Skeleton sk;
            public Vec3 wImp;
            public Vec3 wPos;
            public float expires;
            public int remaining;
        }

        private static readonly ConcurrentQueue<Pending> _pending = new ConcurrentQueue<Pending>();
        private static readonly List<Pending> _carry = new List<Pending>(64);

        private static readonly ConditionalWeakTable<Skeleton, Rag> _rag = new ConditionalWeakTable<Skeleton, Rag>();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _boneCountCache = new ConcurrentDictionary<Type, MethodInfo>();
        private static readonly ConcurrentDictionary<Type, MethodInfo> _boneNameCache = new ConcurrentDictionary<Type, MethodInfo>();
        private static readonly MethodInfo _boneMethodSentinel = typeof(ER_ImpulseRouter).GetMethod(nameof(CacheSentinel), BindingFlags.Static | BindingFlags.NonPublic)
                                                           ?? throw new InvalidOperationException("Missing cache sentinel");

        internal static void ResetUnsafeState()
        {
            _ent1Unsafe = _ent2Unsafe = _ent3Unsafe = _sk1Unsafe = _sk2Unsafe = false;
            for (int i = 0; i < _disableUntil.Length; i++)
            {
                _disableUntil[i] = 0f;
                _avCount[i] = 0;
            }
            _lastNoiseLog = float.NegativeInfinity;
            _lastNoSkNote = float.NegativeInfinity;
            _skEntLogged = false;
            _skToEnt = null;
            _skCandLogCount = 0;
            _bindLogged = false;
            _sk1Name = _sk2Name = _ent1Name = _ent2Name = _ent3Name = null;
            while (_pending.TryDequeue(out _))
            {
            }
            _carry.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsVec3(Type t)
        {
            if (t == typeof(Vec3))
                return true;
            if (t.IsByRef && t.GetElementType() == typeof(Vec3))
                return true;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsIntLike(Type t)
        {
            if (t == null)
                return false;
            if (t.IsByRef)
                return IsIntLike(t.GetElementType());
            return t == typeof(int) || t == typeof(uint)
                   || t == typeof(short) || t == typeof(ushort)
                   || t == typeof(sbyte) || t == typeof(byte);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AcceptsSkeletonArg(Type t)
        {
            if (t == null)
                return false;
            if (t.IsByRef)
                t = t.GetElementType();
            if (t == null)
                return false;
            if (t == typeof(object) || t == typeof(ValueType))
                return false;
            return t.IsAssignableFrom(typeof(Skeleton));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool DeclTypeAcceptsSkeleton(Type t)
        {
            if (t == null)
                return false;
            if (t == typeof(object) || t == typeof(ValueType))
                return false;
            return t.IsAssignableFrom(typeof(Skeleton));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NoByRef(params ParameterInfo[] ps)
        {
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType.IsByRef)
                    return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CacheSentinel()
        {
        }

        private static IEnumerable<Assembly> EnumerateAssemblies()
        {
            Assembly[] all;
            try
            {
                all = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                yield break;
            }

            var preferred = new List<Assembly>();

            foreach (var asm in all)
            {
                if (asm == null || asm.IsDynamic)
                    continue;

                var name = asm.FullName ?? asm.GetName().Name ?? string.Empty;
                if (name.StartsWith("TaleWorlds.Core", StringComparison.Ordinal) ||
                    name.StartsWith("TaleWorlds.Engine", StringComparison.Ordinal) ||
                    name.StartsWith("TaleWorlds.MountAndBlade", StringComparison.Ordinal) ||
                    name.StartsWith("TaleWorlds.Library", StringComparison.Ordinal) ||
                    name.IndexOf("ExtremeRagdoll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    preferred.Add(asm);
                }
            }

            foreach (var asm in preferred)
                yield return asm;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Type TryGetType(Assembly asm, string name)
        {
            if (asm == null || string.IsNullOrEmpty(name))
                return null;
            try
            {
                return asm.GetType(name, false);
            }
            catch
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object BoxBoneArg(Type t, int idx)
        {
            if (t == typeof(int))
                return idx;
            if (t == typeof(uint))
                return (uint)Math.Max(0, idx);
            if (t == typeof(short))
                return (short)(idx < 0 ? 0 : (idx > short.MaxValue ? short.MaxValue : idx));
            if (t == typeof(ushort))
                return (ushort)(idx < 0 ? 0 : (idx > ushort.MaxValue ? ushort.MaxValue : idx));
            if (t == typeof(sbyte))
                return (sbyte)(idx < 0 ? 0 : (idx > sbyte.MaxValue ? sbyte.MaxValue : idx));
            if (t == typeof(byte))
                return (byte)(idx < 0 ? 0 : (idx > byte.MaxValue ? byte.MaxValue : idx));
            return idx;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DefaultBoneIndex(Skeleton s)
        {
            if (s == null)
                return 0;
            if (!_rag.TryGetValue(s, out var rag))
                rag = _rag.GetValue(s, _ => new Rag());
            if (rag.root >= 0)
                return rag.root;
            rag.root = 0;
            try
            {
                var t = s.GetType();
                var getCount = _boneCountCache.GetOrAdd(t, type =>
                {
                    try
                    {
                        return type.GetMethod("GetBoneCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ?? _boneMethodSentinel;
                    }
                    catch
                    {
                        return _boneMethodSentinel;
                    }
                });
                if (ReferenceEquals(getCount, _boneMethodSentinel))
                    getCount = null;

                var getName = _boneNameCache.GetOrAdd(t, type =>
                {
                    try
                    {
                        return type.GetMethod("GetBoneName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null) ?? _boneMethodSentinel;
                    }
                    catch
                    {
                        return _boneMethodSentinel;
                    }
                });
                if (ReferenceEquals(getName, _boneMethodSentinel))
                    getName = null;

                if (getCount != null && getName != null)
                {
                    var nObj = getCount.Invoke(s, null);
                    if (nObj is int n && n > 0)
                    {
                        var prefs = new[] { "pelvis", "hips", "root", "spine", "spine_0" };
                        for (int i = 0; i < n; i++)
                        {
                            var nameObj = getName.Invoke(s, new object[] { i });
                            var name = nameObj as string ?? string.Empty;
                            foreach (var p in prefs)
                            {
                                if (name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    rag.root = i;
                                    return rag.root;
                                }
                            }
                        }
                        rag.root = 0;
                        return rag.root;
                    }
                }
            }
            catch
            {
            }
            return rag.root;
        }

        private static void SetSkNames()
        {
            if (_sk1 != null)
                _sk1Name = FormatSkName(_sk1);
            else if (string.IsNullOrEmpty(_sk1Name))
                _sk1Name = "<null>";

            if (_sk2 != null)
                _sk2Name = FormatSkName(_sk2);
            else if (string.IsNullOrEmpty(_sk2Name))
                _sk2Name = "<null>";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string FormatSkName(MethodInfo mi)
        {
            if (mi == null)
                return "<null>";
            var typeName = mi.DeclaringType?.Name ?? "<null>";
            return typeName + "." + (mi.Name ?? "<null>");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SigMatches(MethodInfo mi, params Type[] want)
        {
            var ps = mi?.GetParameters();
            if (ps == null || ps.Length != want.Length)
                return false;
            for (int i = 0; i < want.Length; i++)
            {
                var pt = ps[i].ParameterType;
                var w = want[i];
                if (w == typeof(Skeleton))
                {
                    if (pt.IsByRef)
                        return false;
                    if (pt.ContainsGenericParameters || pt.IsGenericParameter)
                        return false;
                    if (!AcceptsSkeletonArg(pt))
                        return false;
                }
                if (pt == w)
                    continue;
                if (pt.IsByRef)
                {
                    var e = pt.GetElementType();
                    if (e == w)
                        continue;
                    if (e != null && e.IsAssignableFrom(w))
                        continue;
                    return false;
                }
                if (pt.IsAssignableFrom(w))
                    continue;
                return false;
            }
            return true;
        }

        internal static bool LooksDynamic(GameEntity ent)
        {
            if (ent == null)
                return false;
            // Prefer engine check if available
            try
            {
                if (_isDyn != null)
                    return _isDyn(ent);
            }
            catch
            {
                _isDyn = null;
                ER_Log.Info("ISDYN_EX");
            }
            // Fallback: explicit "Dynamic" beats "Static"; default = optimistic when unknown
            bool reject = false;
            bool none = false;
            string bfStr = null;
            string pdfStr = null;
            try { bfStr = ent.BodyFlag.ToString(); }
            catch { }

            if (!string.IsNullOrEmpty(bfStr))
            {
                if (bfStr.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (bfStr.IndexOf("Kinematic", StringComparison.OrdinalIgnoreCase) >= 0)
                    reject = true;
                if (bfStr.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0)
                    reject = true;
                if (bfStr.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0)
                    none = true;
            }

            try { pdfStr = ent.PhysicsDescBodyFlag.ToString(); }
            catch { }

            if (!string.IsNullOrEmpty(pdfStr))
            {
                if (pdfStr.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (pdfStr.IndexOf("Kinematic", StringComparison.OrdinalIgnoreCase) >= 0)
                    reject = true;
                if (pdfStr.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0)
                    reject = true;
                if (pdfStr.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0)
                    none = true;
            }

            bool haveFlagStrings = !string.IsNullOrEmpty(bfStr) || !string.IsNullOrEmpty(pdfStr);

            if (reject || none)
            {
                if (ER_Config.DebugLogging && haveFlagStrings)
                {
                    try
                    {
                        LogNoiseOncePer(1.0f, $"DYN_REJECT flags={bfStr ?? "<null>"}|{pdfStr ?? "<null>"}");
                    }
                    catch { }
                }

                return false;
            }

            // Be optimistic only when bounds look sane; prevents ownerless props from firing.
            try { return AabbSane(ent); }
            catch { return false; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetSaneAabb(GameEntity ent, out Vec3 mn, out Vec3 mx)
        {
            mn = default;
            mx = default;
            if (ent == null)
                return false;
            try
            {
                mn = ent.GetPhysicsBoundingBoxMin();
                mx = ent.GetPhysicsBoundingBoxMax();
                // Fail-closed: if junk/zero, DO NOT touch native physics.
                if (!ER_Math.IsFinite(in mn) || !ER_Math.IsFinite(in mx))
                    return false;

                var d = mx - mn;
                // reject zero/neg extents, NaNs, and absurd sizes (tunable cap)
                float maxExtent = ER_Config.MaxAabbExtent;
                if (float.IsNaN(maxExtent) || float.IsInfinity(maxExtent) || maxExtent <= 0f)
                    maxExtent = 1024f;
                bool ok = d.x > 0f && d.y > 0f && d.z > 0f
                          && d.LengthSquared > 1e-6f
                          && d.x <= maxExtent && d.y <= maxExtent && d.z <= maxExtent;
                if (!ok && ER_Config.DebugLogging)
                    LogNoiseOncePer(1.0f, $"AABB_REJECT d=({d.x:0.##},{d.y:0.##},{d.z:0.##}) cap={maxExtent:0.##}");
                return ok;
            }
            catch
            {
                // Also fail-closed on exceptions.
                mn = default;
                mx = default;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AabbSane(GameEntity ent)
        {
            return TryGetSaneAabb(ent, out _, out _);
        }

        private static bool TryWorldToLocalSafe(GameEntity ent, Vec3 worldImpulse, Vec3 contact, out Vec3 impLocal, out Vec3 posLocal)
        {
            impLocal = Vec3.Zero;
            posLocal = Vec3.Zero;
            if (ent == null)
                return false;
            try
            {
                return ER_Space.TryWorldToLocal(ent, worldImpulse, contact, out impLocal, out posLocal);
            }
            catch
            {
                impLocal = Vec3.Zero;
                posLocal = Vec3.Zero;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float TimeNow()
        {
            return Mission.Current?.CurrentTime ?? unchecked((uint)Environment.TickCount) * 0.001f;
        }

        // ---------- skeleton -> entity resolver ----------
        private static void EnsureSkToEntResolver()
        {
            if (_skToEnt != null)
                return;
            var resolver = _skToEnt ?? BuildSkToEnt();
            Interlocked.CompareExchange(ref _skToEnt, resolver, null);
        }

        private static Func<Skeleton, GameEntity> BuildSkToEnt()
        {
            var skType = typeof(Skeleton);
            try
            {
                // 1) property returning GameEntity
                var prop = skType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 .FirstOrDefault(p => typeof(GameEntity).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0);
                if (prop != null)
                    return sk => { try { return (GameEntity)prop.GetValue(sk); } catch { return null; } };

                // 2) field returning GameEntity
                var fld = skType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                .FirstOrDefault(f => typeof(GameEntity).IsAssignableFrom(f.FieldType));
                if (fld != null)
                    return sk => { try { return (GameEntity)fld.GetValue(sk); } catch { return null; } };

                // 3) method with no args returning GameEntity
                var m = skType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                              .FirstOrDefault(mi => mi.GetParameters().Length == 0 && typeof(GameEntity).IsAssignableFrom(mi.ReturnType));
                if (m != null)
                    return sk => { try { return (GameEntity)m.Invoke(sk, null); } catch { return null; } };
            }
            catch
            {
            }

            // last resort
            return _ => null;
        }

        // call this from your MissionBehavior.OnMissionTick()
        internal static void Tick()
        {
            if (_pending.IsEmpty)
                return;

            _carry.Clear();
            float now = TimeNow();
            while (_pending.TryDequeue(out var p))
            {
                if (now > p.expires)
                    continue;

                if (p.ent == null)
                {
                    if (p.sk != null)
                    {
                        EnsureSkToEntResolver();
                        try { p.ent = _skToEnt?.Invoke(p.sk); }
                        catch { p.ent = null; }
                    }
                    if (p.ent == null)
                        continue;
                }

                bool dyn = LooksDynamic(p.ent);
                bool needRag = p.sk != null;
                bool warm = !needRag;
                if (needRag)
                {
                    try { warm = RagWarm(p.sk, Ent2WarmupSeconds); }
                    catch { warm = false; }
                }
                bool hasBody = false;
                try { hasBody = p.ent.HasPhysicsBody(); } catch { hasBody = false; }

                // If we haven't started the warm window yet, start it once,
                // then ALWAYS wait one tick before attempting to apply impulses.
                if (needRag && !warm)
                {
                    bool started = false;
                    try { started = TryMarkRagStartOnce(p.sk); } catch { started = false; }

                    // Only do the expensive forcing once; subsequent ticks just wait out the warm window.
                    if (started)
                    {
                        try { p.sk?.ActivateRagdoll(); } catch { }
                        try { WakeDynamicBody(p.ent); } catch { }
                    }

                    _carry.Add(p);
                    continue;
                }

                // Past the warm window: keep trying to force ragdoll/wake, but don't hard-require dyn before attempting.
                // If there's no physics body at all yet, just retry later.
                if (needRag && (!dyn || !hasBody))
                {
                    try { p.sk?.ActivateRagdoll(); } catch { }
                    try { WakeDynamicBody(p.ent); } catch { }
                    try { dyn = LooksDynamic(p.ent); } catch { dyn = false; }
                    try { hasBody = p.ent.HasPhysicsBody(); } catch { hasBody = false; }

                    if (!hasBody)
                    {
                        _carry.Add(p);
                        continue;
                    }
                }
                else if (!needRag && (!dyn || !hasBody))
                {
                    _carry.Add(p);
                    continue;
                }

                var imp = p.wImp;
                CapMagnitude(ref imp, MaxWorldImpulse);
                if (!TryWorldToLocalSafe(p.ent, imp, p.wPos, out var impL, out var posL))
                {
                    _carry.Add(p);
                    continue;
                }
                if (!ER_Math.IsFinite(in impL) || !ER_Math.IsFinite(in posL))
                {
                    _carry.Add(p);
                    continue;
                }

                ClampLocalUp(ref impL);
                CapMagnitude(ref impL, MaxLocalImpulse);
                impL *= BurstScale;
                if (impL.LengthSquared < ImpulseTinySqThreshold)
                {
                    if (p.remaining > 0 && now <= p.expires)
                        _carry.Add(p);
                    continue;
                }

                WakeDynamicBody(p.ent);
                try
                {
                    if (_dEnt2Inst != null)
                        _dEnt2Inst(p.ent, impL, posL);
                    else if (_ent2Inst != null)
                        _ent2Inst.Invoke(p.ent, new object[] { impL, posL });
                    else if (_dEnt2 != null)
                        _dEnt2(p.ent, impL, posL);
                    else if (_ent2 != null)
                        _ent2.Invoke(null, new object[] { p.ent, impL, posL });
                }
                catch
                {
                }

                p.remaining--;
                if (p.remaining > 0 && now <= p.expires)
                    _carry.Add(p);
            }

            for (int i = 0; i < _carry.Count; i++)
                _pending.Enqueue(_carry[i]);

            if (ER_Config.DebugLogging)
            {
                try
                {
                    int pendingCount = _pending.Count;
                    int carryCount = _carry.Count;
                    if (pendingCount > 0 || carryCount > 0)
                        ER_Log.Info($"IMP_TICK pending={pendingCount} carry={carryCount}");
                }
                catch { }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WakeDynamicBody(GameEntity ent)
        {
            if (ent == null)
                return;
            try
            {
                if (_dWake != null)
                    _dWake(ent);
                else
                    _wake?.Invoke(null, new object[] { ent });
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WakeDynamicBodyPublic(GameEntity ent)
        {
            WakeDynamicBody(ent);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Clamp01(float f) => float.IsNaN(f) || float.IsInfinity(f) ? 0f : (f < 0f ? 0f : (f > 1f ? 1f : f));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClampUpCone(ref Vec3 v, float minFrac, float maxFrac)
        {
            if (!IsValidVec(in v))
                return;
            if (v.z < 0f)
                v.z = 0f;

            float l2 = v.LengthSquared;
            if (l2 <= 1e-9f)
                return;

            float len = MathF.Sqrt(l2);
            float minZ = Clamp01(minFrac) * len;
            float maxZ = Clamp01(maxFrac) * len;
            if (maxZ > 0f && minZ > maxZ)
                minZ = maxZ;

            if (v.z < minZ)
                v.z = minZ;
            if (v.z > maxZ)
                v.z = maxZ;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClampWorldUp(ref Vec3 w)
        {
            if (!ER_Math.IsFinite(in w))
                return;
            ClampUpCone(ref w, ER_Config.CorpseLaunchMinUpFraction, ER_Config.CorpseLaunchMaxUpFraction);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClampLocalUp(ref Vec3 v)
        {
            if (!IsValidVec(in v))
                return;
            ClampUpCone(ref v, ER_Config.CorpseLaunchMinUpFraction, ER_Config.CorpseLaunchMaxUpFraction);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CapMagnitude(ref Vec3 v, float cap)
        {
            if (!IsValidVec(in v) || cap <= 0f)
                return;
            float l2 = v.LengthSquared;
            float c2 = cap * cap;
            if (l2 > c2)
            {
                float s = cap / MathF.Sqrt(l2);
                v.x *= s;
                v.y *= s;
                v.z *= s;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void LiftContactFloor(GameEntity ent, ref Vec3 c)
        {
            if (ent == null || !LooksDynamic(ent))
                return;
            try
            {
                var mn = ent.GetPhysicsBoundingBoxMin();
                float zMin = mn.z + ER_Config.CorpseLaunchZNudge;
                if (c.z < zMin)
                    c.z = zMin;
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkRagStart(Skeleton sk)
        {
            if (sk == null)
                return;
            try
            {
                var rag = _rag.GetValue(sk, _ => new Rag());
                rag.t = TimeNow();
                rag.root = -1;
                rag.warmStarted = true;
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryMarkRagStartOnce(Skeleton sk)
        {
            if (sk == null)
                return false;

            var now = TimeNow();
            try
            {
                if (!_rag.TryGetValue(sk, out var rag) || rag == null)
                {
                    try
                    {
                        _rag.Add(sk, new Rag { t = now, root = -1, warmStarted = true });
                        return true;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }

                if (rag.warmStarted)
                    return false;

                rag.t = now;
                rag.root = -1;
                rag.warmStarted = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool RagWarm(Skeleton sk, float minSeconds)
        {
            if (sk == null)
                return false;
            try
            {
                if (!_rag.TryGetValue(sk, out var rag) || rag == null)
                    return false;
                if (!rag.warmStarted)
                    return false;
                return (TimeNow() - rag.t) >= minSeconds;
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float RagWarmSeconds(Skeleton sk)
        {
            if (sk == null)
                return 0f;
            try
            {
                if (_rag.TryGetValue(sk, out var rag) && rag != null && rag.warmStarted)
                {
                    float delta = TimeNow() - rag.t;
                    return delta >= 0f ? delta : 0f;
                }
            }
            catch { }
            return 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Ensure()
        {
            if (Volatile.Read(ref _ensured))
                return;

            lock (_ensureLock)
            {
                if (Volatile.Read(ref _ensured))
                    return;
                _skCandLogCount = 0;

                var entityAsm = typeof(GameEntity).Assembly;
                var ext = entityAsm.GetType("TaleWorlds.Engine.GameEntityPhysicsExtensions");
                var m = typeof(GameEntity).GetMethod("IsDynamicBody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    try { _isDyn = (Func<GameEntity, bool>)m.CreateDelegate(typeof(Func<GameEntity, bool>)); }
                    catch { _isDyn = null; }
                }
                if (ext != null)
                {
                    _ent3 = ext.GetMethod("ApplyImpulseToDynamicBody", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                                          new[] { typeof(GameEntity), typeof(Vec3), typeof(Vec3), typeof(bool) }, null);
                    if (_ent3 != null)
                    {
                        try { _dEnt3 = (Action<GameEntity, Vec3, Vec3, bool>)_ent3.CreateDelegate(typeof(Action<GameEntity, Vec3, Vec3, bool>)); }
                        catch { _dEnt3 = null; }
                    }

                    _ent2 = ext.GetMethod("ApplyLocalImpulseToDynamicBody", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                                          new[] { typeof(GameEntity), typeof(Vec3), typeof(Vec3) }, null);
                    if (_ent2 != null)
                    {
                        try { _dEnt2 = (Action<GameEntity, Vec3, Vec3>)_ent2.CreateDelegate(typeof(Action<GameEntity, Vec3, Vec3>)); }
                        catch { _dEnt2 = null; }
                    }

                    _ent1 = ext.GetMethod("ApplyForceToDynamicBody", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                                          new[] { typeof(GameEntity), typeof(Vec3) }, null);
                    if (_ent1 != null)
                    {
                        try { _dEnt1 = (Action<GameEntity, Vec3>)_ent1.CreateDelegate(typeof(Action<GameEntity, Vec3>)); }
                        catch { _dEnt1 = null; }
                    }

                    _wake = ext.GetMethod("WakeUpDynamicBody", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(GameEntity) }, null);
                    if (_wake != null)
                    {
                        try { _dWake = (Action<GameEntity>)_wake.CreateDelegate(typeof(Action<GameEntity>)); }
                        catch { _dWake = null; }
                    }
                }

                var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var skeletonAsm = typeof(Skeleton).Assembly; // avoid shadowing
                var assemblies = EnumerateAssemblies().ToArray();
                var holderTypes = new List<Type>();
                var seenHolders = new HashSet<Type>();

                void AddHolder(Type type)
                {
                    if (type != null && seenHolders.Add(type))
                        holderTypes.Add(type);
                }

                AddHolder(TryGetType(skeletonAsm, "TaleWorlds.Engine.SkeletonPhysicsExtensions"));
                AddHolder(TryGetType(skeletonAsm, "TaleWorlds.Engine.SkeletonExtensions"));
                AddHolder(TryGetType(skeletonAsm, "TaleWorlds.Engine.ISkeletonExtensions"));

                foreach (var asm in assemblies)
                {
                    AddHolder(TryGetType(asm, "TaleWorlds.Engine.ManagedExtensions.SkeletonExtensions"));
                    AddHolder(TryGetType(asm, "TaleWorlds.Engine.ManagedExtensions.ISkeletonExtensions"));
                }

                AddHolder(typeof(Skeleton));

                foreach (var holder in holderTypes)
                {
                    MethodInfo[] methods;
                    try { methods = holder.GetMethods(flags); }
                    catch { continue; }

                    foreach (var mi in methods)
                    {
                        if (mi == null || mi.ContainsGenericParameters)
                            continue;
                        if (_sk2 == null && SigMatches(mi, typeof(Skeleton), typeof(Vec3), typeof(Vec3)))
                            _sk2 = mi;
                        if (_sk1 == null && SigMatches(mi, typeof(Skeleton), typeof(Vec3)))
                            _sk1 = mi;
                        if (_sk1 != null && _sk2 != null)
                            break;
                    }

                    if (_sk1 != null && _sk2 != null)
                        break;
                }
                if ((_sk1 == null || _sk2 == null) && (_dSk1 == null || _dSk2 == null))
                {
                    try
                    {
                        foreach (var asm in assemblies)
                        {
                            if ((_sk1 != null && _sk2 != null) || (_dSk1 != null && _dSk2 != null))
                                break;

                            Type[] types;
                            try { types = asm.GetTypes(); }
                            catch (ReflectionTypeLoadException ex) { types = ex.Types?.Where(x => x != null).ToArray() ?? Array.Empty<Type>(); }
                            catch { continue; }

                            foreach (var t in types)
                            {
                                MethodInfo[] methods;
                                try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance); }
                                catch { continue; }

                                foreach (var mi in methods)
                                {
                                    if (mi == null || mi.ContainsGenericParameters)
                                        continue;

                                    var ps = mi.GetParameters();

                                    // static extension we can actually call with a Skeleton instance
                                    if (mi.IsStatic && ps.Length >= 2 && AcceptsSkeletonArg(ps[0].ParameterType))
                                    {
                                        if (ps.Length == 3 && IsVec3(ps[1].ParameterType) && IsVec3(ps[2].ParameterType) && _sk2 == null)
                                            _sk2 = mi;
                                        else if (ps.Length == 2 && IsVec3(ps[1].ParameterType) && _sk1 == null)
                                            _sk1 = mi;
                                        // bone-indexed static (Sk, <Vec3/IntLike> x2/1, <Vec3/IntLike> x2/1)
                                        else if (_dSk2 == null && ps.Length == 4 && NoByRef(ps[1], ps[2], ps[3]))
                                        {
                                            var miLocal = mi;
                                            if (!AcceptsSkeletonArg(ps[0].ParameterType))
                                                goto AfterStaticWrap;
                                            // Sk, int, V3, V3
                                            if (IsIntLike(ps[1].ParameterType) && IsVec3(ps[2].ParameterType) && IsVec3(ps[3].ParameterType))
                                            {
                                                var boneT = ps[1].ParameterType;
                                                _dSk2 = (Skeleton s, Vec3 a, Vec3 b) => { try { miLocal.Invoke(null, new object[] { s, BoxBoneArg(boneT, DefaultBoneIndex(s)), a, b }); } catch { } };
                                                _sk2Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                            // Sk, V3, int, V3
                                            else if (IsVec3(ps[1].ParameterType) && IsIntLike(ps[2].ParameterType) && IsVec3(ps[3].ParameterType))
                                            {
                                                var boneT = ps[2].ParameterType;
                                                _dSk2 = (Skeleton s, Vec3 a, Vec3 b) => { try { miLocal.Invoke(null, new object[] { s, a, BoxBoneArg(boneT, DefaultBoneIndex(s)), b }); } catch { } };
                                                _sk2Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                            // Sk, V3, V3, int
                                            else if (IsVec3(ps[1].ParameterType) && IsVec3(ps[2].ParameterType) && IsIntLike(ps[3].ParameterType))
                                            {
                                                var boneT = ps[3].ParameterType;
                                                _dSk2 = (Skeleton s, Vec3 a, Vec3 b) => { try { miLocal.Invoke(null, new object[] { s, a, b, BoxBoneArg(boneT, DefaultBoneIndex(s)) }); } catch { } };
                                                _sk2Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                        AfterStaticWrap: ;
                                        }
                                        else if (_dSk1 == null && ps.Length == 3 && NoByRef(ps[1], ps[2]))
                                        {
                                            var miLocal = mi;
                                            if (!AcceptsSkeletonArg(ps[0].ParameterType))
                                                goto AfterStaticWrap1;
                                            // Sk, int, V3
                                            if (IsIntLike(ps[1].ParameterType) && IsVec3(ps[2].ParameterType))
                                            {
                                                var boneT = ps[1].ParameterType;
                                                _dSk1 = (Skeleton s, Vec3 a) => { try { miLocal.Invoke(null, new object[] { s, BoxBoneArg(boneT, DefaultBoneIndex(s)), a }); } catch { } };
                                                _sk1Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                            // Sk, V3, int
                                            else if (IsVec3(ps[1].ParameterType) && IsIntLike(ps[2].ParameterType))
                                            {
                                                var boneT = ps[2].ParameterType;
                                                _dSk1 = (Skeleton s, Vec3 a) => { try { miLocal.Invoke(null, new object[] { s, a, BoxBoneArg(boneT, DefaultBoneIndex(s)) }); } catch { } };
                                                _sk1Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                        AfterStaticWrap1: ;
                                        }
                                    }
                                    // instance we can call on a Skeleton object
                                    else if (!mi.IsStatic && DeclTypeAcceptsSkeleton(mi.DeclaringType))
                                    {
                                        if (ps.Length == 2 && IsVec3(ps[0].ParameterType) && IsVec3(ps[1].ParameterType) && _sk2 == null)
                                            _sk2 = mi;
                                        else if (ps.Length == 1 && IsVec3(ps[0].ParameterType) && _sk1 == null)
                                            _sk1 = mi;
                                        // bone-indexed instance (int/V3 permutations)
                                        else if (_dSk2 == null && ps.Length == 3 && NoByRef(ps[0], ps[1], ps[2]))
                                        {
                                            var miLocal = mi;
                                            // int, V3, V3
                                            if (IsIntLike(ps[0].ParameterType) && IsVec3(ps[1].ParameterType) && IsVec3(ps[2].ParameterType))
                                            {
                                                var boneT = ps[0].ParameterType;
                                                _dSk2 = (Skeleton s, Vec3 a, Vec3 b) => { try { miLocal.Invoke(s, new object[] { BoxBoneArg(boneT, DefaultBoneIndex(s)), a, b }); } catch { } };
                                                _sk2Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                            // V3, int, V3
                                            else if (IsVec3(ps[0].ParameterType) && IsIntLike(ps[1].ParameterType) && IsVec3(ps[2].ParameterType))
                                            {
                                                var boneT = ps[1].ParameterType;
                                                _dSk2 = (Skeleton s, Vec3 a, Vec3 b) => { try { miLocal.Invoke(s, new object[] { a, BoxBoneArg(boneT, DefaultBoneIndex(s)), b }); } catch { } };
                                                _sk2Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                            // V3, V3, int
                                            else if (IsVec3(ps[0].ParameterType) && IsVec3(ps[1].ParameterType) && IsIntLike(ps[2].ParameterType))
                                            {
                                                var boneT = ps[2].ParameterType;
                                                _dSk2 = (Skeleton s, Vec3 a, Vec3 b) => { try { miLocal.Invoke(s, new object[] { a, b, BoxBoneArg(boneT, DefaultBoneIndex(s)) }); } catch { } };
                                                _sk2Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                        }
                                        else if (_dSk1 == null && ps.Length == 2 && NoByRef(ps[0], ps[1]))
                                        {
                                            var miLocal = mi;
                                            // int, V3
                                            if (IsIntLike(ps[0].ParameterType) && IsVec3(ps[1].ParameterType))
                                            {
                                                var boneT = ps[0].ParameterType;
                                                _dSk1 = (Skeleton s, Vec3 a) => { try { miLocal.Invoke(s, new object[] { BoxBoneArg(boneT, DefaultBoneIndex(s)), a }); } catch { } };
                                                _sk1Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                            // V3, int
                                            else if (IsVec3(ps[0].ParameterType) && IsIntLike(ps[1].ParameterType))
                                            {
                                                var boneT = ps[1].ParameterType;
                                                _dSk1 = (Skeleton s, Vec3 a) => { try { miLocal.Invoke(s, new object[] { a, BoxBoneArg(boneT, DefaultBoneIndex(s)) }); } catch { } };
                                                _sk1Name = "[wrapped] " + (miLocal.DeclaringType?.Name ?? "<null>") + "." + (miLocal.Name ?? "<null>");
                                            }
                                        }
                                    }

                                    if ((_sk1 != null && _sk2 != null) || (_dSk1 != null && _dSk2 != null))
                                        break;

                                    if (ER_Config.DebugLogging && _skCandLogCount < 12)
                                    {
                                        try
                                        {
                                            int pc = ps?.Length ?? -1;
                                            if (pc >= 1 && pc <= 4)
                                            {
                                                Type holder = mi.IsStatic ? (ps != null && ps.Length > 0 ? ps[0].ParameterType : null) : mi.DeclaringType;
                                                if (holder != null && holder.IsByRef)
                                                    holder = holder.GetElementType();
                                                if (holder != typeof(object) && holder != typeof(ValueType))
                                                {
                                                    ER_Log.Info($"SKEL_CANDIDATE {t.FullName}.{mi} | static={mi.IsStatic} params={pc}");
                                                    _skCandLogCount++;
                                                }
                                            }
                                        }
                                        catch { }
                                    }

                                    if ((_sk1 != null && _sk2 != null) || (_dSk1 != null && _dSk2 != null))
                                        break;
                                }

                                if ((_sk1 != null && _sk2 != null) || (_dSk1 != null && _dSk2 != null))
                                    break;
                            }

                            if ((_sk1 != null && _sk2 != null) || (_dSk1 != null && _dSk2 != null))
                                break;
                        }
                    }
                    catch { }
                }
                SetSkNames(); // also have names for wrapped delegates
                try
                {
                    if (_sk2 != null)
                    {
                        var ps = _sk2.GetParameters();
                        if (ps.Length > 1 && !ps[1].ParameterType.IsByRef)
                        {
                            var holder = _sk2.IsStatic ? ps[0].ParameterType : _sk2.DeclaringType;
                            if (holder == typeof(Skeleton))
                            {
                                var d = (Action<Skeleton, Vec3, Vec3>)_sk2.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>));
                                if (d != null)
                                {
                                    _dSk2 = d;
                                    _sk2Name = FormatSkName(_sk2);
                                }
                            }
                        }
                    }
                }
                catch { /* keep any wrapped _dSk2 */ }
                try
                {
                    if (_sk1 != null)
                    {
                        var ps = _sk1.GetParameters();
                        if (ps.Length > 0 && !ps[0].ParameterType.IsByRef)
                        {
                            var holder = _sk1.IsStatic ? ps[0].ParameterType : _sk1.DeclaringType;
                            if (holder == typeof(Skeleton))
                            {
                                var d = (Action<Skeleton, Vec3>)_sk1.CreateDelegate(typeof(Action<Skeleton, Vec3>));
                                if (d != null)
                                {
                                    _dSk1 = d;
                                    _sk1Name = FormatSkName(_sk1);
                                }
                            }
                        }
                    }
                }
                catch { /* keep any wrapped _dSk1 */ }

                if (ER_Config.DebugLogging)
                    ER_Log.Info($"SK_BIND sk2={_sk2Name} dSk2={_dSk2 != null}  sk1={_sk1Name} dSk1={_dSk1 != null}");

                var ge = typeof(GameEntity);

                _ent3Inst = ge.GetMethod("ApplyImpulseToDynamicBody",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Vec3), typeof(Vec3), typeof(bool) }, null);
                if (_ent3Inst != null)
                {
                    try { _dEnt3Inst = (Action<GameEntity, Vec3, Vec3, bool>)_ent3Inst.CreateDelegate(typeof(Action<GameEntity, Vec3, Vec3, bool>)); }
                    catch { _dEnt3Inst = null; }
                }

                _ent2Inst = ge.GetMethod("ApplyLocalImpulseToDynamicBody",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Vec3), typeof(Vec3) }, null);
                if (_ent2Inst != null)
                {
                    try { _dEnt2Inst = (Action<GameEntity, Vec3, Vec3>)_ent2Inst.CreateDelegate(typeof(Action<GameEntity, Vec3, Vec3>)); }
                    catch { _dEnt2Inst = null; }
                }

                _ent1Inst = ge.GetMethod("ApplyForceToDynamicBody",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Vec3) }, null);
                if (_ent1Inst != null)
                {
                    try { _dEnt1Inst = (Action<GameEntity, Vec3>)_ent1Inst.CreateDelegate(typeof(Action<GameEntity, Vec3>)); }
                    catch { _dEnt1Inst = null; }
                }

                var sk = typeof(Skeleton);
                if (_sk2 == null)
                {
                    _sk2 = sk.GetMethod("ApplyForceToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                         ?? sk.GetMethod("AddForceToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                         ?? sk.GetMethod("AddImpulseToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                         ?? sk.GetMethod("ApplyLocalImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                         ?? sk.GetMethod("ApplyImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null);
                }
                if (_sk2 == null)
                {
                    foreach (var mi in sk.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var ps = mi.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(Vec3) && ps[1].ParameterType == typeof(Vec3))
                        {
                            var n = mi.Name.ToLowerInvariant();
                            if ((n.Contains("force") || n.Contains("impulse")) && (n.Contains("bone") || n.Contains("ragdoll")))
                            {
                                _sk2 = mi;
                                break;
                            }
                        }
                    }
                }
                if (_sk2 != null && _dSk2 == null)
                {
                    try
                    {
                        var ps = _sk2.GetParameters();
                        if (ps.Length > 1 && !ps[1].ParameterType.IsByRef)
                        {
                            var holder = _sk2.IsStatic ? ps[0].ParameterType : _sk2.DeclaringType;
                            if (holder == typeof(Skeleton))
                                _dSk2 = (Action<Skeleton, Vec3, Vec3>)_sk2.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>));
                        }
                    }
                catch { /* keep any wrapped _dSk2 */ }
                }
                if (_sk1 == null)
                {
                    _sk1 = sk.GetMethod("ApplyForceToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3) }, null)
                         ?? sk.GetMethod("AddForceToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3) }, null)
                         ?? sk.GetMethod("AddImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3) }, null);
                }
                if (_sk1 == null)
                {
                    foreach (var mi in sk.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var ps = mi.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(Vec3))
                        {
                            var n = mi.Name.ToLowerInvariant();
                            if ((n.Contains("force") || n.Contains("impulse")) && (n.Contains("bone") || n.Contains("ragdoll")))
                            {
                                _sk1 = mi;
                                break;
                            }
                        }
                    }
                }
                if (_sk1 != null && _dSk1 == null)
                {
                    try
                    {
                        var ps = _sk1.GetParameters();
                        if (ps.Length > 0 && !ps[0].ParameterType.IsByRef)
                        {
                            var holder = _sk1.IsStatic ? ps[0].ParameterType : _sk1.DeclaringType;
                            if (holder == typeof(Skeleton))
                                _dSk1 = (Action<Skeleton, Vec3>)_sk1.CreateDelegate(typeof(Action<Skeleton, Vec3>));
                        }
                    }
                catch { /* keep any wrapped _dSk1 */ }
                }

                // --- static Skeleton extension fallbacks ---
                if (_sk2 == null || _dSk2 == null || _sk1 == null || _dSk1 == null)
                {
                    var skAsm = typeof(Skeleton).Assembly;
                    var skExt =
                        skAsm.GetType("TaleWorlds.Engine.SkeletonExtensions") ??
                        skAsm.GetType("TaleWorlds.Engine.Extensions.SkeletonExtensions") ??
                        skAsm.GetType("TaleWorlds.Engine.ManagedExtensions.SkeletonExtensions") ??
                        skAsm.GetType("TaleWorlds.Engine.SkeletonPhysicsExtensions");

                    if (skExt != null)
                    {
                        // (Skeleton, Vec3, Vec3)
                        _sk2 = _sk2 ?? (
                            skExt.GetMethod("ApplyForceToBoneAtPos", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3), typeof(Vec3) }, null)
                         ?? skExt.GetMethod("AddForceToBoneAtPos", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3), typeof(Vec3) }, null)
                         ?? skExt.GetMethod("AddImpulseToBoneAtPos", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3), typeof(Vec3) }, null)
                         ?? skExt.GetMethod("ApplyLocalImpulseToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3), typeof(Vec3) }, null)
                         ?? skExt.GetMethod("ApplyImpulseToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3), typeof(Vec3) }, null));
                        if (_sk2 != null && _dSk2 == null)
                        {
                            var sk2ParamCount = _sk2.GetParameters().Length;
                            _dSk2 = (Skeleton s, Vec3 a, Vec3 b) =>
                            {
                                try
                                {
                                    if (_sk2.IsStatic)
                                    {
                                        if (sk2ParamCount == 3)
                                            _sk2.Invoke(null, new object[] { s, a, b });
                                        else
                                            _sk2.Invoke(null, new object[] { a, b });
                                    }
                                    else
                                    {
                                        _sk2.Invoke(s, new object[] { a, b });
                                    }
                                }
                                catch { }
                            };
                        }

                        // (Skeleton, Vec3)
                        _sk1 = _sk1 ?? (
                            skExt.GetMethod("ApplyForceToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3) }, null)
                         ?? skExt.GetMethod("AddForceToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3) }, null)
                         ?? skExt.GetMethod("AddImpulseToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                            new[] { typeof(Skeleton), typeof(Vec3) }, null));
                        if (_sk1 != null && _dSk1 == null)
                        {
                            var sk1ParamCount = _sk1.GetParameters().Length;
                            _dSk1 = (Skeleton s, Vec3 a) =>
                            {
                                try
                                {
                                    if (_sk1.IsStatic)
                                    {
                                        if (sk1ParamCount == 2)
                                            _sk1.Invoke(null, new object[] { s, a });
                                        else
                                            _sk1.Invoke(null, new object[] { a });
                                    }
                                    else
                                    {
                                        _sk1.Invoke(s, new object[] { a });
                                    }
                                }
                                catch { }
                            };
                        }
                    }
                }

                if ((_dSk1 == null && _sk1 == null) || (_dSk2 == null && _sk2 == null))
                {
                    try
                    {
                        foreach (var t in entityAsm.GetTypes())
                        {
                            var name = t.FullName;
                            if (name != null && name.IndexOf("Skeleton", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foreach (var mInfo in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                                {
                                    if (mInfo.Name.IndexOf("Impulse", StringComparison.OrdinalIgnoreCase) >= 0)
                                        ER_Log.Info($"SKEL_API_CAND {name}.{mInfo}");
                                }
                            }
                        }
                    }
                    catch { }
                }

                SetSkNames();
                if (ER_Config.DebugLogging)
                {
                    _ent3Name = _ent3?.Name; _ent2Name = _ent2?.Name; _ent1Name = _ent1?.Name;
                    ER_Log.Info($"IMP_BIND ent3:{_ent3!=null}|{_dEnt3!=null} inst:{_ent3Inst!=null}|{_dEnt3Inst!=null} " +
                                $"ent2:{_ent2!=null}|{_dEnt2!=null} inst:{_ent2Inst!=null}|{_dEnt2Inst!=null} " +
                                $"ent1:{_ent1!=null}|{_dEnt1!=null} inst:{_ent1Inst!=null}|{_dEnt1Inst!=null} " +
                                $"sk2:{_sk2!=null}|{_dSk2!=null} sk1:{_sk1!=null}|{_dSk1!=null} isDyn:{_isDyn!=null}");
                    ER_Log.Info($"IMP_BIND_NAMES ent3:{_ent3Name} ent2:{_ent2Name} ent1:{_ent1Name} sk2:{_sk2Name} sk1:{_sk1Name}");
                }

                Volatile.Write(ref _ensured, true);
            } // end lock
        } // end Ensure

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaybeReEnable()
        {
                float now = TimeNow();
                if (_ent1Unsafe && _disableUntil[1] <= now) _ent1Unsafe = false;
                if (_ent2Unsafe && _disableUntil[2] <= now) _ent2Unsafe = false;
                if (_ent3Unsafe && _disableUntil[3] <= now) _ent3Unsafe = false;
                if (_sk1Unsafe && _disableUntil[4] <= now) _sk1Unsafe = false;
                if (_sk2Unsafe && _disableUntil[5] <= now) _sk2Unsafe = false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void MarkUnsafe(int which, Exception ex)
            {
                if (ex is TargetInvocationException tie && tie.InnerException != null)
                    ex = tie.InnerException;

                if (!(ex is AccessViolationException))
                    return;

                float now = TimeNow();
                if (which < 1 || which > 5)
                    return;
                if (_disableUntil[which] > now)
                    return;

                int count = ++_avCount[which];
                if (count < 3)
                    return;

                _avCount[which] = 0;
                _disableUntil[which] = now + 5.0f;

                switch (which)
                {
                    case 1: _ent1Unsafe = true; break;
                    case 2: _ent2Unsafe = true; break;
                    case 3: _ent3Unsafe = true; break;
                    case 4: _sk1Unsafe = true; break;
                    case 5: _sk2Unsafe = true; break;
                }

                ER_Log.Info($"IMPULSE_DISABLE route#{which} for 5.0s after {ex.GetType().Name}");
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool ShouldLog(float now, float minDelta = 0.05f)
            {
                if (now < _lastImpulseLog)
                    _lastImpulseLog = now - minDelta;
                if (now - _lastImpulseLog < minDelta)
                    return false;
                _lastImpulseLog = now;
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void Log(string message)
            {
                if (ER_Config.DebugLogging && ShouldLog(TimeNow()))
                    ER_Log.Info(message);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void LogAlways(string message)
            {
                if (ER_Config.DebugLogging)
                    ER_Log.Info(message);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void LogNoiseOncePer(float minDelta, string message)
            {
                float now = TimeNow();
                if (now - _lastNoiseLog < minDelta)
                    return;
                Log(message);
                _lastNoiseLog = now;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void LogFailure(string context, Exception ex)
            {
                // Do not fully hide AVs; throttle them
                if (ex is TargetInvocationException tie && tie.InnerException != null)
                    ex = tie.InnerException;
                if (ex is AccessViolationException)
                {
                    float now = TimeNow();
                    if (now - _lastAvLog >= 0.25f)
                    {
                        _lastAvLog = now;
                        LogAlways($"IMPULSE_FAIL_AV {context}");
                    }
                    return;
                }
                Log($"IMPULSE_FAIL {context}: {ex.GetType().Name}");
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsValidVec(in Vec3 v) => ER_Math.IsFinite(in v);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool TryResolveContact(GameEntity ent, ref Vec3 contact)
            {
                if (IsValidVec(contact) && contact.LengthSquared >= ContactTinySqThreshold)
                    return true;

                if (ent != null)
                {
                    try
                    {
                        MatrixFrame frame;
                        try { frame = ent.GetGlobalFrame(); }
                        catch
                        {
                            try { frame = ent.GetFrame(); }
                            catch { frame = default; }
                        }
                        var fallback = frame.origin;
                        if (IsValidVec(fallback))
                        {
                            fallback.z += ER_Config.CorpseLaunchContactHeight;
                            var l2 = fallback.LengthSquared;
                            if (IsValidVec(fallback) && l2 >= ContactTinySqThreshold && !float.IsNaN(l2) && !float.IsInfinity(l2))
                            {
                                contact = fallback;
                                LiftContactFloor(ent, ref contact);
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // fall through to false
                    }
                }

                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool TryFallbackContact(GameEntity ent, Skeleton skel, ref Vec3 contact)
            {
                if (ent == null)
                    return false;

                _ = skel;

                try
                {
                    var mn = ent.GetPhysicsBoundingBoxMin();
                    var mx = ent.GetPhysicsBoundingBoxMax();
                    if (ER_Math.IsFinite(in mn) && ER_Math.IsFinite(in mx))
                    {
                        var d = mx - mn;
                        if (d.x > 0f && d.y > 0f && d.z > 0f && d.LengthSquared > 1e-6f)
                        {
                            var c = (mn + mx) * 0.5f;
                            c.z += ER_Config.CorpseLaunchContactHeight;
                            contact = c;
                            LiftContactFloor(ent, ref contact);
                            return true;
                        }
                    }
                }
                catch
                {
                }

                try
                {
                    MatrixFrame f;
                    try { f = ent.GetGlobalFrame(); }
                    catch { f = ent.GetFrame(); }
                    var origin = f.origin;
                    if (ER_Math.IsFinite(in origin))
                    {
                        origin.z += ER_Config.CorpseLaunchContactHeight;
                        contact = origin;
                        LiftContactFloor(ent, ref contact);
                        return true;
                    }
                }
                catch
                {
                }

                return false;
            }

            public static bool TryImpulse(GameEntity ent, Skeleton skel, in Vec3 worldImpulse, in Vec3 worldPos)
            {
                Ensure();
                EnsureSkToEntResolver();
                MaybeReEnable();
                // show every exception for this attempt (disable throttling)
                _lastImpulseLog = float.NegativeInfinity;
                // Belt-and-suspenders: clamp world-space impulse before any transforms
                var impW = worldImpulse;
                if (!IsValidVec(in impW))
                {
                    Log("IMPULSE_SKIP invalid impulse");
                    return false;
                }
                CapMagnitude(ref impW, MaxWorldImpulse);
                ClampWorldUp(ref impW);
                float l2 = impW.LengthSquared;
                if (l2 < ImpulseTinySqThreshold)
                {
                    Log("IMPULSE_SKIP tiny impulse");
                    return false;
                }
                var contact = worldPos;
                bool hasEnt = ent != null;
                // If we only have a Skeleton, try to recover its GameEntity and use entity routes.
                if (!hasEnt && skel != null)
                {
                    var fromSk = _skToEnt?.Invoke(skel);
                    if (fromSk != null)
                    {
                        ent = fromSk;
                        hasEnt = true;
                        if (ER_Config.DebugLogging && !_skEntLogged)
                        {
                            ER_Log.Info($"SK→ENT ok: {ent?.Name ?? "<unnamed>"}");
                            _skEntLogged = true;
                        }
                    }
                }
                Vec3 bboxMin = default;
                Vec3 bboxMax = default;
                bool aabbOk = hasEnt && TryGetSaneAabb(ent, out bboxMin, out bboxMax);
                bool dynOk = hasEnt && LooksDynamic(ent);

                if (!_bindLogged)
                {
                    _bindLogged = true;
                    Log($"SK_BIND sk2={( _sk2?.DeclaringType?.FullName + "." + _sk2?.Name) ?? "null"} sk1={( _sk1?.DeclaringType?.FullName + "." + _sk1?.Name) ?? "null"}");
                    if (ER_Config.DebugLogging)
                    {
                        ER_Log.Info($"IMP_BIND_NAMES ent3Inst={_ent3Inst?.Name ?? "null"} ent3={_ent3?.Name ?? "null"} ent2Inst={_ent2Inst?.Name ?? "null"} ent2={_ent2?.Name ?? "null"} sk2={_sk2?.Name ?? "null"} sk1={_sk1?.Name ?? "null"}");
                    }
                }
                if (ER_Config.DebugLogging)
                {
                    ER_Log.Info($"IMP_TRY haveEnt={(ent!=null)} haveSk={(skel!=null)} imp2={l2:0} pos2={worldPos.LengthSquared:0}");
                }

                bool wasRag = false;
                try { wasRag = ER_DeathBlastBehavior.IsRagdollActiveFast(skel); } catch { }
                bool ragActive = wasRag;
                if (!wasRag)
                    MarkRagStart(skel);

                // Do NOT assume ragdoll just because we have a Skeleton reference.
                // Applying impulses to a non-ragdolled skeleton can slide a frozen pose through the world.
                try { skel?.ActivateRagdoll(); } catch { }
                try { ragActive = ragActive || ER_DeathBlastBehavior.IsRagdollActiveFast(skel); } catch { }
                if (ragActive)
                {
                    try { skel?.ForceUpdateBoneFrames(); } catch { }
                }

                try { ent?.ActivateRagdoll(); } catch { }

                // If ragdoll is (now) active but the body stayed kinematic, nudge it awake safely.
                if (hasEnt && ragActive && !dynOk)
                {
                    try
                    {
                        WakeDynamicBody(ent);              // safe wake helper, no ent1 force
                        try { dynOk = LooksDynamic(ent); } // re-check once
                        catch { }
                        if (ER_Config.DebugLogging)
                        {
                            Log($"POST_WAKE dynOk={dynOk}");
                            try { ER_Log.Info($"POST_WAKE flags Body={ent.BodyFlag} Phys={ent.PhysicsDescBodyFlag}"); }
                            catch { }
                        }
                    }
                    catch { }
                }

                bool haveContact = false;
                if (ER_Math.IsFinite(in contact) && aabbOk)
                {
                    try
                    {
                        var mn = bboxMin;
                        var mx = bboxMax;
                        haveContact = contact.x >= mn.x - 0.5f && contact.x <= mx.x + 0.5f &&
                                      contact.y >= mn.y - 0.5f && contact.y <= mx.y + 0.5f &&
                                      contact.z >= mn.z - 0.5f && contact.z <= mx.z + 0.5f;
                        if (haveContact)
                            LiftContactFloor(ent, ref contact);
                    }
                    catch
                    {
                        haveContact = false;
                    }
                }
                if (!haveContact)
                {
                    contact = default;
                    haveContact = TryResolveContact(ent, ref contact);
                }
                if (!haveContact && hasEnt)
                    haveContact = TryFallbackContact(ent, skel, ref contact);

                // If we still don't have a contact but we do have an entity, synth one.
                if (!haveContact && ent != null)
                {
                    try
                    {
                        Vec3 c;
                        Vec3 mx;
                        if (aabbOk)
                        {
                            var mn = bboxMin;
                            mx = bboxMax;
                            c = (mn + mx) * 0.5f;
                        }
                        else
                        {
                            var mn = ent.GetPhysicsBoundingBoxMin();
                            mx = ent.GetPhysicsBoundingBoxMax();
                            c = (mn + mx) * 0.5f;
                        }
                        if (ER_Math.IsFinite(in c))
                        {
                            c.z = MathF.Max(c.z, mx.z - 0.05f);
                            contact = c;
                            haveContact = true;
                            if (ER_Config.DebugLogging)
                                Log("IMP_CONTACT synth=aabb");
                        }
                    }
                    catch { }
                    if (!haveContact)
                    {
                        var c = ent.GlobalPosition;
                        if (ER_Math.IsFinite(in c))
                        {
                            contact = c;
                            haveContact = true;
                            if (ER_Config.DebugLogging)
                                Log("IMP_CONTACT synth=com");
                        }
                    }
                }

                if (!ER_Math.IsFinite(in contact))
                {
                    contact = default;
                    haveContact = false;
                }

                // Do NOT early-return here: ent1 (COM) and skel1 don't need contact.
                // Only skip if we have neither an entity nor a skeleton to target.
                if (!haveContact && skel == null && ent == null)
                {
                    Log("IMPULSE_SKIP: no entity, no skeleton, no contact");
                    return false;
                }

                // Explosion-style shaping: ensure strong lateral push away from hit → COM
                if (ent != null && haveContact && ER_Math.IsFinite(in impW))
                {
                    try
                    {
                        var com = ent.GlobalPosition;
                        var away = com - contact;    // push COM away from contact
                        away.z = 0f;
                        float a2 = away.LengthSquared;
                        if (a2 > 1e-10f)
                        {
                            away *= 1f / MathF.Sqrt(a2);
                            float mag  = MathF.Sqrt(MathF.Max(impW.LengthSquared, 1e-12f));
                            float side = MathF.Sqrt(MathF.Max(impW.x * impW.x + impW.y * impW.y, 1e-12f));
                            float want = 0.6f * mag;                 // >= 60% of total as sideways
                            float extra = want - side;
                            if (extra > 0f) { impW.x += away.x * extra; impW.y += away.y * extra; }
                            ClampWorldUp(ref impW);                   // respect up cone
                            CapMagnitude(ref impW, MaxWorldImpulse);
                        }
                    }
                    catch { }
                }
                l2 = impW.LengthSquared;

                bool forceEntity = ER_ImpulsePrefs.ForceEntityImpulse;
                bool allowFallbackWhenInvalid = ER_ImpulsePrefs.AllowSkeletonFallbackForInvalidEntity;
                bool skeletonAvailable = skel != null;
                bool warmOk = skeletonAvailable && RagWarm(skel, Ent2WarmupSeconds);
                bool allowSkeletonNow = skeletonAvailable && (ragActive || warmOk) && (!forceEntity || allowFallbackWhenInvalid);
                bool skApis = (_dSk1 != null || _sk1 != null || _dSk2 != null || _sk2 != null);
                bool extEnt2Available = (_dEnt2 != null || _ent2 != null); // don’t blanket block; gate below
                if (!skApis)
                {
                    allowSkeletonNow = false;
                    // quiet: we’ll push through entity routes after resolving ent from skel
                }
                // recompute if entity became invalid after earlier checks
                if (hasEnt && !aabbOk)
                    aabbOk = TryGetSaneAabb(ent, out bboxMin, out bboxMax);
                try { ragActive = ER_DeathBlastBehavior.IsRagdollActiveFast(skel) || ragActive; }
                catch { /* keep ragActive as-is */ }
                if (hasEnt && !dynOk && ragActive && aabbOk && ER_Config.DebugLogging)
                {
                    try { LogNoiseOncePer(1.0f, "DYN_UNKNOWN: proceeding due to ragActive/AABB"); }
                    catch { }
                }
                // Allow ent2 even when engine reports BodyOwnerNone / not dynamic.
                // Agent ragdolls often flip to dynamic a frame later; skipping here kills the launch.
                // Entity impulses require a contact point; COM route remains disabled.
                // Don't return; let skeleton routes handle no-contact cases.
                if (dynOk && !haveContact)
                {
                    Log("IMPULSE_SKIP_ENT_ONLY: no contact (COM disabled) — trying skeleton routes");
                    // do not return; entity routes are skipped, skeleton routes may still run
                }

                // if contact got set to NaN/Inf somewhere, don't feed it to transforms
                if (!ER_Math.IsFinite(in contact))
                    haveContact = false;
                // Contact way outside bbox? snap toward center before using entity routes.
                if (haveContact && hasEnt && aabbOk)
                {
                    try
                    {
                        var mn = bboxMin;
                        var mx = bboxMax;
                        var c = (mn + mx) * 0.5f;
                        var half = mx - c;
                        var r2 = half.LengthSquared;
                        if (r2 <= 0f)
                        {
                            haveContact = false;
                        }
                        else
                        {
                            var dc2 = (contact - c).LengthSquared;
                            if (dc2 > r2 * 9f)
                            {
                                c.z += ER_Config.CorpseLaunchContactHeight;
                                if (ER_Config.DebugLogging)
                                    Log("CONTACT_CLAMP: outside AABB*3 — snapping toward center");
                                contact = c;
                                LiftContactFloor(ent, ref contact);
                                haveContact = true;
                            }
                        }
                    }
                    catch { }
                }
                // ---------- skeleton first (local) ----------
                if (allowSkeletonNow && haveContact && !_sk2Unsafe && (_dSk2 != null || _sk2 != null))
                {
                    try
                    {
                        var okLocal = TryWorldToLocalSafe(ent, impW, contact, out var impL, out var posL);
                        if (okLocal && (!ER_Math.IsFinite(in impL) || !ER_Math.IsFinite(in posL)))
                            okLocal = false;
                        // Fallback: if no valid transform (null ent / rare NaNs), use world values as "local".
                        if (!okLocal)
                        {
                            impL = impW;
                            posL = contact;
                            okLocal = ER_Math.IsFinite(in impL) && ER_Math.IsFinite(in posL);
                        }
                        if (okLocal)
                        {
                            ClampLocalUp(ref impL);
                            CapMagnitude(ref impL, MaxLocalImpulse);
                            if (impL.LengthSquared < ImpulseTinySqThreshold)
                                okLocal = false;
                        }
                        if (okLocal)
                        {
                            if (_dSk2 != null)
                            {
                                _dSk2(skel, impL, posL);
                            }
                            else
                            {
                                var pars = _sk2.GetParameters();
                                if (_sk2.IsStatic)
                                {
                                    if (pars.Length == 3)
                                        _sk2.Invoke(null, new object[] { skel, impL, posL });
                                    else
                                        _sk2.Invoke(null, new object[] { impL, posL });
                                }
                                else
                                {
                                    _sk2.Invoke(skel, new object[] { impL, posL });
                                }
                            }
                            Log("IMPULSE_USE skel2(local)");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFailure("skel2(local)", ex);
                        MarkUnsafe(5, ex);
                    }
                }

                if (allowSkeletonNow && !_sk1Unsafe && (_dSk1 != null || _sk1 != null))
                {
                    try
                    {
                        var impWc = impW;
                        ClampWorldUp(ref impWc);
                        CapMagnitude(ref impWc, MaxWorldImpulse);
                        if (impWc.LengthSquared >= ImpulseTinySqThreshold)
                        {
                            if (_dSk1 != null)
                            {
                                _dSk1(skel, impWc);
                            }
                            else
                            {
                                var pars = _sk1.GetParameters();
                                if (_sk1.IsStatic)
                                {
                                    if (pars.Length == 2)
                                        _sk1.Invoke(null, new object[] { skel, impWc });
                                    else
                                        _sk1.Invoke(null, new object[] { impWc });
                                }
                                else
                                {
                                    _sk1.Invoke(skel, new object[] { impWc });
                                }
                            }
                            Log("IMPULSE_USE skel1(world)");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFailure("skel1(world)", ex);
                        MarkUnsafe(4, ex);
                    }
                }

                // Do not skip entity routes just because ragdoll is active.
                // Skeleton routing already attempted; allow ent2/ent3 to fire if available.
                if (ER_Config.DebugLogging)
                    Log($"IMP_SNAPSHOT ragActive={ragActive} ent={hasEnt} skAvail={skeletonAvailable} contact={haveContact} dynOk={dynOk} aabbOk={aabbOk} forceEnt={ER_Config.ForceEntityImpulse} allowEnt3={ER_Config.AllowEnt3World}");

                // Synthesize a safe contact point if missing/invalid.
                if ((!haveContact || !ER_Math.IsFinite(in contact)) && hasEnt)
                {
                    try
                    {
                        var c = ent.GlobalPosition;
                        if (ER_Math.IsFinite(in c))
                        {
                            c.z += MathF.Max(0f, ER_Config.CorpseLaunchContactHeight);
                            float jitter = ER_Config.CorpseLaunchXYJitter;
                            if (jitter > 0f)
                            {
                                c.x += MBRandom.RandomFloatRanged(-jitter, jitter);
                                c.y += MBRandom.RandomFloatRanged(-jitter, jitter);
                            }
                            contact = c;
                            LiftContactFloor(ent, ref contact);
                            haveContact = true;
                            if (ER_Config.DebugLogging)
                                Log("IMP_CONTACT synth=center");
                        }
                    }
                    catch { }
                }

                // Don't hard-stop on dyn/AABB; ent2 can still work with world->local fallback.
                if (!hasEnt && !allowSkeletonNow)
                {
                    Log("IMPULSE_SKIP: no entity or skeleton route");
                    return false;
                }
                // Always try entity routes when we have contact+entity.
                if (!hasEnt)
                {
                    bool skeletonWillHandle = skeletonAvailable && (!forceEntity ? true : allowFallbackWhenInvalid);
                    if (!skeletonWillHandle && !forceEntity)
                        Log("IMPULSE_SKIP ent routes: missing entity");
                }

                // Prefer contact entity routes (world) after skeleton attempt.
                // Relax guards so we still fire when ragdoll is active and when dyn/AABB cannot be proven.
                if (hasEnt && haveContact)
                {
                    try
                    {
                        var com = ent.GlobalPosition;
                        var lateral = com - contact;
                        lateral.z = 0f;
                        float L2 = lateral.x * lateral.x + lateral.y * lateral.y;
                        if (L2 > 1e-10f)
                        {
                            float L = MathF.Sqrt(L2);
                            var tangent = new Vec3(-lateral.y / L, lateral.x / L, 0f);
                            float spin = 0.05f * MathF.Sqrt(MathF.Max(impW.LengthSquared, 1e-12f));
                            impW += tangent * spin;
                            ClampWorldUp(ref impW);
                            CapMagnitude(ref impW, MaxWorldImpulse);
                        }
                    }
                    catch { }
                }

                bool entHasBody = false;
                try { entHasBody = hasEnt && ent.HasPhysicsBody(); } catch { entHasBody = false; }
                bool canFireEnt = hasEnt && haveContact && entHasBody && (dynOk || ragActive || warmOk);

                if (ER_Config.DebugLogging)
                    Log($"ENT3_CHECK canFireEnt={canFireEnt} haveContact={haveContact} dynOk={dynOk} ragActive={ragActive} ent3Inst={_dEnt3Inst != null || _ent3Inst != null} ent3Ext={_dEnt3 != null || _ent3 != null}");

                if (ER_Config.AllowEnt3World && canFireEnt && !_ent3Unsafe && (_dEnt3Inst != null || _ent3Inst != null))
                {
                    try
                    {
                        var impWc = impW;
                        ClampWorldUp(ref impWc);
                        CapMagnitude(ref impWc, MaxWorldImpulse);
                        if (impWc.LengthSquared <= ImpulseTinySqThreshold)
                            return false;
                        if (_dEnt3Inst != null) _dEnt3Inst(ent, impWc, contact, false);
                        else                    _ent3Inst.Invoke(ent, new object[] { impWc, contact, false });
                        Log("IMPULSE_USE ent3(world)");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogFailure("ent3(world)", ex);
                        MarkUnsafe(3, ex);
                    }
                }

                if (ER_Config.AllowEnt3World && canFireEnt && !_ent3Unsafe && (_dEnt3 != null || _ent3 != null))
                {
                    try
                    {
                        var impWc = impW;
                        ClampWorldUp(ref impWc);
                        CapMagnitude(ref impWc, MaxWorldImpulse);
                        if (impWc.LengthSquared <= ImpulseTinySqThreshold)
                            return false;
                        if (_dEnt3 != null)
                            _dEnt3(ent, impWc, contact, false);
                        else
                            _ent3.Invoke(null, new object[] { ent, impWc, contact, false });
                        Log("IMPULSE_USE ext ent3(world)");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogFailure("ext ent3(world)", ex);
                        MarkUnsafe(3, ex);
                    }
                }

                if (canFireEnt && !_ent2Unsafe && (_dEnt2Inst != null || _ent2Inst != null))
                {
                    try
                    {
                        if (!dynOk && !RagWarm(skel, Ent2WarmupSeconds))
                        {
                            if (ER_Config.DebugLogging)
                                Log($"ENT2_DEFER: warm={RagWarmSeconds(skel):0.000}s");
                            _pending.Enqueue(new Pending
                            {
                                ent = ent,
                                sk = skel,
                                wImp = impW,
                                wPos = contact,
                                expires = TimeNow() + PendingLife,
                                remaining = PendingBursts
                            });
                            return true;
                        }

                        var impWc = impW;
                        ClampWorldUp(ref impWc);
                        CapMagnitude(ref impWc, MaxWorldImpulse);
                        if (impWc.LengthSquared <= ImpulseTinySqThreshold)
                        {
                            Log("IMPULSE_SKIP ent2: tiny after world clamp");
                            goto SkipInstEnt2;
                        }

                        if (!aabbOk && !extEnt2Available)
                        {
                            if (ER_Config.DebugLogging)
                                Log("IMPULSE_SKIP inst ent2: aabb invalid");
                            goto SkipInstEnt2;
                        }

                        var ok = TryWorldToLocalSafe(ent, impWc, contact, out var impL, out var posL);
                        if (ok && (!ER_Math.IsFinite(in impL) || !ER_Math.IsFinite(in posL)))
                            ok = false;
                        if (ok)
                        {
                            // keep local contact above plane, kill lever arm when nearly on floor
                            if (posL.z < 0.05f)
                            {
                                posL.x = 0f;
                                posL.y = 0f;
                                posL.z = 0.05f;
                            }

                            // limit XY lever arm so torque can't spike when near the ground
                            float maxXY = MathF.Max(0.03f, posL.z * 0.6f);
                            float r2 = posL.x * posL.x + posL.y * posL.y;
                            if (r2 > maxXY * maxXY)
                            {
                                float s = maxXY / MathF.Sqrt(MathF.Max(r2, 1e-12f));
                                posL.x *= s;
                                posL.y *= s;
                            }

                            // keep some lateral so it doesn't go straight up
                            float side2 = impL.x * impL.x + impL.y * impL.y;
                            if (side2 > 1e-12f)
                            {
                                // at least 35% of sideways even very close to the floor,
                                // blending to 100% by ~0.30m above the floor
                                const float baseKeep = 0.35f;
                                float lerp = Clamp01((posL.z - 0.05f) / 0.25f);
                                float keep = baseKeep + (1f - baseKeep) * lerp;
                                impL.x *= keep;
                                impL.y *= keep;
                            }

                            // add a tiny tangential spin (torque) to avoid freeze-y look
                            if (ER_Math.IsFinite(in posL))
                            {
                                Vec3 tangential = new Vec3(-posL.y, posL.x, 0f);
                                float spin = 0.06f * MathF.Sqrt(MathF.Max(impL.LengthSquared, 1e-12f));
                                impL += tangential * spin;
                            }

                            // keep impulse inside the local up cone, then cap magnitude
                            ClampLocalUp(ref impL);
                            CapMagnitude(ref impL, MaxLocalImpulse);
                            float maxMag = MathF.Max(0f, ER_Config.CorpseImpulseHardCap);
                            if (maxMag > 0f)
                            {
                                float magSq = impL.LengthSquared;
                                float maxMagSq = maxMag * maxMag;
                                if (magSq > maxMagSq)
                                    impL *= (maxMag / MathF.Sqrt(MathF.Max(magSq, 1e-12f)));
                            }
                            if (impL.LengthSquared < ImpulseTinySqThreshold)
                                ok = false;
                        }
                        if (ok)
                        {
                            if (ER_Config.DebugLogging)
                            {
                                Log($"ENT2_LOC posL=({posL.x:0.###},{posL.y:0.###},{posL.z:0.###}) impL=({impL.x:0.###},{impL.y:0.###},{impL.z:0.###})");
                                Log($"ENT2_FIRE(inst) warm={RagWarmSeconds(skel):0.000}s");
                            }
                            if (_dEnt2Inst != null)
                                _dEnt2Inst(ent, impL, posL);
                            else
                                _ent2Inst.Invoke(ent, new object[] { impL, posL });
                            Log("IMPULSE_USE inst ent2(local)");
                            return true;
                        }
                        Log("IMPULSE_SKIP inst ent2: prechecks/local failed");
    SkipInstEnt2:
                        ;
                    }
                    catch (Exception ex)
                    {
                        LogFailure("inst ent2", ex);
                        MarkUnsafe(2, ex);
                    }
                }
                if (canFireEnt && !_ent2Unsafe && extEnt2Available && aabbOk) // ext ent2 only when rag active or truly dynamic
                {
                    try
                    {
                        if (!dynOk && !RagWarm(skel, Ent2WarmupSeconds))
                        {
                            if (ER_Config.DebugLogging)
                                Log($"ENT2_DEFER: warm={RagWarmSeconds(skel):0.000}s");
                            _pending.Enqueue(new Pending
                            {
                                ent = ent,
                                sk = skel,
                                wImp = impW,
                                wPos = contact,
                                expires = TimeNow() + PendingLife,
                                remaining = PendingBursts
                            });
                            return true;
                        }

                        var impWc = impW;
                        ClampWorldUp(ref impWc);
                        CapMagnitude(ref impWc, MaxWorldImpulse);
                        if (impWc.LengthSquared <= ImpulseTinySqThreshold)
                        {
                            Log("IMPULSE_SKIP ent2: tiny after world clamp");
                            goto SkipExtEnt2;
                        }

                        var ok = TryWorldToLocalSafe(ent, impWc, contact, out var impL, out var posL);
                        if (ok && (!ER_Math.IsFinite(in impL) || !ER_Math.IsFinite(in posL)))
                            ok = false;
                        if (ok)
                        {
                            // keep local contact above plane, kill lever arm when nearly on floor
                            if (posL.z < 0.05f)
                            {
                                posL.x = 0f;
                                posL.y = 0f;
                                posL.z = 0.05f;
                            }

                            // limit XY lever arm so torque can't spike when near the ground
                            float maxXY = MathF.Max(0.03f, posL.z * 0.6f);
                            float r2 = posL.x * posL.x + posL.y * posL.y;
                            if (r2 > maxXY * maxXY)
                            {
                                float s = maxXY / MathF.Sqrt(MathF.Max(r2, 1e-12f));
                                posL.x *= s;
                                posL.y *= s;
                            }

                            // keep some lateral so it doesn't go straight up
                            float side2 = impL.x * impL.x + impL.y * impL.y;
                            if (side2 > 1e-12f)
                            {
                                // at least 35% of sideways even very close to the floor,
                                // blending to 100% by ~0.30m above the floor
                                const float baseKeep = 0.35f;
                                float lerp = Clamp01((posL.z - 0.05f) / 0.25f);
                                float keep = baseKeep + (1f - baseKeep) * lerp;
                                impL.x *= keep;
                                impL.y *= keep;
                            }

                            // add a tiny tangential spin (torque) to avoid freeze-y look
                            if (ER_Math.IsFinite(in posL))
                            {
                                Vec3 tangential = new Vec3(-posL.y, posL.x, 0f);
                                float spin = 0.06f * MathF.Sqrt(MathF.Max(impL.LengthSquared, 1e-12f));
                                impL += tangential * spin;
                            }

                            // keep impulse inside the local up cone, then cap magnitude
                            ClampLocalUp(ref impL);
                            CapMagnitude(ref impL, MaxLocalImpulse);
                            float maxMag = MathF.Max(0f, ER_Config.CorpseImpulseHardCap);
                            if (maxMag > 0f)
                            {
                                float magSq = impL.LengthSquared;
                                float maxMagSq = maxMag * maxMag;
                                if (magSq > maxMagSq)
                                    impL *= (maxMag / MathF.Sqrt(MathF.Max(magSq, 1e-12f)));
                            }
                            if (impL.LengthSquared < ImpulseTinySqThreshold)
                                ok = false;
                        }
                        if (ok)
                        {
                            if (ER_Config.DebugLogging)
                            {
                                Log($"ENT2_LOC posL=({posL.x:0.###},{posL.y:0.###},{posL.z:0.###}) impL=({impL.x:0.###},{impL.y:0.###},{impL.z:0.###})");
                                Log($"ENT2_FIRE(ext) warm={RagWarmSeconds(skel):0.000}s");
                            }
                            if (_dEnt2 != null)
                                _dEnt2(ent, impL, posL);
                            else
                                _ent2.Invoke(null, new object[] { ent, impL, posL });
                            Log("IMPULSE_USE ext ent2(local)");
                            return true;
                        }
                        Log("IMPULSE_SKIP ext ent2: prechecks/local failed");
    SkipExtEnt2:
                        ;
                    }
                    catch (Exception ex)
                    {
                        LogFailure("ext ent2", ex);
                        MarkUnsafe(2, ex);
                    }
                }

                if (!skApis && canFireEnt && !_ent3Unsafe && (_dEnt3 != null || _ent3 != null))
                {
                    try
                    {
                        var impWc = impW;
                        ClampWorldUp(ref impWc);
                        CapMagnitude(ref impWc, MaxWorldImpulse);
                        if (impWc.LengthSquared > ImpulseTinySqThreshold)
                        {
                            if (_dEnt3 != null)
                                _dEnt3(ent, impWc, contact, false);
                            else
                                _ent3.Invoke(null, new object[] { ent, impWc, contact, false });
                            Log("IMPULSE_USE ext ent3(world) fallback");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFailure("ext ent3(world) fallback", ex);
                        MarkUnsafe(3, ex);
                    }
                }

                bool skeletonRouteReady = allowSkeletonNow && haveContact && !_sk2Unsafe && (_dSk2 != null || _sk2 != null);
                bool ent3WorldReady = ER_Config.AllowEnt3World && canFireEnt && !_ent3Unsafe
                                       && ((_dEnt3Inst != null || _ent3Inst != null) || (_dEnt3 != null || _ent3 != null));
                bool ent2Ready = canFireEnt && !_ent2Unsafe && aabbOk
                                  && (
                                       (_dEnt2Inst != null || _ent2Inst != null)
                                       || extEnt2Available
                                     );
                Log($"ENT1_CHECK allow={ER_Config.AllowEnt1WorldFallback} dynOk={dynOk} ragActive={ragActive} canFireEnt={canFireEnt} aabbOk={aabbOk} ent1Bound={_dEnt1 != null || _ent1 != null} ent1Unsafe={_ent1Unsafe} ent2Ready={ent2Ready} ent3Ready={ent3WorldReady} skReady={skeletonRouteReady}");
                if (ER_Config.AllowEnt1WorldFallback && hasEnt && aabbOk && entHasBody && (dynOk || ragActive || warmOk) && !skeletonRouteReady && !ent3WorldReady && !ent2Ready
                    && !_ent1Unsafe && (_dEnt1 != null || _ent1 != null))
                {
                    try
                    {
                        var impWc = impW;
                        ClampWorldUp(ref impWc);
                        CapMagnitude(ref impWc, MaxWorldImpulse);
                        if (impWc.LengthSquared > ImpulseTinySqThreshold)
                        {
                            if (_dEnt1 != null) _dEnt1(ent, impWc);
                            else               _ent1.Invoke(null, new object[] { ent, impWc });
                            Log("IMPULSE_USE ext ent1(world) fallback");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFailure("ext ent1(world) fallback", ex);
                        MarkUnsafe(1, ex);
                    }
                }

                if (ER_Config.DebugLogging)
                {
                    Log($"IMPULSE_CTX hasEnt={hasEnt} haveContact={haveContact} entDyn={(hasEnt && LooksDynamic(ent))} entAabb={aabbOk} sk2={(_dSk2 != null || _sk2 != null)} sk1={(_dSk1 != null || _sk1 != null)}");
                    try
                    {
                        if (hasEnt && dynOk) // AABB logging only when dynamic
                        {
                            var mn = ent.GetPhysicsBoundingBoxMin();
                            var mx = ent.GetPhysicsBoundingBoxMax();
                            Log($"IMPULSE_AABB mn=({mn.x:0.###},{mn.y:0.###},{mn.z:0.###}) mx=({mx.x:0.###},{mx.y:0.###},{mx.z:0.###}) BodyFlag={ent.BodyFlag} PhysFlag={ent.PhysicsDescBodyFlag}");
                        }
                    }
                    catch { }
                }
                if (ER_Config.DebugLogging)
                    Log("IMPULSE_END: no entity/skeleton path succeeded");
                return false;
            }
        }

    internal static class ER_Space
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWorldToLocal(GameEntity ent, in Vec3 wImp, in Vec3 wPos, out Vec3 lImp, out Vec3 lPos)
        {
            lImp = wImp;
            lPos = wPos;
            if (ent == null)
                return false;
            try {
                MatrixFrame frame;
                try { frame = ent.GetGlobalFrame(); }
                catch { frame = ent.GetFrame(); }
                lPos = frame.TransformToLocal(wPos);
                lImp = frame.TransformToLocal(wPos + wImp) - lPos;
                return true;
            } catch { return false; }
        }

        // No reliable Skeleton frame API on this TW branch: caller must pass via entity frame.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWorldToLocal(Skeleton skel, in Vec3 wImp, in Vec3 wPos, out Vec3 lImp, out Vec3 lPos)
        {
            lImp = wImp;
            lPos = wPos;
            return false;
        }

    }
}
