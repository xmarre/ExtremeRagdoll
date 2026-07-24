using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.ClothSyncTests
{
    internal sealed class VerifiedClothTargetBehavior : MissionBehavior
    {
        private const float DiagnosticLifetime = 3f;
        private const float ReportDelay = 0.25f;
        private const float LowSpeedRestoreDelay = 0.25f;

        private sealed class TrackedCorpse
        {
            public Agent Agent;
            public float CapturedAt;
            public bool HasLastVisualPosition;
            public Vec3 LastVisualPosition;
            public bool Reported;
            public float LastHighSpeedAt = -1f;
            public bool KeepStateApplied;
        }

        private sealed class ScanResult
        {
            public int EntityCount;
            public int MetaMeshCount;
            public int ClothMetaMeshCount;
            public int EntityMeshCount;
            public int EntityClothMeshCount;
            public int ClothSimulatorCount;
            public int SkeletonMeshCount;
            public int SkeletonClothMeshCount;
            public readonly List<string> ClothMetaMeshNames = new List<string>(8);
        }

        private readonly List<TrackedCorpse> _tracked = new List<TrackedCorpse>(16);

        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        public override void OnBehaviorInitialize()
        {
            _tracked.Clear();
        }

        public override void OnRemoveBehavior()
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
                RestoreKeepState(_tracked[i]);
            _tracked.Clear();
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow killingBlow)
        {
            if (affected == null || state != AgentState.Killed || Mission == null)
                return;

            Settings settings = TryGetSettings();
            if (settings == null || (!settings.VerifiedClothTargetDiagnostics && !settings.VerifiedClothKeepStateDuringFlight))
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
            if (settings == null || (!settings.VerifiedClothTargetDiagnostics && !settings.VerifiedClothKeepStateDuringFlight))
            {
                for (int i = _tracked.Count - 1; i >= 0; i--)
                    RestoreKeepState(_tracked[i]);
                _tracked.Clear();
                return;
            }

            float now = Mission.CurrentTime;
            float threshold = Math.Max(0f, settings.ActivationSpeedThreshold);

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                TrackedCorpse tracked = _tracked[i];
                if (tracked.Agent == null || now - tracked.CapturedAt > DiagnosticLifetime)
                {
                    RestoreKeepState(tracked);
                    _tracked.RemoveAt(i);
                    continue;
                }

                bool hasVelocity = TryGetVelocity(tracked, dt, out Vec3 velocity);
                float speed = hasVelocity ? Length(velocity) : 0f;
                bool highSpeed = hasVelocity && speed >= threshold;

                ScanResult scan = ScanAgentVisuals(tracked.Agent);

                if (settings.VerifiedClothTargetDiagnostics && !tracked.Reported && now - tracked.CapturedAt >= ReportDelay)
                {
                    tracked.Reported = true;
                    string names = scan.ClothMetaMeshNames.Count == 0
                        ? "none"
                        : string.Join(",", scan.ClothMetaMeshNames.ToArray());
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[ER ClothTarget] entity={scan.EntityCount} meta={scan.MetaMeshCount} clothMeta={scan.ClothMetaMeshCount} " +
                        $"entityMeshes={scan.EntityMeshCount} entityClothMeshes={scan.EntityClothMeshCount} clothComponents={scan.ClothSimulatorCount} " +
                        $"skeletonMeshes={scan.SkeletonMeshCount} skeletonCloth={scan.SkeletonClothMeshCount} speed={speed:0.00} clothMetaNames={names}"));
                }

                if (settings.VerifiedClothKeepStateDuringFlight && scan.ClothMetaMeshCount > 0)
                {
                    if (highSpeed)
                    {
                        tracked.LastHighSpeedAt = now;
                        ApplyKeepState(tracked, true);
                    }
                    else if (tracked.KeepStateApplied && tracked.LastHighSpeedAt >= 0f && now - tracked.LastHighSpeedAt >= LowSpeedRestoreDelay)
                    {
                        ApplyKeepState(tracked, false);
                    }
                }
            }
        }

        private static ScanResult ScanAgentVisuals(Agent agent)
        {
            var result = new ScanResult();
            if (agent == null)
                return result;

            try
            {
                GameEntity root = agent.AgentVisuals?.GetEntity();
                if (root != null)
                    ScanEntityRecursive(root, result);
            }
            catch
            {
            }

            try
            {
                Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton != null && skeleton.IsValid)
                {
                    foreach (Mesh mesh in skeleton.GetAllMeshes())
                    {
                        if (mesh == null || !mesh.IsValid)
                            continue;
                        result.SkeletonMeshCount++;
                        try
                        {
                            if (mesh.HasCloth())
                                result.SkeletonClothMeshCount++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static void ScanEntityRecursive(GameEntity entity, ScanResult result)
        {
            if (entity == null)
                return;

            result.EntityCount++;

            try
            {
                result.ClothSimulatorCount += entity.ClothSimulatorComponentCount;
            }
            catch
            {
            }

            int metaCount = 0;
            try
            {
                metaCount = entity.MultiMeshComponentCount;
            }
            catch
            {
            }

            for (int i = 0; i < metaCount; i++)
            {
                MetaMesh meta = null;
                try
                {
                    meta = entity.GetMetaMesh(i);
                }
                catch
                {
                }
                if (meta == null || !meta.IsValid)
                    continue;

                result.MetaMeshCount++;
                bool hasClothData = false;
                try
                {
                    hasClothData = meta.HasClothData();
                }
                catch
                {
                }
                if (hasClothData)
                {
                    result.ClothMetaMeshCount++;
                    if (result.ClothMetaMeshNames.Count < 8)
                    {
                        try
                        {
                            result.ClothMetaMeshNames.Add(meta.GetName() ?? "<unnamed>");
                        }
                        catch
                        {
                            result.ClothMetaMeshNames.Add("<name-error>");
                        }
                    }
                }

                int meshCount = 0;
                try
                {
                    meshCount = meta.MeshCount;
                }
                catch
                {
                }

                for (int m = 0; m < meshCount; m++)
                {
                    Mesh mesh = null;
                    try
                    {
                        mesh = meta.GetMeshAtIndex(m);
                    }
                    catch
                    {
                    }
                    if (mesh == null || !mesh.IsValid)
                        continue;
                    result.EntityMeshCount++;
                    try
                    {
                        if (mesh.HasCloth())
                            result.EntityClothMeshCount++;
                    }
                    catch
                    {
                    }
                }
            }

            int childCount = 0;
            try
            {
                childCount = entity.ChildCount;
            }
            catch
            {
            }
            for (int i = 0; i < childCount; i++)
            {
                GameEntity child = null;
                try
                {
                    child = entity.GetChild(i);
                }
                catch
                {
                }
                if (child != null)
                    ScanEntityRecursive(child, result);
            }
        }

        private static void ApplyKeepState(TrackedCorpse tracked, bool keepState)
        {
            try
            {
                MBAgentVisuals visuals = tracked?.Agent?.AgentVisuals;
                if (visuals == null || !visuals.IsValid())
                    return;
                visuals.SetClothComponentKeepStateOfAllMeshes(keepState);
                tracked.KeepStateApplied = keepState;
            }
            catch
            {
            }
        }

        private static void RestoreKeepState(TrackedCorpse tracked)
        {
            if (tracked != null && tracked.KeepStateApplied)
                ApplyKeepState(tracked, false);
        }

        private static bool TryGetVelocity(TrackedCorpse tracked, float dt, out Vec3 velocity)
        {
            velocity = Vec3.Zero;
            Agent agent = tracked?.Agent;
            if (agent == null)
                return false;

            try
            {
                velocity = agent.GetRealGlobalVelocity();
                if (Length(velocity) > 0.001f)
                {
                    UpdateLastVisualPosition(tracked);
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

        private static void UpdateLastVisualPosition(TrackedCorpse tracked)
        {
            if (TryCaptureVisualPosition(tracked.Agent, out Vec3 position))
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
