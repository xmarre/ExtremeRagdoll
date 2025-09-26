using HarmonyLib;
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
            // Force MCM to discover our attribute settings at main menu
            _ = Settings.Instance;

            if (_adapted) return;
            try { _adapted = ER_TOR_Adapter.TryEnableShockwaves(); }
            catch { _adapted = false; }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            if (!_adapted)
                try { _adapted = ER_TOR_Adapter.TryEnableShockwaves(); }
                catch { _adapted = false; }

            mission.AddMissionBehavior(new ER_DeathBlastBehavior());
        }
    }
}
