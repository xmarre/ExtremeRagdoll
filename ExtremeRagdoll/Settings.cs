using MCM.Abstractions.Base.Global;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;

namespace ExtremeRagdoll
{
    internal sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id => "ExtremeRagdoll_v1";
        public override string DisplayName => "Extreme Ragdoll";
        public override string FolderName => "ExtremeRagdoll";
        public override string FormatType => "json";

        private float _knockbackMultiplier = 6.0f;
        private int   _maxExtraMagnitude   = 2500;
        private bool  _debugLogging        = true;

        [SettingPropertyGroup("General")]
        [SettingPropertyFloatingInteger("Knockback Multiplier", 1f, 10f, "0.0",
            Order = 0, RequireRestart = false, HintText = "Scales death shove strength.")]
        public float KnockbackMultiplier
        {
            get => _knockbackMultiplier;
            set { if (_knockbackMultiplier != value) { _knockbackMultiplier = value; OnPropertyChanged(); } }
        }

        [SettingPropertyGroup("General")]
        [SettingPropertyInteger("Max Extra Magnitude", 0, 5000,
            Order = 1, RequireRestart = false, HintText = "Hard cap for injected impulse.")]
        public int MaxExtraMagnitude
        {
            get => _maxExtraMagnitude;
            set { if (_maxExtraMagnitude != value) { _maxExtraMagnitude = value; OnPropertyChanged(); } }
        }

        [SettingPropertyGroup("General")]
        [SettingPropertyBool("Debug Logging",
            Order = 2, RequireRestart = false, HintText = "Print shove lines to log.")]
        public bool DebugLogging
        {
            get => _debugLogging;
            set { if (_debugLogging != value) { _debugLogging = value; OnPropertyChanged(); } }
        }
    }
}
