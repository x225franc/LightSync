# Yeelight release protocol regression

Runs the production MusicLink source against a local TCP server (no physical bulbs).
Checks the single-argument set_music stop command, delayed and fragmented acknowledgement,
unsolicited notifications, error replies, socket closure, and idempotent disposal.

```powershell
dotnet run --project tests/Yeelight.Protocol/Yeelight.Protocol.csproj -c Debug -p:EngineVariant=Standalone
dotnet run --project tests/Yeelight.Protocol/Yeelight.Protocol.csproj -c Debug -p:EngineVariant=Plugin
```
