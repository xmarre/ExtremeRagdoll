using System;
using System.IO;
using System.Reflection;
using TaleWorlds.Localization;

namespace ExtremeRagdoll.SafeRuntime
{
    /// <summary>
    /// Resolves Extreme Ragdoll's cached MCM labels at binding-read time.
    /// MCM stores mod titles, group headings and setting names in view-model fields, while hint text
    /// is resolved later on hover. Direct getter postfixes prevent those cached fields from
    /// continuing to return the language that was active when MCM first created its hidden view-model.
    /// </summary>
    internal static class McmLiveLocalizationRefreshPatch
    {
        private const string HarmonyId = "xmarre.extremeragdoll.mcm-live-localization-refresh";
        private const string SettingsId = "ExtremeRagdoll_Safe_v4";
        private const string SettingsPropertyVmTypeName = "MCM.UI.GUI.ViewModels.SettingsPropertyVM";
        private const string SettingsPropertyGroupVmTypeName = "MCM.UI.GUI.ViewModels.SettingsPropertyGroupVM";
        private const string SettingsVmTypeName = "MCM.UI.GUI.ViewModels.SettingsVM";
        private const string SettingsEntryVmTypeName = "MCM.UI.GUI.ViewModels.SettingsEntryVM";
        private const string ModOptionsVmTypeName = "MCM.UI.GUI.ViewModels.ModOptionsVM";
        private const string McmMixinTypeName = "MCM.UI.UIExtenderEx.OptionsVMMixin";

        private static readonly object Gate = new object();
        private static bool _installed;
        private static bool _assemblyHooked;
        private static bool _diagnosticReset;
        private static bool _settingNameTranslationObserved;
        private static bool _groupNameTranslationObserved;
        private static bool _displayNameTranslationObserved;

        internal static void ResetDiagnosticLog()
        {
            lock (Gate)
            {
                if (_diagnosticReset)
                    return;

                _diagnosticReset = true;
                try
                {
                    File.WriteAllText(GetDiagnosticPath(), string.Empty);
                }
                catch
                {
                }
            }
        }

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
                    Diagnostic("MCM getter patch deferred until MCM.UI and Harmony are loaded.");
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
                Type propertyVmType = FindLoadedType(SettingsPropertyVmTypeName);
                Type groupVmType = FindLoadedType(SettingsPropertyGroupVmTypeName);
                Type settingsVmType = FindLoadedType(SettingsVmTypeName);
                Type entryVmType = FindLoadedType(SettingsEntryVmTypeName);
                Type modOptionsVmType = FindLoadedType(ModOptionsVmTypeName);
                Type harmonyType = FindLoadedType("HarmonyLib.Harmony");
                Type harmonyMethodType = FindLoadedType("HarmonyLib.HarmonyMethod");
                if (propertyVmType == null || groupVmType == null || settingsVmType == null ||
                    entryVmType == null || modOptionsVmType == null ||
                    harmonyType == null || harmonyMethodType == null)
                {
                    return false;
                }

                ConstructorInfo harmonyConstructor = harmonyType.GetConstructor(new[] { typeof(string) });
                ConstructorInfo harmonyMethodConstructor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
                MethodInfo patchMethod = FindHarmonyPatchMethod(harmonyType, harmonyMethodType);
                MethodInfo getterPostfix = typeof(McmLiveLocalizationRefreshPatch).GetMethod(
                    "LocalizedCachedStringGetterPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo selectionPostfix = typeof(McmLiveLocalizationRefreshPatch).GetMethod(
                    "ModOptionsSelectedPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (harmonyConstructor == null || harmonyMethodConstructor == null || patchMethod == null ||
                    getterPostfix == null || selectionPostfix == null)
                {
                    throw new MissingMethodException("Harmony reflection members required for the MCM localization patches were not found.");
                }

                object harmony = harmonyConstructor.Invoke(new object[] { HarmonyId });
                PatchPostfix(
                    harmony,
                    patchMethod,
                    harmonyMethodType,
                    harmonyMethodConstructor,
                    RequireGetter(propertyVmType, "Name"),
                    getterPostfix);
                PatchPostfix(
                    harmony,
                    patchMethod,
                    harmonyMethodType,
                    harmonyMethodConstructor,
                    RequireGetter(groupVmType, "GroupNameDisplay"),
                    getterPostfix);
                PatchPostfix(
                    harmony,
                    patchMethod,
                    harmonyMethodType,
                    harmonyMethodConstructor,
                    RequireGetter(settingsVmType, "DisplayName"),
                    getterPostfix);
                PatchPostfix(
                    harmony,
                    patchMethod,
                    harmonyMethodType,
                    harmonyMethodConstructor,
                    RequireGetter(entryVmType, "DisplayName"),
                    getterPostfix);
                PatchPostfix(
                    harmony,
                    patchMethod,
                    harmonyMethodType,
                    harmonyMethodConstructor,
                    RequireGetter(modOptionsVmType, "SelectedDisplayName"),
                    getterPostfix);

