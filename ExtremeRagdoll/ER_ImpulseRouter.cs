using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace ExtremeRagdoll
{
    internal static class ER_ImpulseRouter
    {
        private static bool _ent1Unsafe, _ent2Unsafe, _ent3Unsafe, _sk1Unsafe, _sk2Unsafe;
        private static MethodInfo _ent3, _ent2, _ent1;
        private static MethodInfo _sk2, _sk1;
        private static Action<GameEntity, Vec3, Vec3, bool> _dEnt3;
        private static Action<GameEntity, Vec3, Vec3> _dEnt2;
        private static Action<GameEntity, Vec3> _dEnt1;
        private static Action<Skeleton, Vec3, Vec3> _dSk2;
        private static Action<Skeleton, Vec3> _dSk1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Ensure()
        {
            if (_ent1 != null || _ent2 != null || _ent3 != null)
                return;

            var ext = typeof(GameEntity).Assembly.GetType("TaleWorlds.Engine.GameEntityPhysicsExtensions");
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

        public static bool TryImpulse(GameEntity ent, Skeleton skel, in Vec3 worldImpulse, in Vec3 worldPos)
        {
            Ensure();

            if (!_ent3Unsafe && ent != null && (_dEnt3 != null || _ent3 != null))
            {
                try
                {
                    if (_dEnt3 != null)
                        _dEnt3(ent, worldImpulse, worldPos, false);
                    else
                        _ent3.Invoke(null, new object[] { ent, worldImpulse, worldPos, false });
                    if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent3");
                    return true;
                }
                catch (Exception ex)
                {
                    MarkUnsafe(3, ex);
                }
            }

            if (!_ent2Unsafe && ent != null && (_dEnt2 != null || _ent2 != null))
            {
                try
                {
                    if (ER_Space.TryWorldToLocal(ent, worldImpulse, worldPos, out var impL, out var posL))
                    {
                        if (_dEnt2 != null)
                            _dEnt2(ent, impL, posL);
                        else
                            _ent2.Invoke(null, new object[] { ent, impL, posL });
                        if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent2");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    MarkUnsafe(2, ex);
                }
            }

            if (!_ent1Unsafe && ent != null && (_dEnt1 != null || _ent1 != null))
            {
                try
                {
                    if (_dEnt1 != null)
                        _dEnt1(ent, worldImpulse);
                    else
                        _ent1.Invoke(null, new object[] { ent, worldImpulse });
                    if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE ext ent1");
                    return true;
                }
                catch (Exception ex)
                {
                    MarkUnsafe(1, ex);
                }
            }

            if (skel != null)
            {
                if (!_sk2Unsafe && (_dSk2 != null || _sk2 != null))
                {
                    try
                    {
                        if (ER_Space.TryWorldToLocal(skel, worldImpulse, worldPos, out var impL, out var posL))
                        {
                            if (_dSk2 != null)
                                _dSk2(skel, impL, posL);
                            else
                                _sk2.Invoke(skel, new object[] { impL, posL });
                            if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE skel2");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
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
                        if (ER_Config.DebugLogging) ER_Log.Info("IMPULSE_USE skel1");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        MarkUnsafe(4, ex);
                    }
                }
            }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWorldToLocal(Skeleton skel, in Vec3 wImp, in Vec3 wPos, out Vec3 lImp, out Vec3 lPos)
        {
            lImp = wImp;
            lPos = wPos;
            if (skel == null)
                return false;
            try {
                MatrixFrame frame;
                try { frame = skel.GetFrame(); }
                catch { frame = default; }
                lPos = frame.TransformToLocal(wPos);
                lImp = frame.TransformToLocal(wPos + wImp) - lPos;
                return true;
            } catch { return false; }
        }
    }
}
