using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    public sealed class ER_DeathBlastBehavior : MissionBehavior
    {
        private struct Blast { public Vec3 Pos; public float Radius; public float Force; public float T; }
        private struct Kick  { public Agent A; public Vec3 Dir; public float Force; public float T0; public float Dur; }
        private struct Launch { public Agent A; public Vec3 Dir; public float Mag; public Vec3 Pos; public float T; }
        private readonly List<Blast> _recent = new List<Blast>();
        private readonly List<Kick>  _kicks  = new List<Kick>();
        private readonly List<Launch> _launches = new List<Launch>();
        private const float TTL = 0.75f;

        public static ER_DeathBlastBehavior Instance;

        public override void OnBehaviorInitialize() => Instance = this;

        public override void OnRemoveBehavior()
        {
            Instance = null;
            _recent.Clear();
            _kicks.Clear();
            _launches.Clear();
            // also clear any cross-behavior pending impulses
            try { ER_Amplify_RegisterBlowPatch._pending.Clear(); } catch { }
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

        public void EnqueueLaunch(Agent a, Vec3 dir, float mag, Vec3 pos, float delaySec = 0.03f)
        {
            if (a == null) return;
            if (mag <= 0f) return;
            if (delaySec < 0f) delaySec = 0f;
            _launches.Add(new Launch { A = a, Dir = dir, Mag = mag, Pos = pos, T = Mission.CurrentTime + delaySec });
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission == null || Mission.Agents == null) return;
            float now = Mission.CurrentTime;
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
                _launches.RemoveAt(i);
                if (L.A == null || !L.A.IsActive() || L.A.Health > 0f) continue; // only launch ragdolls
                var blow = new Blow(-1)
                {
                    DamageType      = DamageTypes.Blunt,
                    BlowFlag        = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                    BaseMagnitude   = L.Mag,
                    SwingDirection  = L.Dir,
                    GlobalPosition  = L.Pos,
                    InflictedDamage = 0
                };
                AttackCollisionData acd = default;
                L.A.RegisterBlow(blow, in acd);
                if (ER_Config.DebugLogging)
                    ER_Log.Info($"death shove applied to Agent#{L.A.Index} dir={L.Dir} mag={L.Mag}");
            }
            if (_recent.Count == 0) return;

            const int MAX_WORK = 256;
            int worked = 0;
            foreach (var a in Mission.Agents)
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
                    Vec3 flat = pos - b.Pos; flat = new Vec3(flat.X, flat.Y, 0f);
                    if (flat.LengthSquared < 1e-4f) flat = new Vec3(0f, 0f, 1f);
                    Vec3 dir = (flat.NormalizedCopy() * 0.70f + new Vec3(0f, 0f, 0.72f)).NormalizedCopy();
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

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    }
}
