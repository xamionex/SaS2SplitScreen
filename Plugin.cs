using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Timers;
using BepInEx;
using BepInEx.NET.Common;
using HarmonyLib;

namespace SaS2SplitScreen;

[BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
[BepInDependency("amione.SaS2ModOptions", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("amione.SaS2DevTools", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("amione.SaS2IndicatorsColorChanger", BepInDependency.DependencyFlags.SoftDependency)]
// ReSharper disable once ClassNeverInstantiated.Global
public class SaS2SplitScreen : BasePlugin
{
    internal static SaS2SplitScreen Instance;
    private FileSystemWatcher _configWatcher;
    private Timer _debounceTimer;
    private Harmony _harmony;

    public override void Load()
    {
        Instance = this;

        GlobalSettings.Bind(Config);

        var modOptionsType = Type.GetType("SaS2ModOptions.SaS2ModOptions, amione.SaS2ModOptions");
        if (modOptionsType != null)
        {
            TryRegisterModOptions();
            Log.LogInfo("Registered configs with SaS2ModOptions.");
        }
        else
        {
            Log.LogInfo("SaS2ModOptions not present, config file only.");
        }

        var dir = Path.GetDirectoryName(Config.ConfigFilePath);
        var file = Path.GetFileName(Config.ConfigFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            _configWatcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _debounceTimer = new Timer(1000) { AutoReset = false };
            _debounceTimer.Elapsed += (_, _) =>
            {
                Config.Reload();
                Log.LogInfo("Configuration reloaded.");
            };
            _configWatcher.Changed += (_, _) =>
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            };
        }

        _harmony = new Harmony(PluginInfo.PluginGuid);
        _harmony.PatchAll();
        Log.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} loaded.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryRegisterModOptions()
    {
        const string category = "Auto-Disable";
        var order = 0;

        SaS2ModOptions.SaS2ModOptions.RegisterConfig(GlobalSettings.SplitscreenEnabled, "Splitscreen", category,
            "Splitscreen", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(GlobalSettings.IndependentDoors, "Splitscreen", category,
            "Allow P1/2 to travel through doors independently", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(GlobalSettings.AutoDisableWhenClose, "Splitscreen", category,
            "Auto-Disable When Players Are Close", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(GlobalSettings.AutoDisableInBossFights, "Splitscreen",
            category, "Auto-Disable in Boss-esque Fights", order += 1);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(GlobalSettings.AutoDisableDistanceMode, "Splitscreen",
            category, "Auto-Disable Distance", order += 1, acceptableValues: GlobalSettings.DistanceModes);
        SaS2ModOptions.SaS2ModOptions.RegisterConfig(GlobalSettings.AutoDisableCustomDistance, "Splitscreen",
            category, "Custom Distance (meters)", order += 1);
    }

    public override bool Unload()
    {
        _configWatcher?.Dispose();
        _debounceTimer?.Dispose();
        _harmony?.UnpatchSelf();
        return base.Unload();
    }
}