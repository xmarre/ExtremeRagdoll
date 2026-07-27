using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.Localization;
using MCM.Abstractions.Base.Global;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;

namespace ExtremeRagdoll.SafeRuntime
{
    public class SafeSubModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            SafeLog.Reset();
            LocalizationBootstrap.EnsureRegistered();
            SafeLog.Info("Safe rewrite loaded; native death patches are deferred until a real combat mission is initialized.");
            SafeLog.Info("Non-combat character tableaus are excluded before both mission behavior attachment and Harmony death-patch installation.");
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            LocalizationBootstrap.EnsureRegistered();
            SafeLog.Info("Safe runtime menu initialization completed.");
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            if (mission == null)
                return;

            // Party/inventory/character tableaus are implemented as non-combat missions too.
            // Never attach the runtime there: their preview agents can use dead/disabled states
            // and would otherwise be mistaken for real battlefield corpses by the fallback monitor.
            string combatType;
            try { combatType = mission.CombatType.ToString(); }
            catch { return; }
            if (string.Equals(combatType, "NoCombat", StringComparison.OrdinalIgnoreCase))
            {
                SafeLog.Info("Skipped non-combat/tableau mission; no ragdoll behavior or death patch installed.");
                return;
            }

            // Explicit combat-mission boundary for static death-route state. Reusing the existing
            // ForgetDeathRoute API with null avoids adding a new cross-assembly runtime member.
            ClothForceBridge.ForgetDeathRoute(null);
            ClothForceBridge.EnsureNativeDeathPatch();
            bool dismembermentPlusCompatibility = CompatibilityState.IsDismembermentPlusLoaded;
            mission.AddMissionBehavior(new SafeRagdollBehavior(dismembermentPlusCompatibility));
            SafeLog.Info(
                "Mission behavior added and native death patch installed for combat only: SafeRagdollBehavior; " +
                "DismembermentPlusCompatibility=" + dismembermentPlusCompatibility + ".");
        }
    }


    internal static class LocalizationBootstrap
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

    internal static class CompatibilityState
    {
        private static readonly bool DismembermentPlusLoaded = DetectDismembermentPlus();

        internal static bool IsDismembermentPlusLoaded
        {
            get { return DismembermentPlusLoaded; }
        }

        private static bool DetectDismembermentPlus()
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (assembly == null)
                        continue;

                    string name;
                    try { name = assembly.GetName().Name; }
                    catch { continue; }

                    if (string.Equals(name, "DismembermentPlus", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(name) &&
                         name.StartsWith("DismembermentPlus.", StringComparison.OrdinalIgnoreCase)))
                    {
                        SafeLog.Info("Detected Dismemberment Plus; enabled corpse-mesh compatibility safeguards.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog.Error("Dismemberment Plus detection failed; retaining normal Extreme Ragdoll behavior", ex);
            }

            return false;
        }
    }

    internal static class SafeLog
    {
        private static readonly object Gate = new object();
        private static readonly string LogPath = ResolveLogPath();

        private static string ResolveLogPath()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return System.IO.Path.Combine(dir ?? ".", "ExtremeRagdoll.Safe.log");
            }
            catch
            {
                return "ExtremeRagdoll.Safe.log";
            }
        }

        internal static void Reset()
        {
            if (!SafeSettings.DebugLogging)
                return;

            try
            {
                lock (Gate)
                    File.WriteAllText(LogPath, string.Empty);
            }
            catch { }
        }

        internal static void Info(string message)
        {
            if (!SafeSettings.DebugLogging)
                return;

            try
            {
                lock (Gate)
                {
                    File.AppendAllText(
                        LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [INFO] " + message + Environment.NewLine);
                }
            }
            catch { }
        }

        internal static void Error(string message, Exception ex)
        {
            Info(message + (ex == null ? string.Empty : " :: " + ex));
        }
    }

    public sealed class Settings : AttributeGlobalSettings<Settings>
    {
        public override string Id { get { return "ExtremeRagdoll_Safe_v4"; } }
        public override string DisplayName { get { return new TextObject("{=ER_DisplayName}Extreme Ragdoll").ToString(); } }
        public override string FolderName { get { return "ExtremeRagdoll"; } }
        public override string FormatType { get { return "json"; } }

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_OverallStrength_Name}Overall Effect Strength", 0, false, "{=ER_OverallStrength_Hint}Master multiplier used to derive the amplified native killing-blow impulse. Default12. Enter any non-negative finite number.")]
        public string OverallStrength { get; set; } = "12";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_MinimumForce_Name}Minimum Death Force", 1, false, "{=ER_MinimumForce_Hint}Guaranteed requested-force floor used before conversion to the native killing-blow impulse. Default 150000.")]
        public string MinimumForce { get; set; } = "150000";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_UpwardLift_Name}Upward Lift", 2, false, "{=ER_UpwardLift_Hint}Uncapped upward component added to the launch direction. Default 0.25.")]
        public string UpwardLift { get; set; } = "0.25";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_DamageInfluence_Name}Damage Influence", 3, false, "{=ER_DamageInfluence_Hint}Uncapped scaling from killing-blow damage. Set 0 for uniform launches. Default 1.25.")]
        public string DamageInfluence { get; set; } = "1.25";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_ImpactSpin_Name}Impact Spin", 4, false, "{=ER_ImpactSpin_Hint}Adds a lateral bias to the single native death-impulse direction. Default 0.")]
        public string ImpactSpin { get; set; } = "0";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_RagdollHandoffDelay_Name}Death Animation Time Before Ragdoll (seconds)", 5, false, "{=ER_RagdollHandoffDelay_Hint}Original death-animation time before accelerated ragdoll handoff. The launch impulse is already part of the native death event. Default 0.005 seconds.")]
        public string RagdollHandoffDelay { get; set; } = "0.005";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_MomentumCarryover_Name}Movement Momentum Carryover", 6, false, "{=ER_MomentumCarryover_Hint}Carries the victim's movement into the first death-force pulse once. Opposing longitudinal momentum is discarded so movement cannot reverse the killing blow. Default 1.0.")]
        public string MomentumCarryover { get; set; } = "1.0";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_MaximumForce_Name}Maximum Death Force (0 = Unlimited)", 7, false, "{=ER_MaximumForce_Hint}Optional user ceiling. Leave 0 for no mod-imposed force cap.")]
        public string MaximumForce { get; set; } = "0";

        [SettingPropertyGroup("{=ER_Group_MainControls}Main Controls", GroupOrder = 0)]
        [SettingPropertyText("{=ER_MountCollisionKillStrength_Name}Mount Collision Kill Strength", 8, false, "{=ER_MountCollisionKillStrength_Hint}Extra ExtremeRagdoll contribution for lethal horse-body collisions after Bannerlord's native charge/shove impulse. 0 = native Bannerlord mount shove only; 1 = full normal fallback contribution. Default 0.10 based on runtime testing: keeps mount kills enhanced while avoiding the cloth-separation threshold seen with larger stacked post-ragdoll translation.")]
        public string MountCollisionKillStrength { get; set; } = "0.10";

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyBool("{=ER_PushOnDamage_Name}Enable Push On Damage", Order = 20, RequireRestart = false,
            HintText = "{=ER_PushOnDamage_Hint}Applies a visible native KnockBack reaction plus a short directional acceleration burst to surviving agents whenever they take damage. Disabled by default. Agents already in a natural ragdoll use the same mapped central-body ragdoll-force route.")]
        public bool PushOnDamage { get; set; } = false;

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyText("{=ER_DamagePushStrength_Name}Damage Push Strength", 21, false, "{=ER_DamagePushStrength_Hint}Uncapped master multiplier for nonlethal damage pushes. Default 1.0.")]
        public string DamagePushStrength { get; set; } = "1.0";

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyText("{=ER_DamagePushBase_Name}Base Damage Push", 22, false, "{=ER_DamagePushBase_Hint}Base acceleration applied on every damaging nonlethal hit. Default 1.5. Enter any non-negative finite number.")]
        public string DamagePushBase { get; set; } = "1.5";

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyText("{=ER_DamagePushPerDamage_Name}Additional Push Per Damage", 23, false, "{=ER_DamagePushPerDamage_Hint}Damage scaling. This amount is added for every point of damage before the master multiplier. Default 0.04. Set 0 for fixed-strength pushes.")]
        public string DamagePushPerDamage { get; set; } = "0.04";

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyText("{=ER_DamagePushUpwardLift_Name}Damage Push Upward Lift", 24, false, "{=ER_DamagePushUpwardLift_Hint}Adds an upward component to nonlethal pushes. Default 0.12.")]
        public string DamagePushUpwardLift { get; set; } = "0.12";

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyBool("{=ER_KnockdownOnDamage_Name}Enable Knockdown Below Health Threshold", Order = 25, RequireRestart = false,
            HintText = "{=ER_KnockdownOnDamage_Hint}Replaces the normal KnockBack reaction with Bannerlord's native KnockDown reaction after a surviving hit leaves the agent at or below the configured health percentage. Enabled by default.")]
        public bool KnockdownOnDamage { get; set; } = true;

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyText("{=ER_KnockdownHealthThreshold_Name}Knockdown Health Threshold (%)", 26, false, "{=ER_KnockdownHealthThreshold_Hint}Post-hit health percentage at or below which the native knockdown is requested. Default 50. Values above 100 make every surviving hit eligible; 0 effectively disables surviving-agent knockdowns.")]
        public string KnockdownHealthThreshold { get; set; } = "50";

        [SettingPropertyGroup("{=ER_Group_OnDamageMode}On Damage Mode", GroupOrder = 1)]
        [SettingPropertyBool("{=ER_RequireThresholdForDamagePush_Name}Only Push When Knockdown Threshold Is Met", Order = 27, RequireRestart = false,
            HintText = "{=ER_RequireThresholdForDamagePush_Hint}When enabled, the normal on-damage push is withheld unless the actual post-hit health is at or below the knockdown threshold. This creates a combined push-and-knockdown trigger instead of pushing on every hit.")]
        public bool RequireThresholdForDamagePush { get; set; } = false;

        [SettingPropertyGroup("{=ER_Group_Advanced}Advanced", GroupOrder = 2)]
        [SettingPropertyText("{=ER_PulseCount_Name}Force Pulse Count", 100, false, "{=ER_PulseCount_Hint}Legacy pulse count folded into the strength of the single native killing-blow impulse for normal deaths. No post-death translation pulses are applied on the native path. Default 2.")]
        public string PulseCount { get; set; } = "2";

        [SettingPropertyGroup("{=ER_Group_Advanced}Advanced", GroupOrder = 2)]
        [SettingPropertyText("{=ER_PulseInterval_Name}Pulse Interval (seconds)", 101, false, "{=ER_PulseInterval_Hint}Retained for backward-compatible settings. Normal death launches now use one native death impulse, so this no longer spaces post-death translation pulses.")]
        public string PulseInterval { get; set; } = "0.060";

        [SettingPropertyGroup("{=ER_Group_Advanced}Advanced", GroupOrder = 2)]
        [SettingPropertyText("{=ER_PulseDecay_Name}Pulse Decay", 102, false, "{=ER_PulseDecay_Hint}Multiplier used when folding the configured legacy pulse sequence into the equivalent single native killing-blow impulse. Default 0.85.")]
        public string PulseDecay { get; set; } = "0.85";

        [SettingPropertyGroup("{=ER_Group_Advanced}Advanced", GroupOrder = 2)]
        [SettingPropertyText("{=ER_EngineImpulseInfluence_Name}Engine Killing-Blow Direction Influence", 103, false, "{=ER_EngineImpulseInfluence_Hint}Retained for configuration compatibility. The normal death launch now originates directly from the killing Blow inside Bannerlord's native death pipeline, so no separate post-death KillingBlow-direction blend is required.")]
        public string EngineImpulseInfluence { get; set; } = "1.0";

        [SettingPropertyGroup("{=ER_Group_Advanced}Advanced", GroupOrder = 2)]
        [SettingPropertyText("{=ER_MaxLinearVelocity_Name}Linear Velocity Limit (0 = Disabled)", 104, false, "{=ER_MaxLinearVelocity_Hint}Optional engine velocity limit. Leave 0 for no mod-imposed limit.")]
        public string MaxLinearVelocity { get; set; } = "0";

        [SettingPropertyGroup("{=ER_Group_Advanced}Advanced", GroupOrder = 2)]
        [SettingPropertyText("{=ER_MaxAngularVelocity_Name}Angular Velocity Limit (0 = Disabled)", 105, false, "{=ER_MaxAngularVelocity_Hint}Optional engine spin limit. Leave 0 for no mod-imposed limit.")]
        public string MaxAngularVelocity { get; set; } = "0";

        [SettingPropertyGroup("{=ER_Group_Advanced}Advanced", GroupOrder = 2)]
        [SettingPropertyText("{=ER_DeliveredForceCeiling_Name}Delivered Force Ceiling Per Pulse (0 = Unlimited)", 106, false, "{=ER_DeliveredForceCeiling_Hint}Legacy per-pulse delivery ceiling used when folding the configured pulse sequence into one native killing-blow impulse. Default 60000. Set 0 for no equivalent-delivery ceiling.")]
        public string DeliveredForceCeiling { get; set; } = "60000";


        [SettingPropertyGroup("{=ER_Group_Diagnostics}Diagnostics", GroupOrder = 3)]
        [SettingPropertyBool("{=ER_DebugLogging_Name}Debug Logging", Order = 200, RequireRestart = false,
            HintText = "{=ER_DebugLogging_Hint}Writes hit, kill, ragdoll transition and pulse events to ExtremeRagdoll.Safe.log.")]
        public bool DebugLogging { get; set; } = false;
    }

    internal static class SafeSettings
    {
        private static Settings Current
        {
            get
            {
                try { return Settings.Instance; }
                catch { return null; }
            }
        }

        internal static float OverallStrength { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.OverallStrength, 12.0f); } }
        internal static float UpwardLift { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.UpwardLift, 0.25f); } }
        internal static float DamageInfluence { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.DamageInfluence, 1.25f); } }
        internal static float ImpactSpin { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.ImpactSpin, 0f); } }
        internal static float MinimumForce { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.MinimumForce, 150000f); } }
        internal static float MaximumForce { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.MaximumForce, 0f); } }
        internal static float MountCollisionKillStrength { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.MountCollisionKillStrength, 0.10f); } }
        internal static float RagdollHandoffDelay { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.RagdollHandoffDelay, 0.005f); } }
        internal static float MomentumCarryover { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.MomentumCarryover, 1.0f); } }
        internal static float EngineImpulseInfluence { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.EngineImpulseInfluence, 1.0f); } }
        internal static bool PushOnDamage { get { Settings s = Current; return s != null && s.PushOnDamage; } }
        internal static float DamagePushStrength { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.DamagePushStrength, 1.0f); } }
        internal static float DamagePushBase { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.DamagePushBase, 1.5f); } }
        internal static float DamagePushPerDamage { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.DamagePushPerDamage, 0.04f); } }
        internal static float DamagePushUpwardLift { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.DamagePushUpwardLift, 0.12f); } }
        internal static bool KnockdownOnDamage { get { Settings s = Current; return s == null || s.KnockdownOnDamage; } }
        internal static float KnockdownHealthThreshold { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.KnockdownHealthThreshold, 50f); } }
        internal static bool RequireThresholdForDamagePush { get { Settings s = Current; return s != null && s.RequireThresholdForDamagePush; } }
        internal static int PulseCount
        {
            get
            {
                Settings s = Current;
                return ParsePositiveInt(s == null ? null : s.PulseCount, 2);
            }
        }
        internal static float PulseInterval { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.PulseInterval, 0.060f); } }
        internal static float PulseDecay { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.PulseDecay, 0.85f); } }
        internal static float MaxLinearVelocity { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.MaxLinearVelocity, 0f); } }
        internal static float MaxAngularVelocity { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.MaxAngularVelocity, 0f); } }
        internal static float DeliveredForceCeiling { get { Settings s = Current; return ParseNonNegative(s == null ? null : s.DeliveredForceCeiling, 60000f); } }
        internal static bool DebugLogging { get { Settings s = Current; return s != null && s.DebugLogging; } }

        private static float ParseNonNegative(string text, float fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            float value;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return fallback;

            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
                return fallback;
            return value;
        }

        private static int ParsePositiveInt(string text, int fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
                return fallback;
            return value < 1 ? fallback : value;
        }
    }

    public sealed class SafeRagdollBehavior : MissionBehavior
    {
        private const float PendingLifetime = 12f;
        private const float RetryDelay = 0.016f;
        private const float RecentImpactLifetime = 0.75f;
        private const float DamagePushDuplicateWindow = 0.030f;
        private const float KnockdownDuplicateWindow = 0.75f;
        private const float DamageRagdollForceUnitsPerAcceleration = 5000f;
        private const float DamagePushQueueLifetime = 0.45f;
        private const float DamagePushPulseInterval = 0.025f;
        private const float DamagePushPulseDecay = 0.84f;
        private const int DamagePushPulseCount = 5;
        private const float MaxCentralBoneForcePerTick = 15000f;
        private const float CentralChunkInterval = 0.016f;
        private const float RemainingForceTinySq = 0.01f;
        // Start fallback finalization at two seconds and permit retries only until
        // the absolute three-second corpse-collision ceiling.
        private const float CorpseFinalizationHardDeadline = 3f;
        private const float CorpseActiveStateFallbackTimeout = 2f;
        private const float CorpseFinalizationPollInterval = 0.10f;
        private const float CorpseFinalizationMainLoopDeferral = 3600f;
        private const int CorpseFinalizerPendingPulseIndex = -1;
        private const int CorpseFinalizerInvokedPulseIndex = -2;
        private const int VisualResetPassCount = 4;
        private const float VisualResetInterval = 0.016f;

        private sealed class PendingDeath
        {
            internal Agent Agent;
            internal int AgentIndex;
            internal Vec3 RawImpactDirection;
            internal Vec3 Direction;
            internal Vec3 VictimMomentum;
            internal Vec3 EngineImpulse;
            internal bool HasEngineImpulse;
            internal string DirectionSource;
            internal int Damage;
            internal float ForceMagnitude;
            internal float CapturedAt;
            internal float RagdollSeenAt = -1f;
            internal float NextPulseAt;
            internal int PulseIndex;
            internal int PulseCount;
            internal float PulseInterval;
            internal float PulseDecay;
            internal sbyte HitBone;
            internal string Source;
            internal string KillKind;
            internal bool MissingSkeletonLogged;
            internal bool WaitingForRagdollLogged;
            internal bool RagdollStartRequested;
            internal bool DeathConfirmed;
            internal bool CompletionGateArmed;
            internal Vec3 RemainingPulseForce;
            internal float CurrentPulseBaseMagnitude;
            internal float CurrentPulseSpinMagnitude;
            internal int CurrentPulseChunkCount;
        }

        private sealed class PendingKnockdown
        {
            internal Agent Agent;
            internal Blow Blow;
            internal int Damage;
            internal float PostHealthPercent;
            internal float RequestedAt;
            internal string Source;
            internal Vec3 Direction;
            internal string DirectionSource;
            internal int HitBone;
        }

        private sealed class PendingDamagePush
        {
            internal Agent Agent;
            internal Blow ReactionBlow;
            internal Vec3 Direction;
            internal float Magnitude;
            internal int Damage;
            internal float RequestedAt;
            internal float NextPulseAt;
            internal int PulseIndex;
            internal string Source;
            internal string Kind;
            internal string DirectionSource;
            internal int HitBone;
            internal bool NativeReactionAttempted;
            internal bool NativeReactionApplied;
        }

        private sealed class PendingVisualResync
        {
            internal Agent Agent;
            internal float NextPassAt;
            internal int PassesRemaining;
        }

        private sealed class RecentImpact
        {
            internal Agent Affector;
            internal Vec3 Direction;
            internal Vec3 VictimMomentum;
            internal int Damage;
            internal int HitBone;
            internal string KillKind;
            internal string DirectionSource;
            internal Blow OriginalBlow;
            internal bool HasOriginalBlow;
            internal float CapturedAt;
        }

        private readonly bool _dismembermentPlusCompatibility;
        private readonly List<PendingDeath> _pending = new List<PendingDeath>(64);
        private readonly List<PendingKnockdown> _pendingKnockdowns = new List<PendingKnockdown>(32);
        private readonly List<PendingDamagePush> _pendingDamagePushes = new List<PendingDamagePush>(64);
        private readonly List<PendingVisualResync> _pendingVisualResyncs = new List<PendingVisualResync>(64);
        private readonly HashSet<Agent> _tracked = new HashSet<Agent>();
        private readonly Dictionary<Agent, RecentImpact> _recentImpacts = new Dictionary<Agent, RecentImpact>();
        private readonly Dictionary<Agent, float> _lastHealth = new Dictionary<Agent, float>();
        private readonly HashSet<Agent> _healthSubscribed = new HashSet<Agent>();
        private readonly Dictionary<Agent, float> _lastDamagePushAt = new Dictionary<Agent, float>();
        private readonly Dictionary<Agent, float> _lastKnockdownAt = new Dictionary<Agent, float>();
        private readonly Dictionary<Agent, float> _lastKnockdownAppliedAt = new Dictionary<Agent, float>();

        private static readonly object BindGate = new object();
        private static bool _bound;
        private static MethodInfo _applyForceOnRagdoll;
        private static MethodInfo _setVelocityLimitsOnRagdoll;
        private static MethodInfo _startRagdollAsCorpse;
        private static MethodInfo _addAcceleration;
        private static MethodInfo _handleBlowAux;
        private static MethodInfo _getRealBoneIndex;

        private bool _runtimeInitialized;
        private bool _firstRegisterBlowLogged;
        private int _registerBlowCount;
        private int _lethalHitCount;
        private int _earlyRemovedCount;
        private int _removedCount;
        private int _completedDeaths;
        private int _forceChunksProcessed;
        private int _fallbackPollTimeBin;
        private int _healthTransitionDeaths;
        private int _stateMonitorDeaths;
        private int _damagePushes;
        private int _damageAccelerationPushes;
        private int _damageRagdollPushes;
        private int _damagePushesQueued;
        private int _damagePushPulseApplications;
        private int _damagePushesCompleted;
        private int _nativeKnockbacksApplied;
        private int _knockdownRequests;
        private int _knockdownsApplied;
        private int _knockdownsSkipped;
        private int _visualResyncPasses;

        public SafeRagdollBehavior(bool dismembermentPlusCompatibility)
        {
            _dismembermentPlusCompatibility = dismembermentPlusCompatibility;
        }

        public override void OnCreated()
        {
            EnsureRuntimeInitialized("OnCreated");
        }

        public override void OnBehaviorInitialize()
        {
            EnsureRuntimeInitialized("OnBehaviorInitialize");
        }

        private void EnsureRuntimeInitialized(string source)
        {
            if (_runtimeInitialized)
                return;

            _runtimeInitialized = true;
            _pending.Clear();
            _pendingKnockdowns.Clear();
            _pendingDamagePushes.Clear();
            _pendingVisualResyncs.Clear();
            _tracked.Clear();
            _recentImpacts.Clear();
            _lastHealth.Clear();
            _healthSubscribed.Clear();
            _lastDamagePushAt.Clear();
            _lastKnockdownAt.Clear();
            _lastKnockdownAppliedAt.Clear();
            BindAgentForceApi();
            TrackExistingAgents();

            SafeLog.Info(
                "SafeRagdollBehavior initialized via " + source + "; mission health-transition and state-monitor fallback active; " +
                "DismembermentPlusCompatibility=" + _dismembermentPlusCompatibility + "; " +
                (_applyForceOnRagdoll == null
                    ? "ApplyForceOnRagdoll route unavailable."
                    : "ApplyForceOnRagdoll route bound successfully."));
            SafeLog.Info(
                "Effective settings: strength=" + SafeSettings.OverallStrength.ToString("0.00") +
                " lift=" + SafeSettings.UpwardLift.ToString("0.00") +
                " damageInfluence=" + SafeSettings.DamageInfluence.ToString("0.00") +
                " impactSpin=" + SafeSettings.ImpactSpin.ToString("0.00") +
                " handoffDelay=" + SafeSettings.RagdollHandoffDelay.ToString("0.000") +
                " momentumCarryover=" + SafeSettings.MomentumCarryover.ToString("0.00") +
                " engineDirectionInfluence=" + SafeSettings.EngineImpulseInfluence.ToString("0.00") +
                " pushOnDamage=" + SafeSettings.PushOnDamage +
                " damagePush=" + SafeSettings.DamagePushStrength.ToString("0.00") + "*(" +
                    SafeSettings.DamagePushBase.ToString("0.00") + "+damage*" + SafeSettings.DamagePushPerDamage.ToString("0.000") + ")" +
                " damageLift=" + SafeSettings.DamagePushUpwardLift.ToString("0.00") +
                " knockdownOnDamage=" + SafeSettings.KnockdownOnDamage +
                " knockdownThreshold=" + SafeSettings.KnockdownHealthThreshold.ToString("0.0") + "%" +
                " thresholdGatesPush=" + SafeSettings.RequireThresholdForDamagePush +
                " nativeReactionRoute=" + (_handleBlowAux == null ? "unavailable" : "HandleBlowAux") +
                " damagePushPersistence=" + DamagePushPulseCount + "x@" + DamagePushPulseInterval.ToString("0.000") + "s" +
                " minimumForce=" + SafeSettings.MinimumForce.ToString("0") +
                " maximumForce=" + (SafeSettings.MaximumForce <= 0f ? "unlimited" : SafeSettings.MaximumForce.ToString("0")) +
                " mountCollisionKillStrength=" + SafeSettings.MountCollisionKillStrength.ToString("0.00") +
                " pulses=" + SafeSettings.PulseCount +
                " interval=" + SafeSettings.PulseInterval.ToString("0.000") +
                " decay=" + SafeSettings.PulseDecay.ToString("0.00") +
                " deliveredForceCeilingPerPulse=" + (SafeSettings.DeliveredForceCeiling <= 0f ? "unlimited" : SafeSettings.DeliveredForceCeiling.ToString("0")) +
                " velocityLimits=" + (SafeSettings.MaxLinearVelocity <= 0f || SafeSettings.MaxAngularVelocity <= 0f
                    ? "disabled"
                    : SafeSettings.MaxLinearVelocity.ToString("0") + "/" + SafeSettings.MaxAngularVelocity.ToString("0")) + ".");
        }

        public override void OnRemoveBehavior()
        {
            SafeLog.Info(
                "Mission behavior removed: registerBlows=" + _registerBlowCount +
                " lethalHits=" + _lethalHitCount +
                " fallbackPollTimeBin=" + _fallbackPollTimeBin +
                " healthTransitionDeaths=" + _healthTransitionDeaths +
                " stateMonitorDeaths=" + _stateMonitorDeaths +
                " damagePushes=" + _damagePushes +
                " damageAccelerationPushes=" + _damageAccelerationPushes +
                " damageRagdollPushes=" + _damageRagdollPushes +
                " damagePushesQueued=" + _damagePushesQueued +
                " damagePushPulseApplications=" + _damagePushPulseApplications +
                " damagePushesCompleted=" + _damagePushesCompleted +
                " nativeKnockbacksApplied=" + _nativeKnockbacksApplied +
                " knockdownRequests=" + _knockdownRequests +
                " knockdownsApplied=" + _knockdownsApplied +
                " knockdownsSkipped=" + _knockdownsSkipped +
                " visualResyncPasses=" + _visualResyncPasses +
                " earlyRemoved=" + _earlyRemovedCount +
                " removed=" + _removedCount +
                " completedDeaths=" + _completedDeaths +
                " forceChunksProcessed=" + _forceChunksProcessed +
                " pendingDeaths=" + _pending.Count +
                " pendingKnockdowns=" + _pendingKnockdowns.Count +
                " pendingDamagePushes=" + _pendingDamagePushes.Count + ".");

            UnsubscribeAllHealthHandlers();
            _pending.Clear();
            _pendingKnockdowns.Clear();
            _pendingDamagePushes.Clear();
            _pendingVisualResyncs.Clear();
            _tracked.Clear();
            _recentImpacts.Clear();
            _lastHealth.Clear();
            _lastDamagePushAt.Clear();
            _lastKnockdownAt.Clear();
            _lastKnockdownAppliedAt.Clear();
        }

        public override void OnAgentCreated(Agent agent)
        {
            EnsureHealthTracking(agent);
        }

        private void TrackExistingAgents()
        {
            if (Mission == null)
                return;

            try
            {
                foreach (Agent agent in Mission.AllAgents)
                    EnsureHealthTracking(agent);
            }
            catch { }
        }

        private void EnsureHealthTracking(Agent agent)
        {
            if (!IsCurrentMissionAgent(agent))
                return;

            if (!_healthSubscribed.Contains(agent))
            {
                try
                {
                    agent.OnAgentHealthChanged += OnTrackedAgentHealthChanged;
                    _healthSubscribed.Add(agent);
                }
                catch { }
            }

            if (!_lastHealth.ContainsKey(agent))
            {
                try
                {
                    float health = agent.Health;
                    if (IsFinite(health))
                        _lastHealth[agent] = health;
                }
                catch { }
            }
        }

        private void OnTrackedAgentHealthChanged(Agent agent, float oldHealth, float newHealth)
        {
            if (!IsCurrentMissionAgent(agent))
                return;

            if (IsFinite(newHealth))
                _lastHealth[agent] = newHealth;

            if (!IsFinite(oldHealth) || !IsFinite(newHealth) || oldHealth <= 0f)
                return;

            if (!_dismembermentPlusCompatibility &&
                newHealth < oldHealth && newHealth > 0f &&
                (SafeSettings.PushOnDamage || SafeSettings.KnockdownOnDamage))
            {
                int observedDamage = Math.Max(0, (int)Math.Ceiling(oldHealth - newHealth));
                float postHitHealthPercent = GetHealthPercent(agent, newHealth);
                bool thresholdMet = IsKnockdownThresholdMet(postHitHealthPercent);

                // Request the native knockdown first, then add the directional push so the
                // acceleration is not swallowed by the start of the fall animation.
                if (SafeSettings.KnockdownOnDamage && thresholdMet)
                    TryApplyKnockdownFromRecentImpact(agent, observedDamage, postHitHealthPercent, "OnAgentHealthChanged");

                if (SafeSettings.PushOnDamage &&
                    (!SafeSettings.RequireThresholdForDamagePush || thresholdMet))
                {
                    TryApplyDamagePushFromRecentImpact(agent, observedDamage, "OnAgentHealthChanged");
                }
            }

            if (newHealth > 0f)
                return;

            // Bannerlord can expose a transient positive-health step while one native lethal
            // HandleBlow is still resolving. Any live-hit reaction queued from that transient
            // state belongs to the same lethal blow and must not survive as stale pending work.
            for (int i = _pendingKnockdowns.Count - 1; i >= 0; i--)
            {
                PendingKnockdown pendingKnockdown = _pendingKnockdowns[i];
                if (pendingKnockdown != null && ReferenceEquals(pendingKnockdown.Agent, agent))
                {
                    _pendingKnockdowns.RemoveAt(i);
                    _knockdownsSkipped++;
                }
            }
            for (int i = _pendingDamagePushes.Count - 1; i >= 0; i--)
            {
                PendingDamagePush pendingPush = _pendingDamagePushes[i];
                if (pendingPush != null && ReferenceEquals(pendingPush.Agent, agent))
                    _pendingDamagePushes.RemoveAt(i);
            }

            _healthTransitionDeaths++;
            QueueDeathFromRecentImpact(
                agent,
                "OnAgentHealthChanged",
                Math.Max(0, (int)Math.Ceiling(oldHealth - newHealth)),
                "healthTransition");
        }

        private void UnsubscribeAllHealthHandlers()
        {
            Agent[] agents = new Agent[_healthSubscribed.Count];
            _healthSubscribed.CopyTo(agents);
            for (int i = 0; i < agents.Length; i++)
            {
                try { agents[i].OnAgentHealthChanged -= OnTrackedAgentHealthChanged; }
                catch { }
            }
            _healthSubscribed.Clear();
        }

        public void OnRegisterBlowCompat(
            Agent attacker,
            Agent victim,
            WeakGameEntity realHitEntity,
            Blow blow,
            ref AttackCollisionData collisionData,
            ref MissionWeapon attackerWeapon)
        {
            _registerBlowCount++;
            if (!_firstRegisterBlowLogged)
            {
                _firstRegisterBlowLogged = true;
                SafeLog.Info("Mission OnRegisterBlow callback received; all-hit context capture is active.");
            }

            if (!IsCurrentMissionAgent(victim) || blow.InflictedDamage <= 0)
                return;

            EnsureHealthTracking(victim);

            string directionSource;
            Vec3 direction = ChooseBlowDirection(blow, out directionSource);
            Vec3 victimMomentum = CaptureAgentMomentum(victim);
            string kind = ClassifyBlowKind(blow);
            try
            {
                if (!blow.IsMissile && !blow.IsFallDamage &&
                    (blow.BlowFlag & BlowFlags.NoSound) == 0 && collisionData.IsColliderAgent)
                {
                    float chargeVelocity = collisionData.ChargeVelocity;
                    bool mountContext = attacker != null && (attacker.IsMount || attacker.MountAgent != null);
                    if (mountContext || (IsFinite(chargeVelocity) && Math.Abs(chargeVelocity) > 0.10f))
                        kind = "mount-collision";
                }
            }
            catch { }
            float now = GetMissionTime();

            _recentImpacts[victim] = new RecentImpact
            {
                Affector = attacker,
                Direction = direction,
                VictimMomentum = victimMomentum,
                Damage = Math.Max(0, blow.InflictedDamage),
                HitBone = blow.BoneIndex,
                KillKind = kind,
                DirectionSource = directionSource,
                OriginalBlow = blow,
                HasOriginalBlow = true,
                CapturedAt = now
            };

            float health;
            try { health = victim.Health; }
            catch { return; }
            if (!IsFinite(health))
                return;

            bool alreadyDead = health <= 0f;

            // OnRegisterBlow already captures the complete hit into _recentImpacts before this point.
            // Do not speculate that a still-living agent is dead by subtracting InflictedDamage from
            // Agent.Health again: on the supported Bannerlord path the health-change callback can
            // already have committed that damage, which double-counts the hit and creates a stale
            // PendingDeath timer. Real deaths are captured by the health/state/removal callbacks and
            // this same OnRegisterBlow callback then enriches them with the exact impact data.
            bool predictedLethal = false;
            if (!alreadyDead && !predictedLethal)
            {
                if (!_dismembermentPlusCompatibility &&
                    SafeSettings.PushOnDamage && !SafeSettings.RequireThresholdForDamagePush)
                {
                    TryApplyDamagePush(
                        victim, attacker, direction, victimMomentum, blow.BoneIndex, kind,
                        Math.Max(0, blow.InflictedDamage), directionSource, "OnRegisterBlow");
                }
                return;
            }

            _lethalHitCount++;
            QueueDeath(
                victim, attacker, direction, victimMomentum, Vec3.Zero, false, blow.BoneIndex,
                predictedLethal ? "OnRegisterBlowPredictedLethal" : "OnRegisterBlow",
                kind, blow.InflictedDamage, directionSource, alreadyDead);
        }

        public override void OnEarlyAgentRemoved(
            Agent affectedAgent,
            Agent affectorAgent,
            AgentState agentState,
            KillingBlow killingBlow)
        {
            _earlyRemovedCount++;
            if (!IsCurrentMissionAgent(affectedAgent) || !IsDeathState(affectedAgent, agentState))
                return;

            Vec3 engineImpulse = ReadKillingBlowImpulse(killingBlow);
            int damage = ReadKillingBlowDamage(killingBlow);
            string kind = ReadKillingBlowIsMissile(killingBlow) ? "missile-fallback" : "fallback";
            ClothForceBridge.ReportNativeDeathResult(
                affectedAgent, engineImpulse, kind, "OnEarlyAgentRemoved");
            QueueDeath(
                affectedAgent, affectorAgent, Vec3.Zero, CaptureAgentMomentum(affectedAgent),
                engineImpulse, IsUsableVector(engineImpulse), -1, "OnEarlyAgentRemoved", kind, damage,
                IsUsableVector(engineImpulse) ? "KillingBlow.RagdollImpulseAmount" : "fallback", true);
        }

        public override void OnAgentRemoved(
            Agent affectedAgent,
            Agent affectorAgent,
            AgentState agentState,
            KillingBlow killingBlow)
        {
            _removedCount++;
            if (!IsCurrentMissionAgent(affectedAgent) || !IsDeathState(affectedAgent, agentState))
                return;

            Vec3 engineImpulse = ReadKillingBlowImpulse(killingBlow);
            int damage = ReadKillingBlowDamage(killingBlow);
            string kind = ReadKillingBlowIsMissile(killingBlow) ? "missile-fallback" : "fallback";
            ClothForceBridge.ReportNativeDeathResult(
                affectedAgent, engineImpulse, kind, "OnAgentRemoved");
            QueueDeath(
                affectedAgent, affectorAgent, Vec3.Zero, CaptureAgentMomentum(affectedAgent),
                engineImpulse, IsUsableVector(engineImpulse), -1, "OnAgentRemoved", kind, damage,
                IsUsableVector(engineImpulse) ? "KillingBlow.RagdollImpulseAmount" : "fallback", true);
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (affectedAgent == null)
                return;

            ClothForceBridge.ForgetDeathRoute(affectedAgent);

            if (_healthSubscribed.Remove(affectedAgent))
            {
                try { affectedAgent.OnAgentHealthChanged -= OnTrackedAgentHealthChanged; }
                catch { }
            }
            _lastHealth.Remove(affectedAgent);
            _recentImpacts.Remove(affectedAgent);
            _lastDamagePushAt.Remove(affectedAgent);
            _lastKnockdownAt.Remove(affectedAgent);
            _lastKnockdownAppliedAt.Remove(affectedAgent);

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pending[i].Agent, affectedAgent))
                    _pending.RemoveAt(i);
            }

            for (int i = _pendingKnockdowns.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pendingKnockdowns[i].Agent, affectedAgent))
                    _pendingKnockdowns.RemoveAt(i);
            }

            for (int i = _pendingDamagePushes.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_pendingDamagePushes[i].Agent, affectedAgent))
                    _pendingDamagePushes.RemoveAt(i);
            }
        }

        private static float GetHealthPercent(Agent agent, float currentHealth)
        {
            if (agent == null || !IsFinite(currentHealth))
                return float.PositiveInfinity;

            float limit;
            try { limit = agent.HealthLimit; }
            catch { return float.PositiveInfinity; }
            if (!IsFinite(limit) || limit <= 0f)
                return float.PositiveInfinity;
            return currentHealth * 100f / limit;
        }

        private static bool IsKnockdownThresholdMet(float postHitHealthPercent)
        {
            if (!IsFinite(postHitHealthPercent))
                return false;
            return postHitHealthPercent <= SafeSettings.KnockdownHealthThreshold;
        }

        private void TryApplyKnockdownFromRecentImpact(
            Agent affected,
            int observedDamage,
            float postHitHealthPercent,
            string source)
        {
            if (!SafeSettings.KnockdownOnDamage || affected == null || observedDamage <= 0)
                return;
            if (!IsCurrentMissionAgent(affected))
                return;

            float health;
            try { health = affected.Health; }
            catch { return; }
            if (!IsFinite(health) || health <= 0f || IsTerminalAgentState(affected))
                return;

            float now = GetMissionTime();
            float lastAt;
            if (_lastKnockdownAt.TryGetValue(affected, out lastAt) && now - lastAt <= KnockdownDuplicateWindow)
            {
                _knockdownsSkipped++;
                return;
            }

            _knockdownRequests++;

            RecentImpact recent;
            bool hasRecent = _recentImpacts.TryGetValue(affected, out recent) && recent != null &&
                now - recent.CapturedAt <= RecentImpactLifetime;

            Agent affector = hasRecent ? recent.Affector : null;
            Vec3 momentum = CaptureAgentMomentum(affected);
            if (!IsUsableVector(momentum) && hasRecent)
                momentum = recent.VictimMomentum;

            string directionSource;
            Vec3 direction = ResolveDamagePushDirection(
                affected,
                affector,
                hasRecent ? recent.Direction : Vec3.Zero,
                momentum,
                hasRecent ? recent.DirectionSource : "healthChangeFallback",
                out directionSource);

            Blow knockdownBlow = hasRecent && recent.HasOriginalBlow ? recent.OriginalBlow : new Blow();
            knockdownBlow.BlowFlag |= BlowFlags.KnockDown;
            knockdownBlow.InflictedDamage = 0;
            knockdownBlow.Direction = direction;
            knockdownBlow.SwingDirection = direction;
            knockdownBlow.GlobalPosition = SafeAgentPosition(affected);
            knockdownBlow.BoneIndex = ToSByteBone(hasRecent ? recent.HitBone : -1);
            try { knockdownBlow.OwnerId = affector == null ? -1 : affector.Index; }
            catch { knockdownBlow.OwnerId = -1; }

            if (!IsFinite(knockdownBlow.BaseMagnitude) || knockdownBlow.BaseMagnitude <= 0f)
                knockdownBlow.BaseMagnitude = Math.Max(1f, observedDamage);

            if (_handleBlowAux == null)
            {
                _knockdownsSkipped++;
                if (SafeSettings.DebugLogging)
                {
                    SafeLog.Info(
                        "Knockdown request could not be queued for agent #" + SafeAgentIndex(affected) +
                        " source=" + source +
                        " postHealth=" + postHitHealthPercent.ToString("0.0") + "%" +
                        " threshold=" + SafeSettings.KnockdownHealthThreshold.ToString("0.0") + "%" +
                        " nativeRoute=unavailable.");
                }
                return;
            }

            // Health-change notifications fire from inside Agent.HandleBlow before the engine has
            // applied the original hit reaction. Queue the knockdown for the next mission tick so
            // the original HandleBlowAux call cannot immediately overwrite the fall reaction.
            _pendingKnockdowns.Add(new PendingKnockdown
            {
                Agent = affected,
                Blow = knockdownBlow,
                Damage = observedDamage,
                PostHealthPercent = postHitHealthPercent,
                RequestedAt = now,
                Source = source,
                Direction = direction,
                DirectionSource = directionSource,
                HitBone = hasRecent ? recent.HitBone : -1
            });
            _lastKnockdownAt[affected] = now;

            if (SafeSettings.DebugLogging)
            {
                SafeLog.Info(
                    "Queued native on-damage knockdown for agent #" + SafeAgentIndex(affected) +
                    " source=" + source +
                    " damage=" + observedDamage +
                    " postHealth=" + postHitHealthPercent.ToString("0.0") + "%" +
                    " threshold=" + SafeSettings.KnockdownHealthThreshold.ToString("0.0") + "%" +
                    " direction=" + FormatVec(direction) +
                    " directionSource=" + directionSource +
                    " hitBone=" + (hasRecent ? recent.HitBone : -1) + ".");
            }
        }

        private static Vec3 SafeAgentPosition(Agent agent)
        {
            try { return agent == null ? Vec3.Zero : agent.Position; }
            catch { return Vec3.Zero; }
        }

        private static bool TryHandleBlowAux(Agent agent, Blow blow)
        {
            if (agent == null || _handleBlowAux == null)
                return false;

            try
            {
                object[] args = new object[] { blow };
                _handleBlowAux.Invoke(agent, args);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("Agent.HandleBlowAux knockdown invocation failed", ex.InnerException ?? ex);
                return false;
            }
            catch (Exception ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("Agent.HandleBlowAux knockdown invocation failed", ex);
                return false;
            }
        }

        private void TryApplyDamagePushFromRecentImpact(Agent affected, int observedDamage, string source)
        {
            if (!SafeSettings.PushOnDamage || affected == null || observedDamage <= 0)
                return;

            float now = GetMissionTime();
            float lastPushAt;
            if (_lastDamagePushAt.TryGetValue(affected, out lastPushAt) && now - lastPushAt <= DamagePushDuplicateWindow)
                return;

            RecentImpact recent;
            if (_recentImpacts.TryGetValue(affected, out recent) && recent != null &&
                now - recent.CapturedAt <= RecentImpactLifetime)
            {
                TryApplyDamagePush(
                    affected, recent.Affector, recent.Direction, CaptureAgentMomentum(affected),
                    recent.HitBone, string.IsNullOrEmpty(recent.KillKind) ? "health-damage" : recent.KillKind,
                    Math.Max(observedDamage, recent.Damage),
                    string.IsNullOrEmpty(recent.DirectionSource) ? "recentImpact" : recent.DirectionSource, source);
                return;
            }

            TryApplyDamagePush(
                affected, null, Vec3.Zero, CaptureAgentMomentum(affected), -1, "health-damage",
                observedDamage, "healthChangeFallback", source);
        }

        private void TryApplyDamagePush(
            Agent affected,
            Agent affector,
            Vec3 blowDirection,
            Vec3 victimMomentum,
            int hitBone,
            string kind,
            int damage,
            string capturedDirectionSource,
            string source)
        {
            if (!SafeSettings.PushOnDamage || !IsCurrentMissionAgent(affected) || damage <= 0)
                return;

            float health;
            try { health = affected.Health; }
            catch { return; }
            if (!IsFinite(health) || health <= 0f || IsTerminalAgentState(affected))
                return;

            float now = GetMissionTime();
            float lastPushAt;
            if (_lastDamagePushAt.TryGetValue(affected, out lastPushAt) && now - lastPushAt <= DamagePushDuplicateWindow)
                return;

            string directionSource;
            Vec3 direction = ResolveDamagePushDirection(
                affected, affector, blowDirection, victimMomentum, capturedDirectionSource, out directionSource);
            float magnitude = ComputeDamagePushMagnitude(damage);
            if (!IsUsableVector(direction) || magnitude <= 0f)
                return;

            Blow reactionBlow = new Blow();
            RecentImpact recent;
            if (_recentImpacts.TryGetValue(affected, out recent) && recent != null && recent.HasOriginalBlow &&
                now - recent.CapturedAt <= RecentImpactLifetime)
            {
                reactionBlow = recent.OriginalBlow;
            }

            // This is a zero-damage native reaction used only to make the shove visible.
            // KnockBack or KnockDown is selected at execution time from the actual post-hit HP.
            reactionBlow.InflictedDamage = 0;
            reactionBlow.SelfInflictedDamage = 0;
            reactionBlow.AbsorbedByArmor = 0f;
            reactionBlow.BlowFlag = BlowFlags.NoSound;
            reactionBlow.Direction = direction;
            reactionBlow.SwingDirection = direction;
            reactionBlow.GlobalPosition = SafeAgentPosition(affected);
            reactionBlow.BoneIndex = ToSByteBone(hitBone);
            reactionBlow.BaseMagnitude = Math.Max(1f, damage);
            try { reactionBlow.OwnerId = affector == null ? -1 : affector.Index; }
            catch { reactionBlow.OwnerId = -1; }

            _pendingDamagePushes.Add(new PendingDamagePush
            {
                Agent = affected,
                ReactionBlow = reactionBlow,
                Direction = direction,
                Magnitude = magnitude,
                Damage = damage,
                RequestedAt = now,
                NextPulseAt = now + 0.001f,
                PulseIndex = 0,
                Source = source,
                Kind = kind,
                DirectionSource = directionSource,
                HitBone = hitBone
            });

            _lastDamagePushAt[affected] = now;
            _damagePushesQueued++;

            if (SafeSettings.DebugLogging)
            {
                SafeLog.Info(
                    "Queued persistent on-damage push for agent #" + SafeAgentIndex(affected) +
                    " source=" + source +
                    " kind=" + kind +
                    " damage=" + damage +
                    " magnitude=" + magnitude.ToString("0.000") +
                    " pulses=" + DamagePushPulseCount +
                    " direction=" + FormatVec(direction) +
                    " directionSource=" + directionSource +
                    " hitBone=" + hitBone + ".");
            }
        }

        private static Vec3 ResolveDamagePushDirection(
            Agent affected,
            Agent affector,
            Vec3 blowDirection,
            Vec3 victimMomentum,
            string capturedSource,
            out string source)
        {
            Vec3 direction = blowDirection;
            source = IsUsableVector(direction)
                ? (string.IsNullOrEmpty(capturedSource) ? "capturedImpact" : capturedSource)
                : "unknown";

            if (!IsUsableVector(direction))
            {
                try
                {
                    if (affector != null && !ReferenceEquals(affector, affected))
                    {
                        direction = affected.Position - affector.Position;
                        source = "awayFromAffector";
                    }
                }
                catch { direction = Vec3.Zero; }
            }

            if (!IsUsableVector(direction) && IsUsableVector(victimMomentum))
            {
                direction = victimMomentum;
                source = "victimMomentum";
            }

            if (!IsUsableVector(direction))
            {
                try
                {
                    direction = affected.LookDirection;
                    source = "victimLookDirection";
                }
                catch { direction = new Vec3(0f, 1f, 0f); }
            }

            if (!IsUsableVector(direction))
            {
                direction = new Vec3(0f, 1f, 0f);
                source = "worldForwardFallback";
            }

            direction = direction.NormalizedCopy();
            if (IsUsableVector(victimMomentum) && SafeSettings.MomentumCarryover > 0f)
            {
                direction += victimMomentum * (0.035f * SafeSettings.MomentumCarryover);
                source += "+momentum";
            }

            direction.z += SafeSettings.DamagePushUpwardLift;
            if (!IsUsableVector(direction))
                return new Vec3(0f, 1f, 0.1f).NormalizedCopy();
            return direction.NormalizedCopy();
        }

        private static float ComputeDamagePushMagnitude(int damage)
        {
            float magnitude = (SafeSettings.DamagePushBase +
                Math.Max(0, damage) * SafeSettings.DamagePushPerDamage) * SafeSettings.DamagePushStrength;
            if (!IsFinite(magnitude) || magnitude <= 0f)
                return 0f;
            return magnitude;
        }

        private static bool TryAddAcceleration(Agent agent, Vec3 acceleration)
        {
            if (agent == null || !IsUsableVector(acceleration))
                return false;

            if (_addAcceleration == null)
                return false;

            try
            {
                _addAcceleration.Invoke(agent, new object[] { acceleration });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("Agent.AddAcceleration invocation failed", ex.InnerException ?? ex);
                return false;
            }
            catch (Exception ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("Agent.AddAcceleration invocation failed", ex);
                return false;
            }
        }

        private static int SafeAgentIndex(Agent agent)
        {
            try { return agent == null ? -1 : agent.Index; }
            catch { return -1; }
        }

        private void QueueDeathFromRecentImpact(Agent affected, string source, int observedDamage, string fallbackKind)
        {
            if (affected == null || _tracked.Contains(affected))
                return;

            RecentImpact recent;
            float now = GetMissionTime();
            if (_recentImpacts.TryGetValue(affected, out recent) &&
                recent != null && now - recent.CapturedAt <= RecentImpactLifetime)
            {
                Vec3 momentum = CaptureAgentMomentum(affected);
                if (!IsUsableVector(momentum))
                    momentum = recent.VictimMomentum;

                QueueDeath(
                    affected, recent.Affector, recent.Direction, momentum, Vec3.Zero, false,
                    recent.HitBone, source,
                    string.IsNullOrEmpty(recent.KillKind) ? fallbackKind : recent.KillKind,
                    Math.Max(observedDamage, recent.Damage),
                    string.IsNullOrEmpty(recent.DirectionSource) ? "recentImpact" : recent.DirectionSource, true);
                return;
            }

            QueueDeath(
                affected, null, Vec3.Zero, CaptureAgentMomentum(affected), Vec3.Zero, false, -1,
                source, fallbackKind, observedDamage, "healthStateFallback", true);
        }

        private void QueueDeath(
            Agent affected,
            Agent affector,
            Vec3 blowDirection,
            Vec3 victimMomentum,
            Vec3 engineImpulse,
            bool hasEngineImpulse,
            int hitBone,
            string source,
            string killKind,
            int damage,
            string directionSource,
            bool deathConfirmed)
        {
            if (affected == null)
                return;

            PendingDeath existing = FindPending(affected);
            if (existing != null)
            {
                EnrichPendingDeath(
                    existing, affected, affector, blowDirection, victimMomentum, engineImpulse,
                    hasEngineImpulse, hitBone, source, killKind, damage, directionSource, deathConfirmed);
                return;
            }

            Vec3 direction = ResolveDirection(
                affected, affector, blowDirection, victimMomentum, engineImpulse, hasEngineImpulse, directionSource, out directionSource);
            float force = ComputeForceMagnitude(damage);
            if (force <= 0f || !IsFinite(direction))
                return;

            int index;
            try { index = affected.Index; }
            catch { index = -1; }

            float now;
            try { now = Mission == null ? 0f : Mission.CurrentTime; }
            catch { now = 0f; }

            _tracked.Add(affected);
            PendingDeath pending = new PendingDeath
            {
                Agent = affected,
                AgentIndex = index,
                RawImpactDirection = IsFinite(blowDirection) ? blowDirection : Vec3.Zero,
                Direction = direction,
                VictimMomentum = IsFinite(victimMomentum) ? victimMomentum : Vec3.Zero,
                EngineImpulse = IsFinite(engineImpulse) ? engineImpulse : Vec3.Zero,
                HasEngineImpulse = hasEngineImpulse && IsUsableVector(engineImpulse),
                DirectionSource = directionSource,
                Damage = Math.Max(0, damage),
                ForceMagnitude = force,
                CapturedAt = now,
                NextPulseAt = now,
                PulseIndex = 0,
                PulseCount = SafeSettings.PulseCount,
                PulseInterval = SafeSettings.PulseInterval,
                PulseDecay = SafeSettings.PulseDecay,
                HitBone = ToSByteBone(hitBone),
                Source = source,
                KillKind = killKind,
                DeathConfirmed = deathConfirmed
            };
            _pending.Add(pending);

            if (SafeSettings.DebugLogging)
            {
                SafeLog.Info(
                    "Queued killed mission agent #" + index +
                    " source=" + source +
                    " kind=" + killKind +
                    " damage=" + damage +
                    " hitBone=" + hitBone +
                    " directionSource=" + directionSource +
                    " direction=" + FormatVec(direction) +
                    " victimMomentum=" + FormatVec(pending.VictimMomentum) +
                    " engineImpulse=" + FormatVec(pending.EngineImpulse) +
                    " force=" + force.ToString("0") +
                    " confirmed=" + deathConfirmed +
                    " pulses=" + SafeSettings.PulseCount + ".");
            }
        }

        private PendingDeath FindPending(Agent affected)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (ReferenceEquals(_pending[i].Agent, affected))
                    return _pending[i];
            }
            return null;
        }

        private void EnrichPendingDeath(
            PendingDeath pending,
            Agent affected,
            Agent affector,
            Vec3 blowDirection,
            Vec3 victimMomentum,
            Vec3 engineImpulse,
            bool hasEngineImpulse,
            int hitBone,
            string source,
            string killKind,
            int damage,
            string directionSource,
            bool deathConfirmed)
        {
            if (pending == null)
                return;

            if (deathConfirmed)
                pending.DeathConfirmed = true;

            if (IsUsableVector(blowDirection))
                pending.RawImpactDirection = blowDirection;

            if (IsUsableVector(victimMomentum) && !IsUsableVector(pending.VictimMomentum))
                pending.VictimMomentum = victimMomentum;

            if (hasEngineImpulse && IsUsableVector(engineImpulse))
            {
                pending.EngineImpulse = engineImpulse;
                pending.HasEngineImpulse = true;
            }

            if (hitBone >= 0)
                pending.HitBone = ToSByteBone(hitBone);

            if (damage > pending.Damage)
            {
                pending.Damage = damage;
                pending.ForceMagnitude = Math.Max(pending.ForceMagnitude, ComputeForceMagnitude(damage));
            }

            string resolvedSource = string.IsNullOrEmpty(directionSource) ? pending.DirectionSource : directionSource;
            pending.Direction = ResolveDirection(
                affected, affector, pending.RawImpactDirection, pending.VictimMomentum, pending.EngineImpulse,
                pending.HasEngineImpulse, resolvedSource, out resolvedSource);
            pending.DirectionSource = resolvedSource;

            if (pending.Source == null || pending.Source.IndexOf(source, StringComparison.Ordinal) < 0)
                pending.Source = string.IsNullOrEmpty(pending.Source) ? source : pending.Source + "+" + source;
            if (string.Equals(killKind, "mount-collision", StringComparison.Ordinal))
                pending.KillKind = "mount-collision";
            else if (!string.IsNullOrEmpty(killKind) && !string.IsNullOrEmpty(source) &&
                     source.StartsWith("OnRegisterBlow", StringComparison.Ordinal))
                pending.KillKind = killKind;
            else if (!string.IsNullOrEmpty(killKind) && pending.KillKind == "fallback")
                pending.KillKind = killKind;

            if (SafeSettings.DebugLogging)
            {
                SafeLog.Info(
                    "Enriched killed mission agent #" + pending.AgentIndex +
                    " source=" + source +
                    " directionSource=" + pending.DirectionSource +
                    " direction=" + FormatVec(pending.Direction) +
                    " victimMomentum=" + FormatVec(pending.VictimMomentum) +
                    " engineImpulse=" + FormatVec(pending.EngineImpulse) + ".");
            }
        }

        private void PollAgentHealthAndStateTransitions()
        {
            if (Mission == null)
                return;

            // Safety-net monitor only. Immediate hit, health-change, removal and deathblow paths
            // are event-driven and remain unthrottled. Sample the full-agent fallback at 8 Hz
            // of mission time. The +1 keeps the first fallback sample immediate.
            int fallbackPollTimeBin = (int)(GetMissionTime() * 8f) + 1;
            if (_fallbackPollTimeBin == fallbackPollTimeBin)
                return;
            _fallbackPollTimeBin = fallbackPollTimeBin;

            try
            {
                foreach (Agent agent in Mission.AllAgents)
                {
                    if (!IsCurrentMissionAgent(agent))
                        continue;

                    EnsureHealthTracking(agent);

                    float currentHealth;
                    try { currentHealth = agent.Health; }
                    catch { continue; }
                    if (!IsFinite(currentHealth))
                        continue;

                    float previousHealth;
                    bool hadPrevious = _lastHealth.TryGetValue(agent, out previousHealth);
                    _lastHealth[agent] = currentHealth;

                    if (_tracked.Contains(agent))
                        continue;

                    bool transitionedToZero = hadPrevious && previousHealth > 0f && currentHealth <= 0f;
                    bool terminalState = currentHealth <= 0f && IsTerminalAgentState(agent);
                    if (!transitionedToZero && !terminalState)
                        continue;

                    _stateMonitorDeaths++;
                    int observedDamage = transitionedToZero
                        ? (int)Math.Ceiling(previousHealth - currentHealth)
                        : 0;
                    QueueDeathFromRecentImpact(agent, "HealthStateMonitor", observedDamage, "spell-or-scripted");
                }
            }
            catch { }
        }

        private void ProcessPendingKnockdowns(float now)
        {
            for (int i = _pendingKnockdowns.Count - 1; i >= 0; i--)
            {
                PendingKnockdown pending = _pendingKnockdowns[i];
                if (pending == null || pending.Agent == null || now - pending.RequestedAt > 0.50f)
                {
                    _knockdownsSkipped++;
                    _pendingKnockdowns.RemoveAt(i);
                    continue;
                }

                float health;
                try { health = pending.Agent.Health; }
                catch { health = 0f; }
                if (!IsFinite(health) || health <= 0f || IsTerminalAgentState(pending.Agent))
                {
                    _knockdownsSkipped++;
                    _pendingKnockdowns.RemoveAt(i);
                    continue;
                }

                bool applied = TryHandleBlowAux(pending.Agent, pending.Blow);
                _pendingKnockdowns.RemoveAt(i);
                if (!applied)
                {
                    _knockdownsSkipped++;
                    if (SafeSettings.DebugLogging)
                    {
                        SafeLog.Info(
                            "Native on-damage knockdown was rejected for agent #" + SafeAgentIndex(pending.Agent) +
                            " source=" + pending.Source +
                            " postHealth=" + pending.PostHealthPercent.ToString("0.0") + ".");
                    }
                    continue;
                }

                _knockdownsApplied++;
                _lastKnockdownAppliedAt[pending.Agent] = now;
                if (SafeSettings.DebugLogging)
                {
                    SafeLog.Info(
                        "Applied native on-damage knockdown to agent #" + SafeAgentIndex(pending.Agent) +
                        " source=" + pending.Source +
                        " damage=" + pending.Damage +
                        " postHealth=" + pending.PostHealthPercent.ToString("0.0") + "%" +
                        " threshold=" + SafeSettings.KnockdownHealthThreshold.ToString("0.0") + "%" +
                        " route=HandleBlowAux(nextTick)" +
                        " direction=" + FormatVec(pending.Direction) +
                        " directionSource=" + pending.DirectionSource +
                        " hitBone=" + pending.HitBone + ".");
                }
            }
        }

        private void ProcessPendingDamagePushes(float now)
        {
            for (int i = _pendingDamagePushes.Count - 1; i >= 0; i--)
            {
                PendingDamagePush pending = _pendingDamagePushes[i];
                if (pending == null || pending.Agent == null || now - pending.RequestedAt > DamagePushQueueLifetime)
                {
                    _pendingDamagePushes.RemoveAt(i);
                    continue;
                }

                if (now < pending.NextPulseAt)
                    continue;

                float health;
                try { health = pending.Agent.Health; }
                catch { health = 0f; }
                if (!IsFinite(health) || health <= 0f || IsTerminalAgentState(pending.Agent))
                {
                    _pendingDamagePushes.RemoveAt(i);
                    continue;
                }

                float postHealthPercent = GetHealthPercent(pending.Agent, health);
                bool thresholdMet = IsKnockdownThresholdMet(postHealthPercent);
                if (SafeSettings.RequireThresholdForDamagePush && !thresholdMet)
                {
                    if (SafeSettings.DebugLogging)
                    {
                        SafeLog.Info(
                            "Skipped threshold-gated on-damage push for agent #" + SafeAgentIndex(pending.Agent) +
                            " postHealth=" + postHealthPercent.ToString("0.0") + "%" +
                            " threshold=" + SafeSettings.KnockdownHealthThreshold.ToString("0.0") + "." );
                    }
                    _pendingDamagePushes.RemoveAt(i);
                    continue;
                }

                Skeleton skeleton = null;
                RagdollState ragdollState = RagdollState.Disabled;
                try
                {
                    skeleton = ReferenceEquals(pending.Agent.AgentVisuals, null) ? null : pending.Agent.AgentVisuals.GetSkeleton();
                    if (!ReferenceEquals(skeleton, null))
                        ragdollState = skeleton.GetCurrentRagdollState();
                }
                catch { skeleton = null; }

                bool activeRagdoll = !ReferenceEquals(skeleton, null) &&
                    (ragdollState == RagdollState.ActiveFirstTick || ragdollState == RagdollState.Active);
                bool wantsKnockdown = SafeSettings.KnockdownOnDamage && thresholdMet;

                if (!pending.NativeReactionAttempted && !activeRagdoll)
                {
                    pending.NativeReactionAttempted = true;
                    bool alreadyKnockedDown = false;
                    float appliedAt;
                    if (wantsKnockdown && _lastKnockdownAppliedAt.TryGetValue(pending.Agent, out appliedAt))
                        alreadyKnockedDown = now - appliedAt <= 0.20f;

                    if (!alreadyKnockedDown)
                    {
                        Blow reaction = pending.ReactionBlow;
                        reaction.BlowFlag = BlowFlags.NoSound |
                            (wantsKnockdown ? BlowFlags.KnockDown : BlowFlags.KnockBack);
                        reaction.Direction = pending.Direction;
                        reaction.SwingDirection = pending.Direction;
                        reaction.InflictedDamage = 0;
                        reaction.SelfInflictedDamage = 0;
                        pending.NativeReactionApplied = TryHandleBlowAux(pending.Agent, reaction);
                        if (pending.NativeReactionApplied)
                        {
                            if (wantsKnockdown)
                            {
                                _knockdownsApplied++;
                                _lastKnockdownAppliedAt[pending.Agent] = now;
                            }
                            else
                            {
                                _nativeKnockbacksApplied++;
                            }
                        }
                    }
                    else
                    {
                        pending.NativeReactionApplied = true;
                    }
                }

                float scale = PowSafe(DamagePushPulseDecay, pending.PulseIndex);
                Vec3 acceleration = pending.Direction * (pending.Magnitude * scale);
                bool applied;
                string route;
                if (activeRagdoll)
                {
                    Vec3 requestedForce = acceleration * DamageRagdollForceUnitsPerAcceleration;
                    Vec3 appliedForce;
                    // Zero is the existing live-ragdoll damage-push sentinel. The resolver keeps
                    // this path on the mapped central body exactly as before.
                    sbyte applicationBone = 0;
                    string boneSource;
                    float chunkMagnitude;
                    applied = TryApplyMappedCentralRagdollForce(
                        pending.Agent,
                        skeleton,
                        requestedForce,
                        out appliedForce,
                        ref applicationBone,
                        out boneSource,
                        out chunkMagnitude);
                    route = "ApplyForceOnRagdoll(mappedCore:" + applicationBone + "," + boneSource + ")";
                    if (applied)
                    {
                        _damageRagdollPushes++;
                    }
                }
                else
                {
                    applied = TryAddAcceleration(pending.Agent, acceleration);
                    route = "AddAcceleration(persistent)";
                    if (applied)
                        _damageAccelerationPushes++;
                }

                if (!applied)
                {
                    pending.NextPulseAt = now + RetryDelay;
                    continue;
                }

                pending.PulseIndex++;
                _damagePushes++;
                _damagePushPulseApplications++;

                if (SafeSettings.DebugLogging)
                {
                    SafeLog.Info(
                        "Applied on-damage push pulse " + pending.PulseIndex + "/" + DamagePushPulseCount +
                        " to agent #" + SafeAgentIndex(pending.Agent) +
                        " source=" + pending.Source +
                        " kind=" + pending.Kind +
                        " damage=" + pending.Damage +
                        " route=" + route +
                        " nativeReaction=" + (wantsKnockdown ? "KnockDown" : "KnockBack") +
                        " reactionApplied=" + pending.NativeReactionApplied +
                        " magnitude=" + (pending.Magnitude * scale).ToString("0.000") +
                        " vector=" + FormatVec(acceleration) +
                        " directionSource=" + pending.DirectionSource + ".");
                }

                if (pending.PulseIndex >= DamagePushPulseCount)
                {
                    _damagePushesCompleted++;
                    _pendingDamagePushes.RemoveAt(i);
                }
                else
                {
                    pending.NextPulseAt = now + DamagePushPulseInterval;
                }
            }
        }

        public override void OnPreMissionTick(float dt)
        {
            if (dt <= 0f)
                return;

            // Native-transition boundary: StartRagdollAsCorpse requests and death-force chunks are queued by
            // OnMissionTick, then executed here before Bannerlord's next native mission integration. This ensures
            // the animation->ragdoll ownership change is visible to the engine before its skeleton/equipment/cloth
            // integration, while the existing ActiveFirstTick/Active barriers still prevent a custom launch force
            // from being delivered until subsequent native integration has completed.
            ClothForceBridge.Flush();
        }

        public override void OnMissionTick(float dt)
        {
            if (dt <= 0f || Mission == null || !ReferenceEquals(Mission.Current, Mission))
                return;

            PollAgentHealthAndStateTransitions();

            float now = Mission.CurrentTime;
            ProcessPendingVisualResyncs(now);
            ProcessPendingKnockdowns(now);
            ProcessPendingDamagePushes(now);
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingDeath pending = _pending[i];
                if (pending.Agent == null || now - pending.CapturedAt > PendingLifetime)
                {
                    // Never drop a corpse lifecycle that ExtremeRagdoll already took ownership of.
                    // The v1.2.32 invariant requires every successful/accepted StartRagdollAsCorpse
                    // lifecycle to reach the one-shot EndRagdollAsCorpse finalizer. Force processing
                    // may time out independently, so transfer ownership instead of simply removing it.
                    if (pending.Agent != null && pending.RagdollStartRequested)
                    {
                        pending.PulseCount = -1;
                        pending.PulseIndex = CorpseFinalizerPendingPulseIndex;
                        pending.RagdollSeenAt = now;
                        // This field is reused by ProcessPendingVisualResyncs as its next-poll timestamp.
                        // Normal pulse completion already clears it; timeout/early-settle exits must too.
                        pending.CurrentPulseBaseMagnitude = 0f;
                        pending.CapturedAt = now + CorpseFinalizationMainLoopDeferral;
                        pending.NextPulseAt = now + CorpseFinalizationMainLoopDeferral;
                        continue;
                    }

                    _pending.RemoveAt(i);
                    continue;
                }

                if (now < pending.NextPulseAt)
                    continue;

                if (!pending.DeathConfirmed)
                {
                    bool confirmed = false;
                    try { confirmed = pending.Agent.Health <= 0f || IsTerminalAgentState(pending.Agent); }
                    catch { }

                    if (!confirmed)
                    {
                        if (now - pending.CapturedAt > 0.35f)
                        {
                            if (SafeSettings.DebugLogging)
                            {
                                SafeLog.Info(
                                    "Discarded predicted lethal hit for agent #" + pending.AgentIndex +
                                    " because the agent survived or the death was cancelled.");
                            }
                            _pending.RemoveAt(i);
                        }
                        else
                        {
                            pending.NextPulseAt = now + RetryDelay;
                        }
                        continue;
                    }

                    pending.DeathConfirmed = true;
                    if (SafeSettings.DebugLogging)
                        SafeLog.Info("Confirmed predicted death for agent #" + pending.AgentIndex + ".");
                }

                Skeleton skeleton;
                try
                {
                    skeleton = ReferenceEquals(pending.Agent.AgentVisuals, null) ? null : pending.Agent.AgentVisuals.GetSkeleton();
                }
                catch
                {
                    skeleton = null;
                }

                if (ReferenceEquals(skeleton, null))
                {
                    if (!pending.MissingSkeletonLogged)
                    {
                        pending.MissingSkeletonLogged = true;
                        SafeLog.Info("Waiting for corpse skeleton for agent #" + pending.AgentIndex + ".");
                    }
                    pending.NextPulseAt = now + RetryDelay;
                    continue;
                }

                RagdollState ragdollState;
                try { ragdollState = skeleton.GetCurrentRagdollState(); }
                catch
                {
                    pending.NextPulseAt = now + RetryDelay;
                    continue;
                }

                if (ragdollState != RagdollState.ActiveFirstTick && ragdollState != RagdollState.Active)
                {
                    float elapsed = now - pending.CapturedAt;
                    float handoffDelay = SafeSettings.RagdollHandoffDelay;
                    if (!pending.RagdollStartRequested && elapsed >= handoffDelay && _startRagdollAsCorpse != null)
                    {
                        // This flag tracks successful ownership of the accelerated corpse-ragdoll lifecycle.
                        // Failed requests remain eligible for retry and must never enter our finalization path.
                        bool requested = TryStartRagdollAsCorpse(pending.Agent);
                        pending.RagdollStartRequested = requested;
                        if (SafeSettings.DebugLogging)
                        {
                            SafeLog.Info(
                                (requested ? "Requested" : "Failed to request") +
                                " accelerated ragdoll handoff for agent #" + pending.AgentIndex +
                                " after " + elapsed.ToString("0.000") + "s of the original death animation.");
                        }

                        if (requested)
                        {
                            try { ragdollState = skeleton.GetCurrentRagdollState(); }
                            catch { }
                        }
                    }

                    if (ragdollState != RagdollState.ActiveFirstTick && ragdollState != RagdollState.Active)
                    {
                        // A mod-owned corpse can settle into NeedsDeactivation before the force pipeline reaches
                        // its normal completion sentinel. Waiting for Active again is impossible from this state
                        // and previously allowed the 12-second force timeout to discard lifecycle ownership.
                        if (ragdollState == RagdollState.NeedsDeactivation && pending.RagdollStartRequested)
                        {
                            pending.PulseCount = -1;
                            pending.PulseIndex = CorpseFinalizerPendingPulseIndex;
                            pending.RagdollSeenAt = now;
                            pending.CurrentPulseBaseMagnitude = 0f;
                            pending.CapturedAt = now + CorpseFinalizationMainLoopDeferral;
                            pending.NextPulseAt = now + CorpseFinalizationMainLoopDeferral;
                            continue;
                        }

                        pending.NextPulseAt = now + RetryDelay;
                        continue;
                    }
                }

                // Do not inject a large custom force while Bannerlord is still on the
                // ragdoll transition tick. Waiting one mission tick gives the native physics
                // and skinning state time to agree on the same mapped skeleton.
                if (ragdollState == RagdollState.ActiveFirstTick)
                {
                    pending.NextPulseAt = now + RetryDelay;
                    continue;
                }

                if (pending.RagdollSeenAt < 0f)
                {
                    // ActiveFirstTick is already excluded above. Yield one complete mission tick after the
                    // first ordinary Active observation, but never force-update skeleton frames here. Native
                    // skinning/cloth owns that transition; ExtremeRagdoll only chooses the launch route afterward.
                    pending.RagdollSeenAt = now;
                    pending.NextPulseAt = now + RetryDelay;
                    if (SafeSettings.DebugLogging)
                    {
                        SafeLog.Info(
                            "Engine ragdoll became active for agent #" + pending.AgentIndex +
                            "; deferred launch routing by one mission tick without skeleton writes.");
                    }
                    continue;
                }

                DeathLaunchRoute launchRoute = ClothForceBridge.GetDeathLaunchRoute(pending.Agent);
                if (launchRoute == DeathLaunchRoute.NativeHandled)
                {
                    // Genuine missile deaths with a verified non-zero native KillingBlow impulse use exactly
                    // one actuator. The controlled Start/End corpse lifecycle is retained, but no post-ragdoll
                    // translation is added on top of the native impulse.
                    _completedDeaths++;
                    if (pending.RagdollStartRequested)
                    {
                        pending.PulseCount = -1;
                        pending.PulseIndex = CorpseFinalizerPendingPulseIndex;
                        pending.RagdollSeenAt = now;
                        pending.CapturedAt = now + CorpseFinalizationMainLoopDeferral;
                        pending.NextPulseAt = now + CorpseFinalizationMainLoopDeferral;
                    }
                    else
                    {
                        _pending.RemoveAt(i);
                    }
                    if (SafeSettings.DebugLogging)
                    {
                        SafeLog.Info(
                            "Completed native-owned death launch for agent #" + pending.AgentIndex +
                            " route=NATIVE_HANDLED legacyBurst=SUPPRESSED skeletonWrites=SUPPRESSED.");
                    }
                    continue;
                }

                if (string.Equals(pending.KillKind, "mount-collision", StringComparison.Ordinal) &&
                    SafeSettings.MountCollisionKillStrength <= 0f)
                {
                    // Explicit 0 means retain only Bannerlord's native mount shove/charge motion.
                    // Complete the managed corpse lifecycle without adding an ExtremeRagdoll burst.
                    _completedDeaths++;
                    if (pending.RagdollStartRequested)
                    {
                        pending.PulseCount = -1;
                        pending.PulseIndex = CorpseFinalizerPendingPulseIndex;
                        pending.RagdollSeenAt = now;
                        pending.CapturedAt = now + CorpseFinalizationMainLoopDeferral;
                        pending.NextPulseAt = now + CorpseFinalizationMainLoopDeferral;
                    }
                    else
                    {
                        _pending.RemoveAt(i);
                    }
                    if (SafeSettings.DebugLogging)
                    {
                        SafeLog.Info(
                            "Completed mount-collision death with native shove only for agent #" + pending.AgentIndex +
                            " mountCollisionKillStrength=0 legacyBurst=SUPPRESSED.");
                    }
                    continue;
                }

                if (!pending.CompletionGateArmed)
                {
                    pending.CompletionGateArmed = true;

                    // A negative chunk count is the bounded completion sentinel set after the final force
                    // chunk. Proceed directly to the existing
                    // corpse finalization lifecycle on the next eligible mission tick.
                    if (pending.CurrentPulseChunkCount < 0 && pending.PulseIndex >= pending.PulseCount)
                    {
                        pending.CurrentPulseChunkCount = 0;
                        _completedDeaths++;

                        if (pending.RagdollStartRequested)
                        {
                            pending.PulseCount = -1;
                            pending.PulseIndex = CorpseFinalizerPendingPulseIndex;
                            pending.RagdollSeenAt = now;
                            pending.CapturedAt = now + CorpseFinalizationMainLoopDeferral;
                            pending.NextPulseAt = now + CorpseFinalizationMainLoopDeferral;
                        }
                        else
                        {
                            _pending.RemoveAt(i);
                        }
                        continue;
                    }
                }

                if (!IsUsableVector(pending.RemainingPulseForce))
                {
                    float scale = PowSafe(pending.PulseDecay, pending.PulseIndex);
                    float pulseMagnitude = pending.ForceMagnitude * scale;
                    Vec3 pulseDirection = ResolvePulseDirection(pending.Direction, pending.PulseIndex);
                    bool spinPulse = pending.PulseIndex > 0 && pending.PulseIndex % 2 == 1 && SafeSettings.ImpactSpin > 0f;
                    Vec3 fullPulseForce = pulseDirection * pulseMagnitude;
                    float spinMagnitude = 0f;

                    if (pending.PulseIndex == 0)
                    {
                        fullPulseForce = ApplyMomentumCarryover(
                            fullPulseForce, pending.VictimMomentum, pulseDirection);
                    }

                    if (spinPulse)
                    {
                        spinMagnitude = pulseMagnitude * SafeSettings.ImpactSpin;
                        fullPulseForce += ResolveSpinDirection(pulseDirection, pending.PulseIndex) * spinMagnitude;
                    }

                    bool mountCollision = string.Equals(pending.KillKind, "mount-collision", StringComparison.Ordinal);
                    // v1.2.76 restores a visible non-mount deathblow without returning to the full-strength
                    // v1.2.74 force train. Non-mount deaths use a 50% post-ragdoll budget and a larger bounded
                    // central-body integration chunk, producing a compact reaction with far fewer native calls.
                    // Mount collisions retain their independently saved MountCollisionKillStrength scale.
                    float postRagdollScale = mountCollision ? SafeSettings.MountCollisionKillStrength : 0.50f;
                    if (!IsFinite(postRagdollScale) || postRagdollScale < 0f)
                        postRagdollScale = mountCollision ? 0.10f : 0.50f;
                    fullPulseForce *= postRagdollScale;

                    // Deliver each bounded pulse in the existing 15,000-force chunks. Valid lethal hits now
                    // retain their captured impact bone; only unavailable/invalid hit bones use the mapped fallback.
                    // This preserves the configured per-pulse ceiling while avoiding
                    // the ten-call microstep train observed in v1.2.75.
                    float fullPulseMagnitude = (float)Math.Sqrt(Math.Max(0f, fullPulseForce.LengthSquared));
                    float deliveredForceCeiling = SafeSettings.DeliveredForceCeiling;
                    if (deliveredForceCeiling > 0f)
                    {
                        float ceilingScale = SafeSettings.OverallStrength / 6f;
                        if (!IsFinite(ceilingScale) || ceilingScale < 0.25f)
                            ceilingScale = 0.25f;
                        deliveredForceCeiling *= ceilingScale;

                        // Match the native missile route's visual envelope: every deathblow keeps a
                        // strong common baseline, while damage only changes strength modestly. This
                        // prevents fallback/direct deaths from becoming a separate visual class and
                        // prevents the old ceiling saturation from erasing all damage variation.
                        float damageInfluence = SafeSettings.DamageInfluence;
                        if (!IsFinite(damageInfluence) || damageInfluence < 0f)
                            damageInfluence = 0f;
                        float normalizedDamage = pending.Damage * damageInfluence / 1250f;
                        if (!IsFinite(normalizedDamage) || normalizedDamage < 0f)
                            normalizedDamage = 0f;
                        if (normalizedDamage > 1f)
                            normalizedDamage = 1f;
                        float damageDeliveryScale = 0.85f + 0.15f * normalizedDamage;
                        deliveredForceCeiling *= damageDeliveryScale;

                        deliveredForceCeiling *= postRagdollScale;
                    }
                    if (deliveredForceCeiling > 0f && IsFinite(fullPulseMagnitude) && fullPulseMagnitude > deliveredForceCeiling)
                        fullPulseForce *= deliveredForceCeiling / fullPulseMagnitude;

                    pending.RemainingPulseForce = fullPulseForce;
                    pending.CurrentPulseBaseMagnitude = pulseMagnitude;
                    pending.CurrentPulseSpinMagnitude = spinMagnitude;
                    // CurrentPulseChunkCount is already zero for a new PendingDeath and is reset
                    // when each pulse completes. The shipped IL uses this slot immediately after
                    // pulse initialization as the unconditional per-chunk hit-bone reload point.
                }

                Vec3 appliedForce;
                // Reload the killing Blow.BoneIndex for every force chunk, including microsteps 2+
                // that skip pulse initialization on later mission ticks. ApplyForceOnRagdoll has
                // no world/local point overload in Bannerlord 1.3.15, so this is the narrowest native
                // actuator that preserves the real impact body and its natural articulation/torque.
                sbyte applicationBone = pending.HitBone;
                string boneSource;
                float chunkMagnitude;
                // Queue exactly one bounded force chunk for the next pre-mission integration boundary.
                // TryApplyMappedCentralRagdollForce preserves all existing force calculations and returns
                // success only when the deferred call was accepted.
                bool applied = TryApplyMappedCentralRagdollForce(
                    pending.Agent,
                    skeleton,
                    pending.RemainingPulseForce,
                    out appliedForce,
                    ref applicationBone,
                    out boneSource,
                    out chunkMagnitude);
                if (!applied)
                {
                    pending.NextPulseAt = now + RetryDelay;
                    continue;
                }

                pending.RemainingPulseForce -= appliedForce;
                pending.CurrentPulseChunkCount++;
                _forceChunksProcessed++;

                bool exhaustedForce = !IsUsableVector(pending.RemainingPulseForce) ||
                    pending.RemainingPulseForce.LengthSquared <= RemainingForceTinySq;
                bool pulseCompleted = exhaustedForce;

                if (SafeSettings.DebugLogging)
                {
                    SafeLog.Info(
                        "Queued temporally smoothed central-body ragdoll force chunk for pulse " + (pending.PulseIndex + 1) + "/" + pending.PulseCount +
                        " to agent #" + pending.AgentIndex +
                        " kind=" + pending.KillKind +
                        " applicationBone=" + applicationBone +
                        " boneSource=" + boneSource +
                        " originalHitBone=" + pending.HitBone +
                        " baseForce=" + pending.CurrentPulseBaseMagnitude.ToString("0") +
                        (string.Equals(pending.KillKind, "mount-collision", StringComparison.Ordinal) ? " mountCollisionScale=" + SafeSettings.MountCollisionKillStrength.ToString("0.00") : string.Empty) +
                        " chunkVector=" + FormatVec(appliedForce) +
                        " chunkMagnitude=" + chunkMagnitude.ToString("0") +
                        " microstep=" + pending.CurrentPulseChunkCount +
                        " remaining=" + Math.Sqrt(Math.Max(0f, pending.RemainingPulseForce.LengthSquared)).ToString("0") +
                        " directionSource=" + pending.DirectionSource +
                        " spinBias=" + pending.CurrentPulseSpinMagnitude.ToString("0") + ".");
                }

                if (!pulseCompleted)
                {
                    pending.NextPulseAt = now + CentralChunkInterval;
                    continue;
                }

                pending.RemainingPulseForce = Vec3.Zero;
                pending.CurrentPulseBaseMagnitude = 0f;
                pending.CurrentPulseSpinMagnitude = 0f;
                pending.CurrentPulseChunkCount = 0;
                pending.PulseIndex++;

                if (pending.PulseIndex >= pending.PulseCount)
                {
                    // Do not drop force-processing ownership on the same tick as the last large displacement.
                    // Mark only this corpse for one final bone-frame synchronization on the next mission tick.
                    // CompletionGateArmed is deliberately cleared so the bounded one-shot completion gate above
                    // is re-entered without introducing another list, global scan, or persistent callback.
                    pending.CurrentPulseChunkCount = -1;
                    pending.CompletionGateArmed = false;
                    pending.NextPulseAt = now + CentralChunkInterval;
                }
                else
                {
                    pending.NextPulseAt = now + pending.PulseInterval;
                }
            }
        }

        private static Vec3 ChooseBlowDirection(Blow blow, out string source)
        {
            Vec3 direction = Vec3.Zero;
            source = "unknown";

            try
            {
                if (blow.IsMissile && IsUsableVector(blow.WeaponRecord.Velocity))
                {
                    direction = blow.WeaponRecord.Velocity;
                    source = "missileVelocity";
                }
            }
            catch { }

            if (!IsUsableVector(direction) && IsUsableVector(blow.Direction))
            {
                direction = blow.Direction;
                source = "blow.Direction";
            }

            if (!IsUsableVector(direction) && IsUsableVector(blow.SwingDirection))
            {
                direction = blow.SwingDirection;
                source = "blow.SwingDirection";
            }

            return direction;
        }

        private static string ClassifyBlowKind(Blow blow)
        {
            try
            {
                if (blow.IsMissile)
                    return "missile";
                if (blow.IsFallDamage)
                    return "fall";
                if ((blow.BlowFlag & BlowFlags.NoSound) != 0)
                    return "spell-or-scripted";
            }
            catch { }
            return "direct";
        }

        private bool IsDeathState(Agent agent, AgentState state)
        {
            try
            {
                string name = state.ToString();
                if (string.Equals(name, "Killed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Unconscious", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            try
            {
                if (agent != null && agent.Health <= 0f)
                    return true;
            }
            catch { }

            int numeric = (int)state;
            return numeric == 2 || numeric == 3;
        }

        private static bool IsTerminalAgentState(Agent agent)
        {
            if (agent == null)
                return false;
            try
            {
                string name = agent.State.ToString();
                return string.Equals(name, "Killed", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "Unconscious", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "Deleted", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private float GetMissionTime()
        {
            try { return Mission == null ? 0f : Mission.CurrentTime; }
            catch { return 0f; }
        }

        private bool IsCurrentMissionAgent(Agent agent)
        {
            if (agent == null || Mission == null || !ReferenceEquals(Mission.Current, Mission))
                return false;

            try { return ReferenceEquals(agent.Mission, Mission); }
            catch { return false; }
        }

        private static Vec3 ResolveDirection(
            Agent affected,
            Agent affector,
            Vec3 blowDirection,
            Vec3 victimMomentum,
            Vec3 engineImpulse,
            bool hasEngineImpulse,
            string capturedSource,
            out string source)
        {
            // The hit callback already provides the authoritative impact direction. A later
            // KillingBlow.RagdollImpulseAmount is native death-result data and may be expressed
            // with a different sign/space; it is used only when no impact or source geometry exists.
            Vec3 direction = IsFinite(blowDirection) ? blowDirection : Vec3.Zero;
            source = IsUsableVector(direction)
                ? (string.IsNullOrEmpty(capturedSource) ? "capturedImpact" : capturedSource)
                : "unknown";

            Vec3 awayFromAffector;
            bool hasAwayFromAffector = TryGetAwayFromAffectorDirection(
                affected, affector, out awayFromAffector);

            if (!IsUsableVector(direction) && hasAwayFromAffector)
            {
                direction = awayFromAffector;
                source = "awayFromAffector";
            }

            if (!IsUsableVector(direction) && hasEngineImpulse && IsUsableVector(engineImpulse))
            {
                direction = engineImpulse;
                source = "KillingBlow.RagdollImpulseAmountFallbackOnly";
            }

            if (!IsUsableVector(direction) && IsUsableVector(victimMomentum))
            {
                direction = victimMomentum;
                source = "victimMomentumFallbackOnly";
            }

            if (!IsUsableVector(direction))
            {
                try
                {
                    direction = affected.LookDirection;
                    source = "victimLookDirection";
                }
                catch { direction = new Vec3(0f, 1f, 0f); }
            }

            if (!IsUsableVector(direction))
            {
                direction = new Vec3(0f, 1f, 0f);
                source = "worldForwardFallback";
            }

            direction = direction.NormalizedCopy();
            direction.z += SafeSettings.UpwardLift;
            if (!IsUsableVector(direction))
                return new Vec3(0f, 1f, 0.25f).NormalizedCopy();
            return direction.NormalizedCopy();
        }

        private static bool TryGetAwayFromAffectorDirection(
            Agent affected,
            Agent affector,
            out Vec3 awayFromAffector)
        {
            awayFromAffector = Vec3.Zero;
            if (affected == null || affector == null || ReferenceEquals(affected, affector))
                return false;

            try
            {
                awayFromAffector = affected.Position - affector.Position;
                awayFromAffector.z = 0f;
            }
            catch
            {
                awayFromAffector = Vec3.Zero;
                return false;
            }

            if (!IsUsableVector(awayFromAffector))
                return false;
            awayFromAffector = awayFromAffector.NormalizedCopy();
            return true;
        }

        private static Vec3 ApplyMomentumCarryover(
            Vec3 baseForce,
            Vec3 victimMomentum,
            Vec3 launchDirection)
        {
            if (!IsUsableVector(baseForce) || !IsUsableVector(victimMomentum) ||
                !IsUsableVector(launchDirection) || SafeSettings.MomentumCarryover <= 0f)
            {
                return baseForce;
            }

            float momentumForceScale = 3000f * SafeSettings.OverallStrength * SafeSettings.MomentumCarryover;
            Vec3 momentumForce = victimMomentum * momentumForceScale;
            Vec3 axis = launchDirection.NormalizedCopy();
            float parallel = VectorDot(momentumForce, axis);
            if (parallel < 0f)
            {
                // Carry lateral/vertical movement once. Motion directly against the killing blow
                // may reduce neither its sign nor its requested launch strength.
                momentumForce -= axis * parallel;
            }
            return baseForce + momentumForce;
        }

        private static float VectorDot(Vec3 left, Vec3 right)
        {
            return left.x * right.x + left.y * right.y + left.z * right.z;
        }

        private static Vec3 ResolvePulseDirection(Vec3 baseDirection, int pulseIndex)
        {
            // Keep every pulse aligned with the killing blow. Rotation is supplied separately
            // through the off-centre impact-bone impulse, so no artificial left/right jitter is needed.
            if (!IsUsableVector(baseDirection))
                return new Vec3(0f, 1f, 0.25f).NormalizedCopy();
            return baseDirection.NormalizedCopy();
        }

        private static Vec3 CaptureAgentMomentum(Agent agent)
        {
            if (agent == null)
                return Vec3.Zero;

            try
            {
                Vec3 average = agent.AverageVelocity;
                if (IsUsableVector(average))
                    return average;
            }
            catch { }

            try
            {
                Vec2 movement = agent.MovementVelocity;
                Vec3 value = new Vec3(movement.x, movement.y, 0f);
                if (IsUsableVector(value))
                    return value;
            }
            catch { }

            return Vec3.Zero;
        }

        private static Vec3 ReadKillingBlowImpulse(KillingBlow killingBlow)
        {
            try
            {
                Vec3 value = killingBlow.RagdollImpulseAmount;
                return IsFinite(value) ? value : Vec3.Zero;
            }
            catch { return Vec3.Zero; }
        }

        private static int ReadKillingBlowDamage(KillingBlow killingBlow)
        {
            try { return Math.Max(0, killingBlow.InflictedDamage); }
            catch { return 0; }
        }

        private static bool ReadKillingBlowIsMissile(KillingBlow killingBlow)
        {
            try { return killingBlow.IsMissile; }
            catch { return false; }
        }

        private static bool IsUsableVector(Vec3 value)
        {
            return IsFinite(value) && value.LengthSquared >= 0.000001f;
        }

        private static string FormatVec(Vec3 value)
        {
            if (!IsFinite(value))
                return "(invalid)";
            return "(" + value.x.ToString("0.000") + "," + value.y.ToString("0.000") + "," + value.z.ToString("0.000") + ")";
        }

        private static float ComputeForceMagnitude(int damage)
        {
            float force = (30000f + Math.Max(0, damage) * 300f * SafeSettings.DamageInfluence) * SafeSettings.OverallStrength;
            force = Math.Max(force, SafeSettings.MinimumForce);

            float optionalMaximum = SafeSettings.MaximumForce;
            if (optionalMaximum > 0f)
                force = Math.Min(force, optionalMaximum);

            if (!IsFinite(force) || force <= 0f)
                return 0f;
            return force;
        }

        private static float PowSafe(float value, int exponent)
        {
            if (exponent <= 0)
                return 1f;
            double result = Math.Pow(value, exponent);
            if (double.IsNaN(result) || double.IsInfinity(result) || result < 0d)
                return 1f;
            return (float)result;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vec3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static void BindAgentForceApi()
        {
            if (_bound)
                return;

            lock (BindGate)
            {
                if (_bound)
                    return;

                const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo[] methods;
                try { methods = typeof(Agent).GetMethods(Flags); }
                catch { methods = new MethodInfo[0]; }

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null)
                        continue;

                    ParameterInfo[] parameters;
                    try { parameters = method.GetParameters(); }
                    catch { continue; }

                    if (method.Name == "ApplyForceOnRagdoll" && parameters.Length == 2 &&
                        IsIntegerType(parameters[0].ParameterType) && IsVec3Type(parameters[1].ParameterType))
                    {
                        _applyForceOnRagdoll = method;
                    }
                    else if (method.Name == "SetVelocityLimitsOnRagdoll" && parameters.Length == 2 &&
                             Unwrap(parameters[0].ParameterType) == typeof(float) &&
                             Unwrap(parameters[1].ParameterType) == typeof(float))
                    {
                        _setVelocityLimitsOnRagdoll = method;
                    }
                    else if (method.Name == "StartRagdollAsCorpse" && parameters.Length == 0)
                    {
                        _startRagdollAsCorpse = method;
                    }
                    else if (method.Name == "AddAcceleration" && parameters.Length == 1 &&
                             IsVec3Type(parameters[0].ParameterType))
                    {
                        _addAcceleration = method;
                    }
                    else if (method.Name == "HandleBlowAux" && parameters.Length == 1)
                    {
                        Type parameterType = parameters[0].ParameterType;
                        if (parameterType.IsByRef && parameterType.GetElementType() == typeof(Blow))
                            _handleBlowAux = method;
                    }
                }

                try
                {
                    MethodInfo[] visualMethods = typeof(MBAgentVisuals).GetMethods(Flags);
                    for (int i = 0; i < visualMethods.Length; i++)
                    {
                        MethodInfo method = visualMethods[i];
                        if (method == null || method.Name != "GetRealBoneIndex")
                            continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum &&
                            IsIntegerType(method.ReturnType))
                        {
                            _getRealBoneIndex = method;
                            break;
                        }
                    }
                }
                catch { _getRealBoneIndex = null; }

                _bound = true;
            }
        }


        private static bool TryStartRagdollAsCorpse(Agent agent)
        {
            if (agent == null || _startRagdollAsCorpse == null)
                return false;

            try
            {
                // Queue the ownership transition for OnPreMissionTick instead of invoking it after Bannerlord's
                // native mission integration in OnMissionTick. The helper retries only this handoff call on a
                // managed invocation failure; no cloth/visual state is manipulated here.
                ClothForceBridge.DeferInvoke(_startRagdollAsCorpse, agent, null);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("StartRagdollAsCorpse invocation failed", ex.InnerException ?? ex);
                return false;
            }
            catch (Exception ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("StartRagdollAsCorpse invocation failed", ex);
                return false;
            }
        }

        private void QueueVisualResync(Agent agent, float now)
        {
            // This legacy-named helper is now a corpse-ragdoll lifecycle finalizer. It deliberately does not
            // touch renderer/visual reset APIs. A non-negative time only permits the native NeedsDeactivation
            // transition; a negative value is the bounded fallback for a ragdoll that never settles naturally.
            if (agent == null)
                throw new InvalidOperationException("Corpse finalization requires a live Agent wrapper.");

            if (now >= 0f)
            {
                MBAgentVisuals visuals = agent.AgentVisuals;
                Skeleton skeleton = ReferenceEquals(visuals, null) ? null : visuals.GetSkeleton();
                if (ReferenceEquals(skeleton, null))
                    throw new InvalidOperationException("Natural corpse finalization requires an active skeleton wrapper.");

                RagdollState state = skeleton.GetCurrentRagdollState();
                if (state != RagdollState.NeedsDeactivation)
                    return;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo[] methods = typeof(Agent).GetMethods(Flags);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != "EndRagdollAsCorpse")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 0)
                    continue;

                method.Invoke(agent, null);
                _visualResyncPasses++;
                return;
            }

            throw new MissingMethodException(typeof(Agent).FullName, "EndRagdollAsCorpse");
        }

        private void ProcessPendingVisualResyncs(float now)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                try
                {
                    PendingDeath pending = _pending[i];
                    if (pending == null || pending.PulseCount >= 0)
                        continue;

                    // Sentinel entries are cheap to scan every frame, while native visuals/skeleton crossings are
                    // throttled to 10 Hz per corpse. This keeps large battles from turning lifecycle cleanup into
                    // a new per-frame native-call hot path.
                    if (now < pending.CurrentPulseBaseMagnitude)
                        continue;

                    if (pending.Agent == null)
                    {
                        _pending.RemoveAt(i);
                        continue;
                    }

                    Skeleton skeleton = null;
                    try
                    {
                        MBAgentVisuals visuals = pending.Agent.AgentVisuals;
                        skeleton = ReferenceEquals(visuals, null) ? null : visuals.GetSkeleton();
                    }
                    catch { skeleton = null; }

                    float finalizationAge = now - pending.RagdollSeenAt;
                    if (ReferenceEquals(skeleton, null))
                    {
                        if (finalizationAge <= CorpseActiveStateFallbackTimeout)
                        {
                            pending.CurrentPulseBaseMagnitude = now + CorpseFinalizationPollInterval;
                            continue;
                        }

                        if (pending.PulseIndex == CorpseFinalizerPendingPulseIndex)
                        {
                            // The forced fallback does not require a skeleton wrapper; invoke the
                            // paired native end call directly once the short safety bound expires.
                            QueueVisualResync(pending.Agent, -1f);
                            pending.PulseIndex = CorpseFinalizerInvokedPulseIndex;
                        }
                        _pending.RemoveAt(i);
                        continue;
                    }

                    RagdollState state = skeleton.GetCurrentRagdollState();
                    if (state == RagdollState.Disabled)
                    {
                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (state == RagdollState.NeedsDeactivation)
                    {
                        if (pending.PulseIndex == CorpseFinalizerPendingPulseIndex)
                        {
                            // Pair only the StartRagdollAsCorpse call that ExtremeRagdoll itself successfully made.
                            // PulseIndex changes only after Invoke returns, so an exception leaves this retryable.
                            QueueVisualResync(pending.Agent, now);
                            pending.PulseIndex = CorpseFinalizerInvokedPulseIndex;
                        }
                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (finalizationAge <= CorpseActiveStateFallbackTimeout)
                    {
                        pending.CurrentPulseBaseMagnitude = now + CorpseFinalizationPollInterval;
                        continue;
                    }

                    if (pending.PulseIndex == CorpseFinalizerPendingPulseIndex)
                    {
                        // Bannerlord and modded corpse paths can leave a settled corpse in Active.
                        // Begin forced paired finalization at two seconds, leaving one second for
                        // bounded retries without exceeding the three-second collision ceiling.
                        QueueVisualResync(pending.Agent, -1f);
                        pending.PulseIndex = CorpseFinalizerInvokedPulseIndex;
                        if (_dismembermentPlusCompatibility)
                        {
                            SafeLog.Info(
                                "Dismemberment Plus bounded corpse-safety window elapsed; paired EndRagdollAsCorpse for agent #" +
                                SafeAgentIndex(pending.Agent) + ".");
                        }
                    }
                    _pending.RemoveAt(i);
                }
                catch (Exception ex)
                {
                    if (SafeSettings.DebugLogging)
                    {
                        TargetInvocationException invocationException = ex as TargetInvocationException;
                        SafeLog.Error(
                            "Corpse finalization retry failed",
                            invocationException == null || invocationException.InnerException == null
                                ? ex
                                : invocationException.InnerException);
                    }

                    // Retry failed paired finalization only inside the absolute three-second
                    // collision ceiling. Clamp the next poll to that deadline so no 30-second
                    // compatibility path can be reintroduced through failure handling.
                    PendingDeath pending = i >= 0 && i < _pending.Count ? _pending[i] : null;
                    if (pending != null && pending.PulseCount < 0)
                    {
                        float finalizationAge = now - pending.RagdollSeenAt;
                        if (finalizationAge >= CorpseFinalizationHardDeadline)
                        {
                            if (SafeSettings.DebugLogging)
                                SafeLog.Error("Corpse finalization exhausted the three-second retry ceiling", ex);
                            _pending.RemoveAt(i);
                        }
                        else
                        {
                            float hardDeadlineAt = pending.RagdollSeenAt + CorpseFinalizationHardDeadline;
                            pending.CurrentPulseBaseMagnitude =
                                Math.Min(now + CorpseFinalizationPollInterval, hardDeadlineAt);
                        }
                    }
                }
            }
        }

        private static void ForceUpdateCorpseBoneFrames(Agent agent, Skeleton skeleton)
        {
            if (agent == null || ReferenceEquals(skeleton, null))
                return;

            try
            {
                // Narrow native synchronization used by TaleWorlds for corpse/equipment-sensitive paths.
                // This updates the existing skeleton in place; it does not reset visuals, rebuild equipment,
                // invalidate entity frames, restart cloth simulation, or create any persistent work.
                skeleton.ForceUpdateBoneFrames();
            }
            catch (Exception ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("Corpse bone-frame refresh failed", ex);
            }
        }

        private static bool TryApplyMappedCentralRagdollForce(
            Agent agent,
            Skeleton skeleton,
            Vec3 requestedForce,
            out Vec3 processedForce,
            ref sbyte applicationBone,
            out string boneSource,
            out float chunkMagnitude)
        {
            processedForce = Vec3.Zero;
            boneSource = null;
            chunkMagnitude = 0f;

            if (agent == null || ReferenceEquals(skeleton, null) || _applyForceOnRagdoll == null || !IsUsableVector(requestedForce))
                return false;

            // Death callers provide the captured killing Blow.BoneIndex. Live on-damage ragdoll
            // pushes retain their historical central-body route by passing the zero sentinel. A
            // genuine lethal bone 0 remains valid whenever the agent is dead or terminal.
            int boneCount = skeleton.GetBoneCount();
            bool liveDamagePushSentinel = applicationBone == 0 && agent.Health > 0f && !IsTerminalAgentState(agent);
            if (applicationBone < 0 || applicationBone >= boneCount || liveDamagePushSentinel)
                applicationBone = ResolveMappedCentralBone(agent, skeleton, out boneSource);

            float requestedMagnitude = (float)Math.Sqrt(requestedForce.LengthSquared);
            if (!IsFinite(requestedMagnitude) || requestedMagnitude <= 0f)
                return false;

            chunkMagnitude = Math.Min(requestedMagnitude, MaxCentralBoneForcePerTick);
            Vec3 chunkForce = requestedForce * (chunkMagnitude / requestedMagnitude);

            try
            {
                float linearLimit = SafeSettings.MaxLinearVelocity;
                float angularLimit = SafeSettings.MaxAngularVelocity;
                if (_setVelocityLimitsOnRagdoll != null && linearLimit > 0f && angularLimit > 0f)
                    _setVelocityLimitsOnRagdoll.Invoke(agent, new object[] { linearLimit, angularLimit });

                DeathLaunchRoute route = ClothForceBridge.GetDeathLaunchRoute(agent);
                string routeLabel = ClothForceBridge.FormatRoute(route);
                if (route != DeathLaunchRoute.NativeHandled)
                {
                    ParameterInfo[] parameters = _applyForceOnRagdoll.GetParameters();
                    object boneArgument = BoxInteger(parameters[0].ParameterType, applicationBone);
                    // FALLBACK and NATIVE_INEFFECTIVE both need a real post-ragdoll actuator. NativeHandled
                    // missile deaths are the only route that suppresses this translation.
                    ClothForceBridge.DeferInvoke(
                        _applyForceOnRagdoll, agent, new object[] { boneArgument, chunkForce });
                    boneSource += ";route=" + routeLabel + ";legacyBurst=QUEUED";
                }
                else
                {
                    boneSource += ";route=" + routeLabel + ";legacyBurst=SUPPRESSED";
                }
                processedForce = chunkForce;
                return true;
            }
            catch (TargetInvocationException ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("Mapped central-body death-launch route processing failed", ex.InnerException ?? ex);
                return false;
            }
            catch (Exception ex)
            {
                if (SafeSettings.DebugLogging)
                    SafeLog.Error("Mapped central-body death-launch route processing failed", ex);
                return false;
            }
        }

        private static sbyte ResolveMappedCentralBone(Agent agent, Skeleton skeleton, out string source)
        {
            source = "fallback";
            int count;
            try { count = skeleton.GetBoneCount(); }
            catch { return 0; }
            if (count <= 0)
                return 0;

            MBAgentVisuals visuals = null;
            try { visuals = agent == null ? null : agent.AgentVisuals; } catch { }
            if (!ReferenceEquals(visuals, null) && _getRealBoneIndex != null)
            {
                string[] humanBones = { "Abdomen", "Spine1", "Thorax" };
                for (int i = 0; i < humanBones.Length; i++)
                {
                    try
                    {
                        Type enumType = _getRealBoneIndex.GetParameters()[0].ParameterType;
                        object enumValue = Enum.Parse(enumType, humanBones[i], true);
                        object rawResult = _getRealBoneIndex.Invoke(visuals, new[] { enumValue });
                        int mapped = Convert.ToInt32(rawResult, CultureInfo.InvariantCulture);
                        if (mapped >= 0 && mapped < count && mapped <= sbyte.MaxValue)
                        {
                            // Apply the launch one hierarchy level rootward from the mapped torso bone.
                            // HumanBone.Abdomen maps reliably across Bannerlord/TOR humanoid rigs, but driving
                            // that torso rigid body directly can pull it away from pelvis-rooted garment/helper
                            // bones during high-speed flight. Its parent is the pelvis/rootward core on these rigs,
                            // which translates the ragdoll coherently and avoids the long stretched cloth triangles.
                            sbyte mappedBone = (sbyte)mapped;
                            sbyte parentBone = skeleton.GetParentBoneIndex(mappedBone);
                            if (parentBone >= 0 && parentBone < count)
                            {
                                source = "parentOf:HumanBone." + humanBones[i];
                                return parentBone;
                            }

                            source = "HumanBone." + humanBones[i];
                            return mappedBone;
                        }
                    }
                    catch { }
                }
            }

            string[] preferred = { "pelvis", "hips", "root", "abdomen", "spine_0", "spine0", "spine" };
            for (int p = 0; p < preferred.Length; p++)
            {
                for (int i = 0; i < count && i <= sbyte.MaxValue; i++)
                {
                    string name;
                    try { name = skeleton.GetBoneName((sbyte)i); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(name) || name.IndexOf(preferred[p], StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    source = "name:" + name;
                    return (sbyte)i;
                }
            }

            source = "bone0";
            return 0;
        }

        private static Vec3 ResolveSpinDirection(Vec3 baseDirection, int pulseIndex)
        {
            float sign = pulseIndex % 2 == 0 ? 1f : -1f;
            Vec3 direction = new Vec3(-baseDirection.y * sign, baseDirection.x * sign, 0.15f + 0.05f * pulseIndex);
            if (!IsFinite(direction) || direction.LengthSquared < 0.000001f)
                direction = new Vec3(sign, 0f, 0.2f);
            return direction.NormalizedCopy();
        }

        private static sbyte ToSByteBone(int value)
        {
            if (value < sbyte.MinValue)
                return sbyte.MinValue;
            if (value > sbyte.MaxValue)
                return sbyte.MaxValue;
            return (sbyte)value;
        }

        private static Type Unwrap(Type type)
        {
            return type != null && type.IsByRef ? type.GetElementType() : type;
        }

        private static bool IsVec3Type(Type type)
        {
            return Unwrap(type) == typeof(Vec3);
        }

        private static bool IsIntegerType(Type type)
        {
            type = Unwrap(type);
            return type == typeof(sbyte) || type == typeof(byte) ||
                   type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint);
        }

        private static object BoxInteger(Type type, int value)
        {
            type = Unwrap(type);
            if (type == typeof(sbyte)) return (sbyte)Math.Max(sbyte.MinValue, Math.Min(sbyte.MaxValue, value));
            if (type == typeof(byte)) return (byte)Math.Max(byte.MinValue, Math.Min(byte.MaxValue, value));
            if (type == typeof(short)) return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
            if (type == typeof(ushort)) return (ushort)Math.Max(ushort.MinValue, Math.Min(ushort.MaxValue, value));
            if (type == typeof(uint)) return (uint)Math.Max(0, value);
            return value;
        }

        public override MissionBehaviorType BehaviorType
        {
            get { return MissionBehaviorType.Other; }
        }
    }
}

namespace ExtremeRagdoll
{
    public sealed class SubModule : ExtremeRagdoll.SafeRuntime.SafeSubModule { }
}
