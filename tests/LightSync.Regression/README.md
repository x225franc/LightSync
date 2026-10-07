Run on Windows with a .NET SDK supporting net9.0:

```powershell
dotnet run --project tests/LightSync.Regression/LightSync.Regression.csproj -c Debug -p:EngineVariant=Standalone
dotnet run --project tests/LightSync.Regression/LightSync.Regression.csproj -c Debug -p:EngineVariant=Plugin
```

The tests compile each variant's actual Engine, ScreenState and ChromaFrameSender sources with fake transports. They never start discovery threads or send commands to real lights.

Coverage: Govee discovery loss and recovery without power/brightness resets; group changes without reinitialization; startup command spacing and stream-mode failure; Yeelight reconnect with paused capture or no canvas color; atomic first color/brightness scene; concurrent Chroma writes, completion and asynchronous error propagation.

Hardware behavior and native Chroma SDK performance must still be observed on the installed application.
