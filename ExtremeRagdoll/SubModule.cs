using System;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    public class SubModule : MBSubModuleBase
    {
        private static bool _adapted, _patched;

        protected override void OnSubModuleLoad()
        {
            ER_Log.Info($"Debug logging enabled: writing to {ER_Log.LogFilePath}");
            if (_patched) return;
            new Harmony("extremeragdoll.patch").PatchAll();
            _patched = true;
            ER_Log.Info("OnSubModuleLoad: PatchAll requested");
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
                ER_Log.Info($"TOR adapter at {where}: adapted={_adapted}");
            }
            catch (Exception ex)
            {
                _adapted = false;
                ER_Log.Error($"TOR adapter at {where} failed", ex);
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            mission.AddMissionBehavior(new ER_DeathBlastBehavior());
            if (!_adapted)
            {
                try
                {
                    _adapted = ER_TOR_Adapter.TryEnableShockwaves();
                }
                catch
                {
                }
            }
            ER_Log.Info("MissionBehavior added: ER_DeathBlastBehavior");
        }
    }
}
