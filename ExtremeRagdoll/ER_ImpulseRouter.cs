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
        private static Action<GameEntity, Vec3, Vec3, bool> _dEnt3;
        private static Action<GameEntity, Vec3, Vec3> _dEnt2;
        private static Action<GameEntity, Vec3> _dEnt1;
        private static Action<GameEntity, Vec3, Vec3, bool> _dEnt3Inst;
        private static Action<GameEntity, Vec3, Vec3> _dEnt2Inst;
        private static Action<GameEntity, Vec3> _dEnt1Inst;
        private static Action<Skeleton, Vec3, Vec3> _dSk2;
        private static Action<Skeleton, Vec3> _dSk1;
        private static Func<GameEntity, bool> _isDyn;
        private static float _lastImpulseLog = float.NegativeInfinity; // keep
        private static float _lastNoiseLog = float.NegativeInfinity;
        private static string _ent1Name, _ent2Name, _ent3Name, _sk1Name, _sk2Name; // debug
        private static bool _bindLogged;
        private static float _lastAvLog = float.NegativeInfinity;
        // AV throttling state: indexes 1..5 map to routes.
        private static readonly float[] _disableUntil = new float[6];
        private static readonly int[] _avCount = new int[6];

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

        private static bool LooksDynamic(GameEntity ent)
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
        private static void ClampLocalUp(ref Vec3 v)
        {
            if (!IsValidVec(in v))
                return;
            if (v.z < 0f)
                v.z = 0f;
            float l2 = v.LengthSquared;
            if (l2 <= 1e-9f || float.IsNaN(l2) || float.IsInfinity(l2))
                return;
            float cap = MathF.Sqrt(l2) * ER_Config.CorpseLaunchMaxUpFraction;
            if (float.IsNaN(cap) || float.IsInfinity(cap))
                return;
            if (v.z > cap)
                v.z = cap;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClampWorldUp(ref Vec3 w)
        {
            if (!ER_Math.IsFinite(in w))
                return;
            if (w.z < 0f)
                w.z = 0f;
            float len = w.Length;
            if (len <= 1e-6f)
                return;
            float upMax = MathF.Max(0f, ER_Config.CorpseLaunchMaxUpFraction) * len;
            if (w.z > upMax)
                w.z = upMax;
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
            }

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
            _sk2 = sk.GetMethod("ApplyForceToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                 ?? sk.GetMethod("AddForceToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                 ?? sk.GetMethod("AddImpulseToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                 ?? sk.GetMethod("ApplyLocalImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null)
                 ?? sk.GetMethod("ApplyImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3) }, null);
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
            if (_sk2 != null)
            {
                try { _dSk2 = (Action<Skeleton, Vec3, Vec3>)_sk2.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>)); }
                catch { _dSk2 = null; }
            }
            _sk1 = sk.GetMethod("ApplyForceToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3) }, null)
                 ?? sk.GetMethod("AddForceToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3) }, null)
                 ?? sk.GetMethod("AddImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3) }, null);
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
            if (_sk1 != null)
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
            float l2 = impW.LengthSquared;
            if (l2 < ImpulseTinySqThreshold)
            {
                Log("IMPULSE_SKIP tiny impulse");
                return false;
            }
            if (impW.z < 0f)
                impW.z = 0f;
            float len = MathF.Sqrt(l2);
            float zMax = len * ER_Config.CorpseLaunchMaxUpFraction;
            if (!float.IsNaN(zMax) && !float.IsInfinity(zMax) && impW.z > zMax)
            {
                impW.z = zMax;
                l2 = impW.LengthSquared;
                if (l2 < ImpulseTinySqThreshold)
                {
                    Log("IMPULSE_SKIP tiny after clamp");
                    return false;
                }
            }

            if (!_bindLogged && ER_Config.DebugLogging)
            {
                _bindLogged = true;
                ER_Log.Info($"IMP_BIND_NAMES ent3Inst={_ent3Inst?.Name ?? "null"} ent3={_ent3?.Name ?? "null"} ent2Inst={_ent2Inst?.Name ?? "null"} ent2={_ent2?.Name ?? "null"} sk2={_sk2?.Name ?? "null"} sk1={_sk1?.Name ?? "null"}");
            }
            if (ER_Config.DebugLogging)
            {
                ER_Log.Info($"IMP_TRY haveEnt={(ent!=null)} haveSk={(skel!=null)} imp2={l2:0} pos2={worldPos.LengthSquared:0}");
            }

            try { skel?.ActivateRagdoll(); } catch { }
            try { skel?.ForceUpdateBoneFrames(); } catch { }

            try { ent?.ActivateRagdoll(); } catch { }

            var contact = worldPos;
            bool haveContact = TryResolveContact(ent, ref contact);
            bool hasEnt = ent != null;
            // If no contact, try AABB center first (safer than origin), then origin.
            if (!haveContact && ent != null)
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
                            haveContact = true;
                        }
                    }
                }
                catch { }
            }

            if (!haveContact && ent != null)
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
                        haveContact = true;
                    }
                }
                catch { }
            }

            // Do NOT early-return here: ent1 (COM) and skel1 don't need contact.
            // Only skip if we have neither an entity nor a skeleton to target.
            if (!haveContact && skel == null && ent == null)
            {
                Log("IMPULSE_SKIP: no entity, no skeleton, no contact");
                return false;
            }

            bool forceEntity = ER_ImpulsePrefs.ForceEntityImpulse;
            bool allowFallbackWhenInvalid = ER_ImpulsePrefs.AllowSkeletonFallbackForInvalidEntity;
            bool skeletonAvailable = skel != null;
            bool allowSkeletonNow = skeletonAvailable && (!forceEntity || allowFallbackWhenInvalid);
            bool skApis = (_dSk1 != null || _sk1 != null || _dSk2 != null || _sk2 != null);
            if (!skApis)
            {
                allowSkeletonNow = false;
                Log("IMPULSE_NOTE: no skeleton API bound");
            }
            bool dynOk = hasEnt && LooksDynamic(ent);
            bool aabbOk = hasEnt && AabbSane(ent);
            bool dynSure = dynOk && aabbOk;
            bool ragActive = false;
            try { ragActive = ER_DeathBlastBehavior.IsRagdollActiveFast(skel); }
            catch { }
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
                Log($"IMP_SNAPSHOT ragActive={ragActive} ent={hasEnt} skAvail={skeletonAvailable} contact={haveContact} dynOk={dynOk} dynSure={dynSure} aabbOk={aabbOk} forceEnt={ER_Config.ForceEntityImpulse} allowEnt3={ER_Config.AllowEnt3World}");

            // Synthesize a safe contact point if missing/invalid.
            if ((!haveContact || !ER_Math.IsFinite(in contact)) && hasEnt)
            {
                try
                {
                    var c = ent.GlobalPosition;
                    if (ER_Math.IsFinite(in c))
                    {
                        c.z += MathF.Max(0f, ER_Config.CorpseLaunchContactHeight);
                        // Keep contact above ground/AABB floor to avoid downward spikes.
                        try
                        {
                            var mn = ent.GetPhysicsBoundingBoxMin();
                            if (c.z < mn.z + 0.02f)
                                c.z = mn.z + 0.02f;
                        }
                        catch { }
                        float jitter = ER_Config.CorpseLaunchXYJitter;
                        if (jitter > 0f)
                        {
                            c.x += MBRandom.RandomFloatRanged(-jitter, jitter);
                            c.y += MBRandom.RandomFloatRanged(-jitter, jitter);
                        }
                        contact = c;
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
            if (!ragActive && ER_Config.AllowEnt3World && haveContact && hasEnt && dynOk && !_ent3Unsafe && (_dEnt3Inst != null || _ent3Inst != null))
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

            if (!ragActive && ER_Config.AllowEnt3World && haveContact && hasEnt && dynOk && !_ent3Unsafe && (_dEnt3 != null || _ent3 != null))
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

            // Use ent2(local) once ragdoll is active (ignore IsDynamicBody on this TW branch).
            if (ragActive && haveContact && hasEnt && !_ent2Unsafe && (_dEnt2Inst != null || _ent2Inst != null))
            {
                try
                {
                    var ok = TryWorldToLocalSafe(ent, impW, contact, out var impL, out var posL);
                    if (ok && (!ER_Math.IsFinite(in impL) || !ER_Math.IsFinite(in posL)))
                        ok = false;
                    if (ok)
                    {
                        ClampLocalUp(ref impL);
                        // Cap overall magnitude (safety against crazy locals on some bodies)
                        float maxMag = MathF.Max(0f, ER_Config.CorpseImpulseHardCap);
                        if (maxMag > 0f)
                        {
                            float len = impL.Length;
                            if (len > maxMag) impL *= (maxMag / MathF.Max(len, 1e-6f));
                        }
                        if (impL.LengthSquared < ImpulseTinySqThreshold)
                            ok = false;
                    }
                    if (ok)
                    {
                        if (_dEnt2Inst != null)
                            _dEnt2Inst(ent, impL, posL);
                        else
                            _ent2Inst.Invoke(ent, new object[] { impL, posL });
                        Log("IMPULSE_USE inst ent2(local)");
                        return true;
                    }
                    Log("IMPULSE_SKIP inst ent2: prechecks/local failed");
                }
                catch (Exception ex)
                {
                    LogFailure("inst ent2", ex);
                    MarkUnsafe(2, ex);
                }
            }
            if (ragActive && haveContact && hasEnt && !_ent2Unsafe && (_dEnt2 != null || _ent2 != null))
            {
                try
                {
                    var ok = TryWorldToLocalSafe(ent, impW, contact, out var impL, out var posL);
                    if (ok && (!ER_Math.IsFinite(in impL) || !ER_Math.IsFinite(in posL)))
                        ok = false;
                    if (ok)
                    {
                        ClampLocalUp(ref impL);
                        float maxMag = MathF.Max(0f, ER_Config.CorpseImpulseHardCap);
                        if (maxMag > 0f)
                        {
                            float len = impL.Length;
                            if (len > maxMag) impL *= (maxMag / MathF.Max(len, 1e-6f));
                        }
                        if (impL.LengthSquared < ImpulseTinySqThreshold)
                            ok = false;
                    }
                    if (ok)
                    {
                        if (_dEnt2 != null)
                            _dEnt2(ent, impL, posL);
                        else
                            _ent2.Invoke(null, new object[] { ent, impL, posL });
                        Log("IMPULSE_USE ext ent2(local)");
                        return true;
                    }
                    Log("IMPULSE_SKIP ext ent2: prechecks/local failed");
                }
                catch (Exception ex)
                {
                    LogFailure("ext ent2", ex);
                    MarkUnsafe(2, ex);
                }
            }

            // COM fallback DISABLED on this TW branch: ApplyForceToDynamicBody is AV-prone.
            // Leave a breadcrumb when it would have fired so we can see frequency in logs.
            if (hasEnt && LooksDynamic(ent) && (_dEnt1 != null || _ent1 != null))
            {
                if (ER_Config.DebugLogging)
                    Log("ENT1_DISABLED: skipping COM route on this branch");
            }

            if (ER_Config.DebugLogging)
            {
                Log($"IMPULSE_CTX hasEnt={hasEnt} haveContact={haveContact} entDyn={(hasEnt && LooksDynamic(ent))} entAabb={(hasEnt && AabbSane(ent))} sk2={(_dSk2 != null || _sk2 != null)} sk1={(_dSk1 != null || _sk1 != null)}");
                try
                {
                    if (hasEnt)
                    {
                        var mn = ent.GetPhysicsBoundingBoxMin();
                        var mx = ent.GetPhysicsBoundingBoxMax();
                        Log($"IMPULSE_AABB mn=({mn.x:0.###},{mn.y:0.###},{mn.z:0.###}) mx=({mx.x:0.###},{mx.y:0.###},{mx.z:0.###}) BodyFlag={ent.BodyFlag} PhysFlag={ent.PhysicsDescBodyFlag}");
                    }
                }
                catch { }
            }
            if (ER_Config.DebugLogging)
                Log("IMPULSE_END: no route accepted");
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
