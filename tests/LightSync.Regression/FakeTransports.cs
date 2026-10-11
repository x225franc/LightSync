// Transport doubles only. The engine and Chroma sender under test are the production source files.
using System;
using System.Collections.Generic;
using System.Net;

namespace Ambilight.Lights
{
    public static class Kinds { public const string Govee = "govee", Yeelight = "yeelight"; }
    public class BulbConfig
    {
        public string Id, Kind, Name;
        public int Group = 1, Brightness = 80;
        public bool Enabled = true, UseScreenPosition;
        public double PosX, PosY, PosW, PosH;
        public bool Controlled => Enabled && (Group > 0 || UseScreenPosition);
    }
    public class Config
    {
        public List<BulbConfig> Bulbs = new List<BulbConfig>();
        public bool ControlEnabled = true, Interpolate, ForceLocalZoneControl;
        public bool TurnOffOnSuspendOrShutdown, TurnOnAtStartupOrResume;
        public int YeelightOffBelow = 2, YeelightFadeMs = 115, YeelightFps = 2, GoveeFps = 25;
        public void Save() { }
    }
    internal class GoveeSeen { public string Mac, Sku, Ip; public IPAddress Local; }
    internal class GoveeStatus { public bool On; public int Brightness, R, G, B, Kelvin; }
    internal class DiscoveredBulb { public string Id, Ip, Model, Name; public int Port; public IPAddress LocalIp; }
    internal static class Discovery { public static List<DiscoveredBulb> Scan(int ms) => new List<DiscoveredBulb>(); }
    internal static class GoveeCatalog { public static int SegmentCount(string id, string model) => 4; }
    internal static class Log
    {
        public static void Info(string message) { }
        public static void Warn(string message) { }
        public static void Error(string message, Exception exception = null) { }
    }
    internal sealed class RazerBroadcast : IDisposable
    {
        public bool Live, IsStarted;
        public int Events;
        public string LastError;
        public bool TryStart() => IsStarted = true;
        public int GetRgb(int zone) => 0;
        public void Dispose() { }
    }
    internal sealed class GoveeNet : IDisposable
    {
        public static GoveeNet Latest;
        public readonly List<string> Calls = new List<string>();
        public bool FailMode;
        public int[] LastGradient;
        public GoveeNet(Action<GoveeSeen> onSeen) { Latest = this; }
        public void Refresh() { }
        public void Scan() { }
        public bool Turn(string ip, IPAddress local, bool on) { Calls.Add("power:" + on); return true; }
        public bool Brightness(string ip, IPAddress local, int value) { Calls.Add("brightness:" + value); return true; }
        public bool RazerMode(string ip, IPAddress local, bool on) { Calls.Add("mode:" + on); return !FailMode; }
        public bool Segments(string ip, IPAddress local, int rgb, int count) { Calls.Add("frame:" + rgb); return true; }
        public bool SegmentColors(string ip, IPAddress local, int[] colors) { Calls.Add("gradient"); LastGradient = (int[])colors.Clone(); return true; }
        public bool Color(string ip, IPAddress local, int rgb) { Calls.Add("color"); return true; }
        public bool Color(string ip, IPAddress local, int r, int g, int b, int kelvin) => Color(ip, local, 0);
        public GoveeStatus QueryStatus(string ip, IPAddress local, int timeout) => null;
        public void Dispose() { }
    }
    internal sealed class MusicLink : IDisposable
    {
        public readonly List<string> Calls = new List<string>();
        public bool IsAlive = true;
        public string CloseReason, LastReply;
        public MusicLink(string ip, int port, IPAddress local) { }
        public static readonly List<string> PowerOffCalls = new List<string>();
        public static bool SendPowerOff(string ip, int port) { PowerOffCalls.Add(ip); return true; }
        public bool Open(Func<bool> cancelled = null) => cancelled == null || !cancelled();
        public bool SetPower(bool on) { Calls.Add("power:" + on); return true; }
        public bool SetBrightness(int brightness) { Calls.Add("brightness:" + brightness); return true; }
        public bool SetColorAndBrightness(int rgb, int brightness) { Calls.Add($"scene:{rgb}:{brightness}"); return true; }
        public bool SetPowerOffSmooth(int duration) { Calls.Add("off"); return true; }
        public bool SetRgb(int rgb) => true;
        public bool SetCt(int kelvin) => true;
        public class Snapshot { public bool On; public int Brightness, ColorMode, Ct, Rgb; }
        public Snapshot QueryState() => null;
        public static int Released;
        public void Dispose() { System.Threading.Thread.Sleep(50); System.Threading.Interlocked.Increment(ref Released); }
    }
}
