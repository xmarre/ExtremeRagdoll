from pathlib import Path

source_path = Path("Source/SafeSubModule.cs")
text = source_path.read_text(encoding="utf-8")

start = text.index("    internal static class LocalizationBootstrap")
end = text.index("    internal static class CompatibilityState", start)
bootstrap = r'''    internal static class LocalizationBootstrap
    {
        private static bool _registered;

        internal static void EnsureRegistered()
        {
            if (_registered)
                return;

            try
            {
                string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                DirectoryInfo platformDirectory = string.IsNullOrEmpty(assemblyDirectory)
                    ? null
                    : new DirectoryInfo(assemblyDirectory);
                DirectoryInfo binDirectory = platformDirectory == null ? null : platformDirectory.Parent;
                DirectoryInfo moduleDirectory = binDirectory == null ? null : binDirectory.Parent;
                if (moduleDirectory == null)
                    throw new InvalidOperationException("Could not resolve the ExtremeRagdoll module root.");

                Assembly localizationAssembly = typeof(TextObject).Assembly;
                Type localizedTextManager = localizationAssembly.GetType(
                    "TaleWorlds.Localization.LocalizedTextManager", false);
                MethodInfo addLocalizationXml = localizedTextManager == null
                    ? null
                    : localizedTextManager.GetMethod(
                        "AddLocalizationXml",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(string) },
                        null);
                if (addLocalizationXml == null)
                {
                    throw new MissingMethodException(
                        "TaleWorlds.Localization.LocalizedTextManager",
                        "AddLocalizationXml");
                }

                // Bannerlord's initial localization discovery runs before submodule loading.
                // Merge this module's manifest into the live LanguageData registry before MCM
                // resolves its setting labels. LanguageData de-duplicates an existing path.
                addLocalizationXml.Invoke(null, new object[] { moduleDirectory.FullName });

                Type mbTextManager = localizationAssembly.GetType(
                    "TaleWorlds.Localization.MBTextManager", false);
                PropertyInfo activeLanguageProperty = mbTextManager == null
                    ? null
                    : mbTextManager.GetProperty(
                        "ActiveTextLanguage", BindingFlags.Public | BindingFlags.Static);
                MethodInfo changeLanguage = mbTextManager == null
                    ? null
                    : mbTextManager.GetMethod(
                        "ChangeLanguage",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(string) },
                        null);
                string activeLanguage = activeLanguageProperty == null
                    ? null
                    : activeLanguageProperty.GetValue(null, null) as string;

                // The active dictionary was populated before OnSubModuleLoad. Reload the same
                // non-English language once after registration. Later language changes retain
                // and use the native LanguageData path without any per-tick work.
                if (!string.IsNullOrEmpty(activeLanguage) &&
                    !string.Equals(activeLanguage, "English", StringComparison.OrdinalIgnoreCase))
                {
                    if (changeLanguage == null)
                    {
                        throw new MissingMethodException(
                            "TaleWorlds.Localization.MBTextManager", "ChangeLanguage");
                    }

                    object changed = changeLanguage.Invoke(null, new object[] { activeLanguage });
                    if (changed is bool && !(bool)changed)
                    {
                        throw new InvalidOperationException(
                            "Bannerlord rejected localization reload for " + activeLanguage + ".");
                    }
                }

                if (IsSimplifiedChinese(activeLanguage))
                {
                    string probe = new TextObject("{=ER_DisplayName}Extreme Ragdoll").ToString();
                    if (string.Equals(probe, "Extreme Ragdoll", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Simplified Chinese manifest was registered, but ER_DisplayName still resolved to its English fallback.");
                    }
                }

                _registered = true;
                SafeLog.Info(
                    "Localization manifest registered through Bannerlord LanguageData; activeLanguage=" +
                    (activeLanguage ?? "<unavailable>") + ".");
            }
            catch (TargetInvocationException ex)
            {
                SafeLog.Error(
                    "ExtremeRagdoll localization registration failed",
                    ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                SafeLog.Error("ExtremeRagdoll localization registration failed", ex);
            }
        }

        private static bool IsSimplifiedChinese(string language)
        {
            if (string.IsNullOrEmpty(language))
                return false;

            return language.IndexOf("简体中文", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   language.IndexOf("zh-HANS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   language.IndexOf("zh-CN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   language.IndexOf("ChineseSimplified", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

'''
text = text[:start] + bootstrap + text[end:]

