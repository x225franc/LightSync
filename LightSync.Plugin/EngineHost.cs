using System;
using System.IO;
using Ambilight.GUI;
using Ambilight.Lights;
using Ambilight.Logic;
using LenovoLegionToolkit.Lib.Station.Logging;
using Newtonsoft.Json;

namespace LightSync.Plugin;

/// <summary>
/// Composition root for the ported engine - mirrors what the standalone app's Program.Main does. Kept as a static
/// class (not a local inside Initialize) for the exact reason the standalone app's own comment on its
/// LogicManager field explains: the LogicManager transitively owns the Chroma SDK's IChroma instance, which has a
/// finalizer that calls into the native SDK. If nothing keeps it rooted, the GC can collect it mid-session and
/// that finalizer runs while the effect is still live, which crashes the process (AccessViolationException).
/// A static field is rooted for the app's lifetime, same guarantee the standalone app relies on.
/// </summary>
public static class EngineHost
{
    private static bool _started;
    private static string? _settingsPath;
    private static IExtensionLogger? _logger;

    public static TraySettings Settings { get; } = new();
    internal static LogicManager? LogicManagerInstance { get; private set; }

    /// <summary>Raised after a settings save, so open pages can refresh anything derived from the new values
    /// (e.g. re-reading the bulb list after a group change goes through the engine, not through this event).</summary>
    public static event EventHandler? SettingsSaved;

    public static void Start(IExtensionLogger logger, string storagePath)
    {
        if (_started)
            return;

        _started = true;
        _logger = logger;
        NLog.LogManager.Sink = logger;

        _settingsPath = Path.Combine(storagePath, "settings.json");
        LoadSettings();

        LightsService.Start();
        if (LightsService.Engine != null) LightsService.Engine.Enabled = Settings.MasterEnabled;
        LogicManagerInstance = new LogicManager(Settings);
    }

    public static void Stop()
    {
        if (!_started)
            return;

        _started = false;
        LightsService.Stop();
        LogicManagerInstance?.Stop();
        LogicManagerInstance = null;
    }

    /// <summary>Tears down and recreates only the Yeelight/Govee engine - mainly useful to recover from a stuck
    /// bulb connection. Deliberately leaves <see cref="LogicManagerInstance"/> (Razer Chroma, the laptop keyboard,
    /// the lights canvas) untouched: recreating it tears down and re-acquires the Windows Dynamic Lighting
    /// LampArray, which causes a visible glitch/flicker on the keyboard for no reason - this button is not meant
    /// to touch the keyboard at all.</summary>
    public static void Restart()
    {
        if (!_started)
            return;

        LightsService.Stop();
        LightsService.Start();
        if (LightsService.Engine != null) LightsService.Engine.Enabled = Settings.MasterEnabled;
    }

    private static void LoadSettings()
    {
        try
        {
            if (_settingsPath is null || !File.Exists(_settingsPath))
                return;

            var json = File.ReadAllText(_settingsPath);
            JsonConvert.PopulateObject(json, Settings);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Failed to load LightSync plugin settings, using defaults.");
        }
    }

    /// <summary>Call after changing any <see cref="Settings"/> property from the UI.</summary>
    public static void SaveSettings()
    {
        try
        {
            if (_settingsPath is null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(Settings, Formatting.Indented));
            SettingsSaved?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Failed to save LightSync plugin settings.");
        }
    }
}
