using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.ClothSyncTests
{
    internal sealed class ClothSafeVelocityGovernorBehavior : MissionBehavior
    {
        private const float PendingLifetime = 12f;
        private const float FallbackUnlimitedAngularVelocity = 1000f;

        private sealed class TrackedCorpse
        {
            public Agent Agent;
            public float CapturedAt;
            public bool HasClothMesh;
            public bool DetectionCompleted;
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
            if (settings == null || !settings.ClothSafeVelocityGovernor)
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

            DetectClothMesh(tracked);
            if (tracked.HasClothMesh)
                _tracked.Add(tracked);
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null || !settings.ClothSafeVelocityGovernor)
            {
                _tracked.Clear();
                return;
            }

            float now = Mission.CurrentTime;
            float configuredCap = Math.Max(2f, settings.ClothSafeLinearVelocityLimit);
            float mainLinearLimit = ReadMainVelocitySetting("MaxLinearVelocity");
            float mainAngularLimit = ReadMainVelocitySetting("MaxAngularVelocity");

            float effectiveLinearLimit = mainLinearLimit > 0f
                ? Math.Min(configuredCap, mainLinearLimit)
                : configuredCap;
            float effectiveAngularLimit = mainAngularLimit > 0f
                ? mainAngularLimit
                : FallbackUnlimitedAngularVelocity;

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedCorpse tracked = _tracked[i];
                Agent agent = tracked.Agent;

                if (agent == null || now - tracked.CapturedAt > PendingLifetime)
                {
                    _tracked.RemoveAt(i);
                    continue;
                }

                if (!tracked.DetectionCompleted)
                    DetectClothMesh(tracked);

                if (!tracked.HasClothMesh)
                {
                    _tracked.RemoveAt(i);
                    continue;
                }

                try
                {
                    agent.SetVelocityLimitsOnRagdoll(effectiveLinearLimit, effectiveAngularLimit);
                }
                catch
                {
                    // Diagnostic only; never destabilize mission logic if the native wrapper rejects a late corpse.
                }
            }
        }

        private static void DetectClothMesh(TrackedCorpse tracked)
        {
            if (tracked?.Agent == null)
                return;

            try
            {
                Skeleton skeleton = tracked.Agent.AgentVisuals?.GetSkeleton();
                if (skeleton == null || !skeleton.IsValid)
                    return;

                foreach (Mesh mesh in skeleton.GetAllMeshes())
                {
                    if (mesh != null && mesh.IsValid && mesh.HasCloth())
                    {
                        tracked.HasClothMesh = true;
                        tracked.DetectionCompleted = true;
                        return;
                    }
                }

                tracked.DetectionCompleted = true;
            }
            catch
            {
                // Keep detection pending briefly; the dead agent visuals may still be transitioning to ragdoll.
            }
        }

        private static float ReadMainVelocitySetting(string propertyName)
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Type settingsType = assemblies[i].GetType("ExtremeRagdoll.SafeRuntime.SafeSettings", false);
                    if (settingsType == null)
                        continue;

                    PropertyInfo instanceProperty = settingsType.GetProperty(
                        "Instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    object instance = instanceProperty?.GetValue(null, null);
                    if (instance == null)
                        return 0f;

                    PropertyInfo valueProperty = settingsType.GetProperty(
                        propertyName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object value = valueProperty?.GetValue(instance, null);
                    if (value == null)
                        return 0f;

                    return Convert.ToSingle(value);
                }
            }
            catch
            {
            }

            return 0f;
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