old_constants = '''        private const float CorpseFinalizationTimeout = 30f;
        private const float CorpseActiveStateFallbackTimeout = 2f;
        private const float CorpseFinalizationPollInterval = 0.10f;'''
new_constants = '''        private const float CorpseFinalizationTimeout = 30f;
        private const float CorpseFinalizationFailureGrace = 5f;
        private const float CorpseActiveStateFallbackTimeout = 2f;
        private const float CorpseFinalizationPollInterval = 0.10f;'''
if "private const float CorpseFinalizationFailureGrace = 5f;" not in text:
    if text.count(old_constants) != 1:
        raise RuntimeError("Expected corpse finalization constant block once.")
    text = text.replace(old_constants, new_constants)

old_catch = '''                catch
                {
                    // Keep only this corpse retryable and throttle failures. One broken corpse must not starve later
                    // entries or crash the mission. The same 30-second ownership bound prevents a permanently failing
                    // wrapper/reflection path from remaining in the mission queue indefinitely.
                    PendingDeath pending = i >= 0 && i < _pending.Count ? _pending[i] : null;
                    if (pending != null && pending.PulseCount < 0)
                    {
                        if (now - pending.RagdollSeenAt > CorpseFinalizationTimeout)
                            _pending.RemoveAt(i);
                        else
                            pending.CurrentPulseBaseMagnitude = now + CorpseFinalizationPollInterval;
                    }
                }'''
new_catch = '''                catch
                {
                    // Keep only this corpse retryable and throttle failures. A transient
                    // EndRagdollAsCorpse failure after the Dismemberment Plus safety window gets
                    // a short bounded grace period; one broken native wrapper still cannot remain
                    // in the mission queue indefinitely or starve later corpses.
                    PendingDeath pending = i >= 0 && i < _pending.Count ? _pending[i] : null;
                    if (pending != null && pending.PulseCount < 0)
                    {
                        float failureDeadline =
                            CorpseFinalizationTimeout + CorpseFinalizationFailureGrace;
                        if (now - pending.RagdollSeenAt > failureDeadline)
                            _pending.RemoveAt(i);
                        else
                            pending.CurrentPulseBaseMagnitude = now + CorpseFinalizationPollInterval;
                    }
                }'''
if old_catch in text:
    text = text.replace(old_catch, new_catch)
elif new_catch not in text:
    raise RuntimeError("Recognized corpse finalization catch block was not found.")

source_path.write_text(text, encoding="utf-8")

changelog_path = Path("CHANGELOG.md")
changelog = changelog_path.read_text(encoding="utf-8")
anchor = "- Paired the delayed `EndRagdollAsCorpse` call after that safety window so corpses do not remain permanent physical obstacles.\n"
retry_line = "- Retries transient corpse-finalization failures for five seconds after the mesh-safety timeout before abandoning a broken native wrapper.\n"
if retry_line not in changelog:
    if changelog.count(anchor) != 1:
        raise RuntimeError("Expected v1.3.15 changelog anchor once.")
    changelog_path.write_text(changelog.replace(anchor, anchor + retry_line), encoding="utf-8")

final = source_path.read_text(encoding="utf-8")
for marker in (
    "private const float CorpseFinalizationFailureGrace = 5f;",
    "CorpseFinalizationTimeout + CorpseFinalizationFailureGrace",
    "internal static class LocalizationBootstrap",
):
    if marker not in final:
        raise RuntimeError("Missing refinement invariant: " + marker)
