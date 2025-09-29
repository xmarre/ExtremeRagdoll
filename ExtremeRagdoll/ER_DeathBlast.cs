using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;           // for GameEntity, Skeleton
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using System.Reflection;           // reflection fallback for impulse API
using System.Linq;

namespace ExtremeRagdoll
{
    public sealed class ER_DeathBlastBehavior : MissionBehavior
    {
        // Cache possible impulse methods across TW versions
        private static MethodInfo _entImp2, _entImp3, _entImp1;
        private static MethodInfo _skelImp2, _skelImp1;
        private static bool _scanLogged;
        private static bool TryApplyImpulse(GameEntity ent, Skeleton skel, Vec3 impulse, Vec3 pos)
        {
            bool ok = false;
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

            IEnumerable<MethodInfo> Cand(Type t) =>
                t?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? Array.Empty<MethodInfo>();
            // --- GameEntity route ---
            if (ent != null)
            {
                if (_entImp2 == null && _entImp3 == null && _entImp1 == null)
                {
                    var ms = Cand(ent?.GetType()).Concat(Cand(typeof(GameEntity)));
                    foreach (var m in ms)
                    {
                        var ps = m.GetParameters();
                        var name = m.Name.ToLowerInvariant();
                        bool looks = name.Contains("impulse") || name.Contains("force") || name.Contains("apply")
                                     || name.Contains("addforce") || name.Contains("addimpulse") || name.Contains("atposition")
                                     || name.Contains("velocity");
                        if (!looks) continue;
                        if (ps.Length == 3 && ps[0].ParameterType == typeof(Vec3) && ps[1].ParameterType == typeof(Vec3) && ps[2].ParameterType == typeof(bool)) _entImp3 = m;
                        else if (ps.Length == 2 && ps[0].ParameterType == typeof(Vec3) && ps[1].ParameterType == typeof(Vec3)) _entImp2 = m;
                        else if (ps.Length == 1 && ps[0].ParameterType == typeof(Vec3)) _entImp1 = m;
                    }
                    if (_entImp3 == null)
                        _entImp3 = typeof(GameEntity).GetMethod("ApplyImpulseToDynamicBody", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vec3), typeof(Vec3), typeof(bool) }, null);
                }
                try
                {
                    if (_entImp3 != null)
                    {
                        try
                        {
                            _entImp3.Invoke(ent, new object[] { impulse, pos, false });
                            ER_Log.Info("IMPULSE_USE ent3(false)");
                            ok = true;
                        }
                        catch
                        {
                            _entImp3.Invoke(ent, new object[] { impulse, pos, true });
                            ER_Log.Info("IMPULSE_USE ent3(true)");
                            ok = true;
                        }
                    }
                    else if (_entImp2 != null)
                    {
                        _entImp2.Invoke(ent, new object[] { impulse, pos });
                        ER_Log.Info("IMPULSE_USE ent2");
                        ok = true;
                    }
                    else if (_entImp1 != null)
                    {
                        _entImp1.Invoke(ent, new object[] { impulse });
                        ER_Log.Info("IMPULSE_USE ent1");
                        ok = true;
                    }
                }
                catch { /* keep ok as-is */ }
            }
            // --- Skeleton route (ragdoll bones) ---
            if (!ok && skel != null)
            {
                if (_skelImp2 == null && _skelImp1 == null)
                {
                    var ts = skel.GetType();
                    foreach (var m in ts.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var ps = m.GetParameters();
                        bool looksImpulse =
                            (m.Name.IndexOf("Impulse", StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (m.Name.IndexOf("Force",   StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (m.Name.IndexOf("Velocity",StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (m.Name.IndexOf("Ragdoll", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!looksImpulse) continue;
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(Vec3) && ps[1].ParameterType == typeof(Vec3))
                            _skelImp2 = m; // (force, atPos)
                        else if (ps.Length == 1 && ps[0].ParameterType == typeof(Vec3))
                            _skelImp1 = m; // (force)
                    }
                }
                try
                {
                    if (_skelImp2 != null)
                    {
                        _skelImp2.Invoke(skel, new object[] { impulse, pos });
                        ER_Log.Info("IMPULSE_USE skel2");
                        ok = true;
                    }
                    else if (_skelImp1 != null)
                    {
                        _skelImp1.Invoke(skel, new object[] { impulse });
                        ER_Log.Info("IMPULSE_USE skel1");
                        ok = true;
                    }
                }
                catch { }
            }
            return ok;
        }

        private static float ToPhysicsImpulse(float mag)
        {
            // Convert RegisterBlow magnitude to a reasonable physics impulse scale.
            // Conservative default. Tune if needed.
            float imp = mag * 1e-4f;
            if (imp < 50000f) imp = 50000f;
            if (imp > 400_000f) imp = 400_000f;
            if (imp < 0f || float.IsNaN(imp) || float.IsInfinity(imp)) return 0f;
            return imp;
        }

        private struct Blast { public Vec3 Pos; public float Radius; public float Force; public float T; }
        private struct Kick  { public Agent A; public Vec3 Dir; public float Force; public float T0; public float Dur; }
        private struct Launch { public Agent A; public GameEntity Ent; public Vec3 Dir; public float Mag; public Vec3 Pos; public float T; public int Tries; public int AgentId; public bool Warmed; public Vec3 P0; public Vec3 V0; }
        private readonly List<Blast> _recent = new List<Blast>();
        private readonly List<Kick>  _kicks  = new List<Kick>();
        private readonly List<Launch> _launches = new List<Launch>();
        private readonly HashSet<int> _launchFailLogged = new HashSet<int>();
        private readonly Dictionary<int, int> _queuedPerAgent = new Dictionary<int, int>();
        private const float TTL = 0.75f;

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
            _launches.Clear();
            _launchFailLogged.Clear();
            _queuedPerAgent.Clear();
            // also clear any cross-behavior pending impulses
            ER_Amplify_RegisterBlowPatch.ClearPending();
        }

        public void RecordBlast(Vec3 center, float radius, float force)
        {
            if (radius <= 0f || force <= 0f) return;
            _recent.Add(new Blast { Pos = center, Radius = radius, Force = force, T = Mission.CurrentTime });
        }

        public void EnqueueKick(Agent a, Vec3 dir, float force, float duration)
        {
            if (a == null) return;
            _kicks.Add(new Kick { A = a, Dir = dir, Force = force, T0 = Mission.CurrentTime, Dur = duration });
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
            if (retries > 12) retries = 12;
            int queueCap = ER_Config.CorpseLaunchQueueCap;
            int agentIndex = a.Index;
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
            Vec3 safeDir = dir.LengthSquared > 1e-6f ? dir.NormalizedCopy() : new Vec3(0f, 1f, 0f);
            safeDir = (safeDir + new Vec3(0f, 0f, 0.15f)).NormalizedCopy();
            float safeDirSq = safeDir.LengthSquared;
            if (safeDirSq < 1e-8f || float.IsNaN(safeDirSq) || float.IsInfinity(safeDirSq))
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

        public override void OnMissionTick(float dt)
        {
            var mission = Mission;
            if (mission == null || mission.Agents == null) return;
            float now = mission.CurrentTime;
            const int MAX_LAUNCHES_PER_TICK = 128;
            int launchesWorked = 0;
            float tookScale = ER_Config.CorpseLaunchVelocityScaleThreshold;
            float tookOffset = ER_Config.CorpseLaunchVelocityOffset;
            float tookVertical = 0.05f;       // temporary tuning for verification
            float tookDisplacement = 0.03f;   // temporary tuning for verification
            float contactHeight = ER_Config.CorpseLaunchContactHeight;
            float retryDelay = ER_Config.CorpseLaunchRetryDelay;
            int queueCap = ER_Config.CorpseLaunchQueueCap;
            float zNudge = ER_Config.CorpseLaunchZNudge;
            float zClamp = ER_Config.CorpseLaunchZClampAbove;
            float tickMaxSetting = ER_Config.MaxCorpseLaunchMagnitude;
            bool clampMag = tickMaxSetting > 0f; // <=0 bedeutet: nicht kappen
            for (int i = _recent.Count - 1; i >= 0; i--)
                if (now - _recent[i].T > TTL) _recent.RemoveAt(i);
            for (int i = _kicks.Count - 1; i >= 0; i--)
            {
                var k = _kicks[i];
                if (k.A == null || k.A.Health > 0f) { _kicks.RemoveAt(i); continue; }
                float age = now - k.T0;
                if (age > k.Dur) { _kicks.RemoveAt(i); continue; }
                float gain = 1f - (age / k.Dur);
                float mag = k.Force * gain * 0.30f;
                if (mag > 0f)
                {
                    var kb = new Blow(-1)
                    {
                        DamageType      = DamageTypes.Blunt,
                        BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                        BaseMagnitude   = mag,
                        SwingDirection  = k.Dir,
                        GlobalPosition  = k.A.Position,
                        InflictedDamage = 0
                    };
                    AttackCollisionData acd = default;
                    k.A.RegisterBlow(kb, in acd);
                }
            }

            // delayed corpse launches (run AFTER ragdoll is active)
            for (int i = _launches.Count - 1; i >= 0; i--)
            {
                var L = _launches[i];
                if (now < L.T) continue;
                if (launchesWorked++ >= MAX_LAUNCHES_PER_TICK) break;
                var agent = L.A;
                int agentIndex = L.AgentId >= 0 ? L.AgentId : agent?.Index ?? -1;
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
                            bool ok = TryApplyImpulse(ent, skel, L.Dir * impMag, contactMiss);
                            nudged |= ok;
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

                Vec3 dir = L.Dir.LengthSquared > 1e-8f ? L.Dir : new Vec3(0f, 1f, 0f);
                dir = (dir + new Vec3(0f, 0f, 0.1f)).NormalizedCopy();
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
                                bool ok2 = TryApplyImpulse(ent2, skel, L.Dir * impMag2, contactPoint);
                                nudged |= ok2;
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
                            bool ok = TryApplyImpulse(ent, skel, L.Dir * impMag, contactEntity);
                            nudged |= ok;
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
                            bool ok = TryApplyImpulse(entLocal, skelLocal, impulse, contactPoint);
                            nudged |= ok;
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
                }
                else
                {
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    if (ER_Config.DebugLogging)
                    {
                        ER_Log.Info($"corpse launch took Agent#{agentIndex} moved={moved:F4} v↑Δ={velAfter.z - L.V0.z:F4}");
                        _queuedPerAgent.TryGetValue(agentIndex, out var queued);
                        ER_Log.Info($"death shove applied to Agent#{agentIndex} took={took} mag={mag} tries={L.Tries} queued={queued}");
                    }
                }
            }
            if (_recent.Count == 0) return;

            const int MAX_WORK = 256;
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
                    if (flat.LengthSquared < 1e-4f) flat = new Vec3(0f, 0f, 1f);
                    Vec3 dir = (flat.NormalizedCopy() * 0.70f + new Vec3(0f, 0f, 0.72f)).NormalizedCopy();
                    float maxForce = ER_Config.MaxAoEForce;
                    if (maxForce > 0f && force > maxForce)
                    {
                        force = maxForce;
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
                            BaseMagnitude   = 1f,
                            SwingDirection  = dir,
                            GlobalPosition  = b.Pos,
                            InflictedDamage = 0
                        };
                        AttackCollisionData kbAcd = default;
                        a.RegisterBlow(kb, in kbAcd);
                    }
                    break; // pro Agent nur ein Blast pro Tick
                }
                if (affected && ++worked >= MAX_WORK) break;
            }
        }

        // Fire post-death fallback if MakeDead scheduling failed to consume the pending entry
        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null) return;
            _launchFailLogged.Remove(affected.Index);
            ER_Amplify_RegisterBlowPatch.ForgetScheduled(affected.Index);
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

