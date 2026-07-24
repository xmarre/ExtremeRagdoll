using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.ClothSyncTests
{
    internal sealed class AgentClothOwnerTestBehavior : MissionBehavior
    {
        private const float PendingLifetime = 12f;
        private const float LowSpeedRebindDelay = 0.20f;

        private sealed class TrackedCorpse
        {
            public Agent Agent;
            public float CapturedAt;
            public bool HasLastVisualPosition;
            public Vec3 LastVisualPosition;
            public float LastHighSpeedAt = -1f;
            public bool CaptureEverSucceeded;
            public object SavedCapeClothSimulator;
            public bool Detached;
        }

        private readonly List<TrackedCorpse> _tracked = new List<TrackedCorpse>(16);
        private FieldInfo _capeClothField;
        private MethodInfo _setCapeClothSimulator;
        private MethodInfo _checkEquipmentClothState;
        private Type _cachedAgentType;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnBehaviorInitialize()
        {
            _tracked.Clear();
            ClearReflectionCache();
        }

        public override void OnRemoveBehavior()
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
                TryRestoreAgentCloth(_tracked[i]);

            _tracked.Clear();
            ClearReflectionCache();
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null || state != AgentState.Killed || Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null || !settings.HighSpeedDirectAgentClothDetach)
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
            TryCaptureAgentCapeCloth(tracked);
            _tracked.Add(tracked);
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null || !settings.HighSpeedDirectAgentClothDetach)
            {
                for (int i = _tracked.Count - 1; i >= 0; i--)
                    TryRestoreAgentCloth(_tracked[i]);
                _tracked.Clear();
                return;
            }

            float now = Mission.CurrentTime;
            float threshold = Math.Max(0f, settings.ActivationSpeedThreshold);

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedCorpse tracked = _tracked[i];
                Agent agent = tracked.Agent;
                if (agent == null || now - tracked.CapturedAt > PendingLifetime)
                {
                    TryRestoreAgentCloth(tracked);
                    _tracked.RemoveAt(i);
                    continue;
                }

                if (!tracked.CaptureEverSucceeded || tracked.SavedCapeClothSimulator == null)
                    TryCaptureAgentCapeCloth(tracked);

                bool hasVelocity = TryGetCorpseVelocity(agent, tracked, dt, out Vec3 velocity);
                float speed = hasVelocity ? Length(velocity) : 0f;
                bool highSpeed = hasVelocity && speed >= threshold;

                if (highSpeed)
                {
                    tracked.LastHighSpeedAt = now;
                    if (!tracked.Detached)
                        TryDetachAgentCloth(tracked);
                    continue;
                }

                if (tracked.Detached && tracked.LastHighSpeedAt >= 0f && now - tracked.LastHighSpeedAt >= LowSpeedRebindDelay)
                    TryRestoreAgentCloth(tracked);

                if (!tracked.Detached && tracked.LastHighSpeedAt >= 0f && now - tracked.LastHighSpeedAt > 1f)
                    _tracked.RemoveAt(i);
            }
        }

        private bool TryCaptureAgentCapeCloth(TrackedCorpse tracked)
        {
            if (tracked?.Agent == null)
                return false;

            try
            {
                ResolveAgentClothMembers(tracked.Agent.GetType());
                if (_capeClothField == null)
                    return false;

                object value = _capeClothField.GetValue(tracked.Agent);
                tracked.CaptureEverSucceeded = true;
                if (value != null)
                    tracked.SavedCapeClothSimulator = value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryDetachAgentCloth(TrackedCorpse tracked)
        {
            if (tracked?.Agent == null)
                return false;

            try
            {
                ResolveAgentClothMembers(tracked.Agent.GetType());

                if (tracked.SavedCapeClothSimulator == null && _capeClothField != null)
                    tracked.SavedCapeClothSimulator = _capeClothField.GetValue(tracked.Agent);

                bool invokedSetter = false;
                if (_setCapeClothSimulator != null)
                {
                    _setCapeClothSimulator.Invoke(tracked.Agent, new object[] { null });
                    invokedSetter = true;
                }

                if (_capeClothField != null)
                {
                    object current = _capeClothField.GetValue(tracked.Agent);
                    if (current != null)
                        _capeClothField.SetValue(tracked.Agent, null);
                }

                tracked.Detached = invokedSetter || _capeClothField != null;
                return tracked.Detached;
            }
            catch
            {
                return false;
            }
        }

        private bool TryRestoreAgentCloth(TrackedCorpse tracked)
        {
            if (tracked?.Agent == null || !tracked.Detached)
                return false;

            try
            {
                ResolveAgentClothMembers(tracked.Agent.GetType());

                if (tracked.SavedCapeClothSimulator != null)
                {
                    if (_setCapeClothSimulator != null)
                        _setCapeClothSimulator.Invoke(tracked.Agent, new[] { tracked.SavedCapeClothSimulator });
                    else if (_capeClothField != null)
                        _capeClothField.SetValue(tracked.Agent, tracked.SavedCapeClothSimulator);
                }

                if (_checkEquipmentClothState != null)
                    _checkEquipmentClothState.Invoke(tracked.Agent, null);

                tracked.Detached = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ResolveAgentClothMembers(Type agentType)
        {
            if (agentType == null)
                return;
            if (_cachedAgentType == agentType && (_capeClothField != null || _setCapeClothSimulator != null))
                return;

            _cachedAgentType = agentType;
            _capeClothField = null;
            _setCapeClothSimulator = null;
            _checkEquipmentClothState = null;

            Type type = agentType;
            while (type != null)
            {
                if (_capeClothField == null)
                {
                    FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < fields.Length; i++)
                    {
                        FieldInfo field = fields[i];
                        if (string.Equals(field.Name, "_capeClothSimulator", StringComparison.Ordinal) ||
                            field.Name.IndexOf("capeClothSimulator", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _capeClothField = field;
                            break;
                        }
                    }
                }

                MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (_setCapeClothSimulator == null && method.Name == "SetCapeClothSimulator" && method.GetParameters().Length == 1)
                        _setCapeClothSimulator = method;
                    else if (_checkEquipmentClothState == null && method.Name == "CheckEquipmentForCapeClothSimulationStateChange" && method.GetParameters().Length == 0)
                        _checkEquipmentClothState = method;
                }

                type = type.BaseType;
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

        private void ClearReflectionCache()
        {
            _capeClothField = null;
            _setCapeClothSimulator = null;
            _checkEquipmentClothState = null;
            _cachedAgentType = null;
        }
    }
}
