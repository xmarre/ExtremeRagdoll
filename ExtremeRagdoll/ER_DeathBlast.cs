using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;           // for GameEntity, Skeleton, MBCommon
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using System.Reflection;           // reflection fallback for impulse API
using System.Runtime.CompilerServices;
using System.Threading;

namespace ExtremeRagdoll
{
    public sealed class ER_DeathBlastBehavior : MissionBehavior
    {
        private static readonly ConcurrentDictionary<Type, Func<object, bool>> _ragdollStateCache = new ConcurrentDictionary<Type, Func<object, bool>>();
        private static readonly ConditionalWeakTable<GameEntity, object> _preparedEntities = new ConditionalWeakTable<GameEntity, object>();
        private static readonly object _preparedMarker = new object();
        private static float CorpseLaunchMaxUpFrac => ER_Config.CorpseLaunchMaxUpFraction;
        private const float DirectionTinySqThreshold = ER_Math.DirectionTinySq;
        private const float PositionTinySqThreshold = ER_Math.PositionTinySq;
        // tiny, cheap per-frame guard
        private float _lastTickT;
        private static int _impulseLogCount;
        internal static Vec3 ClampVertical(Vec3 dir)
        {
            if (dir.LengthSquared < DirectionTinySqThreshold)
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
            if (lenSq < DirectionTinySqThreshold)
                return new Vec3(0f, 1f, 0f);

            return dir.NormalizedCopy();
        }

