using HarmonyLib;
using MCM.Abstractions.FluentBuilder;
using MCM.Abstractions.FluentBuilder.Implementation;
using MCM.Abstractions.Ref;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    public class SubModule : MBSubModuleBase
    {
        private static bool _adapted;

        protected override void OnSubModuleLoad()
        {
            new Harmony("extremeragdoll.patch").PatchAll();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            var attr = Settings.Instance; // Touch attribute settings so values persist to disk (FormatType=json).

            try
            {
                var builder = BaseSettingsBuilder.Create("ExtremeRagdoll_v1", "Extreme Ragdoll")!
                    .SetFolderName("ExtremeRagdoll")
                    .SetFormat("json")
                    .CreateGroup("General", group => group
                        .AddFloatingInteger(nameof(Settings.KnockbackMultiplier), "Knockback Multiplier",
                            1f, 10f,
                            new ProxyRef<float>(() => Settings.Instance.KnockbackMultiplier,
                                                v => Settings.Instance.KnockbackMultiplier = v),
                            b => b.SetHintText("Scales death shove strength.").SetRequireRestart(false))
                        .AddInteger(nameof(Settings.MaxExtraMagnitude), "Max Extra Magnitude",
                            0, 5000,
                            new ProxyRef<int>(() => Settings.Instance.MaxExtraMagnitude,
                                              v => Settings.Instance.MaxExtraMagnitude = v),
                            b => b.SetHintText("Hard cap for injected impulse.").SetRequireRestart(false))
                        .AddBool(nameof(Settings.DebugLogging), "Debug Logging",
                            new ProxyRef<bool>(() => Settings.Instance.DebugLogging,
                                               v => Settings.Instance.DebugLogging = v),
                            b => b.SetHintText("Print shove lines to rgl_log.").SetRequireRestart(false)));

                builder.BuildAsGlobal().Register();
            }
            catch
            {
                /* swallow to avoid hard fails if MCM missing */
            }

            Debug.Print($"[ExtremeRagdoll] MCM detected={ (attr != null) } id={attr?.Id}");

            if (_adapted) return;

            try
            {
                _adapted = ER_TOR_Adapter.TryEnableShockwaves();
            }
            catch
            {
                _adapted = false;
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            if (!_adapted)
            {
                try
                {
                    _adapted = ER_TOR_Adapter.TryEnableShockwaves();
                }
                catch
                {
                    _adapted = false;
                }
            }

            mission.AddMissionBehavior(new ER_DeathBlastBehavior()); // optional fallback
        }
    }
}
