using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace ExtremeRagdoll.ClothSyncTests
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "ExtremeRagdoll_ClothSyncTests_v141";
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

        [SettingPropertyGroup("Native Visual Tick Catch-Up")]
        [SettingPropertyBool("Force High-Speed Corpse Visual Ticks", Order = 60, RequireRestart = false,
            HintText = "Directly ticks the killed corpse's native MBAgentVisuals while it is moving faster than the activation threshold, passing entityMoving=true and the actual corpse/ragdoll speed instead of relying on the normal dead-agent visual scheduling path.")]
        public bool HighSpeedVisualTickCatchUp { get; set; } = false;

        [SettingPropertyGroup("Native Visual Tick Catch-Up")]
        [SettingPropertyFloatingInteger("Visual Tick Catch-Up Substeps", 1f, 8f, "0", Order = 61, RequireRestart = false,
            HintText = "Number of additional native MBAgentVisuals.Tick substeps per mission tick while the corpse is above the activation threshold. Start with 1, then test 2 and 4 if needed.")]
        public float VisualTickCatchUpSubsteps { get; set; } = 1f;

        [SettingPropertyGroup("High-Speed Thresholds")]
        [SettingPropertyFloatingInteger("Activation Speed Threshold", 0f, 100f, "0.0 m/s", Order = 70, RequireRestart = false,
            HintText = "All high-speed diagnostics apply only while corpse speed is at or above this threshold.")]
        public float ActivationSpeedThreshold { get; set; } = 6f;

        [SettingPropertyGroup("High-Speed Thresholds")]
        [SettingPropertyFloatingInteger("Cloth Max Distance Multiplier", 0.05f, 1f, "0.00x", Order = 71, RequireRestart = false)]
        public float ClothMaxDistanceMultiplier { get; set; } = 0.35f;
    }
}
