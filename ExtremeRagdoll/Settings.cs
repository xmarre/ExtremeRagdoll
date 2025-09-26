using MCM.Abstractions.Base.Global;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;

namespace ExtremeRagdoll
{
    // Attribute settings => automatically discovered by MCM
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        // Keep Id stable & unique; matching module Id is fine
        public override string Id => "ExtremeRagdoll";
        public override string DisplayName => "Extreme Ragdoll";
        public override string FolderName => "ExtremeRagdoll";
        public override string FormatType => "json";

        [SettingPropertyGroup("General")]
        [SettingPropertyFloatingInteger("Knockback Multiplier", 1f, 10f, "0.0",
            Order = 0, RequireRestart = false, HintText = "Scales death shove strength.")]
        public float KnockbackMultiplier { get; set; } = 6.0f;

        [SettingPropertyGroup("General")]
        [SettingPropertyInteger("Max Extra Magnitude", 0, 5000,
            Order = 1, RequireRestart = false, HintText = "Hard cap for injected impulse.")]
        public int MaxExtraMagnitude { get; set; } = 2500;

        [SettingPropertyGroup("General")]
        [SettingPropertyBool("Debug Logging",
            Order = 2, RequireRestart = false, HintText = "Print shove lines to log.")]
        public bool DebugLogging { get; set; } = true;
    }
}
