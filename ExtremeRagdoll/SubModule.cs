using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    public class SubModule : MBSubModuleBase
    {
        private static bool _adapted;

        protected override void OnSubModuleLoad()
        {
            _ = Settings.Instance;
            new Harmony("extremeragdoll.patch").PatchAll();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            InformationManager.DisplayMessage(
                new InformationMessage("[ExtremeRagdoll] MCM settings loaded"));
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
