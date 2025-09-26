using HarmonyLib;
using TaleWorlds.Core;
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
            Debug.Print("[ExtremeRagdoll] Harmony patch pass requested");
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            try
            {
                _ = Settings.Instance; // ensure discovery at main menu when available
            }
            catch
            {
                // MCM not installed: ignore
            }

            TryAdapt("menu");
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            TryAdapt("game_start");
        }

        private static void TryAdapt(string where)
        {
            if (_adapted) return;
            try
            {
                _adapted = ER_TOR_Adapter.TryEnableShockwaves();
                Debug.Print($"[ExtremeRagdoll] TOR adapter at {where}: adapted={_adapted}");
            }
            catch
            {
                _adapted = false;
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new ER_DeathBlastBehavior());
        }
    }
}
