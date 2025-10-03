using System;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    internal static class ER_Config
    {
        public static float KnockbackMultiplier       => Settings.Instance?.KnockbackMultiplier ?? 1.8f;
        public static float ExtraForceMultiplier      => MathF.Max(0f, Settings.Instance?.ExtraForceMultiplier ?? 1f);
        public static float DeathBlastRadius          => Settings.Instance?.DeathBlastRadius ?? 3.0f;
        public static float DeathBlastForceMultiplier => MathF.Max(0f, Settings.Instance?.DeathBlastForceMultiplier ?? 1f);
        public static bool  DebugLogging              => Settings.Instance?.DebugLogging ?? false;
        public static bool  RespectEngineBlowFlags    => Settings.Instance?.RespectEngineBlowFlags ?? false;
        public static bool  ForceEntityImpulse        => Settings.Instance?.ForceEntityImpulse ?? true;
        public static bool  AllowSkeletonFallbackForInvalidEntity => Settings.Instance?.AllowSkeletonFallbackForInvalidEntity ?? false;
        public static float MinMissileSpeedForPush    => MathF.Max(0f, Settings.Instance?.MinMissileSpeedForPush ?? 5f);
        public static bool  BlockedMissilesCanPush    => Settings.Instance?.BlockedMissilesCanPush ?? false;
        public static float LaunchDelay1              => Settings.Instance?.LaunchDelay1 ?? 0.02f;
        public static float LaunchDelay2              => Settings.Instance?.LaunchDelay2 ?? 0.06f;
        public static float LaunchPulse2Scale
        {
            get
            {
                float scale = Settings.Instance?.LaunchPulse2Scale ?? 0.50f;
                if (scale < 0f) scale = 0f;
                else if (scale > 2f) scale = 2f;
                return scale;
            }
        }
        public static float CorpseLaunchVelocityScaleThreshold => MathF.Max(1f, Settings.Instance?.CorpseLaunchVelocityScaleThreshold ?? 1.02f);
        public static float CorpseLaunchVelocityOffset         => Settings.Instance?.CorpseLaunchVelocityOffset ?? 0.01f;
        public static float CorpseLaunchVerticalDelta           => Settings.Instance?.CorpseLaunchVerticalDelta ?? 0.05f;
        public static float CorpseLaunchDisplacement            => Settings.Instance?.CorpseLaunchDisplacement ?? 0.03f;
        public static float MaxCorpseLaunchMagnitude            => Settings.Instance?.MaxCorpseLaunchMagnitude ?? 200_000_000f;
        public static float MaxAoEForce                         => Settings.Instance?.MaxAoEForce ?? 200_000_000f;
        public static float MaxBlowBaseMagnitude                => MathF.Max(0f, Settings.Instance?.MaxBlowBaseMagnitude ?? 0f);
        public static float MaxNonLethalKnockback               => Settings.Instance?.MaxNonLethalKnockback ?? 0f;
        public static float WarmupBlowBaseMagnitude
        {
            get
            {
                float value = Settings.Instance?.WarmupBlowBaseMagnitude ?? 20f;
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return 0f;
                return MathF.Min(value, 200f);
            }
        }
        public static float HorseRamKnockDownThreshold
        {
            get
            {
                float threshold = Settings.Instance?.HorseRamKnockDownThreshold ?? 12_000f;
                if (threshold < 0f) return 0f;
                return threshold;
            }
        }
        public static float CorpseImpulseMinimum                => MathF.Max(0f, Settings.Instance?.CorpseImpulseMinimum ?? 0f);
        public static float CorpseImpulseMaximum                => MathF.Max(0f, Settings.Instance?.CorpseImpulseMaximum ?? 0f);
        public static float CorpseImpulseHardCap
        {
            get
            {
                float cap = Settings.Instance?.CorpseImpulseHardCap ?? 60f;
                if (float.IsNaN(cap) || float.IsInfinity(cap) || cap <= 0f)
                    return 0f;
                return cap;
            }
        }
        public static float CorpseLaunchXYJitter                => MathF.Max(0f, Settings.Instance?.CorpseLaunchXYJitter ?? 0.002f);
        public static float CorpseLaunchContactHeight           => MathF.Max(0f, Settings.Instance?.CorpseLaunchContactHeight ?? 0.18f);
        public static float MaxAabbExtent
        {
            get
            {
                float cap = Settings.Instance?.MaxAabbExtent ?? 1024f;
                if (float.IsNaN(cap) || float.IsInfinity(cap) || cap <= 0f)
                    return 1024f;
                if (cap < 1f) cap = 1f;
                else if (cap > 10_000f) cap = 10_000f;
                return cap;
            }
        }
        public static float CorpseLaunchRetryDelay              => MathF.Max(0f, Settings.Instance?.CorpseLaunchRetryDelay ?? 0.03f);
        public static float CorpseLaunchRetryJitter             => MathF.Max(0f, Settings.Instance?.CorpseLaunchRetryJitter ?? 0.005f);
        public static float CorpseLaunchScheduleWindow          => MathF.Max(0f, Settings.Instance?.CorpseLaunchScheduleWindow ?? 0.08f);
        public static float CorpseLaunchZNudge                  => MathF.Max(0f, Settings.Instance?.CorpseLaunchZNudge ?? 0.05f);
        public static float CorpseLaunchZClampAbove             => MathF.Max(0f, Settings.Instance?.CorpseLaunchZClampAbove ?? 0.05f);
        public static float DeathBlastTtl                       => MathF.Max(0f, Settings.Instance?.DeathBlastTtl ?? 0.75f);
        public static float ImmediateImpulseScale
        {
            get
            {
                float scale = Settings.Instance?.ImmediateImpulseScale ?? 0.40f;
                if (float.IsNaN(scale) || float.IsInfinity(scale))
                    return 0f;
                if (scale < 0f) return 0f;
                if (scale > 1f) return 1f;
                return scale;
            }
        }
        public static float CorpseLaunchMaxUpFraction
        {
            get
            {
                float frac = Settings.Instance?.CorpseLaunchMaxUpFraction ?? 0.05f;
                if (frac < 0f) return 0f;
                if (frac > 1f) return 1f;
                return frac;
            }
        }
        public static int   CorpseLaunchQueueCap                => Math.Max(0, Settings.Instance?.CorpseLaunchQueueCap ?? 3);
        public static int   CorpsePrelaunchTries
        {
            get
            {
                int value = Settings.Instance?.CorpsePrelaunchTries ?? 12;
                if (value < 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }
        public static int   CorpsePostDeathTries
        {
            get
            {
                int value = Settings.Instance?.CorpsePostDeathTries ?? 20;
                if (value < 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }
        public static int   CorpseLaunchesPerTickCap
        {
            get
            {
                int value = Settings.Instance?.CorpseLaunchesPerTick ?? 128;
                if (value < 0) return 0;
                if (value > 2048) return 2048;
                return value;
            }
        }
        public static int   KicksPerTickCap
        {
            get
            {
                int value = Settings.Instance?.KicksPerTick ?? 128;
                if (value < 0) return 0;
                if (value > 2048) return 2048;
                return value;
            }
        }
        public static int   AoEAgentsPerTickCap
        {
            get
            {
                int value = Settings.Instance?.AoEAgentsPerTick ?? 256;
                if (value < 0) return 0;
                if (value > 4096) return 4096;
                return value;
            }
        }
    }

    [HarmonyPatch]
    internal static class ER_Amplify_RegisterBlowPatch
    {
        // --- HARD SAFETY CAPS (always enforced, even if MCM says 0) ---
        private const float HARD_BASE_CAP           = 80_000f;
        private const float HARD_ARROW_FLOOR_CAP    = 25_000f;
        private const float HARD_BIGSHOVE_FLOOR_CAP = 22_000f;
        private const float HARD_CORPSE_MAG_CAP     = 30_000f;
        private static float _lastAnyLog;

        internal struct PendingLaunch
        {
            public Vec3 dir;
            public float mag;
            public Vec3 pos;
            public float time;
        }

        internal static readonly Dictionary<int, PendingLaunch> _pending =
            new Dictionary<int, PendingLaunch>();
        private static readonly Dictionary<int, float> _lastScheduled = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _lastBigShoveLog = new Dictionary<int, float>();
        private static float _lastBigShoveTime;
        private static readonly HashSet<int> _bigShoveThisTick = new HashSet<int>();
        private static readonly FieldInfo MissileSpeedField = typeof(AttackCollisionData).GetField("MissileSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo MissileSpeedProperty = typeof(AttackCollisionData).GetProperty("MissileSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo SpeedFieldFallback = typeof(AttackCollisionData).GetField("Speed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MissileVelocityField = typeof(AttackCollisionData).GetField("MissileVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo VelocityField = typeof(AttackCollisionData).GetField("Velocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo MissileVelocityProperty = typeof(AttackCollisionData).GetProperty("MissileVelocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo VelocityProperty = typeof(AttackCollisionData).GetProperty("Velocity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static float ResolveMissileSpeed(AttackCollisionData data)
        {
            try
            {
                object boxed = data;
                if (MissileSpeedField != null)
                {
                    var value = MissileSpeedField.GetValue(boxed);
                    if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f)) return f;
                    if (value != null)
                    {
                        float converted = Convert.ToSingle(value);
                        if (!float.IsNaN(converted) && !float.IsInfinity(converted))
                            return converted;
                    }
                }

                if (MissileSpeedProperty != null)
                {
                    var value = MissileSpeedProperty.GetValue(boxed, null);
                    if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f)) return f;
                    if (value != null)
                    {
                        float converted = Convert.ToSingle(value);
                        if (!float.IsNaN(converted) && !float.IsInfinity(converted))
                            return converted;
                    }
                }

                if (SpeedFieldFallback != null)
                {
                    var value = SpeedFieldFallback.GetValue(boxed);
                    if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f)) return f;
                    if (value != null)
                    {
                        float converted = Convert.ToSingle(value);
                        if (!float.IsNaN(converted) && !float.IsInfinity(converted))
                            return converted;
                    }
                }

                if (MissileVelocityField != null)
                {
                    var value = MissileVelocityField.GetValue(boxed);
                    if (value is Vec3 vec)
                    {
                        float length = vec.Length;
                        if (!float.IsNaN(length) && !float.IsInfinity(length))
                            return MathF.Max(0f, length);
                    }
                }

                if (VelocityField != null)
                {
                    var value = VelocityField.GetValue(boxed);
                    if (value is Vec3 vec)
                    {
                        float length = vec.Length;
                        if (!float.IsNaN(length) && !float.IsInfinity(length))
                            return MathF.Max(0f, length);
                    }
                }

                if (MissileVelocityProperty != null)
                {
                    var value = MissileVelocityProperty.GetValue(boxed, null);
                    if (value is Vec3 vec)
                    {
                        float length = vec.Length;
                        if (!float.IsNaN(length) && !float.IsInfinity(length))
                            return MathF.Max(0f, length);
                    }
                }

                if (VelocityProperty != null)
                {
                    var value = VelocityProperty.GetValue(boxed, null);
                    if (value is Vec3 vec)
                    {
                        float length = vec.Length;
                        if (!float.IsNaN(length) && !float.IsInfinity(length))
                            return MathF.Max(0f, length);
                    }
                }
            }
            catch
            {
                // ignore reflection failures; treat as non-missile hit
            }

            return 0f;
        }
        private static float Cap(float value, float settingCap, float hardCap)
        {
            float cap = settingCap > 0f ? MathF.Min(settingCap, hardCap) : hardCap;
            return value > cap ? cap : value;
        }
        internal static void ClearPending()
        {
            _pending.Clear();
            _lastScheduled.Clear();
            _lastBigShoveLog.Clear();
            _bigShoveThisTick.Clear();
            _lastBigShoveTime = 0f;
            _lastAnyLog = 0f;
        }

        internal static bool TryTakePending(int agentId, out PendingLaunch pending)
        {
            if (_pending.TryGetValue(agentId, out pending))
            {
                _pending.Remove(agentId);
                return true;
            }
            pending = default;
            return false;
        }

        internal static void ForgetScheduled(int agentId)
        {
            _pending.Remove(agentId);
            _lastScheduled.Remove(agentId);
            _lastBigShoveLog.Remove(agentId);
        }

        internal static bool TryMarkScheduled(int agentId, float now, float windowSec = -1f)
        {
            if (windowSec < 0f)
                windowSec = ER_Config.CorpseLaunchScheduleWindow;
            if (_lastScheduled.TryGetValue(agentId, out var last) && now - last < windowSec)
                return false;
            _lastScheduled[agentId] = now;
            return true;
        }

        [HarmonyPrepare]
        static bool Prepare()
        {
            var method = TargetMethod();
            if (method == null)
            {
                foreach (var cand in AccessTools.GetDeclaredMethods(typeof(Agent)))
                {
                    if (cand.Name != nameof(Agent.RegisterBlow)) continue;
                    var sig = string.Join(", ", Array.ConvertAll(cand.GetParameters(), p => p.ParameterType.FullName));
                    ER_Log.Info("RegisterBlow overload: (" + sig + ")");
                }
                ER_Log.Error("Harmony target not found: Agent.RegisterBlow with byref AttackCollisionData");
                return false;
            }

            ER_Log.Info("Patching: " + method);
            return true;
        }

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var agent = typeof(Agent);
            var want = typeof(AttackCollisionData);
            var byRef = want.MakeByRefType();

            var method = AccessTools.Method(agent, nameof(Agent.RegisterBlow), new[] { typeof(Blow), byRef });
            if (method != null) return method;

            foreach (var candidate in AccessTools.GetDeclaredMethods(agent))
            {
                if (candidate.Name != nameof(Agent.RegisterBlow)) continue;

                var parameters = candidate.GetParameters();
                if (parameters.Length < 2) continue;
                if (parameters[0].ParameterType != typeof(Blow)) continue;

                var second = parameters[1].ParameterType;
                if (!second.IsByRef) continue;
                if (second.GetElementType() != want) continue;

                return candidate;
            }

            return null;
        }

        [HarmonyPrefix]
        [HarmonyAfter(new[] { "TOR", "TOR_Core" })]
        [HarmonyPriority(HarmonyLib.Priority.First)]
        private static void Prefix(
            Agent __instance,
            [HarmonyArgument(0)] ref Blow blow,
            [HarmonyArgument(1)] ref AttackCollisionData attackCollisionData)
        {
            if (__instance == null) return;

            float timeNow = __instance.Mission?.CurrentTime ?? Mission.Current?.CurrentTime ?? 0f;
            if (timeNow != _lastBigShoveTime)
            {
                _lastBigShoveTime = timeNow;
                _bigShoveThisTick.Clear();
            }

            float hp = __instance.Health;
            if (hp <= 0f)
            {
                _pending.Remove(__instance.Index);
                return;
            }

            // Zero-Damage-Spells können tödlich sein → nur aussteigen, wenn auch keine Projektil-Geschwindigkeit.
            // (ResolveMissileSpeed ist dank gecachtem Reflection-Zugriff günstig.)
            float missileSpeed = 0f;
            try { missileSpeed = ResolveMissileSpeed(attackCollisionData); }
            catch { missileSpeed = 0f; }
            if (float.IsNaN(missileSpeed) || float.IsInfinity(missileSpeed)) missileSpeed = 0f;
            float minPushSpeed = ER_Config.MinMissileSpeedForPush;
            if (missileSpeed < minPushSpeed)
                missileSpeed = 0f;

            bool lethal = hp - blow.InflictedDamage <= 0f;
            bool missileBlocked = !lethal && missileSpeed > 0f && blow.InflictedDamage <= 0f;
            bool allowBlockedPush = ER_Config.BlockedMissilesCanPush;

            // bail gate: only truly trivial taps
            if (!lethal && blow.InflictedDamage <= 0f && missileSpeed <= 0f && blow.BaseMagnitude <= 1f)
            {
                _pending.Remove(__instance.Index);
                return;
            }

            // compute a sane push dir (used only when we decide to replace)
            Vec3 dir = __instance.Position - blow.GlobalPosition;
            if (dir.LengthSquared < 1e-6f)
            {
                try { dir = __instance.LookDirection; }
                catch { dir = new Vec3(0f, 1f, 0.25f); }
            }
            dir = ER_DeathBlastBehavior.PrepDir(dir, 0.35f, 1.05f);

            // ensure flags so lethal always ragdolls; missiles shove
            if (lethal)
            {
                blow.BlowFlag |= BlowFlags.KnockBack;
                if (!__instance.HasMount)
                    blow.BlowFlag |= BlowFlags.KnockDown;
                if (blow.SwingDirection.LengthSquared <= 1e-6f)
                    blow.SwingDirection = dir;
            }
            else if (missileSpeed > 0f)
            {
                if (!missileBlocked || allowBlockedPush)
                    blow.BlowFlag |= BlowFlags.KnockBack;
            }

            // Horse/body shove fallback: strong non-missile hits should still knock back.
            bool bigShove = !lethal && missileSpeed <= 0f && blow.BaseMagnitude >= 3000f;
            bool canBoostBigShove = false;
            if (bigShove)
            {
                blow.BlowFlag |= BlowFlags.KnockBack;
                if (blow.SwingDirection.LengthSquared < 1e-6f) blow.SwingDirection = dir;
                float kdThreshold = ER_Config.HorseRamKnockDownThreshold;
                if (kdThreshold > 0f && blow.BaseMagnitude >= kdThreshold && !__instance.HasMount)
                    blow.BlowFlag |= BlowFlags.KnockDown;
                canBoostBigShove = _bigShoveThisTick.Add(__instance.Index);
            }

            bool respectBlow = ER_Config.RespectEngineBlowFlags;

            if ((!missileBlocked || allowBlockedPush) && !lethal && missileSpeed > 0f)
            {
                // Mostly planar. Final vertical clamp happens in PrepDir/ClampVertical.
                var flat = ER_DeathBlastBehavior.PrepDir(dir, 1f, 0f);
                blow.SwingDirection = flat;
            }

            if (!respectBlow)
            {
                if (lethal)
                {
                    // lethal must ragdoll
                    if (blow.SwingDirection.LengthSquared <= 1e-6f)
                        blow.SwingDirection = dir;

                    // lethal magnitude (reduced missile weight + hard cap)
                    // Safe lethal boost (low) to avoid vertical pop while impulses are validated
                    float mult    = MathF.Max(1f, ER_Config.ExtraForceMultiplier);
                    float desired = (6000f + blow.InflictedDamage * 150f + missileSpeed * 4f) * mult;
                    desired       = Cap(desired, ER_Config.MaxBlowBaseMagnitude, 20000f);
                    if (desired > 0f && !float.IsNaN(desired) && !float.IsInfinity(desired) && blow.BaseMagnitude < desired)
                    {
                        if (ER_Config.DebugLogging)
                        {
                            float nowLog = timeNow;
                            if (nowLog - _lastAnyLog > 0.5f)
                            {
                                _lastAnyLog = nowLog;
                                ER_Log.Info($"[ER] LethalBoost -> {desired:0}");
                            }
                        }
                        blow.BaseMagnitude = desired;
                    }
                }
            }

            if (lethal)
            {
                // Let physics impulses drive motion; keep ragdoll/no-sound only.
                blow.BlowFlag &= ~BlowFlags.KnockBack;
                blow.BlowFlag |= BlowFlags.KnockDown | BlowFlags.NoSound;

                if (ER_Config.DebugLogging)
                {
                    float nowLog = timeNow;
                    if (nowLog - _lastAnyLog > 0.5f)
                    {
                        _lastAnyLog = nowLog;
                        ER_Log.Info("[ER] LethalKeep: engine KB stripped");
                    }
                }

                // Let impulses drive motion; neutralize engine KB
                var lethalDir = ER_DeathBlastBehavior.PrepDir(dir,
                                   1f,
                                   missileSpeed > 0f ? 0f : 0.02f);
                blow.BaseMagnitude = 0f;
                if (missileSpeed > 0f) lethalDir.z = 0f; // hard-flat missiles
                lethalDir = ER_DeathBlastBehavior.FinalizeImpulseDir(lethalDir);
                blow.SwingDirection = lethalDir;
                // ensure pending corpse-launch uses the same (flattened) direction
                dir = lethalDir;
            }
            // Apply magnitude floors even when respecting engine flags
            if (!lethal)
            {
                if (missileSpeed > 0f && (!missileBlocked || allowBlockedPush))
                {
                    float floor = 3000f + missileSpeed * 25f;
                    floor = Cap(floor, ER_Config.MaxBlowBaseMagnitude, 15000f);
                    if (ER_Config.MaxNonLethalKnockback > 0f) floor = MathF.Min(floor, ER_Config.MaxNonLethalKnockback);
                    float origBase = blow.BaseMagnitude;
                    if (origBase + 500f < floor)
                    {
                        blow.BaseMagnitude = floor;
                        if (ER_Config.DebugLogging)
                        {
                            float nowLog = timeNow;
                            if (nowLog - _lastAnyLog > 0.5f)
                            {
                                _lastAnyLog = nowLog;
                                ER_Log.Info($"[ER] ArrowFloor -> {floor:0}");
                            }
                        }
                    }
                }
                else if (bigShove)
                {
                    float origBase   = blow.BaseMagnitude;
                    float shoveFloor = MathF.Max(10000f, 7000f + origBase * 0.20f);
                    shoveFloor       = Cap(shoveFloor, ER_Config.MaxBlowBaseMagnitude, HARD_BIGSHOVE_FLOOR_CAP);
                    if (ER_Config.MaxNonLethalKnockback > 0f) shoveFloor = MathF.Min(shoveFloor, ER_Config.MaxNonLethalKnockback);
                    if (canBoostBigShove && origBase + 800f < shoveFloor) // ignore tiny nudges
                    {
                        blow.BaseMagnitude = shoveFloor;
                        if (ER_Config.DebugLogging)
                        {
                            float nowLog = timeNow;
                            if (!_lastBigShoveLog.TryGetValue(__instance.Index, out var last) || nowLog - last >= 0.5f)
                            {
                                _lastBigShoveLog[__instance.Index] = nowLog;
                                ER_Log.Info($"[ER] BigShove {origBase:0}->{blow.BaseMagnitude:0}");
                            }
                        }
                    }
                }
            }

            // Global max cap (applies regardless of lethal state)
            blow.BaseMagnitude = Cap(blow.BaseMagnitude, ER_Config.MaxBlowBaseMagnitude, HARD_BASE_CAP);

            // IMPORTANT: do NOT touch SwingDirection for non-missile, non-lethal blows (keeps horse charge intact)

            float swingLenSq = blow.SwingDirection.LengthSquared;
            if (swingLenSq <= 1e-6f)
            {
                // keep a valid push dir instead of zeroing it
                if (lethal || missileSpeed > 0f || bigShove)
                    blow.SwingDirection = dir;
            }
            else
            {
                blow.SwingDirection = blow.SwingDirection.NormalizedCopy();
            }

            if (!(blow.BaseMagnitude > 0f) || float.IsNaN(blow.BaseMagnitude) || float.IsInfinity(blow.BaseMagnitude))
                blow.BaseMagnitude = 1f;

            if (lethal)
            {

                if (ER_Config.MaxCorpseLaunchMagnitude > 0f)
                {
                    float extraMult = MathF.Max(1f, ER_Config.ExtraForceMultiplier);
                    float mag = (10000f + blow.InflictedDamage * 120f) * extraMult * 0.25f;
                    if (missileSpeed > 0f) mag += missileSpeed * 50f;
                    if (mag > HARD_CORPSE_MAG_CAP) mag = HARD_CORPSE_MAG_CAP;
                    float maxMag = ER_Config.MaxCorpseLaunchMagnitude;
                    if (mag > maxMag)
                    {
                        mag = maxMag;
                    }
                    if (mag <= 0f || float.IsNaN(mag) || float.IsInfinity(mag))
                    {
                        _pending.Remove(__instance.Index);
                        return;
                    }
                    Vec3 contact = blow.GlobalPosition;
                    if (float.IsNaN(contact.x) || float.IsNaN(contact.y) || float.IsNaN(contact.z) ||
                        float.IsInfinity(contact.x) || float.IsInfinity(contact.y) || float.IsInfinity(contact.z))
                    {
                        contact = __instance.Position;
                    }
                    float recorded = __instance.Mission?.CurrentTime ?? 0f;
                    _pending[__instance.Index] = new PendingLaunch
                    {
                        dir  = dir,
                        mag  = mag,
                        pos  = contact,
                        time = recorded,
                    };
                    var mission = __instance.Mission ?? Mission.Current;
                    mission?
                        .GetMissionBehavior<ER_DeathBlastBehavior>()
                        ?.QueuePreDeath(__instance, dir, mag, contact);
                }
                else
                {
                    _pending.Remove(__instance.Index);
                }
            }
            else
            {
                // Non-lethal: nudge via our behavior after the engine finishes (no damage side-effects).
                var beh = (__instance.Mission ?? Mission.Current)?.GetMissionBehavior<ER_DeathBlastBehavior>();
                float kickMag = (10000f + blow.InflictedDamage * 120f) * MathF.Max(1f, ER_Config.KnockbackMultiplier);
                beh?.EnqueueKick(__instance, dir, kickMag, 0.10f);
                _pending.Remove(__instance.Index);
            }
        }
    }

}
