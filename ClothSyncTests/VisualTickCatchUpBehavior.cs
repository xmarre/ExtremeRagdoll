using System;
using System.Collections.Generic;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.ClothSyncTests
{
    internal sealed class VisualTickCatchUpBehavior : MissionBehavior
    {
        private const float PendingLifetime = 12f;
        private const float LowSpeedRetainWindow = 1f;

        private sealed class TrackedCorpse
        {
            public Agent Agent;
            public float CapturedAt;
            public bool HasLastVisualPosition;
            public Vec3 LastVisualPosition;
            public float LastHighSpeedAt = -1f;
        }

        private readonly List<TrackedCorpse> _tracked = new List<TrackedCorpse>(16);

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnBehaviorInitialize()
        {
            _tracked.Clear();
        }

        public override void OnRemoveBehavior()
        {
            _tracked.Clear();
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null || state != AgentState.Killed || Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null || !settings.HighSpeedVisualTickCatchUp)
                return;

            for (int i = 0; i < _tracked.Count; i++)
            {
                if (ReferenceEquals(_tracked[i].Agent, affected))
                    return;
            }

            var tracked = new TrackedCorpse
            {
                Agent = affected,
                CapturedAt = Mission.CurrentTime
            };
            tracked.HasLastVisualPosition = TryCaptureVisualPosition(affected, out tracked.LastVisualPosition);
            _tracked.Add(tracked);
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null || !settings.HighSpeedVisualTickCatchUp)
            {
                _tracked.Clear();
                return;
            }

            float now = Mission.CurrentTime;
            float threshold = Math.Max(0f, settings.ActivationSpeedThreshold);
            int substeps = Math.Max(1, Math.Min(8, (int)Math.Round(settings.VisualTickCatchUpSubsteps)));

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedCorpse tracked = _tracked[i];
                Agent agent = tracked.Agent;
                if (agent == null || now - tracked.CapturedAt > PendingLifetime)
                {
                    _tracked.RemoveAt(i);
                    continue;
                }

                if (!TryGetCorpseVelocity(agent, tracked, dt, out Vec3 velocity))
                    continue;

                float speed = Length(velocity);
                bool highSpeed = speed >= threshold;
                if (!highSpeed)
                {
                    if (tracked.LastHighSpeedAt >= 0f && now - tracked.LastHighSpeedAt > LowSpeedRetainWindow)
                        _tracked.RemoveAt(i);
                    continue;
                }

                tracked.LastHighSpeedAt = now;

                MBAgentVisuals visuals;
                try
                {
                    visuals = agent.AgentVisuals;
                }
                catch
                {
                    visuals = null;
                }

                if (visuals == null || !visuals.IsValid())
                    continue;

                float stepDt = dt / substeps;
                for (int step = 0; step < substeps; step++)
                {
                    try
                    {
                        visuals.Tick(null, stepDt, true, speed);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        private static bool TryGetCorpseVelocity(Agent agent, TrackedCorpse tracked, float dt, out Vec3 velocity)
        {
            velocity = Vec3.Zero;
            try
            {
                velocity = agent.GetRealGlobalVelocity();
                if (Length(velocity) > 0.001f)
                {
                    UpdateLastVisualPosition(agent, tracked);
                    return true;
                }
            }
            catch
            {
            }

            if (!TryCaptureVisualPosition(agent, out Vec3 currentPosition))
                return false;

            if (tracked.HasLastVisualPosition && dt > 0.000001f)
            {
                float invDt = 1f / dt;
                velocity = new Vec3(
                    (currentPosition.x - tracked.LastVisualPosition.x) * invDt,
                    (currentPosition.y - tracked.LastVisualPosition.y) * invDt,
                    (currentPosition.z - tracked.LastVisualPosition.z) * invDt);
            }

            tracked.LastVisualPosition = currentPosition;
            tracked.HasLastVisualPosition = true;
            return true;
        }

        private static void UpdateLastVisualPosition(Agent agent, TrackedCorpse tracked)
        {
            if (TryCaptureVisualPosition(agent, out Vec3 position))
            {
                tracked.LastVisualPosition = position;
                tracked.HasLastVisualPosition = true;
            }
        }

        private static bool TryCaptureVisualPosition(Agent agent, out Vec3 position)
        {
            position = Vec3.Zero;
            try
            {
                if (agent?.AgentVisuals == null)
                    return false;
                position = agent.AgentVisuals.GetGlobalFrame().origin;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static float Length(Vec3 value)
        {
            return (float)Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
        }

        private static Settings TryGetSettings()
        {
            try
            {
                return Settings.Instance;
            }
            catch
            {
                return null;
            }
        }
    }
}
