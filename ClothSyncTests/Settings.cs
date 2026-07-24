using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace ExtremeRagdoll.ClothSyncTests
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "ExtremeRagdoll_ClothSyncTests_v137";
        public override string DisplayName => "Extreme Ragdoll - Cloth Sync Tests";
        public override string FolderName => "ExtremeRagdoll";
        public override string FormatType => "json";

        [SettingPropertyGroup("Experimental Cloth Sync")]
        [SettingPropertyBool("Force Bone Frames During Ragdoll Stabilization",
            Order = 0, RequireRestart = false)]
        public bool ForceBoneFramesDuringRagdollStabilization { get; set; } = false;

        [SettingPropertyGroup("Experimental Cloth Sync")]
        [SettingPropertyBool("Timer-Based Forced Skeleton Updates",
            Order = 1, RequireRestart = false)]
        public bool TimerBasedForcedSkeletonUpdates { get; set; } = false;

        [SettingPropertyGroup("Experimental Cloth Sync")]
        [SettingPropertyBool("One-Shot Cloth Reset On Ragdoll",
            Order = 2, RequireRestart = false)]
        public bool OneShotClothResetOnRagdoll { get; set; } = false;
    }
}
