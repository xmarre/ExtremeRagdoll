using HarmonyLib;
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
            _ = Settings.Instance; // lets MCM pick it up; earliest safe point per docs
            TaleWorlds.Core.InformationManager.DisplayMessage(
                new TaleWorlds.Core.InformationMessage($"[ExtremeRagdoll] Settings discovered: {Settings.Instance != null}"));
            TaleWorlds.Core.InformationManager.DisplayMessage(
                new TaleWorlds.Core.InformationMessage("[ExtremeRagdoll] Startup loading confirmed."));
            if (_adapted) return;
            try { _adapted = ER_TOR_Adapter.TryEnableShockwaves(); }
            catch { _adapted = false; }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new ER_DeathBlastBehavior());
        }
    }
}
