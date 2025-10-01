using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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
                Settings.Instance.DebugLogging = true;
                ER_Log.Info("Debug logging forced ON at menu");
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

        private static bool IsTorModuleLoaded()
        {
            try
            {
                var helperType = AccessTools.TypeByName("TaleWorlds.ModuleManager.ModuleHelper")
                                 ?? AccessTools.TypeByName("TaleWorlds.MountAndBlade.ModuleHelper");
                if (helperType == null)
                    return false;
                var getModules = helperType.GetMethod("GetModules", BindingFlags.Static | BindingFlags.Public);
                if (getModules == null)
                    return false;
                if (!(getModules.Invoke(null, null) is IEnumerable modules))
                    return false;
                foreach (var module in modules)
                {
                    if (module == null)
                        continue;
                    var nameProp = module.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var rawName = nameProp?.GetValue(module)?.ToString() ?? string.Empty;
                    var normalized = new string(rawName.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
                    if (normalized.Contains("theoldrealms") || normalized.Contains("oldrealms"))
                        return true;
                }
            }
            catch
            {
                // ignored: treat as not loaded
            }
            return false;
        }

        private static void TryAdapt(string where)
        {
            if (_adapted) return;
            try
            {
                bool changed = ER_TOR_Adapter.TryEnableShockwaves();
                bool torLoaded = changed || IsTorModuleLoaded();
                _adapted = torLoaded;
                ER_Log.Info($"TOR adapter at {where}: adapted={_adapted} (torLoaded={torLoaded}, changed={changed})");
            }
            catch (Exception ex)
            {
                _adapted = false;
                ER_Log.Error($"TOR adapter at {where} failed", ex);
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            ER_Amplify_RegisterBlowPatch._pending.Clear();
            mission.AddMissionBehavior(new ER_DeathBlastBehavior());
            if (!_adapted)
            {
                try
                {
                    bool changed = ER_TOR_Adapter.TryEnableShockwaves();
                    bool torLoaded = changed || IsTorModuleLoaded();
                    _adapted = torLoaded;
                    ER_Log.Info($"TOR adapter at mission start: adapted={_adapted} (torLoaded={torLoaded}, changed={changed})");
                }
                catch
                {
                }
            }
            ER_Log.Info("MissionBehavior added: ER_DeathBlastBehavior");
        }
        // No OnMissionEnded override needed; pending gets cleared on behavior removal and mission start.
    }
}
