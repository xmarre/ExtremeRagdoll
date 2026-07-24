using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.ClothSyncTests
{
    internal sealed class ClothSyncTestBehavior : MissionBehavior
    {
        private const float StabilizationWindow = 0.35f;
        private const float PendingLifetime = 12.0f;
        private const float LowSpeedRetainWindow = 1.0f;

        private sealed class TrackedCorpse
        {
            public Agent Agent;
            public float CapturedAt;
            public float RagdollSeenAt = -1f;
            public bool ClothResetAttempted;
            public bool HasLastVisualPosition;
            public Vec3 LastVisualPosition;
            public float LastHighSpeedAt = -1f;
            public bool ForcedVelocityWasWritten;
            public bool DistanceClampWasWritten;
        }

        private readonly List<TrackedCorpse> _tracked = new List<TrackedCorpse>(16);
        private object _rendererController;
        private MethodInfo _setTimerBasedUpdates;
        private bool _timerStateApplied;
        private bool _lastTimerRequested;

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnBehaviorInitialize()
        {
            _tracked.Clear();
            _rendererController = null;
            _setTimerBasedUpdates = null;
            _timerStateApplied = false;
            _lastTimerRequested = false;
        }

        public override void OnRemoveBehavior()
        {
            if (_timerStateApplied && _lastTimerRequested)
                TrySetTimerBasedForcedSkeletonUpdates(false);

            for (int i = _tracked.Count - 1; i >= 0; i--)
                RestoreHighSpeedOverrides(_tracked[i]);

            _tracked.Clear();
            _rendererController = null;
            _setTimerBasedUpdates = null;
            _timerStateApplied = false;
            _lastTimerRequested = false;
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null || state != AgentState.Killed || Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null || !NeedsPerCorpseTracking(settings))
                return;

            for (int i = 0; i < _tracked.Count; i++)
            {
                if (ReferenceEquals(_tracked[i].Agent, affected))
                    return;
            }

            TrackedCorpse tracked = new TrackedCorpse
            {
                Agent = affected,
                CapturedAt = Mission.CurrentTime
            };

            TryCaptureVisualPosition(affected, out tracked.LastVisualPosition);
            tracked.HasLastVisualPosition = TryCaptureVisualPosition(affected, out tracked.LastVisualPosition);
            _tracked.Add(tracked);
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null)
                return;

            SyncTimerBasedForcedSkeletonUpdates(settings.TimerBasedForcedSkeletonUpdates);

            if (!NeedsPerCorpseTracking(settings))
            {
                for (int i = _tracked.Count - 1; i >= 0; i--)
                    RestoreHighSpeedOverrides(_tracked[i]);
                _tracked.Clear();
                return;
            }

            float now = Mission.CurrentTime;
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedCorpse tracked = _tracked[i];
                Agent agent = tracked.Agent;
                if (agent == null || now - tracked.CapturedAt > PendingLifetime)
                {
                    RestoreHighSpeedOverrides(tracked);
                    _tracked.RemoveAt(i);
                    continue;
                }

                Skeleton skeleton = TryGetSkeleton(agent);
                if (skeleton == null || !skeleton.IsValid || !IsRagdollActive(skeleton))
                    continue;

                if (tracked.RagdollSeenAt < 0f)
                    tracked.RagdollSeenAt = now;

                RunPreviousDiagnostics(settings, tracked, skeleton, now);
                RunHighSpeedDiagnostics(settings, tracked, dt, now);

                bool previousDiagnosticsFinished =
                    (!settings.ForceBoneFramesDuringRagdollStabilization || now - tracked.RagdollSeenAt > StabilizationWindow) &&
                    (!settings.OneShotClothResetOnRagdoll || tracked.ClothResetAttempted);

                bool highSpeedDiagnosticsEnabled =
                    settings.HighSpeedClothVelocityCompensation ||
                    settings.DiagnosticZeroClothVelocity ||
                    settings.HighSpeedClothDistanceClamp;

                bool highSpeedWindowFinished = !highSpeedDiagnosticsEnabled ||
                    (tracked.LastHighSpeedAt >= 0f && now - tracked.LastHighSpeedAt > LowSpeedRetainWindow);

                if (previousDiagnosticsFinished && highSpeedWindowFinished)
                {
                    RestoreHighSpeedOverrides(tracked);
                    _tracked.RemoveAt(i);
                }
            }
        }

        private static bool NeedsPerCorpseTracking(Settings settings)
        {
            return settings.ForceBoneFramesDuringRagdollStabilization ||
                   settings.OneShotClothResetOnRagdoll ||
                   settings.HighSpeedClothVelocityCompensation ||
                   settings.DiagnosticZeroClothVelocity ||
                   settings.HighSpeedClothDistanceClamp;
        }

        private static Skeleton TryGetSkeleton(Agent agent)
        {
            try
            {
                return agent.AgentVisuals?.GetSkeleton();
            }
            catch
            {
                return null;
            }
        }

        private static void RunPreviousDiagnostics(Settings settings, TrackedCorpse tracked, Skeleton skeleton, float now)
        {
            if (settings.OneShotClothResetOnRagdoll && !tracked.ClothResetAttempted)
            {
                tracked.ClothResetAttempted = true;
                try
                {
                    skeleton.ForceUpdateBoneFrames();
                    skeleton.ResetCloths();
                    skeleton.ForceUpdateBoneFrames();
                }
                catch
                {
                }
            }

            if (settings.ForceBoneFramesDuringRagdollStabilization &&
                tracked.RagdollSeenAt >= 0f &&
                now - tracked.RagdollSeenAt <= StabilizationWindow)
            {
                try
                {
                    skeleton.ForceUpdateBoneFrames();
                }
                catch
                {
                }
            }
        }

        private static void RunHighSpeedDiagnostics(Settings settings, TrackedCorpse tracked, float dt, float now)
        {
            Agent agent = tracked.Agent;
            Vec3 physicsVelocity = Vec3.Zero;
            bool hasPhysicsVelocity = false;
            try
            {
                physicsVelocity = agent.GetRealGlobalVelocity();
                hasPhysicsVelocity = true;
            }
            catch
            {
            }

            Vec3 currentVisualPosition;
            bool hasCurrentVisualPosition = TryCaptureVisualPosition(agent, out currentVisualPosition);
            Vec3 measuredVelocity = Vec3.Zero;
            bool hasMeasuredVelocity = false;
            if (hasCurrentVisualPosition && tracked.HasLastVisualPosition && dt > 0.000001f)
            {
                float invDt = 1f / dt;
                measuredVelocity = new Vec3(
                    (currentVisualPosition.x - tracked.LastVisualPosition.x) * invDt,
                    (currentVisualPosition.y - tracked.LastVisualPosition.y) * invDt,
                    (currentVisualPosition.z - tracked.LastVisualPosition.z) * invDt);
                hasMeasuredVelocity = true;
            }

            if (hasCurrentVisualPosition)
            {
                tracked.LastVisualPosition = currentVisualPosition;
                tracked.HasLastVisualPosition = true;
            }

            Vec3 selectedVelocity;
            bool hasSelectedVelocity;
            if (settings.UseMeasuredVisualDisplacementVelocity)
            {
                selectedVelocity = hasMeasuredVelocity ? measuredVelocity : physicsVelocity;
                hasSelectedVelocity = hasMeasuredVelocity || hasPhysicsVelocity;
            }
            else
            {
                selectedVelocity = hasPhysicsVelocity ? physicsVelocity : measuredVelocity;
                hasSelectedVelocity = hasPhysicsVelocity || hasMeasuredVelocity;
            }

            float speed = hasSelectedVelocity ? Length(selectedVelocity) : 0f;
            float threshold = Math.Max(0f, settings.ActivationSpeedThreshold);
            bool highSpeed = hasSelectedVelocity && speed >= threshold;

            if (highSpeed)
            {
                tracked.LastHighSpeedAt = now;
                Vec3 forcedVelocity = settings.DiagnosticZeroClothVelocity ? Vec3.Zero : selectedVelocity;

                bool writeVelocity = settings.DiagnosticZeroClothVelocity || settings.HighSpeedClothVelocityCompensation;
                bool writeDistance = settings.HighSpeedClothDistanceClamp;
                ApplyToAllClothSimulators(agent, forcedVelocity, writeVelocity,
                    Clamp(settings.ClothMaxDistanceMultiplier, 0.05f, 1f), writeDistance);

                if (writeVelocity)
                    tracked.ForcedVelocityWasWritten = true;
                if (writeDistance)
                    tracked.DistanceClampWasWritten = true;
            }
            else
            {
                if (tracked.ForcedVelocityWasWritten || tracked.DistanceClampWasWritten)
                    RestoreHighSpeedOverrides(tracked);
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

        private static void RestoreHighSpeedOverrides(TrackedCorpse tracked)
        {
            if (tracked == null || tracked.Agent == null)
                return;

            if (!tracked.ForcedVelocityWasWritten && !tracked.DistanceClampWasWritten)
                return;

            ApplyToAllClothSimulators(
                tracked.Agent,
                Vec3.Zero,
                tracked.ForcedVelocityWasWritten,
                1f,
                tracked.DistanceClampWasWritten);

            tracked.ForcedVelocityWasWritten = false;
            tracked.DistanceClampWasWritten = false;
        }

        private static void ApplyToAllClothSimulators(
            Agent agent,
            Vec3 forcedVelocity,
            bool setForcedVelocity,
            float maxDistanceMultiplier,
            bool setMaxDistanceMultiplier)
        {
            if ((!setForcedVelocity && !setMaxDistanceMultiplier) || agent == null)
                return;

            GameEntity root;
            try
            {
                root = agent.AgentVisuals?.GetEntity();
            }
            catch
            {
                root = null;
            }

            if (root == null)
                return;

            ApplyToEntityAndChildren(root, forcedVelocity, setForcedVelocity,
                maxDistanceMultiplier, setMaxDistanceMultiplier);
        }

        private static void ApplyToEntityAndChildren(
            GameEntity entity,
            Vec3 forcedVelocity,
            bool setForcedVelocity,
            float maxDistanceMultiplier,
            bool setMaxDistanceMultiplier)
        {
            if (entity == null)
                return;

            try
            {
                int clothCount = entity.ClothSimulatorComponentCount;
                for (int i = 0; i < clothCount; i++)
                {
                    ClothSimulatorComponent cloth = entity.GetClothSimulator(i);
                    if (cloth == null)
                        continue;

                    try
                    {
                        if (setForcedVelocity)
                            cloth.SetForcedVelocity(in forcedVelocity);
                        if (setMaxDistanceMultiplier)
                            cloth.SetMaxDistanceMultiplier(maxDistanceMultiplier);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            int childCount;
            try
            {
                childCount = entity.ChildCount;
            }
            catch
            {
                return;
            }

            for (int i = 0; i < childCount; i++)
            {
                GameEntity child;
                try
                {
                    child = entity.GetChild(i);
                }
                catch
                {
                    child = null;
                }

                if (child != null)
                    ApplyToEntityAndChildren(child, forcedVelocity, setForcedVelocity,
                        maxDistanceMultiplier, setMaxDistanceMultiplier);
            }
        }

        private static float Length(Vec3 value)
        {
            return (float)Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
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

        private static bool IsRagdollActive(Skeleton skeleton)
        {
            try
            {
                string state = skeleton.GetCurrentRagdollState().ToString();
                return state.IndexOf("Active", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private void SyncTimerBasedForcedSkeletonUpdates(bool requested)
        {
            if (_timerStateApplied && requested == _lastTimerRequested)
                return;

            if (TrySetTimerBasedForcedSkeletonUpdates(requested))
            {
                _timerStateApplied = true;
                _lastTimerRequested = requested;
            }
        }

        private bool TrySetTimerBasedForcedSkeletonUpdates(bool value)
        {
            try
            {
                if (_rendererController == null || _setTimerBasedUpdates == null)
                {
                    object controller = FindRendererController();
                    if (controller == null)
                        return false;

                    MethodInfo setter = controller.GetType().GetMethod(
                        "SetDoTimerBasedForcedSkeletonUpdates",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(bool) },
                        null);
                    if (setter == null)
                        return false;

                    _rendererController = controller;
                    _setTimerBasedUpdates = setter;
                }

                _setTimerBasedUpdates.Invoke(_rendererController, new object[] { value });
                return true;
            }
            catch
            {
                _rendererController = null;
                _setTimerBasedUpdates = null;
                return false;
            }
        }

        private object FindRendererController()
        {
            object mission = Mission;
            if (mission == null)
                return null;

            Type type = mission.GetType();
            while (type != null)
            {
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field.FieldType.Name != "MBAgentRendererSceneController" &&
                        field.Name.IndexOf("agentRendererSceneController", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    object value = field.GetValue(mission);
                    if (value != null)
                        return value;
                }
                type = type.BaseType;
            }

            return null;
        }
    }
}
