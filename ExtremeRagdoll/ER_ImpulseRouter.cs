using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
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
        private static float _lastImpulseLog = float.NegativeInfinity;
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
        }

        private static bool LooksDynamic(GameEntity ent)
        {
            if (_isDyn != null)
            {
                try
                {
                    return _isDyn(ent);
                }
                catch { }
            }

            try
            {
                var bf = ent.BodyFlag.ToString();
                if (!string.IsNullOrEmpty(bf) && bf.IndexOf("Dynamic", StringComparison.Ordinal) >= 0)
                    return true;
            }
            catch { }
            try
            {
                var pdf = ent.PhysicsDescBodyFlag.ToString();
                if (!string.IsNullOrEmpty(pdf) && pdf.IndexOf("Dynamic", StringComparison.Ordinal) >= 0)
                    return true;
            }
            catch { }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AabbSane(GameEntity ent)
        {
            try
            {
                var mn = ent.GetPhysicsBoundingBoxMin();
                var mx = ent.GetPhysicsBoundingBoxMax();
                if (!ER_Math.IsFinite(in mn) || !ER_Math.IsFinite(in mx))
                    return false;

                var d = mx - mn;
                return d.x > 0f && d.y > 0f && d.z > 0f && d.LengthSquared > 1e-6f;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWorldToLocalSafe(GameEntity ent, Vec3 worldImpulse, Vec3 contact, out Vec3 impLocal, out Vec3 posLocal)
        {
            if (ent == null)
            {
                impLocal = Vec3.Zero;
                posLocal = Vec3.Zero;
                return false;
            }
            return ER_Space.TryWorldToLocal(ent, worldImpulse, contact, out impLocal, out posLocal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Ensure()
        {
            if (Volatile.Read(ref _ensured))
                return;

            var ext = typeof(GameEntity).Assembly.GetType("TaleWorlds.Engine.GameEntityPhysicsExtensions");
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
            _sk2 = sk.GetMethod("ApplyForceToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3), typeof(Vec3) }, null)
                ?? sk.GetMethod("AddForceToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3), typeof(Vec3) }, null)
                ?? sk.GetMethod("AddImpulseToBoneAtPos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3), typeof(Vec3) }, null)
                ?? sk.GetMethod("ApplyLocalImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3), typeof(Vec3) }, null)
                ?? sk.GetMethod("ApplyImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3), typeof(Vec3) }, null);
            if (_sk2 != null)
            {
                try { _dSk2 = (Action<Skeleton, Vec3, Vec3>)_sk2.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>)); }
                catch { _dSk2 = null; }
            }
            // Fallback: bind any reasonable (Vec3, Vec3) bone force/impulse method.
            if (_sk2 == null)
            {
                foreach (var mi in sk.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = mi.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(Vec3) && ps[1].ParameterType == typeof(Vec3))
                    {
                        var name = mi.Name.ToLowerInvariant();
                        if ((name.Contains("force") || name.Contains("impulse")) && (name.Contains("bone") || name.Contains("ragdoll")))
                        {
                            try
                            {
                                _dSk2 = (Action<Skeleton, Vec3, Vec3>)mi.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>));
                                _sk2 = mi;
                                break;
                            }
                            catch { }
                        }
                    }
                }
            }
            _sk1 = sk.GetMethod("ApplyForceToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3) }, null)
                ?? sk.GetMethod("AddForceToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3) }, null)
                ?? sk.GetMethod("AddImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3) }, null);
            if (_sk1 != null)
            {
                try { _dSk1 = (Action<Skeleton, Vec3>)_sk1.CreateDelegate(typeof(Action<Skeleton, Vec3>)); }
                catch { _dSk1 = null; }
            }
            if (_sk1 == null)
            {
                foreach (var mi in sk.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var ps = mi.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Vec3))
                    {
                        var name = mi.Name.ToLowerInvariant();
                        if ((name.Contains("force") || name.Contains("impulse")) && (name.Contains("bone") || name.Contains("ragdoll")))
                        {
                            try
                            {
                                _dSk1 = (Action<Skeleton, Vec3>)mi.CreateDelegate(typeof(Action<Skeleton, Vec3>));
                                _sk1 = mi;
                                break;
                            }
                            catch { }
                        }
                    }
                }
            }

            if (ER_Config.DebugLogging)
            {
                ER_Log.Info($"IMP_BIND ent3:{_ent3!=null}|{_dEnt3!=null} inst:{_ent3Inst!=null}|{_dEnt3Inst!=null} " +
                            $"ent2:{_ent2!=null}|{_dEnt2!=null} inst:{_ent2Inst!=null}|{_dEnt2Inst!=null} " +
                            $"ent1:{_ent1!=null}|{_dEnt1!=null} inst:{_ent1Inst!=null}|{_dEnt1Inst!=null} " +
                            $"sk2:{_sk2!=null}|{_dSk2!=null} sk1:{_sk1!=null}|{_dSk1!=null} isDyn:{_isDyn!=null}");
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
        private static void LogFailure(string context, Exception ex)
        {
            if (ex is AccessViolationException)
                return;
            if (ex is TargetInvocationException tie && tie.InnerException is AccessViolationException)
                return;
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

            if (!IsValidVec(worldImpulse) || worldImpulse.LengthSquared < ImpulseTinySqThreshold)
            {
                Log("IMPULSE_SKIP invalid impulse");
                return false;
            }

            var contact = worldPos;
            bool haveContact = TryResolveContact(ent, ref contact);
            if (!haveContact && skel == null)
            {
                Log("IMPULSE_SKIP invalid contact");
                return false;
            }

            // Always try entity routes when we have contact+entity.
            bool hasEnt = ent != null;
            bool forceEntity = ER_ImpulsePrefs.ForceEntityImpulse;
            bool allowFallbackWhenInvalid = ER_ImpulsePrefs.AllowSkeletonFallbackForInvalidEntity;
            bool skeletonAvailable = skel != null;
            bool allowSkeletonNow = skeletonAvailable && (!forceEntity || allowFallbackWhenInvalid);
            if (!hasEnt)
            {
                bool skeletonWillHandle = skeletonAvailable && (!forceEntity ? true : allowFallbackWhenInvalid);
                if (!skeletonWillHandle && !forceEntity)
                    Log("IMPULSE_SKIP ent routes: missing entity");
            }

            // Prefer contact entity routes first
            if (haveContact && hasEnt && !_ent3Unsafe && (_dEnt3Inst != null || _ent3Inst != null))
            {
                if (!LooksDynamic(ent) || !AabbSane(ent))
                    goto SKIP_ENT3_INST;
                try
                {
                    if (TryWorldToLocalSafe(ent, worldImpulse, contact, out var impL, out var posL))
                    {
                        if (_dEnt3Inst != null)
                            _dEnt3Inst(ent, impL, posL, true);
                        else
                            _ent3Inst.Invoke(ent, new object[] { impL, posL, true });
                        Log("IMPULSE_USE inst ent3(true)");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogFailure("inst ent3(true)", ex);
                    try
                    {
                        var ok = TryWorldToLocalSafe(ent, worldImpulse, contact, out var impL, out var posL);
                        var imp = ok ? impL : worldImpulse;
                        var pos = ok ? posL : contact;
                        if (_dEnt3Inst != null)
                            _dEnt3Inst(ent, imp, pos, ok);
                        else
                            _ent3Inst.Invoke(ent, new object[] { imp, pos, ok });
                        Log($"IMPULSE_USE inst ent3({ok})");
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        LogFailure("inst ent3(fallback)", ex2);
                        MarkUnsafe(3, ex2);
                    }
                }
            }
SKIP_ENT3_INST:

            if (haveContact && hasEnt && !_ent3Unsafe && (_dEnt3 != null || _ent3 != null))
            {
                if (!LooksDynamic(ent) || !AabbSane(ent))
                    goto SKIP_ENT3_EXT;
                try
                {
                    if (TryWorldToLocalSafe(ent, worldImpulse, contact, out var impL, out var posL))
                    {
                        if (_dEnt3 != null)
                            _dEnt3(ent, impL, posL, true);
                        else
                            _ent3.Invoke(null, new object[] { ent, impL, posL, true });
                        Log("IMPULSE_USE ext ent3(true)");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogFailure("ext ent3(true)", ex);
                    try
                    {
                        var ok = TryWorldToLocalSafe(ent, worldImpulse, contact, out var impL, out var posL);
                        var imp = ok ? impL : worldImpulse;
                        var pos = ok ? posL : contact;
                        if (_dEnt3 != null)
                            _dEnt3(ent, imp, pos, ok);
                        else
                            _ent3.Invoke(null, new object[] { ent, imp, pos, ok });
                        Log($"IMPULSE_USE ext ent3({ok})");
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        LogFailure("ext ent3(fallback)", ex2);
                        MarkUnsafe(3, ex2);
                    }
                }
            }
SKIP_ENT3_EXT:

            if (haveContact && hasEnt && !_ent2Unsafe && (_dEnt2Inst != null || _ent2Inst != null))
            {
                // Only touch ent2 when the body is truly dynamic and sane
                if (!LooksDynamic(ent) || !AabbSane(ent))
                    goto SKIP_ENT2_INST;
                try
                {
                    if (TryWorldToLocalSafe(ent, worldImpulse, contact, out var impL, out var posL))
                    {
                        if (_dEnt2Inst != null)
                            _dEnt2Inst(ent, impL, posL);
                        else
                            _ent2Inst.Invoke(ent, new object[] { impL, posL });
                        Log("IMPULSE_USE inst ent2");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogFailure("inst ent2", ex);
                    MarkUnsafe(2, ex);
                }
            }
SKIP_ENT2_INST:

            if (haveContact && hasEnt && !_ent2Unsafe && (_dEnt2 != null || _ent2 != null))
            {
                if (!LooksDynamic(ent) || !AabbSane(ent))
                    goto SKIP_ENT2_EXT;
                try
                {
                    if (TryWorldToLocalSafe(ent, worldImpulse, contact, out var impL, out var posL))
                    {
                        if (_dEnt2 != null)
                            _dEnt2(ent, impL, posL);
                        else
                            _ent2.Invoke(null, new object[] { ent, impL, posL });
                        Log("IMPULSE_USE ext ent2");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogFailure("ext ent2", ex);
                    MarkUnsafe(2, ex);
                }
            }
SKIP_ENT2_EXT:

            // New guarded ent1 (center-of-mass) fallback for cases where ent2 routes are missing/unsafe.
            if (hasEnt && !_ent1Unsafe && (_dEnt1 != null || _ent1 != null))
            {
                if (LooksDynamic(ent) && AabbSane(ent))
                {
                    try
                    {
                        if (_dEnt1 != null)
                            _dEnt1(ent, worldImpulse);
                        else
                            _ent1.Invoke(null, new object[] { ent, worldImpulse });
                        Log("IMPULSE_USE ext ent1");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogFailure("ext ent1", ex);
                        MarkUnsafe(1, ex);
                    }
                }
            }

            // Fallback to skeleton if entity routes were unavailable/unsafe.
            if (allowSkeletonNow)
            {
                if (haveContact && !_sk2Unsafe && (_dSk2 != null || _sk2 != null))
                {
                    try
                    {
                        if (TryWorldToLocalSafe(ent, worldImpulse, contact, out var impL, out var posL))
                        {
                            if (_dSk2 != null)
                                _dSk2(skel, impL, posL);
                            else
                                _sk2.Invoke(skel, new object[] { impL, posL });
                            Log("IMPULSE_USE skel2");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFailure("skel2", ex);
                        MarkUnsafe(5, ex);
                    }
                }

                // New: skel1 fallback does not require contact (handles scripted/spell deaths).
                if (!_sk1Unsafe && (_dSk1 != null || _sk1 != null))
                {
                    try
                    {
                        if (_dSk1 != null)
                            _dSk1(skel, worldImpulse);
                        else
                            _sk1.Invoke(skel, new object[] { worldImpulse });
                        Log("IMPULSE_USE skel1");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogFailure("skel1", ex);
                        MarkUnsafe(4, ex);
                    }
                }
            }

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
