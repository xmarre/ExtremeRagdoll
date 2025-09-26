using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    public sealed class ER_DeathBlastBehavior : MissionBehavior
    {
        private struct Blast { public Vec3 Pos; public float Radius; public float Force; public float T; }
        private readonly List<Blast> _recent = new List<Blast>();
        private const float TTL = 0.40f;

        public static ER_DeathBlastBehavior Instance;

        public override void OnBehaviorInitialize() => Instance = this;

        public void RecordBlast(Vec3 center, float radius, float force)
        {
            _recent.Add(new Blast { Pos = center, Radius = radius, Force = force, T = Mission.CurrentTime });
        }

        public override void OnMissionTick(float dt)
        {
            float now = Mission.CurrentTime;
            _recent.RemoveAll(b => now - b.T > TTL);
        }

        public override void OnAgentRemoved(Agent victim, Agent killer, AgentState state, KillingBlow kb)
        {
            if (state != AgentState.Killed || victim == null) return;
            if (_recent.Count == 0) return;

            Vec3 vPos = victim.Position;
            float now = Mission.CurrentTime;

            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                Blast b = _recent[i];
                if (now - b.T > TTL) continue;

                float d = vPos.Distance(b.Pos);
                if (d > b.Radius) continue;

                float falloff = 1f / (1f + d * d);
                float baseForce = b.Force * falloff;

                Vec3 dir = (vPos - b.Pos).NormalizedCopy();
                if (dir.X == 0f && dir.Y == 0f && dir.Z == 0f) dir = new Vec3(0f, 0f, 1f);

                var blow = new Blow(-1)
                {
                    DamageType = DamageTypes.Blunt,
                    BlowFlag = BlowFlags.KnockBack | BlowFlags.KnockDown | BlowFlags.NoSound,
                    BaseMagnitude = baseForce,
                    SwingDirection = dir,
                    GlobalPosition = vPos,
                    InflictedDamage = 0
                };

                AttackCollisionData dummy = default;
                victim.RegisterBlow(blow, in dummy);
                break;
            }
        }

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;
    }
}
