using LenovoLegionToolkit.Lib.Station.Core;
using LenovoLegionToolkit.Lib.Station.Services;

namespace LightSync.Plugin;

/// <summary>
/// Entry point Toolkit's plugin loader discovers via reflection (any non-abstract <see cref="IExtensionProvider"/>
/// in a DLL dropped under %LOCALAPPDATA%\LenovoLegionToolkit\Plugins). This first version only proves the loading
/// pipeline end to end - registers one nav item with a placeholder page - before the real Ambilight engine
/// (screen capture, Chroma, Yeelight/Govee) gets ported in behind it.
/// </summary>
public sealed class LightSyncProvider : IExtensionProvider
{
    private IExtensionContext? _context;

    public void Initialize(IExtensionContext context)
    {
        _context = context;

        context.Navigation.Register(new ExtensionNavigationItem
        {
            Id = "lightsync",
            Title = "LightSync",
            PageTag = "lightsync",
            PageType = typeof(LightSyncPage),
            Icon = ExtensionIcon.Gauge,
        });

        EngineHost.Start(context.Logger, context.GetPluginStoragePath("lightsync"));

        context.Logger.Trace("LightSync plugin initialized.");
    }

    public Task ExecuteAsync(string action, params object[] args) => Task.CompletedTask;

    public object? GetData(string key) => key switch
    {
        nameof(ExtensionDataKey.Capability) => "lightsync",
        nameof(ExtensionDataKey.Version) => typeof(LightSyncProvider).Assembly.GetName().Version?.ToString(),
        _ => null,
    };

    public void SetData(string key, object? value) { }

    public ValueTask DisposeAsync()
    {
        EngineHost.Stop();
        _context?.Logger.Trace("LightSync plugin disposed.");
        return ValueTask.CompletedTask;
    }
}
