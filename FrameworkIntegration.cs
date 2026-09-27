using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;

namespace GK2AutoStationService
{
    // GK2 Mod Framework (https://github.com/SuperMan4eg/GK2-Mod-Framework) is optional and gives
    // the mod a settings page in its Mods menu. every framework type lives behind Register(), which
    // is only entered once the framework assembly is loaded: the runtime resolves a referenced
    // assembly when it compiles a method that uses it, so a player without the framework never
    // touches GK2.Framework and the mod keeps working from its own config file
    internal static class FrameworkBridge
    {
        internal const string FrameworkGuid = "ru.superman4eg.gk2.framework";

        private const string FrameworkAssemblyName = "GK2.Framework";

        internal static bool TryRegister(AutoStationServicePlugin plugin)
        {
            if (plugin == null || !IsFrameworkLoaded())
            {
                return false;
            }

            try
            {
                Register(plugin);
                AutoStationServicePlugin.LogInfo("[ASS] registered with GK2 Mod Framework - the setting shows up in the Mods menu");
                return true;
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] GK2 Mod Framework registration failed: " + ex);
                return false;
            }
        }

        private static bool IsFrameworkLoaded()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    if (string.Equals(assemblies[i].GetName().Name, FrameworkAssemblyName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Register(AutoStationServicePlugin plugin)
        {
            GK2.Framework.FrameworkApi.RegisterMod(new AutoStationFrameworkMod(plugin), plugin.Config);
        }
    }

    internal sealed class AutoStationFrameworkMod : GK2.Framework.Gk2ModBase
    {
        // keys of GK2.Framework/Localization/<ModGuid>/<language>.json; the english constants below are
        // the fallback, so a language without a file (or a player without the files) sees plain english
        private const string LocModName = "mod.name";
        private const string LocModDescription = "mod.description";
        private const string LocSettingName = "settings.caretaker_tech_points.name";
        private const string LocSettingDescription = "settings.caretaker_tech_points.description";
        private const string LocLogName = "settings.detailed_log.name";
        private const string LocLogDescription = "settings.detailed_log.description";

        private const string ModName = "Auto Station Service";
        private const string ModDescription = "Caretaker zombies pick up the finished products from auto-crafting stations that have no zombie assigned.";

        private readonly GK2.Framework.Gk2ModMetadata metadata;
        private readonly IReadOnlyList<GK2.Framework.Gk2ModDependency> dependencies;

        public override GK2.Framework.Gk2ModMetadata Metadata => metadata;

        public override IReadOnlyList<GK2.Framework.Gk2ModDependency> Dependencies => dependencies;

        internal AutoStationFrameworkMod(AutoStationServicePlugin plugin)
        {
            metadata = new GK2.Framework.Gk2ModMetadata(
                AutoStationServicePlugin.ModGuid,
                Localized(LocModName, ModName),
                "zhujingwei",
                AutoStationServicePlugin.ModVersion,
                Localized(LocModDescription, ModDescription),
                supportsRuntimeToggle: false,
                requiresKnownBuild: false,
                // the gameplay side is a plain BepInEx plugin that is always active, so the framework
                // only contributes metadata and the settings page - no Enable/Disable control
                frameworkManagesEnabledState: false);

            dependencies = new GK2.Framework.Gk2ModDependency[]
            {
                new GK2.Framework.Gk2ModDependency(FrameworkBridge.FrameworkGuid, "0.1.0", "0.2.0")
            };
        }

        public override void OnRegister(GK2.Framework.Gk2ModContext context)
        {
            // the labels are captured here and the framework never re-resolves them, so a language
            // change in the game options needs a restart before the menu shows the new texts
            AutoStationServicePlugin.LogInfo($"[ASS] GK2 Mod Framework language: {GK2.Framework.FrameworkLocalization.CurrentLanguage}");

            ConfigEntry<bool> entry = context.Settings.AddToggle(
                AutoStationServicePlugin.TechPointsSection,
                AutoStationServicePlugin.TechPointsKey,
                AutoStationServicePlugin.TechPointsDefault,
                Localized(LocSettingName, AutoStationServicePlugin.TechPointsLabel),
                Localized(LocSettingDescription, AutoStationServicePlugin.TechPointsDescription),
                0);

            entry.SettingChanged += OnSettingChanged;
            AutoStationServicePlugin.CaretakerTakesTechPoints = entry;

            ConfigEntry<bool> logEntry = context.Settings.AddToggle(
                AutoStationServicePlugin.LogSection,
                AutoStationServicePlugin.LogKey,
                AutoStationServicePlugin.LogDefault,
                Localized(LocLogName, AutoStationServicePlugin.LogLabel),
                Localized(LocLogDescription, AutoStationServicePlugin.LogDescription),
                1);

            logEntry.SettingChanged += OnLogSettingChanged;
            AutoStationServicePlugin.DetailedLog = logEntry;
        }

        private static string Localized(string key, string englishFallback) =>
            GK2.Framework.FrameworkLocalization.Get(AutoStationServicePlugin.ModGuid, key, englishFallback);

        private static void OnSettingChanged(object sender, EventArgs e)
        {
            ConfigEntry<bool> entry = sender as ConfigEntry<bool>;
            AutoStationServicePlugin.LogInfo($"[ASS] caretaker takes tech points: {(entry != null && entry.Value ? "on" : "off")}");
        }

        private static void OnLogSettingChanged(object sender, EventArgs e)
        {
            // the change itself has to be reported through the source that was just switched off
            ConfigEntry<bool> entry = sender as ConfigEntry<bool>;
            AutoStationServicePlugin.Log?.LogInfo($"[ASS] detailed log: {(entry != null && entry.Value ? "on" : "off")}");
        }
    }
}