                // Retain MCM's own refresh path as a secondary optimisation. Correctness no longer
                // depends on this setter firing because every visible cached label getter is patched.
                Type mixinType = FindLoadedType(McmMixinTypeName);
                if (mixinType != null)
                {
                    PropertyInfo selectedProperty = mixinType.GetProperty(
                        "ModOptionsSelected",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    MethodInfo setter = selectedProperty == null ? null : selectedProperty.GetSetMethod(true);
                    if (setter != null)
                    {
                        PatchPostfix(
                            harmony,
                            patchMethod,
                            harmonyMethodType,
                            harmonyMethodConstructor,
                            setter,
                            selectionPostfix);
                    }
                }

                _installed = true;
                if (_assemblyHooked)
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    _assemblyHooked = false;
                }

                Diagnostic(
                    "Installed direct MCM cached-label getter patches: SettingsPropertyVM.Name, " +
                    "SettingsPropertyGroupVM.GroupNameDisplay, SettingsVM.DisplayName, " +
                    "SettingsEntryVM.DisplayName, ModOptionsVM.SelectedDisplayName.");
                SafeLog.Info("Installed direct MCM cached-label localization getter patches.");
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception actual = ex.InnerException ?? ex;
                Diagnostic("MCM getter patch installation failed: " + actual);
                SafeLog.Error("MCM cached-label localization patch failed", actual);
                return false;
            }
            catch (Exception ex)
            {
                Diagnostic("MCM getter patch installation failed: " + ex);
                SafeLog.Error("MCM cached-label localization patch failed", ex);
                return false;
            }
        }

        private static MethodInfo RequireGetter(Type type, string propertyName)
        {
            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo getter = property == null ? null : property.GetGetMethod(true);
            if (getter == null)
                throw new MissingMethodException(type.FullName, "get_" + propertyName);
            return getter;
        }

        private static void PatchPostfix(
            object harmony,
            MethodInfo patchMethod,
            Type harmonyMethodType,
            ConstructorInfo harmonyMethodConstructor,
            MethodBase original,
            MethodInfo postfixMethod)
        {
            object postfix = harmonyMethodConstructor.Invoke(new object[] { postfixMethod });
            ParameterInfo[] parameters = patchMethod.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = original;
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

                for (int p = 1; p < parameters.Length; p++)
                {
                    if (parameters[p].ParameterType == harmonyMethodType &&
                        string.Equals(parameters[p].Name, "postfix", StringComparison.OrdinalIgnoreCase))
                    {
                        return method;
                    }
                }
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

        private static void LocalizedCachedStringGetterPostfix(
            object __instance,
            MethodBase __originalMethod,
            ref string __result)
        {
            if (__instance == null || __originalMethod == null || !IsExtremeRagdollViewModel(__instance))
                return;

            try
            {
                string declaringType = __originalMethod.DeclaringType == null
                    ? string.Empty
                    : __originalMethod.DeclaringType.FullName;
                string token = null;
                int diagnosticKind = 0;

                if (string.Equals(declaringType, SettingsPropertyVmTypeName, StringComparison.Ordinal) &&
                    string.Equals(__originalMethod.Name, "get_Name", StringComparison.Ordinal))
                {
                    object definition = GetPropertyValue(__instance, "SettingPropertyDefinition");
                    token = GetStringProperty(definition, "DisplayName");
                    diagnosticKind = 1;
                }
                else if (string.Equals(declaringType, SettingsPropertyGroupVmTypeName, StringComparison.Ordinal) &&
                         string.Equals(__originalMethod.Name, "get_GroupNameDisplay", StringComparison.Ordinal))
                {
                    object definition = GetPropertyValue(__instance, "SettingPropertyGroupDefinition");
                    token = GetStringProperty(definition, "GroupName");
                    diagnosticKind = 2;
                }
                else if ((string.Equals(declaringType, SettingsVmTypeName, StringComparison.Ordinal) &&
                          string.Equals(__originalMethod.Name, "get_DisplayName", StringComparison.Ordinal)) ||
                         (string.Equals(declaringType, SettingsEntryVmTypeName, StringComparison.Ordinal) &&
                          string.Equals(__originalMethod.Name, "get_DisplayName", StringComparison.Ordinal)) ||
                         (string.Equals(declaringType, ModOptionsVmTypeName, StringComparison.Ordinal) &&
                          string.Equals(__originalMethod.Name, "get_SelectedDisplayName", StringComparison.Ordinal)))
                {
                    token = "{=ER_DisplayName}Extreme Ragdoll";
                    diagnosticKind = 3;
                }

                if (string.IsNullOrEmpty(token))
                    return;

                string translated = new TextObject(token).ToString();
                if (string.IsNullOrEmpty(translated))
                    return;

                string previous = __result;
                if (diagnosticKind == 2 && !string.IsNullOrEmpty(previous))
                {
                    string fallback = ExtractFallback(token);
                    if (!string.IsNullOrEmpty(fallback) &&
                        previous.IndexOf(fallback, StringComparison.Ordinal) >= 0)
                    {
                        __result = previous.Replace(fallback, translated);
                    }
                    else
                    {
                        __result = translated;
                    }
                }
                else
                {
                    __result = translated;
                }

                if (!string.Equals(previous, __result, StringComparison.Ordinal))
                    RecordFirstObservedTranslation(diagnosticKind, previous, __result);
            }
            catch (Exception ex)
            {
                Diagnostic("MCM cached-label getter translation failed: " + ex);
            }
        }

        private static bool IsExtremeRagdollViewModel(object instance)
        {
            string typeName = instance.GetType().FullName;
            object settingsVm;
            if (string.Equals(typeName, SettingsVmTypeName, StringComparison.Ordinal))
            {
                settingsVm = instance;
            }
            else if (string.Equals(typeName, ModOptionsVmTypeName, StringComparison.Ordinal))
            {
                settingsVm = GetPropertyValue(instance, "SelectedMod");
            }
            else
            {
                settingsVm = GetPropertyValue(instance, "SettingsVM");
            }

            if (settingsVm == null)
                return false;

            object definition = GetPropertyValue(settingsVm, "SettingsDefinition");
            string id = GetStringProperty(definition, "SettingsId");
            if (string.IsNullOrEmpty(id))
            {
                object settingsInstance = GetPropertyValue(settingsVm, "SettingsInstance");
                id = GetStringProperty(settingsInstance, "Id");
            }

            return string.Equals(id, SettingsId, StringComparison.Ordinal);
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            if (instance == null)
                return null;

            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static string GetStringProperty(object instance, string propertyName)
        {
            object value = GetPropertyValue(instance, propertyName);
            return value as string;
        }

        private static string ExtractFallback(string token)
        {
            if (string.IsNullOrEmpty(token) || token[0] != '{')
                return token;

            int end = token.IndexOf('}');
            return end >= 0 && end + 1 < token.Length ? token.Substring(end + 1) : token;
        }

        private static void RecordFirstObservedTranslation(int kind, string previous, string translated)
        {
            lock (Gate)
            {
                bool shouldWrite = false;
                if (kind == 1 && !_settingNameTranslationObserved)
                {
                    _settingNameTranslationObserved = true;
                    shouldWrite = true;
                }
                else if (kind == 2 && !_groupNameTranslationObserved)
                {
                    _groupNameTranslationObserved = true;
                    shouldWrite = true;
                }
                else if (kind == 3 && !_displayNameTranslationObserved)
                {
                    _displayNameTranslationObserved = true;
                    shouldWrite = true;
                }

                if (shouldWrite)
                {
                    Diagnostic(
                        "Observed live cached-label translation kind=" + kind +
                        "; previous=" + (previous ?? "<null>") +
                        "; translated=" + translated + ".");
                }
            }
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
                object modOptions = GetPropertyValue(__instance, "ModOptions");
                if (modOptions == null)
                    return;

                MethodInfo refreshValues = modOptions.GetType().GetMethod(
                    "RefreshValues",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (refreshValues != null)
                    refreshValues.Invoke(modOptions, null);
            }
            catch (Exception ex)
            {
                Diagnostic("Secondary MCM view-model refresh failed: " + ex);
            }
        }

        private static string GetDiagnosticPath()
        {
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(directory ?? ".", "ExtremeRagdoll.Localization.log");
        }

        private static void Diagnostic(string message)
        {
            try
            {
                File.AppendAllText(
                    GetDiagnosticPath(),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}

namespace ExtremeRagdoll
{
    /// <summary>
    /// Runtime entry point that installs the MCM localization compatibility patches before
    /// delegating to the maintained safe ragdoll submodule.
    /// </summary>
    public sealed class LocalizedSubModule : ExtremeRagdoll.SafeRuntime.SafeSubModule
    {
        protected override void OnSubModuleLoad()
        {
            ExtremeRagdoll.SafeRuntime.McmLiveLocalizationRefreshPatch.ResetDiagnosticLog();
            base.OnSubModuleLoad();
            ExtremeRagdoll.SafeRuntime.McmLiveLocalizationRefreshPatch.EnsureInstalled();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            ExtremeRagdoll.SafeRuntime.McmLiveLocalizationRefreshPatch.EnsureInstalled();
        }
    }
}
