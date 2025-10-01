using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    internal static class ER_ImpulseRouter
    {
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

        private static bool AabbSane(GameEntity ent)
        {
            try
            {
                var mn = ent.GetPhysicsBoundingBoxMin();
                var mx = ent.GetPhysicsBoundingBoxMax();
                if (float.IsNaN(mn.x) || float.IsNaN(mn.y) || float.IsNaN(mn.z) ||
                    float.IsNaN(mx.x) || float.IsNaN(mx.y) || float.IsNaN(mx.z) ||
                    float.IsInfinity(mn.x) || float.IsInfinity(mn.y) || float.IsInfinity(mn.z) ||
                    float.IsInfinity(mx.x) || float.IsInfinity(mx.y) || float.IsInfinity(mx.z))
                    return false;

                if (MathF.Abs(mn.x) > 1e5f || MathF.Abs(mn.y) > 1e5f || MathF.Abs(mn.z) > 1e5f)
                    return false;
                if (MathF.Abs(mx.x) > 1e5f || MathF.Abs(mx.y) > 1e5f || MathF.Abs(mx.z) > 1e5f)
                    return false;

                var d = mx - mn;
                if (d.x <= 0f || d.y <= 0f || d.z <= 0f)
                    return false;
                if (d.x * d.y * d.z < 1e-6f)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Ensure()
        {
            if (_ensured)
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
            _sk2 = sk.GetMethod("ApplyLocalImpulseToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3), typeof(Vec3) }, null);
            if (_sk2 != null)
            {
                try { _dSk2 = (Action<Skeleton, Vec3, Vec3>)_sk2.CreateDelegate(typeof(Action<Skeleton, Vec3, Vec3>)); }
                catch { _dSk2 = null; }
            }
            _sk1 = sk.GetMethod("ApplyForceToBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new[] { typeof(Vec3) }, null);
            if (_sk1 != null)
            {
                try { _dSk1 = (Action<Skeleton, Vec3>)_sk1.CreateDelegate(typeof(Action<Skeleton, Vec3>)); }
                catch { _dSk1 = null; }
            }

            _ensured = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MarkUnsafe(int which, Exception ex)
        {
            if (!(ex is AccessViolationException))
                return;

            switch (which)
            {
                case 1: _ent1Unsafe = true; break;
                case 2: _ent2Unsafe = true; break;
                case 3: _ent3Unsafe = true; break;
                case 4: _sk1Unsafe = true; break;
                case 5: _sk2Unsafe = true; break;
            }

            ER_Log.Info($"IMPULSE_DISABLE route#{which} after {ex.GetType().Name}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldLog(float now, float minDelta = 0.25f)
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
            Log($"IMPULSE_FAIL {context}: {ex.GetType().Name}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsValidVec(in Vec3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                   !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }

        public static bool TryImpulse(GameEntity ent, Skeleton skel, in Vec3 worldImpulse, in Vec3 worldPos)
        {
            Ensure();

            if (!IsValidVec(worldImpulse) || worldImpulse.LengthSquared < 1e-12f)
            {
                Log("IMPULSE_SKIP invalid impulse");
                return false;
            }

            if (!IsValidVec(worldPos))
            {
                Log("IMPULSE_SKIP invalid contact");
                return false;
            }

            bool canEnt = ent != null && LooksDynamic(ent) && AabbSane(ent);
            if (!canEnt)
                Log("IMPULSE_SKIP ent routes: non-dynamic or bad AABB");

            // Prefer skeleton paths before entity routes; corpses often retain non-dynamic entities
            if (skel != null)
            {
                if (!_sk2Unsafe && (_dSk2 != null || _sk2 != null))
                {
                    try
                    {
                        if (ER_Space.TryWorldToLocal(ent, worldImpulse, worldPos, out var impL, out var posL))
                        {
                            if (_dSk2 != null)
                                _dSk2(skel, impL, posL);
                            else
                                _sk2.Invoke(skel, new object[] { impL, posL });
                            Log("IMPULSE_USE skel2-first");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogFailure("skel2-first", ex);
                        MarkUnsafe(5, ex);
                    }
                }

                if (!_sk1Unsafe && (_dSk1 != null || _sk1 != null))
                {
                    try
                    {
                        if (_dSk1 != null)
                            _dSk1(skel, worldImpulse);
                        else
                            _sk1.Invoke(skel, new object[] { worldImpulse });
                        Log("IMPULSE_USE skel1-first");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogFailure("skel1-first", ex);
                        MarkUnsafe(4, ex);
                    }
                }
            }

            if (canEnt && !_ent3Unsafe && (_dEnt3Inst != null || _ent3Inst != null))
            {
                try
                {
                    if (_dEnt3Inst != null)
                        _dEnt3Inst(ent, worldImpulse, worldPos, false);
                    else
                        _ent3Inst.Invoke(ent, new object[] { worldImpulse, worldPos, false });
                    Log("IMPULSE_USE inst ent3(false)");
                    return true;
                }
                catch (Exception ex)
                {
                    LogFailure("inst ent3(false)", ex);
                    try
                    {
                        var ok = ER_Space.TryWorldToLocal(ent, worldImpulse, worldPos, out var impL, out var posL);
                        if (_dEnt3Inst != null)
                            _dEnt3Inst(ent, ok ? impL : worldImpulse, ok ? posL : worldPos, true);
                        else
                            _ent3Inst.Invoke(ent, new object[] { ok ? impL : worldImpulse, ok ? posL : worldPos, true });
                        Log("IMPULSE_USE inst ent3(true)");
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        LogFailure("inst ent3(true)", ex2);
                        MarkUnsafe(3, ex2);
                    }
                }
            }

            if (canEnt && !_ent3Unsafe && (_dEnt3 != null || _ent3 != null))
            {
                try
                {
                    if (_dEnt3 != null)
                        _dEnt3(ent, worldImpulse, worldPos, false);
                    else
                        _ent3.Invoke(null, new object[] { ent, worldImpulse, worldPos, false });
                    Log("IMPULSE_USE ext ent3(false)");
                    return true;
                }
                catch (Exception ex)
                {
                    LogFailure("ext ent3(false)", ex);
                    try
                    {
                        var ok = ER_Space.TryWorldToLocal(ent, worldImpulse, worldPos, out var impL, out var posL);
                        if (_dEnt3 != null)
                            _dEnt3(ent, ok ? impL : worldImpulse, ok ? posL : worldPos, true);
                        else
                            _ent3.Invoke(null, new object[] { ent, ok ? impL : worldImpulse, ok ? posL : worldPos, true });
                        Log("IMPULSE_USE ext ent3(true)");
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        LogFailure("ext ent3(true)", ex2);
                        MarkUnsafe(3, ex2);
                    }
                }
            }

            if (canEnt && !_ent2Unsafe && (_dEnt2Inst != null || _ent2Inst != null))
            {
                try
                {
                    if (ER_Space.TryWorldToLocal(ent, worldImpulse, worldPos, out var impL, out var posL))
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

            if (canEnt && !_ent2Unsafe && (_dEnt2 != null || _ent2 != null))
            {
                try
                {
                    if (ER_Space.TryWorldToLocal(ent, worldImpulse, worldPos, out var impL, out var posL))
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

            if (canEnt && !_ent1Unsafe && (_dEnt1Inst != null || _ent1Inst != null))
            {
                try
                {
                    if (_dEnt1Inst != null)
                        _dEnt1Inst(ent, worldImpulse);
                    else
                        _ent1Inst.Invoke(ent, new object[] { worldImpulse });
                    Log("IMPULSE_USE inst ent1");
                    return true;
                }
                catch (Exception ex)
                {
                    LogFailure("inst ent1", ex);
                    MarkUnsafe(1, ex);
                }
            }

            if (canEnt && !_ent1Unsafe && (_dEnt1 != null || _ent1 != null))
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

            if (skel != null)
            {
                if (!_sk2Unsafe && (_dSk2 != null || _sk2 != null))
                {
                    try
                    {
                        if (ER_Space.TryWorldToLocal(ent, worldImpulse, worldPos, out var impL, out var posL))
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

            Log("IMPULSE_END: no route usable (after gating)");
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
