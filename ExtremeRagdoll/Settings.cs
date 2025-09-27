using MCM.Abstractions.Base.Global;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;

namespace ExtremeRagdoll
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "ExtremeRagdoll_v1";
        public override string DisplayName => "Extreme Ragdoll";
        public override string FolderName => "ExtremeRagdoll";
        public override string FormatType => "json";

        [SettingPropertyGroup("General")]
        [SettingPropertyFloatingInteger("Knockback Multiplier", 1f, 1000f, "0.0",
            Order = 0, RequireRestart = false)]
        public float KnockbackMultiplier { get; set; } = 20.0f;

        [SettingPropertyGroup("General")]
        [SettingPropertyBool("Debug Logging",
            Order = 1, RequireRestart = false)]
        public bool DebugLogging { get; set; } = true;

        [SettingPropertyGroup("General")]
        [SettingPropertyFloatingInteger("Death Blast Radius", 0f, 30f, "0.0",
            Order = 2, RequireRestart = false)]
        public float DeathBlastRadius { get; set; } = 6.0f;

        [SettingPropertyGroup("General")]
        [SettingPropertyFloatingInteger("Extra Force Multiplier", 0f, 1000f, "0.0",
            Order = 3, RequireRestart = false)]
        public float ExtraForceMultiplier { get; set; } = 4.0f;

        [SettingPropertyGroup("General")]
        [SettingPropertyFloatingInteger("Death Blast Force Multiplier", 0f, 1000f, "0.0",
            Order = 4, RequireRestart = false)]
        public float DeathBlastForceMultiplier { get; set; } = 4.0f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Delay #1 (s)", 0f, 0.25f, "0.00",
            Order = 100, RequireRestart = false)]
        public float LaunchDelay1 { get; set; } = 0.06f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Delay #2 (s)", 0f, 0.30f, "0.00",
            Order = 101, RequireRestart = false)]
        public float LaunchDelay2 { get; set; } = 0.14f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Second Pulse Scale", 0f, 2.0f, "0.00",
            Order = 102, RequireRestart = false)]
        public float LaunchPulse2Scale { get; set; } = 0.80f;
    }
}
