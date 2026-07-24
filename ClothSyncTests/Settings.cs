using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace ExtremeRagdoll.ClothSyncTests
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "ExtremeRagdoll_ClothSyncTests_v142";
        public override string DisplayName => "Extreme Ragdoll - Cloth Sync Tests";
        public override string FolderName => "ExtremeRagdoll";
        public override string FormatType => "json";

        [SettingPropertyGroup("Previous Diagnostics")]
        [SettingPropertyBool("Force Bone Frames During Ragdoll Stabilization", Order = 0, RequireRestart = false)]
        public bool ForceBoneFramesDuringRagdollStabilization { get; set; } = false;

        [SettingPropertyGroup("Previous Diagnostics")]
        [SettingPropertyBool("Timer-Based Forced Skeleton Updates", Order = 1, RequireRestart = false)]
        public bool TimerBasedForcedSkeletonUpdates { get; set; } = false;

        [SettingPropertyGroup("Previous Diagnostics")]
        [SettingPropertyBool("One-Shot Cloth Reset On Ragdoll", Order = 2, RequireRestart = false)]
        public bool OneShotClothResetOnRagdoll { get; set; } = false;

        [SettingPropertyGroup("Previous High-Speed Diagnostics")]
        [SettingPropertyBool("High-Speed Cloth Velocity Compensation", Order = 10, RequireRestart = false)]
        public bool HighSpeedClothVelocityCompensation { get; set; } = false;

        [SettingPropertyGroup("Previous High-Speed Diagnostics")]
        [SettingPropertyBool("Use Measured Visual Displacement Velocity", Order = 11, RequireRestart = false,
            HintText = "OFF uses Agent.GetRealGlobalVelocity(). ON derives velocity from frame-to-frame visual-root displacement divided by mission dt.")]
        public bool UseMeasuredVisualDisplacementVelocity { get; set; } = false;

        [SettingPropertyGroup("Previous High-Speed Diagnostics")]
        [SettingPropertyBool("Diagnostic Zero Cloth Velocity", Order = 12, RequireRestart = false)]
        public bool DiagnosticZeroClothVelocity { get; set; } = false;

        [SettingPropertyGroup("Previous High-Speed Diagnostics")]
        [SettingPropertyBool("High-Speed Cloth Distance Clamp", Order = 13, RequireRestart = false)]
        public bool HighSpeedClothDistanceClamp { get; set; } = false;

        [SettingPropertyGroup("Previous High-Speed Hard Rebase")]
        [SettingPropertyBool("Invalidate Previous Cloth Frames", Order = 20, RequireRestart = false)]
        public bool HighSpeedInvalidatePreviousFrames { get; set; } = false;

        [SettingPropertyGroup("Previous High-Speed Hard Rebase")]
        [SettingPropertyBool("Continuous Cloth Hard Reset", Order = 21, RequireRestart = false)]
        public bool HighSpeedContinuousClothReset { get; set; } = false;

        [SettingPropertyGroup("Previous High-Speed Hard Rebase")]
        [SettingPropertyBool("Teleport-Rebase Cloth Entities", Order = 22, RequireRestart = false)]
        public bool HighSpeedTeleportRebase { get; set; } = false;

        [SettingPropertyGroup("Previous Direct Agent Cloth Test")]
        [SettingPropertyBool("Detach Agent Cloth During High-Speed Flight", Order = 40, RequireRestart = false,
            HintText = "Directly clears the killed Agent's _capeClothSimulator during high-speed corpse flight, then restores it after slowdown.")]
        public bool HighSpeedDirectAgentClothDetach { get; set; } = false;

        [SettingPropertyGroup("Previous Native Visual Tick Test")]
        [SettingPropertyBool("Force High-Speed Corpse Visual Ticks", Order = 60, RequireRestart = false)]
        public bool HighSpeedVisualTickCatchUp { get; set; } = false;

        [SettingPropertyGroup("Previous Native Visual Tick Test")]
        [SettingPropertyFloatingInteger("Visual Tick Catch-Up Substeps", 1f, 8f, "0", Order = 61, RequireRestart = false)]
        public float VisualTickCatchUpSubsteps { get; set; } = 1f;

        [SettingPropertyGroup("Cloth-Safe Velocity Governor")]
        [SettingPropertyBool("Limit Speed Of Cloth-Bearing Corpses", Order = 80, RequireRestart = false,
            HintText = "Detects actual cloth-bearing meshes with Skeleton.GetAllMeshes()/Mesh.HasCloth() and applies a ragdoll linear-velocity ceiling only to those dead agents. This addresses the cause directly instead of trying to make the native cloth solver catch up after it has fallen behind.")]
        public bool ClothSafeVelocityGovernor { get; set; } = false;

        [SettingPropertyGroup("Cloth-Safe Velocity Governor")]
        [SettingPropertyFloatingInteger("Cloth-Safe Linear Speed Limit", 2f, 60f, "0.0 m/s", Order = 81, RequireRestart = false,
            HintText = "Maximum ragdoll linear velocity for corpses whose equipped skeleton contains at least one Mesh.HasCloth() mesh. Start at 12 m/s; test 8, 12, 16 and 20 to find the highest stable value for your clothing set. Existing lower Extreme Ragdoll global linear limits are respected.")]
        public float ClothSafeLinearVelocityLimit { get; set; } = 12f;

        [SettingPropertyGroup("High-Speed Thresholds")]
        [SettingPropertyFloatingInteger("Activation Speed Threshold", 0f, 100f, "0.0 m/s", Order = 90, RequireRestart = false)]
        public float ActivationSpeedThreshold { get; set; } = 6f;

        [SettingPropertyGroup("High-Speed Thresholds")]
        [SettingPropertyFloatingInteger("Cloth Max Distance Multiplier", 0.05f, 1f, "0.00x", Order = 91, RequireRestart = false)]
        public float ClothMaxDistanceMultiplier { get; set; } = 0.35f;
    }
}
