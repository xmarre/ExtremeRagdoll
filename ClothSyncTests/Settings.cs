using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace ExtremeRagdoll.ClothSyncTests
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "ExtremeRagdoll_ClothSyncTests_v138";
        public override string DisplayName => "Extreme Ragdoll - Cloth Sync Tests";
        public override string FolderName => "ExtremeRagdoll";
        public override string FormatType => "json";

        [SettingPropertyGroup("Previous Diagnostics")]
        [SettingPropertyBool("Force Bone Frames During Ragdoll Stabilization",
            Order = 0, RequireRestart = false)]
        public bool ForceBoneFramesDuringRagdollStabilization { get; set; } = false;

        [SettingPropertyGroup("Previous Diagnostics")]
        [SettingPropertyBool("Timer-Based Forced Skeleton Updates",
            Order = 1, RequireRestart = false)]
        public bool TimerBasedForcedSkeletonUpdates { get; set; } = false;

        [SettingPropertyGroup("Previous Diagnostics")]
        [SettingPropertyBool("One-Shot Cloth Reset On Ragdoll",
            Order = 2, RequireRestart = false)]
        public bool OneShotClothResetOnRagdoll { get; set; } = false;

        [SettingPropertyGroup("High-Speed Corpse Cloth")]
        [SettingPropertyBool("High-Speed Cloth Velocity Compensation",
            Order = 10, RequireRestart = false)]
        public bool HighSpeedClothVelocityCompensation { get; set; } = false;

        [SettingPropertyGroup("High-Speed Corpse Cloth")]
        [SettingPropertyBool("Use Measured Visual Displacement Velocity",
            Order = 11, RequireRestart = false,
            HintText = "OFF uses Agent.GetRealGlobalVelocity(). ON derives velocity from the corpse visual root's frame-to-frame world displacement divided by mission dt.")]
        public bool UseMeasuredVisualDisplacementVelocity { get; set; } = false;

        [SettingPropertyGroup("High-Speed Corpse Cloth")]
        [SettingPropertyBool("Diagnostic Zero Cloth Velocity",
            Order = 12, RequireRestart = false,
            HintText = "Overrides velocity compensation above the activation threshold and feeds Vec3.Zero to each cloth simulator as an A/B diagnostic.")]
        public bool DiagnosticZeroClothVelocity { get; set; } = false;

        [SettingPropertyGroup("High-Speed Corpse Cloth")]
        [SettingPropertyBool("High-Speed Cloth Distance Clamp",
            Order = 13, RequireRestart = false)]
        public bool HighSpeedClothDistanceClamp { get; set; } = false;

        [SettingPropertyGroup("High-Speed Corpse Cloth")]
        [SettingPropertyFloatingInteger("Activation Speed Threshold", 0f, 100f, "0.0 m/s",
            Order = 14, RequireRestart = false,
            HintText = "Velocity and distance-clamp diagnostics are applied only while corpse speed is at or above this threshold.")]
        public float ActivationSpeedThreshold { get; set; } = 6f;

        [SettingPropertyGroup("High-Speed Corpse Cloth")]
        [SettingPropertyFloatingInteger("Cloth Max Distance Multiplier", 0.05f, 1f, "0.00x",
            Order = 15, RequireRestart = false,
            HintText = "Applied only while the High-Speed Cloth Distance Clamp is enabled and the corpse is above the activation speed threshold. Restores to 1.0 when the corpse slows down.")]
        public float ClothMaxDistanceMultiplier { get; set; } = 0.35f;
    }
}
