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
            _ = Settings.Instance; // ensure discovery at main menu
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
