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
        private const float PendingLifetime = 3.0f;

        private sealed class TrackedCorpse
        {
            public Agent Agent;
            public float CapturedAt;
            public float RagdollSeenAt = -1f;
            public bool ClothResetAttempted;
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
            if (settings == null ||
                (!settings.ForceBoneFramesDuringRagdollStabilization && !settings.OneShotClothResetOnRagdoll))
                return;

            for (int i = 0; i < _tracked.Count; i++)
            {
                if (ReferenceEquals(_tracked[i].Agent, affected))
                    return;
            }

            _tracked.Add(new TrackedCorpse
            {
                Agent = affected,
                CapturedAt = Mission.CurrentTime
            });
        }

        public override void OnMissionTick(float dt)
        {
            if (Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null)
                return;

            SyncTimerBasedForcedSkeletonUpdates(settings.TimerBasedForcedSkeletonUpdates);

            bool forceFrames = settings.ForceBoneFramesDuringRagdollStabilization;
            bool resetCloth = settings.OneShotClothResetOnRagdoll;
            if (!forceFrames && !resetCloth)
            {
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
                    _tracked.RemoveAt(i);
                    continue;
                }

                Skeleton skeleton;
                try
                {
                    skeleton = agent.AgentVisuals?.GetSkeleton();
                }
                catch
                {
                    skeleton = null;
                }

                if (skeleton == null || !skeleton.IsValid)
                    continue;

                if (!IsRagdollActive(skeleton))
                    continue;

                if (tracked.RagdollSeenAt < 0f)
                    tracked.RagdollSeenAt = now;

                if (resetCloth && !tracked.ClothResetAttempted)
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

                if (forceFrames && now - tracked.RagdollSeenAt <= StabilizationWindow)
                {
                    try
                    {
                        skeleton.ForceUpdateBoneFrames();
                    }
                    catch
                    {
                    }
                }

                bool forceWindowFinished = !forceFrames || now - tracked.RagdollSeenAt > StabilizationWindow;
                bool resetFinished = !resetCloth || tracked.ClothResetAttempted;
                if (forceWindowFinished && resetFinished)
                    _tracked.RemoveAt(i);
            }
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
