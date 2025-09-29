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
        public float LaunchDelay1 { get; set; } = 0.05f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Delay #2 (s)", 0f, 0.30f, "0.00",
            Order = 101, RequireRestart = false)]
        public float LaunchDelay2 { get; set; } = 0.12f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Second Pulse Scale", 0f, 2.0f, "0.00",
            Order = 102, RequireRestart = false)]
        public float LaunchPulse2Scale { get; set; } = 0.80f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Velocity Scale Threshold", 1.0f, 2.0f, "0.00",
            Order = 103, RequireRestart = false)]
        public float CorpseLaunchVelocityScaleThreshold { get; set; } = 1.08f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Velocity Offset", 0f, 1f, "0.000",
            Order = 104, RequireRestart = false)]
        public float CorpseLaunchVelocityOffset { get; set; } = 0.02f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Vertical Delta", 0f, 1f, "0.000",
            Order = 105, RequireRestart = false)]
        public float CorpseLaunchVerticalDelta { get; set; } = 0.07f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Displacement Threshold", 0f, 0.5f, "0.000",
            Order = 106, RequireRestart = false)]
        public float CorpseLaunchDisplacement { get; set; } = 0.020f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max Corpse Launch Magnitude", 0f, 500_000_000f, "0.0",
            Order = 107, RequireRestart = false)]
        public float MaxCorpseLaunchMagnitude { get; set; } = 200_000_000f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max AOE Force", 0f, 500_000_000f, "0.0",
            Order = 108, RequireRestart = false)]
        public float MaxAoEForce { get; set; } = 200_000_000f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max Non-Lethal Knockback", 0f, 500_000_000f, "0.0",
            Order = 109, RequireRestart = false)]
        public float MaxNonLethalKnockback { get; set; } = 0f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch XY Jitter", 0f, 0.5f, "0.000",
            Order = 110, RequireRestart = false)]
        public float CorpseLaunchXYJitter { get; set; } = 0.003f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Z Nudge", 0f, 0.5f, "0.000",
            Order = 111, RequireRestart = false)]
        public float CorpseLaunchZNudge { get; set; } = 0.05f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Z Clamp Above", 0f, 1.0f, "0.000",
            Order = 112, RequireRestart = false)]
        public float CorpseLaunchZClampAbove { get; set; } = 0.12f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Contact Height", 0f, 1.0f, "0.000",
            Order = 113, RequireRestart = false)]
        public float CorpseLaunchContactHeight { get; set; } = 0.35f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Retry Delay (s)", 0f, 0.5f, "0.000",
            Order = 114, RequireRestart = false)]
        public float CorpseLaunchRetryDelay { get; set; } = 0.11f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Retry Jitter (s)", 0f, 0.5f, "0.000",
            Order = 115, RequireRestart = false)]
        public float CorpseLaunchRetryJitter { get; set; } = 0.005f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Corpse Launch Queue Cap", 0, 20,
            Order = 116, RequireRestart = false)]
        public int CorpseLaunchQueueCap { get; set; } = 3;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Minimum Corpse Impulse", 0f, 500_000f, "0.0",
            Order = 117, RequireRestart = false)]
        public float CorpseImpulseMinimum { get; set; } = 15_000f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Maximum Corpse Impulse", 0f, 2_000_000f, "0.0",
            Order = 118, RequireRestart = false)]
        public float CorpseImpulseMaximum { get; set; } = 400_000f;
    }
}
