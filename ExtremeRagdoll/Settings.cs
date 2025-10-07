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
        public float KnockbackMultiplier { get; set; } = 1.8f;

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
        public float LaunchDelay1 { get; set; } = 0.060f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Delay #2 (s)", 0f, 0.30f, "0.00",
            Order = 101, RequireRestart = false)]
        public float LaunchDelay2 { get; set; } = 0.120f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Second Pulse Scale", 0f, 2.0f, "0.00",
            Order = 102, RequireRestart = false)]
        public float LaunchPulse2Scale { get; set; } = 0.18f;

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
        public float CorpseLaunchVerticalDelta { get; set; } = 0.05f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Displacement Threshold", 0f, 0.5f, "0.000",
            Order = 106, RequireRestart = false)]
        public float CorpseLaunchDisplacement { get; set; } = 0.030f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max Corpse Launch Magnitude", 0f, 500_000_000f, "0.0",
            Order = 107, RequireRestart = false)]
        public float MaxCorpseLaunchMagnitude { get; set; } = 200_000_000f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max AOE Force", 0f, 500_000_000f, "0.0",
            Order = 108, RequireRestart = false)]
        public float MaxAoEForce { get; set; } = 200_000_000f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max Blow Base Magnitude", 0f, 500_000_000f, "0.0",
            Order = 109, RequireRestart = false)]
        public float MaxBlowBaseMagnitude { get; set; } = 400_000f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max Non-Lethal Knockback", 0f, 500_000_000f, "0.0",
            Order = 110, RequireRestart = false)]
        public float MaxNonLethalKnockback { get; set; } = 0f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Warmup Blow Base Magnitude", 0f, 100000f, "0.0",
            Order = 111, RequireRestart = false)]
        public float WarmupBlowBaseMagnitude { get; set; } = 20f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Horse Ram Knockdown Threshold", 0f, 500_000_000f, "0.0",
            Order = 112, RequireRestart = false)]
        public float HorseRamKnockDownThreshold { get; set; } = 9_000f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Minimum Missile Speed For Push", 0f, 1_000f, "0.0",
            Order = 113, RequireRestart = false)]
        public float MinMissileSpeedForPush { get; set; } = 5f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyBool("Blocked Missiles Can Push",
            Order = 114, RequireRestart = false)]
        public bool BlockedMissilesCanPush { get; set; } = false;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch XY Jitter", 0f, 0.5f, "0.000",
            Order = 115, RequireRestart = false)]
        public float CorpseLaunchXYJitter { get; set; } = 0.002f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Z Nudge", 0f, 0.5f, "0.000",
            Order = 116, RequireRestart = false)]
        public float CorpseLaunchZNudge { get; set; } = 0.15f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Z Clamp Above", 0f, 1.0f, "0.000",
            Order = 117, RequireRestart = false)]
        public float CorpseLaunchZClampAbove { get; set; } = 0.05f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Contact Height", 0f, 1.0f, "0.000",
            Order = 118, RequireRestart = false)]
        public float CorpseLaunchContactHeight { get; set; } = 0.04f; // avoid ground clipping

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Retry Delay (s)", 0f, 0.5f, "0.000",
            Order = 119, RequireRestart = false)]
        public float CorpseLaunchRetryDelay { get; set; } = 0.03f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Retry Jitter (s)", 0f, 0.5f, "0.000",
            Order = 120, RequireRestart = false)]
        public float CorpseLaunchRetryJitter { get; set; } = 0.005f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Corpse Launch Retry Attempts", 0, 20,
            Order = 121, RequireRestart = false)]
        public int CorpseLaunchRetryMaxTries { get; set; } = 4;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Corpse Launch Queue Cap", 0, 20,
            Order = 122, RequireRestart = false)]
        public int CorpseLaunchQueueCap { get; set; } = 3;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Corpse Retry Queue Limit", 0, 4096,
            Order = 123, RequireRestart = false)]
        public int CorpseLaunchRetryQueueLimit { get; set; } = 256;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Minimum Corpse Impulse", 0f, 500_000f, "0.0",
            Order = 124, RequireRestart = false)]
        public float CorpseImpulseMinimum { get; set; } = 6f;   // guarantees visible nudge

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Maximum Corpse Impulse", 0f, 2_000_000f, "0.0",
            Order = 125, RequireRestart = false)]
        public float CorpseImpulseMaximum { get; set; } = 0f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Impulse Hard Cap (physics units)", 0f, 1_000f, "0.0",
            Order = 126, RequireRestart = false)]
        public float CorpseImpulseHardCap { get; set; } = 17f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Min Up Fraction", 0f, 1.0f, "0.00",
            Order = 127, RequireRestart = false)]
        public float CorpseLaunchMinUpFraction { get; set; } = 0.00f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Max Up Fraction", 0f, 1.0f, "0.00",
            Order = 128, RequireRestart = false)]
        public float CorpseLaunchMaxUpFraction { get; set; } = 0.08f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Corpse Prelaunch Tries", 0, 100,
            Order = 129, RequireRestart = false)]
        public int CorpsePrelaunchTries { get; set; } = 12;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Corpse Post-Death Launch Tries", 0, 100,
            Order = 130, RequireRestart = false)]
        public int CorpsePostDeathTries { get; set; } = 6;    // raise reliability of post-death nudges

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Max Corpse Launches Per Tick", 0, 2048,
            Order = 131, RequireRestart = false)]
        public int CorpseLaunchesPerTick { get; set; } = 128;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Max AoE Agents Per Tick", 0, 4096,
            Order = 132, RequireRestart = false)]
        public int AoEAgentsPerTick { get; set; } = 256;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyInteger("Max Kick Impulses Per Tick", 0, 2048,
            Order = 133, RequireRestart = false)]
        public int KicksPerTick { get; set; } = 128;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Corpse Launch Schedule Window (s)", 0f, 0.5f, "0.00",
            Order = 134, RequireRestart = false)]
        public float CorpseLaunchScheduleWindow { get; set; } = 0.08f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Death Blast Memory (s)", 0f, 5f, "0.00",
            Order = 135, RequireRestart = false)]
        public float DeathBlastTtl { get; set; } = 0.75f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyBool("Force Entity-Space Impulses",
            Order = 136, RequireRestart = false)]
        // Default to letting skeleton delivery handle most impulses.
        public bool ForceEntityImpulse { get; set; } = false;  // entity routes only when skeleton rejects

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyBool("Allow Skeleton Fallback When Entity Missing/Invalid",
            Order = 137, RequireRestart = false)]
        public bool AllowSkeletonFallbackForInvalidEntity { get; set; } = true;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyBool("Respect Engine Blow Flags",
            Order = 138, RequireRestart = false)]
        public bool RespectEngineBlowFlags { get; set; } = false;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Max Entity AABB Extent (m)", 1f, 5000f, "0.0",
            Order = 139, RequireRestart = false)]
        public float MaxAabbExtent { get; set; } = 1024f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Immediate Impulse Scale", 0f, 1f, "0.00",
            Order = 140, RequireRestart = false)]
        public float ImmediateImpulseScale { get; set; } = 0.30f; // stronger wake-up nudge to keep ragdolls dynamic

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Schedule Direction Duplicate Threshold", 0f, 4f, "0.0000",
            Order = 141, RequireRestart = false)]
        public float ScheduleDirDuplicateSqThreshold { get; set; } = 0.001f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Schedule Position Duplicate Threshold", 0f, 1f, "0.000",
            Order = 142, RequireRestart = false)]
        public float SchedulePosDuplicateSqThreshold { get; set; } = 0.01f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Schedule Magnitude Duplicate Fraction", 0f, 1f, "0.00",
            Order = 143, RequireRestart = false)]
        public float ScheduleMagDuplicateFraction { get; set; } = 0.05f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyBool("Allow ent3(world) impulses", Order = 144, RequireRestart = false)]
        public bool AllowEnt3World { get; set; } = true; // enable world-route fallback while skeleton APIs remain unbound

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyBool("Allow ent1(world) fallback when ent2 unavailable", Order = 145, RequireRestart = false)]
        public bool AllowEnt1WorldFallback { get; set; } = true;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("World Impulse Cap", 200f, 1800f, "0.0",
            Order = 146, RequireRestart = false)]
        public float WorldImpulseCap { get; set; } = 1200f;

        [SettingPropertyGroup("Advanced")]
        [SettingPropertyFloatingInteger("Local Impulse Cap", 200f, 1800f, "0.0",
            Order = 147, RequireRestart = false)]
        public float LocalImpulseCap { get; set; } = 1200f;
    }
}
