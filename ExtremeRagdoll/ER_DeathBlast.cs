using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;           // for GameEntity
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using System.Reflection;           // reflection fallback for impulse API

namespace ExtremeRagdoll
{
    public sealed class ER_DeathBlastBehavior : MissionBehavior
    {
        // Cache possible GameEntity impulse methods across TW versions
        private static MethodInfo _impulse2, _impulse3;
        private static bool TryApplyImpulse(GameEntity ent, Vec3 impulse, Vec3 pos)
        {
            if (ent == null) return false;
            var t = typeof(GameEntity);
            if (_impulse2 == null && _impulse3 == null)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!(m.Name.Contains("Impulse") || m.Name.Contains("Force"))) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(Vec3) && ps[1].ParameterType == typeof(Vec3) && ps[2].ParameterType == typeof(bool))
                        _impulse3 = m;
                    else if (ps.Length == 2 && ps[0].ParameterType == typeof(Vec3) && ps[1].ParameterType == typeof(Vec3))
                        _impulse2 = m;
                }
            }
            try
            {
                if (_impulse3 != null) { _impulse3.Invoke(ent, new object[] { impulse, pos, true }); return true; }
                if (_impulse2 != null) { _impulse2.Invoke(ent, new object[] { impulse, pos }); return true; }
            }
            catch { }
            return false;
        }

        private static float ToPhysicsImpulse(float mag)
        {
            // Convert RegisterBlow magnitude to a reasonable physics impulse scale.
            // Conservative default. Tune if needed.
            float imp = mag * 1e-5f;
            if (imp < 50f) imp = 50f;
            if (imp > 100_000f) imp = 100_000f;
            if (imp < 0f || float.IsNaN(imp) || float.IsInfinity(imp)) return 0f;
            return imp;
        }

        private struct Blast { public Vec3 Pos; public float Radius; public float Force; public float T; }
        private struct Kick  { public Agent A; public Vec3 Dir; public float Force; public float T0; public float Dur; }
        private struct Launch { public Agent A; public Vec3 Dir; public float Mag; public Vec3 Pos; public float T; public int Tries; public int AgentId; }
        private readonly List<Blast> _recent = new List<Blast>();
        private readonly List<Kick>  _kicks  = new List<Kick>();
        private readonly List<Launch> _launches = new List<Launch>();
        private readonly HashSet<int> _launchFailLogged = new HashSet<int>();
        private readonly Dictionary<int, int> _queuedPerAgent = new Dictionary<int, int>();
        private const float TTL = 0.75f;

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
            if (a.Mission != mission) return;
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
            nudgedPos.z = MathF.Min(nudgedPos.z + zNudge, a.Position.z + zClamp);

            _launches.Add(new Launch { A = a, Dir = safeDir, Mag = mag, Pos = nudgedPos, T = mission.CurrentTime + delaySec, Tries = retries, AgentId = agentIndex });
            IncQueue(agentIndex);
        }

        public override void OnMissionTick(float dt)
        {
            var mission = Mission;
            if (mission == null || mission.Agents == null) return;
            float now = mission.CurrentTime;
            if (ER_Config.MaxCorpseLaunchMagnitude <= 0f)
            {
                for (int i = _launches.Count - 1; i >= 0; i--)
                {
                    DecQueue(_launches[i].AgentId);
                    _launches.RemoveAt(i);
                }
                return;
            }
            float tookScale = ER_Config.CorpseLaunchVelocityScaleThreshold;
            float tookOffset = ER_Config.CorpseLaunchVelocityOffset;
            float tookVertical = ER_Config.CorpseLaunchVerticalDelta;
            float tookDisplacement = ER_Config.CorpseLaunchDisplacement;
            float contactHeight = ER_Config.CorpseLaunchContactHeight;
            float retryDelay = ER_Config.CorpseLaunchRetryDelay;
            int queueCap = ER_Config.CorpseLaunchQueueCap;
            float zNudge = ER_Config.CorpseLaunchZNudge;
            float zClamp = ER_Config.CorpseLaunchZClampAbove;
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
                var agent = L.A;
                int agentIndex = L.AgentId >= 0 ? L.AgentId : agent?.Index ?? -1;
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
                if (agent == null || agent.IsRemoved)
                {
                    DecOnce();
                    continue;
                }
                if (agent.Health > 0f || agent.Mission != mission)
                {
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue; // only launch ragdolls still in mission
                }

                Vec3 posBefore = agent.Position;
                Vec3 velBefore = agent.Velocity;
                float vBefore2 = velBefore.LengthSquared;
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
                float tickMax = ER_Config.MaxCorpseLaunchMagnitude;
                if (tickMax > 0f && mag > tickMax)
                {
                    mag = tickMax;
                }
                if (mag <= 0f || float.IsNaN(mag) || float.IsInfinity(mag))
                {
                    _launchFailLogged.Remove(agentIndex);
                    DecOnce();
                    continue;
                }
                Vec3 hit = XYJitter(L.Pos);
                Vec3 contact = hit;
                contact.z += contactHeight;
                contact.z = MathF.Min(contact.z, agent.Position.z + zClamp);

                var blow = new Blow(-1)
                {
                    DamageType      = DamageTypes.Blunt,
                    BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                    BaseMagnitude   = mag,
                    SwingDirection  = dir,
                    GlobalPosition  = contact,
                    InflictedDamage = 0
                };
                AttackCollisionData acd = default;
                agent.RegisterBlow(blow, in acd);
                if (!agent.IsRagdollActive)
                {
                    // too early: requeue same launch shortly, keep queue counts unchanged
                    L.T   = now + ApplyDelayJitter(MathF.Max(0.02f, retryDelay));
                    L.Pos = XYJitter(agent.Position);
                    _launches.Add(L);
                    continue;
                }
                if (agent.AgentVisuals == null || agent.IsRemoved)
                {
                    DecOnce();
                    continue;
                }
                Vec3 velAfter = agent.Velocity;
                float vAfter2 = velAfter.LengthSquared;
                float moved = posBefore.Distance(agent.Position);
                bool took = (vAfter2 > vBefore2 * tookScale + tookOffset)
                    || (velAfter.z > velBefore.z + tookVertical)
                    || (moved > tookDisplacement);

                if (!took)
                {
                    // Fallback: directly impulse ragdoll physics if RegisterBlow had no effect
                    try
                    {
                        var ent = (agent?.AgentVisuals != null) ? agent.AgentVisuals.GetEntity() : null;
                        float impMag = ToPhysicsImpulse(mag);
                        if (ent != null && impMag > 0f)
                        {
                            var impulse = dir * impMag;
                            if (TryApplyImpulse(ent, impulse, contact) && ER_Config.DebugLogging)
                                ER_Log.Info($"corpse physics impulse attempted Agent#{agentIndex} impMag={impMag:F1}");
                        }
                    }
                    catch { /* never throw here */ }

                    if (ER_Config.DebugLogging && _launchFailLogged.Add(agentIndex))
                    {
                        float deltaZ = velAfter.z - velBefore.z;
                        ER_Log.Info($"corpse launch miss Agent#{agentIndex} vBefore2={vBefore2:F4} vAfter2={vAfter2:F4} deltaZ={deltaZ:F4}");
                    }

                    if (L.Tries > 0)
                    {
                        float nextTime = now + ApplyDelayJitter(retryDelay);
                        Vec3 retryPos = XYJitter(agent.Position);
                        retryPos.z = MathF.Min(retryPos.z + zNudge, agent.Position.z + zClamp);

                        float maxMagSetting = ER_Config.MaxCorpseLaunchMagnitude;
                        if (maxMagSetting > 0f && L.Mag > maxMagSetting)
                        {
                            L.Mag = maxMagSetting;
                        }

                        bool canQueue = true;
                        int existingQueued = 0;
                        if (agentIndex >= 0)
                        {
                            _queuedPerAgent.TryGetValue(agentIndex, out existingQueued);
                        }
                        if (!agent.IsActive() || agent.IsRemoved)
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
                            _launches.Add(L);
                            IncQueue(agentIndex);
                            if (ER_Config.DebugLogging && agentIndex >= 0)
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
            if (affected.Mission != Mission) return;
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
