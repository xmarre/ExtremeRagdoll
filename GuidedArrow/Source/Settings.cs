using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace GuidedArrow
{
    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "guided_arrow_v100";
        public override string DisplayName => "Guided Arrow";
        public override string FolderName => "GuidedArrow";
        public override string FormatType => "json";

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyBool("Enable Guided Arrows", Order = 0, RequireRestart = false,
            HintText = "Guide player-fired arrows and bolts with the mouse. Q slows time, E speeds it up, Esc or right mouse cancels guidance.")]
        public bool Enabled { get; set; } = true;

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyFloatingInteger("Initial Guidance Time Speed", 0.05f, 1.0f, "0.00", Order = 1, RequireRestart = false,
            HintText = "Mission speed used when guidance starts. Q/E move through fixed speed steps from 0.05x to 1.00x.")]
        public float InitialGuidanceTimeSpeed { get; set; } = 0.15f;

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyFloatingInteger("Minimum Turn Radius", 3f, 120f, "0.0", Order = 2, RequireRestart = false,
            HintText = "Physical steering limit in metres. Lower values allow tighter curves; faster arrows still require proportionally more angular rate.")]
        public float MinimumTurnRadius { get; set; } = 24f;

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyFloatingInteger("Mouse Steering Sensitivity", 0.10f, 4.0f, "0.00", Order = 3, RequireRestart = false,
            HintText = "Mouse steering sensitivity while guiding the projectile.")]
        public float MouseSensitivity { get; set; } = 1.0f;

        [SettingPropertyGroup("General", GroupOrder = 0)]
        [SettingPropertyFloatingInteger("Maximum Guidance Time", 5f, 120f, "0.0", Order = 4, RequireRestart = false,
            HintText = "Real-time failsafe duration before a missed projectile automatically returns the camera to the player.")]
        public float MaximumGuidanceTime { get; set; } = 35f;

        [SettingPropertyGroup("Projectile Camera", GroupOrder = 1)]
        [SettingPropertyInteger("Camera Mode", 0, 1, "0", Order = 10, RequireRestart = false,
            HintText = "0 = First Person (camera travels as the arrow), 1 = Third Person (locked projectile-relative chase camera).")]
        public int ProjectileCameraMode { get; set; } = 1;

        [SettingPropertyGroup("Projectile Camera", GroupOrder = 1)]
        [SettingPropertyBool("Show Crosshair", Order = 11, RequireRestart = false,
            HintText = "Shows a centered crosshair while actively guiding an arrow or bolt.")]
        public bool ShowCrosshair { get; set; } = false;

        [SettingPropertyGroup("Projectile Camera/First Person", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Rear Offset", 0.01f, 1.50f, "0.00", Order = 12, RequireRestart = false,
            HintText = "Places the first-person camera this far behind the projectile while looking exactly along its flight path.")]
        public float FirstPersonRearOffset { get; set; } = 0.12f;

        [SettingPropertyGroup("Projectile Camera/First Person", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Vertical Offset", -0.50f, 0.50f, "0.00", Order = 13, RequireRestart = false,
            HintText = "Small vertical offset in the arrow's local frame. Zero gives a true arrow-eye view.")]
        public float FirstPersonVerticalOffset { get; set; } = 0f;

        [SettingPropertyGroup("Projectile Camera/Third Person", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Locked Distance", 0.5f, 15f, "0.00", Order = 14, RequireRestart = false,
            HintText = "Fixed chase-camera distance from the projectile.")]
        public float CameraDistance { get; set; } = 3.4f;

        [SettingPropertyGroup("Projectile Camera/Third Person", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Locked Elevation Angle", -30f, 60f, "0.0", Order = 15, RequireRestart = false,
            HintText = "Camera elevation relative to the arrow's flight angle. The full rig pitches and yaws with the arrow.")]
        public float ThirdPersonElevationAngle { get; set; } = 12f;

        [SettingPropertyGroup("Projectile Camera/Third Person", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Look-Ahead Distance", 0f, 25f, "0.0", Order = 16, RequireRestart = false,
            HintText = "How far ahead of the projectile the chase camera looks. Higher values emphasize the future flight path.")]
        public float ThirdPersonLookAhead { get; set; } = 5f;

        public float CameraHeight { get; set; } = 0.55f;
        public float CameraPositionSmoothing { get; set; } = 18f;
        public float CameraRotationSmoothing { get; set; } = 22f;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 2)]
        [SettingPropertyInteger("Cinematic Mode", 0, 2, "0", Order = 20, RequireRestart = false,
            HintText = "0 = Fixed Duration, 1 = Until Corpse Settles, 2 = Full Until Native Corpse Finalization. Cinematics trigger only when the guided shot kills an agent.")]
        public int CinematicMode { get; set; } = 2;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Fixed Cinematic Duration", 0.1f, 10f, "0.0", Order = 21, RequireRestart = false,
            HintText = "Duration used by Cinematic Mode 0. Minimum is 0.1 seconds.")]
        public float FixedCinematicDuration { get; set; } = 1.5f;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Cinematic Time Speed", 0.05f, 1.0f, "0.00", Order = 22, RequireRestart = false,
            HintText = "Mission speed while watching a confirmed kill cinematic.")]
        public float CinematicTimeSpeed { get; set; } = 0.10f;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Settled Motion Threshold", 0.002f, 0.20f, "0.000", Order = 23, RequireRestart = false,
            HintText = "Maximum movement between 0.1 second samples before a corpse is considered settled in Mode 1.")]
        public float SettledMotionThreshold { get; set; } = 0.025f;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Settled Hold Time", 0.1f, 3f, "0.0", Order = 24, RequireRestart = false,
            HintText = "How long movement must remain below the threshold before Mode 1 ends.")]
        public float SettledHoldTime { get; set; } = 0.5f;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Full Cinematic Failsafe", 5f, 60f, "0.0", Order = 25, RequireRestart = false,
            HintText = "Real-time failsafe for Mode 2 if the corpse never reaches Bannerlord's native NeedsDeactivation/finalization boundary.")]
        public float FullCinematicTimeout { get; set; } = 30f;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 2)]
        [SettingPropertyFloatingInteger("Cinematic Camera Distance", 1.5f, 12f, "0.0", Order = 26, RequireRestart = false,
            HintText = "Base distance used by the kill camera while following the victim.")]
        public float CinematicCameraDistance { get; set; } = 4.0f;

        [SettingPropertyGroup("Return Transition", GroupOrder = 3)]
        [SettingPropertyFloatingInteger("Return Flight Duration", 0.08f, 2f, "0.00", Order = 30, RequireRestart = false,
            HintText = "Duration of the eased camera flight back to the player. The camera travels back instead of teleporting.")]
        public float ReturnDuration { get; set; } = 0.32f;

        [SettingPropertyGroup("Diagnostics", GroupOrder = 4)]
        [SettingPropertyBool("Debug Logging", Order = 40, RequireRestart = false,
            HintText = "Writes concise state-transition diagnostics to Documents/Mount and Blade II Bannerlord/Configs/ModLogs/GuidedArrow.log.")]
        public bool DebugLogging { get; set; } = false;
    }
}