            // Kontaktpunkt: konservativ das Agent-Center verwenden (kein KillingBlow.GlobalPosition vorhanden)
            Vec3 hitPos = affected.Position;
            Vec3 flat   = new Vec3(affected.LookDirection.x, affected.LookDirection.y, 0f);
            if (flat.LengthSquared < 1e-6f) flat = new Vec3(0f, 1f, 0f);
            Vec3 fallbackDir = (flat.NormalizedCopy() * 0.35f + new Vec3(0f, 0f, 1.05f)).NormalizedCopy();

            // Fallback only if MakeDead didn’t consume the pending entry
            if (!ER_Amplify_RegisterBlowPatch.TryTakePending(affected.Index, out var p))
                return;

            float mag = p.mag;
            Vec3 dir = p.dir.LengthSquared > 1e-6f ? p.dir : fallbackDir;
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
                        TryApplyImpulse(ent, skel, dir * imp, contactImmediate);
                }
            }
            catch { }

            // Schedule with retries (safety net in case MakeDead timing was late)
            EnqueueLaunch(affected, dir, mag,                         hitPos, ER_Config.LaunchDelay1, retries: 10);
            EnqueueLaunch(affected, dir, mag * ER_Config.LaunchPulse2Scale, hitPos, ER_Config.LaunchDelay2, retries: 6);
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
            var dir = p.dir.LengthSquared > 1e-6f ? p.dir : new Vec3(0f, 1f, 0f);
            ER_DeathBlastBehavior.Instance.EnqueueLaunch(a, dir, p.mag,                           p.pos, ER_Config.LaunchDelay1, retries: 10);
            ER_DeathBlastBehavior.Instance.EnqueueLaunch(a, dir, p.mag * ER_Config.LaunchPulse2Scale, p.pos, ER_Config.LaunchDelay2, retries: 6);
            ER_DeathBlastBehavior.Instance.EnqueueKick  (a, dir, p.mag, 1.2f);
            ER_DeathBlastBehavior.Instance.RecordBlast(a.Position, ER_Config.DeathBlastRadius, p.mag);
            if (ER_Config.DebugLogging) ER_Log.Info($"{tag}: scheduled corpse launch Agent#{a.Index} mag={p.mag}");
        }
    }
}
