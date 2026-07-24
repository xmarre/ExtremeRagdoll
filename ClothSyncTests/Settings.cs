using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace ExtremeRagdoll.ClothSyncTests
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "ExtremeRagdoll_ClothSyncTests_v139";
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

        [SettingPropertyGroup("High-Speed Hard Rebase")]
        [SettingPropertyBool("Invalidate Previous Cloth Frames", Order = 20, RequireRestart = false,
            HintText = "While a ragdolled corpse exceeds the activation speed, invalidates the previous frame on cloth-owning visual entities every mission tick.")]
        public bool HighSpeedInvalidatePreviousFrames { get; set; } = false;

        [SettingPropertyGroup("High-Speed Hard Rebase")]
        [SettingPropertyBool("Continuous Cloth Hard Reset", Order = 21, RequireRestart = false,
            HintText = "While above the activation speed, marks every cloth simulator on the corpse visual hierarchy as reset-required each mission tick to prevent accumulated spatial debt.")]
        public bool HighSpeedContinuousClothReset { get; set; } = false;

        [SettingPropertyGroup("High-Speed Hard Rebase")]
        [SettingPropertyBool("Teleport-Rebase Cloth Entities", Order = 22, RequireRestart = false,
            HintText = "Strongest diagnostic. Rewrites each cloth-owning entity's current global frame as a teleport every high-speed tick, then optionally combines with the other rebase toggles.")]
        public bool HighSpeedTeleportRebase { get; set; } = false;

        [SettingPropertyGroup("High-Speed Thresholds")]
        [SettingPropertyFloatingInteger("Activation Speed Threshold", 0f, 100f, "0.0 m/s", Order = 30, RequireRestart = false,
            HintText = "All high-speed diagnostics and hard-rebase tests apply only while corpse speed is at or above this threshold.")]
        public float ActivationSpeedThreshold { get; set; } = 6f;

        [SettingPropertyGroup("High-Speed Thresholds")]
        [SettingPropertyFloatingInteger("Cloth Max Distance Multiplier", 0.05f, 1f, "0.00x", Order = 31, RequireRestart = false)]
        public float ClothMaxDistanceMultiplier { get; set; } = 0.35f;
    }
}
