using System;
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
        private static bool _ensured;
        private static bool _ent1Unsafe, _ent2Unsafe, _ent3Unsafe, _sk1Unsafe, _sk2Unsafe;
        private static MethodInfo _ent3, _ent2, _ent1;
        private static MethodInfo _ent3Inst, _ent2Inst, _ent1Inst;
        private static MethodInfo _sk2, _sk1;
        private static MethodInfo _wake;
        private static Action<GameEntity, Vec3, Vec3, bool> _dEnt3;
        private static Action<GameEntity, Vec3, Vec3> _dEnt2;
        private static Action<GameEntity, Vec3> _dEnt1;
        private static Action<GameEntity, Vec3, Vec3, bool> _dEnt3Inst;
        private static Action<GameEntity, Vec3, Vec3> _dEnt2Inst;
        private static Action<GameEntity, Vec3> _dEnt1Inst;
        private static Action<Skeleton, Vec3, Vec3> _dSk2;
        private static Action<Skeleton, Vec3> _dSk1;
        private static Action<GameEntity> _dWake;
        private static Func<GameEntity, bool> _isDyn;
        private static float _lastImpulseLog = float.NegativeInfinity; // keep
        private static float _lastNoiseLog = float.NegativeInfinity;
        private static string _ent1Name, _ent2Name, _ent3Name, _sk1Name, _sk2Name; // debug
        private static bool _bindLogged;
        private static float _lastAvLog = float.NegativeInfinity;
        // AV throttling state: indexes 1..5 map to routes.
        private static readonly float[] _disableUntil = new float[6];
        private static readonly int[] _avCount = new int[6];
        private const float Ent2WarmupSeconds = 0.02f;
        private sealed class Rag
        {
            public float t;
        }

        private static readonly ConditionalWeakTable<Skeleton, Rag> _rag = new ConditionalWeakTable<Skeleton, Rag>();

        internal static void ResetUnsafeState()
        {
            _ent1Unsafe = _ent2Unsafe = _ent3Unsafe = _sk1Unsafe = _sk2Unsafe = false;
            for (int i = 0; i < _disableUntil.Length; i++)
            {
                _disableUntil[i] = 0f;
                _avCount[i] = 0;
            }
            _lastNoiseLog = float.NegativeInfinity;
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
                if (pt == w)
                    continue;
                if (pt.IsByRef && pt.GetElementType() == w)
                    continue;
                return false;
            }
            return true;
        }

        internal static bool LooksDynamic(GameEntity ent)
        {
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
            // Fallback: explicit "Dynamic" beats "Static"; default = NOT dynamic
            try
            {
                var bf = ent.BodyFlag.ToString();
                if (!string.IsNullOrEmpty(bf))
                {
                    // Treat "None" owner as NOT dynamic to avoid native calls on missing bodies
                    if (bf.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                    if (bf.IndexOf("Kinematic", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                    if (bf.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    if (bf.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                }
            }
            catch { }

            try
            {
                var pdf = ent.PhysicsDescBodyFlag.ToString();
                if (!string.IsNullOrEmpty(pdf))
                {
                    if (pdf.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                    if (pdf.IndexOf("Kinematic", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                    if (pdf.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    if (pdf.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                }
            }
            catch { }
            if (ER_Config.DebugLogging)
            {
                try { LogNoiseOncePer(1.0f, $"DYN_REJECT flags={ent.BodyFlag}|{ent.PhysicsDescBodyFlag}"); }
                catch { }
            }

            return false; // fail-closed: if we can't prove dynamic, assume static
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AabbSane(GameEntity ent)
        {
            try
            {
                var mn = ent.GetPhysicsBoundingBoxMin();
                var mx = ent.GetPhysicsBoundingBoxMax();
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
                return false;
            }
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
        private static float Now()
        {
            try
            {
                var mission = Mission.Current;
                if (mission != null)
                    return mission.CurrentTime;
            }
            catch { }

            // Versionsunabhängiger Fallback (ungefähr Sekunden seit Start)
            uint ms = unchecked((uint)Environment.TickCount);
            return ms * 0.001f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkRagStart(Skeleton sk)
        {
            if (sk == null)
                return;
            try
            {
                _rag.Remove(sk);
                _rag.Add(sk, new Rag { t = Now() });
            }
            catch { }
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
                return (Now() - rag.t) >= minSeconds;
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
                if (_rag.TryGetValue(sk, out var rag) && rag != null)
                {
                    float delta = Now() - rag.t;
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
            foreach (var t in new[]
            {
                skeletonAsm.GetType("TaleWorlds.Engine.SkeletonPhysicsExtensions"),
                skeletonAsm.GetType("TaleWorlds.Engine.SkeletonExtensions"),
                typeof(Skeleton)
            })
            {
                if (t == null)
                    continue;
                foreach (var mi in t.GetMethods(flags))
                {
                    var name = mi.Name.ToLowerInvariant();
                    bool looks = name.Contains("impulse") || name.Contains("force");
                    if (!looks)
                        continue;
                    if (_sk2 == null && SigMatches(mi, typeof(Skeleton), typeof(Vec3), typeof(Vec3)))
                        _sk2 = mi;
                    if (_sk1 == null && SigMatches(mi, typeof(Skeleton), typeof(Vec3)))
                        _sk1 = mi;
                }
            }
            if (_sk1 == null && _sk2 == null)
            {
                try
                {
                    var asm = typeof(Skeleton).Assembly;
                    foreach (var t in asm.GetTypes())
                    {
                        var n = t.FullName ?? string.Empty;
                        if (string.IsNullOrEmpty(n) || n.IndexOf("Skeleton", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                        {
                            var ln = mi.Name.ToLowerInvariant();
                            if (ln.Contains("impulse") || ln.Contains("force"))
                                Log($"SKEL_CANDIDATE {t.FullName}.{mi}");
                        }
                    }
                }
                catch { }
            }
            try
            {
                if (_sk2 != null && !_sk2.GetParameters()[1].ParameterType.IsByRef)
                    _dSk2 = (Action<Skeleton, Vec3, Vec3>)_sk2.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>));
            }
            catch { _dSk2 = null; }
            try
            {
                if (_sk1 != null && !_sk1.GetParameters()[0].ParameterType.IsByRef)
                    _dSk1 = (Action<Skeleton, Vec3>)_sk1.CreateDelegate(typeof(Action<Skeleton, Vec3>));
            }
            catch { _dSk1 = null; }

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
                try { _dSk2 = (Action<Skeleton, Vec3, Vec3>)_sk2.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>)); }
                catch { _dSk2 = null; }
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
                try { _dSk1 = (Action<Skeleton, Vec3>)_sk1.CreateDelegate(typeof(Action<Skeleton, Vec3>)); }
                catch { _dSk1 = null; }
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
                        _dSk2 = (Skeleton s, Vec3 a, Vec3 b) => { try { _sk2.Invoke(null, new object[] { s, a, b }); } catch { } };

                    // (Skeleton, Vec3)
                    _sk1 = _sk1 ?? (
                        skExt.GetMethod("ApplyForceToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                        new[] { typeof(Skeleton), typeof(Vec3) }, null)
                     ?? skExt.GetMethod("AddForceToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                        new[] { typeof(Skeleton), typeof(Vec3) }, null)
                     ?? skExt.GetMethod("AddImpulseToBone", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic, null,
                                        new[] { typeof(Skeleton), typeof(Vec3) }, null));
                    if (_sk1 != null && _dSk1 == null)
                        _dSk1 = (Skeleton s, Vec3 a) => { try { _sk1.Invoke(null, new object[] { s, a }); } catch { } };
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

            if (ER_Config.DebugLogging)
            {
                _ent3Name = _ent3?.Name; _ent2Name = _ent2?.Name; _ent1Name = _ent1?.Name;
                _sk2Name = _sk2?.Name; _sk1Name = _sk1?.Name;
                ER_Log.Info($"IMP_BIND ent3:{_ent3!=null}|{_dEnt3!=null} inst:{_ent3Inst!=null}|{_dEnt3Inst!=null} " +
                            $"ent2:{_ent2!=null}|{_dEnt2!=null} inst:{_ent2Inst!=null}|{_dEnt2Inst!=null} " +
                            $"ent1:{_ent1!=null}|{_dEnt1!=null} inst:{_ent1Inst!=null}|{_dEnt1Inst!=null} " +
                            $"sk2:{_sk2!=null}|{_dSk2!=null} sk1:{_sk1!=null}|{_dSk1!=null} isDyn:{_isDyn!=null}");
                ER_Log.Info($"IMP_BIND_NAMES ent3:{_ent3Name} ent2:{_ent2Name} ent1:{_ent1Name} sk2:{_sk2Name} sk1:{_sk1Name}");
            }

            Volatile.Write(ref _ensured, true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MaybeReEnable()
        {
            float now = GetNow();
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

            float now = GetNow();
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
        private static float GetNow()
        {
            return Mission.Current?.CurrentTime ?? 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Log(string message)
        {
            if (ER_Config.DebugLogging && ShouldLog(GetNow()))
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
            float now = GetNow();
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
                float now = GetNow();
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

        public static bool TryImpulse(GameEntity ent, Skeleton skel, in Vec3 worldImpulse, in Vec3 worldPos)
        {
            Ensure();
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
            ClampWorldUp(ref impW);
            float l2 = impW.LengthSquared;
            if (l2 < ImpulseTinySqThreshold)
            {
                Log("IMPULSE_SKIP tiny impulse");
                return false;
            }
            var contact = worldPos;
            bool hasEnt = ent != null;
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
            try { wasRag = ER_DeathBlastBehavior.IsRagdollActiveFast(skel); }
            catch { }
            bool ragActive = wasRag;
            if (!wasRag)
                MarkRagStart(skel);
            try
            {
                skel?.ActivateRagdoll();
                ragActive = (ER_DeathBlastBehavior.IsRagdollActiveFast(skel) || ragActive || skel != null);
            }
            catch
            {
                ragActive = ragActive || skel != null;
            }
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
                        Log($"POST_WAKE dynOk={dynOk}");
                }
                catch { }
            }

            LiftContactFloor(ent, ref contact);
            bool haveContact = TryResolveContact(ent, ref contact);
            // If no contact, try AABB center first (safer than origin), then origin.
            if (!haveContact && ent != null && dynOk)
            {
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
                            haveContact = true;
                        }
                    }
                }
                catch { }
            }

            if (!haveContact && ent != null && dynOk)
            {
                try
                {
                    MatrixFrame f;
                    try { f = ent.GetGlobalFrame(); } catch { f = ent.GetFrame(); }
                    var o = f.origin;
                    if (o.IsValid)
                    {
                        o.z += ER_Config.CorpseLaunchContactHeight;
                        contact = o;
                        LiftContactFloor(ent, ref contact);
                        haveContact = true;
                    }
                }
                catch { }
            }

            // If we still don't have a contact but we do have an entity, synth one.
            if (!haveContact && ent != null)
            {
                if (dynOk)
                {
                    try
                    {
                        var mn = ent.GetPhysicsBoundingBoxMin();
                        var mx = ent.GetPhysicsBoundingBoxMax();
                        var c = (mn + mx) * 0.5f;
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
                }
                if (!haveContact && dynOk)
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
                    }
                }
                catch { }
            }
            l2 = impW.LengthSquared;

            bool forceEntity = ER_ImpulsePrefs.ForceEntityImpulse;
            bool allowFallbackWhenInvalid = ER_ImpulsePrefs.AllowSkeletonFallbackForInvalidEntity;
            bool skeletonAvailable = skel != null;
            bool allowSkeletonNow = skeletonAvailable && (!forceEntity || allowFallbackWhenInvalid);
            bool skApis = (_dSk1 != null || _sk1 != null || _dSk2 != null || _sk2 != null);
            bool extEnt2Available = (_dEnt2 != null || _ent2 != null); // don’t blanket block; gate below
            if (!skApis)
            {
                allowSkeletonNow = false;
                Log("IMPULSE_NOTE: no skeleton API bound");
                Log("IMPULSE_NOTE: ext ent2 will be gated by dyn/AABB instead");
            }
            bool requireRagdollForEnt2 = skApis; // only warmup-gate if skeleton routes exist
            bool aabbOk = hasEnt && dynOk && AabbSane(ent);
            try { ragActive = ER_DeathBlastBehavior.IsRagdollActiveFast(skel) || ragActive; }
            catch { ragActive = ragActive || skel != null; }
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
                    var mn = ent.GetPhysicsBoundingBoxMin();
                    var mx = ent.GetPhysicsBoundingBoxMax();
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
                        if (impL.LengthSquared < ImpulseTinySqThreshold)
                            okLocal = false;
                    }
                    if (okLocal)
                    {
                        if (_dSk2 != null) _dSk2(skel, impL, posL);
                        else               _sk2.Invoke(skel, new object[] { impL, posL });
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
            // If we only have an entity and it’s still kinematic *after* wake, defer.
            if (hasEnt && !allowSkeletonNow && !dynOk)
            {
                Log("IMPULSE_DEFER: entity non-dynamic post-wake");
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
                    }
                }
                catch { }
            }

            if (ER_Config.DebugLogging)
                Log($"ENT3_CHECK haveContact={haveContact} dynOk={dynOk} ent3Inst={_dEnt3Inst != null || _ent3Inst != null} ent3Ext={_dEnt3 != null || _ent3 != null}");

            if (ER_Config.AllowEnt3World && haveContact && hasEnt && dynOk && !_ent3Unsafe && (_dEnt3Inst != null || _ent3Inst != null))
            {
                try
                {
                    var impWc = impW;
                    ClampWorldUp(ref impWc);
                    if (impWc.LengthSquared <= 1e-8f)
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

            if (ER_Config.AllowEnt3World && haveContact && hasEnt && dynOk && !_ent3Unsafe && (_dEnt3 != null || _ent3 != null))
            {
                try
                {
                    var impWc = impW;
                    ClampWorldUp(ref impWc);
                    if (impWc.LengthSquared <= 1e-8f)
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

            if (haveContact && hasEnt && !_ent2Unsafe && (_dEnt2Inst != null || _ent2Inst != null) && dynOk)
            {
                try
                {
                    if (requireRagdollForEnt2 && !RagWarm(skel, Ent2WarmupSeconds))
                    {
                        if (ER_Config.DebugLogging)
                            Log($"ENT2_DEFER: warm={RagWarmSeconds(skel):0.000}s");
                        goto SkipInstEnt2;
                    }

                    var impWc = impW;
                    ClampWorldUp(ref impWc);
                    if (impWc.LengthSquared <= ImpulseTinySqThreshold)
                    {
                        Log("IMPULSE_SKIP ent2: tiny after world clamp");
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
            if (haveContact && hasEnt && !_ent2Unsafe && extEnt2Available
                && dynOk && aabbOk) // ext ent2 only when truly dynamic
            {
                try
                {
                    if (requireRagdollForEnt2 && !RagWarm(skel, Ent2WarmupSeconds))
                    {
                        if (ER_Config.DebugLogging)
                            Log($"ENT2_DEFER: warm={RagWarmSeconds(skel):0.000}s");
                        goto SkipExtEnt2;
                    }

                    var impWc = impW;
                    ClampWorldUp(ref impWc);
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

            if (!skApis && haveContact && hasEnt && dynOk && !_ent3Unsafe && (_dEnt3 != null || _ent3 != null))
            {
                try
                {
                    var impWc = impW;
                    ClampWorldUp(ref impWc);
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
            bool ent3WorldReady = ER_Config.AllowEnt3World && haveContact && hasEnt && dynOk && !_ent3Unsafe
                                   && ((_dEnt3Inst != null || _ent3Inst != null) || (_dEnt3 != null || _ent3 != null));
            bool ent2Ready = haveContact && hasEnt && !_ent2Unsafe && aabbOk && dynOk
                              && (
                                   (_dEnt2Inst != null || _ent2Inst != null)
                                   || extEnt2Available
                                 );
            Log($"ENT1_CHECK allow={ER_Config.AllowEnt1WorldFallback} dynOk={dynOk} ent1Bound={_dEnt1 != null || _ent1 != null} ent1Unsafe={_ent1Unsafe} ent2Ready={ent2Ready} ent3Ready={ent3WorldReady} skReady={skeletonRouteReady}");
            if (ER_Config.AllowEnt1WorldFallback && hasEnt && aabbOk && dynOk && !skeletonRouteReady && !ent3WorldReady && !ent2Ready
                && !_ent1Unsafe && (_dEnt1 != null || _ent1 != null))
            {
                try
                {
                    var impWc = impW;
                    ClampWorldUp(ref impWc);
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
                Log($"IMPULSE_CTX hasEnt={hasEnt} haveContact={haveContact} entDyn={(hasEnt && LooksDynamic(ent))} entAabb={(hasEnt && AabbSane(ent))} sk2={(_dSk2 != null || _sk2 != null)} sk1={(_dSk1 != null || _sk1 != null)}");
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
