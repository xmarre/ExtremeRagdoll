using System;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll
{
    /// <summary>
    /// Installs the live MCM localization patches only from stable Bannerlord lifecycle callbacks.
    /// v1.3.16 could subscribe before MCM.UI was available and then enter Harmony patching from an
    /// AppDomain.AssemblyLoad callback. Harmony itself loads dynamic assemblies while patching, so
    /// that route could re-enter the installer before its completed flag was set and terminate the
    /// process without a managed crash report. This entry point never enables that deferred callback.
    /// </summary>
    public sealed class CompatibleLocalizedSubModule : ExtremeRagdoll.SafeRuntime.SafeSubModule
    {
        private static readonly string[] RequiredRuntimeTypes =
        {
            "HarmonyLib.Harmony",
            "HarmonyLib.HarmonyMethod",
            "MCM.UI.GUI.ViewModels.SettingsPropertyVM",
            "MCM.UI.GUI.ViewModels.SettingsPropertyGroupVM",
            "MCM.UI.GUI.ViewModels.SettingsVM",
            "MCM.UI.GUI.ViewModels.SettingsEntryVM",
            "MCM.UI.GUI.ViewModels.ModOptionsVM"
        };

        protected override void OnSubModuleLoad()
        {
            ExtremeRagdoll.SafeRuntime.McmLiveLocalizationRefreshPatch.ResetDiagnosticLog();
            base.OnSubModuleLoad();
            TryInstallLocalizationPatches("OnSubModuleLoad");
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            TryInstallLocalizationPatches("OnBeforeInitialModuleScreenSetAsRoot");
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            // Final bounded retry before any combat mission behavior or native death patch is added.
            // This is a normal lifecycle call, not an assembly-load callback.
            TryInstallLocalizationPatches("OnMissionBehaviorInitialize");
            base.OnMissionBehaviorInitialize(mission);
        }

        private static void TryInstallLocalizationPatches(string stage)
        {
            try
            {
                for (int i = 0; i < RequiredRuntimeTypes.Length; i++)
                {
                    if (FindLoadedType(RequiredRuntimeTypes[i]) == null)
                    {
                        ExtremeRagdoll.SafeRuntime.SafeLog.Info(
                            "MCM localization patch not ready during " + stage +
                            "; missing " + RequiredRuntimeTypes[i] + ".");
                        return;
                    }
                }

                // All target types are already loaded, so EnsureInstalled completes synchronously
                // and cannot register its AssemblyLoad fallback.
                ExtremeRagdoll.SafeRuntime.McmLiveLocalizationRefreshPatch.EnsureInstalled();
            }
            catch (Exception ex)
            {
                // Localization compatibility must never prevent a battle from loading.
                ExtremeRagdoll.SafeRuntime.SafeLog.Error(
                    "MCM localization compatibility initialization failed during " + stage,
                    ex);
            }
        }

        private static Type FindLoadedType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null)
                    continue;
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch
                {
                }
            }
            return null;
        }
    }
}