        internal static Vec3 PrepDir(Vec3 dir, float planarScale = 0.90f, float upBias = 0.10f)
        {
            if (!ER_Math.IsFinite(in dir) || dir.LengthSquared < DirectionTinySqThreshold)
                dir = new Vec3(0f, 1f, 0f);
            else
                dir = dir.NormalizedCopy();

            var biased = dir * planarScale + new Vec3(0f, 0f, upBias);
            biased = ClampVertical(biased);

            float lenSq = biased.LengthSquared;
            if (lenSq < DirectionTinySqThreshold || float.IsNaN(lenSq) || float.IsInfinity(lenSq))
                return new Vec3(0f, 1f, 0f);

            return biased.NormalizedCopy();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vec3 FinalizeImpulseDir(Vec3 dir)
        {
            if (!ER_Math.IsFinite(in dir))
                return new Vec3(0f, 1f, 0f);
            dir = ClampVertical(dir);
            float l2 = dir.LengthSquared;
            if (l2 < DirectionTinySqThreshold || float.IsNaN(l2) || float.IsInfinity(l2))
                return new Vec3(0f, 1f, 0f);
            return dir.NormalizedCopy();
        }

        private static bool IsRagdollActiveFast(Skeleton sk)
        {
            if (sk == null)
                return false;
            try
            {
                var t = sk.GetType();
                Func<object, bool> eval = _ragdollStateCache.GetOrAdd(t, type =>
                {
                    var pi = type.GetProperty("IsRagdollActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? type.GetProperty("IsRagdollModeActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? type.GetProperty("RagdollActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (pi == null)
                        return _ => false;

                    return obj =>
                    {
                        try { return (bool)pi.GetValue(obj); }
                        catch { return false; }
                    };
                });
                return eval?.Invoke(sk) ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static Vec3 ResolveHitPosition(Vec3 candidate, GameEntity ent, in Vec3 fallback)
        {
            bool invalid = !ER_Math.IsFinite(in candidate);
            if (!invalid)
            {
                float lenSq = candidate.LengthSquared;
                if (lenSq < PositionTinySqThreshold || float.IsNaN(lenSq) || float.IsInfinity(lenSq))
                    invalid = true;
            }

            if (invalid)
            {
                bool resolved = false;
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
                        var origin = frame.origin;
                        if (ER_Math.IsFinite(in origin))
                        {
                            float originLenSq = origin.LengthSquared;
                            if (originLenSq >= PositionTinySqThreshold && !float.IsNaN(originLenSq) && !float.IsInfinity(originLenSq))
                            {
                                candidate = origin;
                                resolved = true;
                            }
                        }
                    }
                    catch
                    {
                        // fall through
                    }
                }
                if (!resolved)
                    candidate = fallback;
            }

            if (!ER_Math.IsFinite(in candidate))
                candidate = fallback;

            float candLenSq = candidate.LengthSquared;
            if (candLenSq < PositionTinySqThreshold || float.IsNaN(candLenSq) || float.IsInfinity(candLenSq))
                candidate = fallback;

            return candidate;
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

        private static void PrepareRagdoll(GameEntity ent, Skeleton skel, out bool prepared)
        {
            prepared = false;
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
        }

        private static bool TryApplyImpulse(GameEntity ent, Skeleton skel, Vec3 impulse, Vec3 pos, int agentId = -1)
        {
            _ = agentId;
            if (!ER_Math.IsFinite(in impulse) || !ER_Math.IsFinite(in pos))
                return false;

            float impulseSq = impulse.LengthSquared;
            if (impulseSq < ER_Math.ImpulseTinySq || float.IsNaN(impulseSq) || float.IsInfinity(impulseSq))
                return false;

            PrepareRagdoll(ent, skel, out bool prepared);
            if (!prepared)
            {
                ER_RagdollPrep.Prep(ent, skel);
                MarkRagdollPrepared(ent);
            }

            bool ok = ER_ImpulseRouter.TryImpulse(ent, skel, impulse, pos);
            if (!ok && (ent != null || skel != null))
            {
                WarmRagdoll(ent, skel);
                ok = ER_ImpulseRouter.TryImpulse(ent, skel, impulse, pos);
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
            var eval = _ragdollStateCache.GetOrAdd(type, CreateRagdollStateEvaluator);
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
            // hard safety to prevent “rocket” corpses if config is mis-set
            float hardImpulseCap = ER_Config.CorpseImpulseHardCap;
            bool hardCap = false;
            if (hardImpulseCap > 0f && imp > hardImpulseCap)
            {
                imp = hardImpulseCap;
                hardCap = true;
            }

            if (imp < 0f || float.IsNaN(imp) || float.IsInfinity(imp))
                return 0f;

            if (ER_Config.DebugLogging && _impulseLogCount < 16)
            {
                _impulseLogCount++;
                var tag = hardCap ? " HARD_CAP" : string.Empty;
                string hardCapInfo = hardImpulseCap > 0f ? $" hardCap={hardImpulseCap:F1}" : string.Empty;
                ER_Log.Info($"IMPULSE_MAP{tag} mag={mag:F1} -> imp={imp:F1} min={minImpulse:F1} max={maxImpulse:F1}{hardCapInfo}");
            }
            return imp;
        }

        private struct Blast { public Vec3 Pos; public float Radius; public float Force; public float T; }
        private struct Kick  { public Agent A; public Vec3 Dir; public float Force; public float T0; public float Dur; }
        private struct Launch { public Agent A; public GameEntity Ent; public Skeleton Skel; public Vec3 Dir; public float Mag; public Vec3 Pos; public float T; public int Tries; public int AgentId; public bool Warmed; public bool Boosted; public Vec3 P0; public Vec3 V0; }
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
            if (!ER_Math.IsFinite(in safeDir) || safeDir.LengthSquared < DirectionTinySqThreshold)
                return;

            Vec3 contact = pos;
            if (!ER_Math.IsFinite(in contact) || contact.LengthSquared < PositionTinySqThreshold)
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
            if (a.Health <= 0f) return; // kicks are for living agents only
            Vec3 safeDir = PrepDir(dir, 1f, 0f);
            safeDir = FinalizeImpulseDir(safeDir);
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
            if (!ER_Math.IsFinite(in safeDir) || safeDir.LengthSquared < DirectionTinySqThreshold)
                return;
            Vec3 nudgedPos = pos;
            float zNudge = ER_Config.CorpseLaunchZNudge;
            float zClamp = ER_Config.CorpseLaunchZClampAbove;
            float agentZ = nudgedPos.z;
            try { agentZ = a.Position.z; } catch { }
            nudgedPos.z = MathF.Min(nudgedPos.z + zNudge, agentZ + zClamp);

            GameEntity ent = null; Skeleton sk = null;
            try { ent = a.AgentVisuals?.GetEntity(); } catch { }
            try { sk = a.AgentVisuals?.GetSkeleton(); } catch { }
            _launches.Add(new Launch { A = a, Ent = ent, Skel = sk, Dir = safeDir, Mag = mag, Pos = nudgedPos, T = mission.CurrentTime + delaySec, Tries = retries, AgentId = agentIndex, Warmed = false, Boosted = false });
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
            // cheapest exits first
            if (dt <= 1e-6f) return;
            var mission = Mission;
            if (mission == null || mission.Agents == null) return;
            if (IsPausedFast()) return;
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
            int warmedThisTick = 0;
            const int warmCapPerTick = 8;
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
                    if (warmedThisTick >= warmCapPerTick)
                    {
                        entry.NextTry = now + preRetryDelay;
                        _preLaunches[i] = entry;
                        continue;
                    }
                    GameEntity warmEnt = null;
                    Skeleton warmSkel = null;
                    try { warmEnt = agent?.AgentVisuals?.GetEntity(); } catch { }
                    try { warmSkel = agent?.AgentVisuals?.GetSkeleton(); } catch { }
                    WarmRagdoll(warmEnt, warmSkel);
                    entry.Warmed = true;
                    entry.NextTry = now + preRetryDelay;
                    _preLaunches[i] = entry;
                    warmedThisTick++;
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
                        Vec3 fallbackContact = entry.Pos;
                        try { fallbackContact = agent.Position; }
                        catch { fallbackContact = entry.Pos; }
                        Vec3 contact = ResolveHitPosition(entry.Pos, ent, fallbackContact);
                        try
                        {
                            contact.z = MathF.Min(contact.z, agent.Position.z + zClamp);
                        }
                        catch
                        {
                        }
                        Vec3 dir = entry.Dir;
                        try
                        {
                            // Pre-death warm: push while agent is still alive so ragdoll becomes dynamic.
                            if (dir.LengthSquared < DirectionTinySqThreshold)
                            {
                                try { dir = agent.LookDirection; } catch { dir = new Vec3(0f, 1f, 0.25f); }
                            }
                            // Minimal up-bias to prevent vertical rockets
                            dir = ER_DeathBlastBehavior.PrepDir(dir, 0.35f, 0.25f);
                            dir = FinalizeImpulseDir(dir);

                            // Warm ragdoll only. Do not shove pre-death.
                            float warmBase = ER_Config.WarmupBlowBaseMagnitude;
                            var kb = new Blow(-1)
                            {
                                DamageType      = DamageTypes.Blunt,
                                BlowFlag        = BlowFlags.KnockDown | BlowFlags.NoSound,
                                BaseMagnitude   = warmBase,
                                SwingDirection  = dir,
                                GlobalPosition  = contact,
                                InflictedDamage = 0
                            };
                            AttackCollisionData acd = default;
                            agent.RegisterBlow(kb, in acd);

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
                // Skip kicks once dead to avoid stacking engine knockback with physics impulses
                if (k.A.Health <= 0f)
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

                Vec3 dir = k.Dir;

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
                bool launchRemoved = false;
                void RemoveLaunch()
                {
                    if (!launchRemoved)
                    {
                        _launches.RemoveAt(i);
                        launchRemoved = true;
                    }
                }
                Vec3 dir = FinalizeImpulseDir(L.Dir);
                L.Dir = dir;
                GameEntity ent = L.Ent;
                Skeleton skel = L.Skel;
                var visualsCurrent = agent?.AgentVisuals;
                if (visualsCurrent != null)
                {
                    if (ent == null)
                    {
                        try { ent = visualsCurrent.GetEntity(); } catch { }
                    }
                    if (skel == null)
                    {
                        try { skel = visualsCurrent.GetSkeleton(); } catch { }
                    }
                }
                L.Ent  = ent;
                L.Skel = skel;

                if (skel != null && !IsRagdollActiveFast(skel))
                {
                    WarmRagdoll(ent, skel);
                    L.T = now + MathF.Max(0.02f, ER_Config.CorpseLaunchRetryDelay);
                    L.Warmed = true;
                    L.Pos = agent?.Position ?? L.Pos;
                    _launches[i] = L;
                    continue;
                }
                bool agentMissing = agent == null || agent.Mission == null || agent.Mission != mission;
                if (agentMissing)
                {
                    RemoveLaunch();
                    if (ent != null || skel != null)
                    {
                        float impMag = ToPhysicsImpulse(L.Mag);
                        if (impMag > 0f)
                        {
                            var contactMiss = XYJitter(ResolveHitPosition(L.Pos, ent, L.Pos));
                            contactMiss.z += contactHeight;
                            bool ok = TryApplyImpulse(ent, skel, dir * impMag, contactMiss, agentIndex);
                            nudged |= ok;
                            if (ok)
                                MarkLaunched(agentIndex);   // ensure one-shot across retries
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
                    RemoveLaunch();
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue; // only launch ragdolls still in mission
                }

                float dirSq = dir.LengthSquared;
                if (dirSq < DirectionTinySqThreshold || float.IsNaN(dirSq) || float.IsInfinity(dirSq))
                {
                    RemoveLaunch();
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
                    RemoveLaunch();
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue;
                }
                Vec3 fallbackPos = L.Pos;
                try { fallbackPos = agent.Position; }
                catch { fallbackPos = L.Pos; }
                Vec3 baseContact = ResolveHitPosition(L.Pos, ent, fallbackPos);
                Vec3 hit = XYJitter(baseContact);
                Vec3 contactPoint = hit;
                contactPoint.z += contactHeight;
                contactPoint.z = MathF.Min(contactPoint.z, agent.Position.z + zClamp);
                // direction finalized above before any impulse attempt

                if (!L.Warmed)
                {
                    L.P0 = agent.Position;
                    L.V0 = agent.Velocity;
                    var blow = new Blow(-1)
                    {
                        DamageType      = DamageTypes.Blunt,
                        // Ragdoll activation only. Do not impart engine knockback here.
                        BlowFlag        = BlowFlags.KnockDown | BlowFlags.NoSound,
                        BaseMagnitude   = 1f,
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
                    L.Skel = skel;
                    _launches[i] = L;
                    continue; // measure on next tick
                }
                if (!RagdollActive(agent, L.Warmed))
                {
                    // too early: requeue same launch shortly, keep queue counts unchanged
                    L.Warmed = false;
                    L.T   = now + ApplyDelayJitter(MathF.Max(0.04f, retryDelay)); // mehr Luft bis Ragdoll aktiv
                    L.Pos = agent.Position;
                    L.Ent = ent;
                    L.Skel = skel;
                    _launches[i] = L;
                    continue;
                }
                // Ragdoll/Visuals kommen oft 1–2 Ticks verspätet. Requeue statt Drop.
                visualsCurrent = agent.AgentVisuals;
                if (visualsCurrent == null)
                {
                    if (!AgentRemoved(agent) && L.Tries > 0)
                    {
                        RemoveLaunch();
                        L.Tries--;
                        L.Warmed = false;                 // neu messen, wenn Visuals dran sind
                        L.T      = now + MathF.Max(0.05f, retryDelay);
                        L.Pos    = agent.Position;
                        L.Ent    = ent;
                        L.Skel   = skel;
                        _launches.Add(L);
                        IncQueue(agentIndex);
                        if (ER_Config.DebugLogging && agentIndex >= 0)
                            ER_Log.Info($"corpse launch re-queued (no visuals yet) Agent#{agentIndex} tries={L.Tries}");
                        DecOnce(); // alten Eintrag sauber abbuchen
                        continue;
                    }
                    RemoveLaunch();
                    DecOnce();
                    continue;
                }
                if (visualsCurrent != null)
                {
                    try
                    {
                        var ent2 = visualsCurrent.GetEntity();
                        skel = visualsCurrent.GetSkeleton();
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
                                    MarkLaunched(agentIndex);   // ensure one-shot across retries
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
                    RemoveLaunch();
                    if (ent != null || skel != null)
                    {
                        float impMag = ToPhysicsImpulse(L.Mag);
                        if (impMag > 0f)
                        {
                            Vec3 fallbackEntity = L.Pos;
                            try { fallbackEntity = agent.Position; }
                            catch { fallbackEntity = L.Pos; }
                            var contactEntity = XYJitter(ResolveHitPosition(L.Pos, ent, fallbackEntity));
                            contactEntity.z += contactHeight;
                            bool ok = TryApplyImpulse(ent, skel, dir * impMag, contactEntity, agentIndex);
                            nudged |= ok;
                            if (ok)
                                MarkLaunched(agentIndex);   // ensure one-shot across retries
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
                    RemoveLaunch();
                    // Fallback: directly impulse ragdoll physics if RegisterBlow had no effect
                    try
                    {
                        var entLocal = visualsCurrent?.GetEntity();
                        var skelLocal = visualsCurrent?.GetSkeleton();
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
                                MarkLaunched(agentIndex);   // ensure one-shot across retries
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

                        if (!L.Boosted)
                        {
                            L.Mag *= 2.0f;
                            L.Boosted = true;
                        }
                        else
                        {
                            float target = ER_Config.CorpseImpulseMaximum;
                            if (target > 0f)
                            {
                                float cur = ToPhysicsImpulse(L.Mag);
                                if (cur + 1e-3f < target)
                                    L.Mag *= MathF.Max(1.25f, target / MathF.Max(1f, cur));
                            }
                        }
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
                            L.Skel = skel;
                            RemoveLaunch();
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
                    RemoveLaunch();
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPausedFast() => MBCommon.IsPaused;

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
            Vec3 fallbackDir = PrepDir(flat, 0.35f, 0.25f);

            // Fallback only if MakeDead didn’t consume the pending entry
            if (!ER_Amplify_RegisterBlowPatch.TryTakePending(affected.Index, out var p))
            {
                ER_Amplify_RegisterBlowPatch.ForgetScheduled(affected.Index);
                // Scripted/spell death: synthesize a push + queued launches
                var d = affected.LookDirection;
                if (d.LengthSquared < DirectionTinySqThreshold)
                    d = new Vec3(0f, 1f, 0f);
                d = PrepDir(d, 0.35f, 0.25f);
                float m = MathF.Min(12000f, ER_Config.MaxBlowBaseMagnitude > 0f
                    ? ER_Config.MaxBlowBaseMagnitude : 12000f);
                var pos0 = affected.Position;
                int postTriesSynth = ER_Config.CorpsePostDeathTries;
                int pulse2 = Math.Max(0, (int)MathF.Round(postTriesSynth * MathF.Max(0f, ER_Config.LaunchPulse2Scale)));
                EnqueueLaunch(affected, d, m,                         pos0, ER_Config.LaunchDelay1, retries: postTriesSynth);
                EnqueueLaunch(affected, d, m * ER_Config.LaunchPulse2Scale, pos0, ER_Config.LaunchDelay2, retries: pulse2);
                if (ER_Config.DebugLogging)
                    ER_Log.Info($"OnAgentRemoved: synthesized corpse launch Agent#{affected.Index}");
                return;
            }
            ER_Amplify_RegisterBlowPatch.ForgetScheduled(affected.Index);

            float mag = p.mag;
            Vec3 dir = ER_Math.IsFinite(in p.dir) && p.dir.LengthSquared >= DirectionTinySqThreshold
                ? FinalizeImpulseDir(p.dir)
                : FinalizeImpulseDir(fallbackDir);
            if (ER_Math.IsFinite(in p.pos) && p.pos.LengthSquared > PositionTinySqThreshold)
                hitPos = p.pos;

            GameEntity ent = null;
            Skeleton skel = null;
            Vec3 resolvedHit = hitPos;
            try
            {
                var visuals = affected.AgentVisuals;
                ent  = visuals?.GetEntity();
                skel = visuals?.GetSkeleton();
                if (ent != null || skel != null)
                {
                    WarmRagdoll(ent, skel);
                    resolvedHit = ResolveHitPosition(hitPos, ent, affected.Position);
                    var contactImmediate = resolvedHit;
                    contactImmediate.z += ER_Config.CorpseLaunchContactHeight;
                    // direction finalized above before feeding the impulse
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

            hitPos = ResolveHitPosition(resolvedHit, ent, affected.Position);

            // Schedule with retries (safety net in case MakeDead timing was late)
            int postTries = ER_Config.CorpsePostDeathTries;
            int pulse2Tries = Math.Max(0, (int)MathF.Round(postTries * MathF.Max(0f, ER_Config.LaunchPulse2Scale)));
            EnqueueLaunch(affected, dir, mag,                         hitPos, ER_Config.LaunchDelay1, retries: postTries);
            EnqueueLaunch(affected, dir, mag * ER_Config.LaunchPulse2Scale, hitPos, ER_Config.LaunchDelay2, retries: pulse2Tries);
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
            var dir = ER_DeathBlastBehavior.PrepDir(p.dir, 0.90f, 0.05f);
            dir = ER_DeathBlastBehavior.FinalizeImpulseDir(dir);
            int postTries = ER_Config.CorpsePostDeathTries;
            int pulse2Tries = Math.Max(0, (int)MathF.Round(postTries * MathF.Max(0f, ER_Config.LaunchPulse2Scale)));
            ER_DeathBlastBehavior.Instance.EnqueueLaunch(a, dir, p.mag,                           p.pos, ER_Config.LaunchDelay1, retries: postTries);
            ER_DeathBlastBehavior.Instance.EnqueueLaunch(a, dir, p.mag * ER_Config.LaunchPulse2Scale, p.pos, ER_Config.LaunchDelay2, retries: pulse2Tries);
            ER_DeathBlastBehavior.Instance.RecordBlast(a.Position, ER_Config.DeathBlastRadius, p.mag);
            if (ER_Config.DebugLogging) ER_Log.Info($"{tag}: scheduled corpse launch Agent#{a.Index} mag={p.mag}");
        }
    }
}
