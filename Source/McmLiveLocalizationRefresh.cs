using System;
using System.Collections;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace ExtremeRagdoll.SafeRuntime
{
    /// <summary>
    /// Refreshes MCM's cached label view-models when the Mod Options category is selected.
    /// MCM v5.12.1 resolves setting hints on hover, but stores mod names, group names and
    /// setting names when its hidden options view-model is first created. A language change
    /// therefore updates hints while leaving those cached labels in the previous language.
    /// </summary>
    internal static class McmLiveLocalizationRefreshPatch
    {
        private const string HarmonyId = "xmarre.extremeragdoll.mcm-live-localization-refresh";
        private const string McmMixinTypeName = "MCM.UI.UIExtenderEx.OptionsVMMixin";
        private static readonly object Gate = new object();
        private static bool _installed;
        private static bool _assemblyHooked;

        internal static void EnsureInstalled()
        {
            lock (Gate)
            {
                if (_installed)
                    return;

                if (TryInstall())
                    return;

                if (!_assemblyHooked)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    _assemblyHooked = true;
                    SafeLog.Info("MCM localization refresh patch deferred until MCM.UI and Harmony are loaded.");
                }
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            lock (Gate)
            {
                if (_installed)
                    return;

                TryInstall();
            }
        }

        private static bool TryInstall()
        {
            try
            {
                Type mixinType = FindLoadedType(McmMixinTypeName);
                Type harmonyType = FindLoadedType("HarmonyLib.Harmony");
                Type harmonyMethodType = FindLoadedType("HarmonyLib.HarmonyMethod");
                if (mixinType == null || harmonyType == null || harmonyMethodType == null)
                    return false;

                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
                                           BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo selectedProperty = mixinType.GetProperty("ModOptionsSelected", Flags);
                MethodInfo setter = selectedProperty == null ? null : selectedProperty.GetSetMethod(true);
                if (setter == null)
                    throw new MissingMethodException(McmMixinTypeName, "set_ModOptionsSelected");

                ConstructorInfo harmonyConstructor = harmonyType.GetConstructor(new[] { typeof(string) });
                ConstructorInfo harmonyMethodConstructor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
                if (harmonyConstructor == null || harmonyMethodConstructor == null)
                    throw new MissingMethodException("Harmony reflection constructors required for the MCM refresh patch were not found.");

                MethodInfo postfixMethod = typeof(McmLiveLocalizationRefreshPatch).GetMethod(
                    "ModOptionsSelectedPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (postfixMethod == null)
                    throw new MissingMethodException(typeof(McmLiveLocalizationRefreshPatch).FullName, "ModOptionsSelectedPostfix");

                MethodInfo patchMethod = FindHarmonyPatchMethod(harmonyType, harmonyMethodType);
                if (patchMethod == null)
                    throw new MissingMethodException("HarmonyLib.Harmony", "Patch");

                object harmony = harmonyConstructor.Invoke(new object[] { HarmonyId });
                object postfix = harmonyMethodConstructor.Invoke(new object[] { postfixMethod });
                ParameterInfo[] parameters = patchMethod.GetParameters();
                object[] arguments = new object[parameters.Length];
                arguments[0] = setter;
                for (int i = 1; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType == harmonyMethodType &&
                        string.Equals(parameters[i].Name, "postfix", StringComparison.OrdinalIgnoreCase))
                    {
                        arguments[i] = postfix;
                    }
                    else if (parameters[i].HasDefaultValue)
                    {
                        arguments[i] = parameters[i].DefaultValue;
                    }
                    else
                    {
                        arguments[i] = null;
                    }
                }

                patchMethod.Invoke(harmony, arguments);
                _installed = true;
                if (_assemblyHooked)
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    _assemblyHooked = false;
                }

                SafeLog.Info("Installed event-driven MCM label localization refresh patch.");
                return true;
            }
            catch (TargetInvocationException ex)
            {
                SafeLog.Error("MCM label localization refresh patch failed", ex.InnerException ?? ex);
                return false;
            }
            catch (Exception ex)
            {
                SafeLog.Error("MCM label localization refresh patch failed", ex);
                return false;
            }
        }

        private static MethodInfo FindHarmonyPatchMethod(Type harmonyType, Type harmonyMethodType)
        {
            MethodInfo[] methods = harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "Patch", StringComparison.Ordinal))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 3 || !typeof(MethodBase).IsAssignableFrom(parameters[0].ParameterType))
                    continue;

                bool hasPostfix = false;
                for (int p = 1; p < parameters.Length; p++)
                {
                    if (parameters[p].ParameterType == harmonyMethodType &&
                        string.Equals(parameters[p].Name, "postfix", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPostfix = true;
                        break;
                    }
                }

                if (hasPostfix)
                    return method;
            }

            return null;
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

        private static void ModOptionsSelectedPostfix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length == 0 ||
                !(__args[0] is bool) || !(bool)__args[0])
            {
                return;
            }

            try
            {
                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo modOptionsProperty = __instance.GetType().GetProperty("ModOptions", Flags);
                object modOptions = modOptionsProperty == null ? null : modOptionsProperty.GetValue(__instance, null);
                if (modOptions == null)
                    return;

                MethodInfo refreshValues = modOptions.GetType().GetMethod(
                    "RefreshValues", Flags, null, Type.EmptyTypes, null);
                if (refreshValues == null)
                    throw new MissingMethodException(modOptions.GetType().FullName, "RefreshValues");

                refreshValues.Invoke(modOptions, null);

                PropertyInfo entriesProperty = modOptions.GetType().GetProperty("ModSettingsList", Flags);
                IEnumerable entries = entriesProperty == null ? null : entriesProperty.GetValue(modOptions, null) as IEnumerable;
                if (entries != null)
                {
                    foreach (object entry in entries)
                    {
                        if (entry != null)
                            NotifyPropertyChanged(entry, "DisplayName");
                    }
                }

                NotifyPropertyChanged(modOptions, "SelectedDisplayName");
                NotifyPropertyChanged(modOptions, "SelectedMod");
                SafeLog.Info("Refreshed MCM labels after Mod Options selection.");
            }
            catch (TargetInvocationException ex)
            {
                SafeLog.Error("MCM live label refresh failed", ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                SafeLog.Error("MCM live label refresh failed", ex);
            }
        }

        private static void NotifyPropertyChanged(object viewModel, string propertyName)
        {
            Type type = viewModel.GetType();
            while (type != null)
            {
                MethodInfo method = type.GetMethod(
                    "OnPropertyChanged",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    new[] { typeof(string) },
                    null);
                if (method != null)
                {
                    method.Invoke(viewModel, new object[] { propertyName });
                    return;
                }

                type = type.BaseType;
            }
        }
    }
}

namespace ExtremeRagdoll
{
    /// <summary>
    /// Runtime entry point that installs the MCM language-refresh compatibility patch before
    /// delegating to the maintained safe ragdoll submodule.
    /// </summary>
    public sealed class LocalizedSubModule : ExtremeRagdoll.SafeRuntime.SafeSubModule
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            ExtremeRagdoll.SafeRuntime.McmLiveLocalizationRefreshPatch.EnsureInstalled();
        }
    }
}
