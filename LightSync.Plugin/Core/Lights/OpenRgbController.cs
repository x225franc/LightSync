#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;
using OpenRGB.NET;
using Color = OpenRGB.NET.Color;

namespace Ambilight.Lights
{
    /// <summary>One OpenRGB-visible device, as reported by the server - for the UI's device list.</summary>
    public class OpenRgbDeviceInfo
    {
        public string Name;
        public int LedCount;
    }

    /// <summary>
    /// Drives Razer (and any other OpenRGB-supported) hardware directly over OpenRGB's own USB/HID link, instead
    /// of through the Razer Chroma SDK - so it works even on a machine where Razer Synapse is never installed at
    /// all (e.g. a headless PC). Runs its own connection on its own thread, independent of Chroma: whether it is
    /// actually allowed to send colors right now (Chroma normally takes the lead when it is live) is decided by
    /// whoever constructs this, via <see cref="ShouldStreamProvider"/>.
    ///
    /// Each device gets a single flat color, the same "Chroma group" a Yeelight/Govee light can be assigned to
    /// (see <see cref="Config.OpenRgbDevices"/>) - no per-key precision, just ambilight-style lighting. OpenRGB
    /// itself is what actually talks to the hardware (a reverse-engineered USB/HID protocol per device), not us.
    /// </summary>
    public class OpenRgbController : IDisposable
    {
        struct Handle { public int Index; public int LedCount; }

        readonly Dictionary<string, Handle> _devices = new Dictionary<string, Handle>();
        readonly HashSet<int> _customModeSet = new HashSet<int>();

        Thread _thread;
        volatile bool _stop;
        OpenRgbClient _client;
        DateTime _nextConnectAttempt = DateTime.MinValue;

        /// <summary>Whether OpenRGB should actually be sent colors right now - false while Chroma itself is live
        /// and driving these devices (unless the user forced OpenRGB on), so the two never fight over the same
        /// physical device at once.</summary>
        public Func<bool> ShouldStreamProvider;

        public volatile bool Connected;
        public string LastError { get; private set; }
        public List<OpenRgbDeviceInfo> DiscoveredDevices { get; private set; } = new List<OpenRgbDeviceInfo>();

        // Resolved fresh every tick (not captured once at construction) - LightsService.Config can still be null
        // for a moment at startup depending on init order, exactly like LightsCanvasLogic/RazerCanvasFeed already
        // have to account for.
        static Config Cfg { get { return LightsService.Config; } }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OpenRGB" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _thread?.Join(1000); } catch { }
            _thread = null;
            DisconnectClient();
        }

        void Loop()
        {
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception e) { LastError = e.Message; }
                Thread.Sleep(100);      // a flat ambilight color per device - no need to go faster than this
            }
        }

        void Tick()
        {
            var config = Cfg;
            if (config == null || !config.OpenRgbEnabled)
            {
                DisconnectClient();
                return;
            }

            if (_client == null)
            {
                if (DateTime.UtcNow < _nextConnectAttempt) return;
                TryConnect(config);
                return;
            }

            bool shouldStream = ShouldStreamProvider == null || ShouldStreamProvider();
            var engine = LightsService.Engine;

            foreach (var kv in _devices)
            {
                var cfg = config.GetOpenRgbDevice(kv.Key);
                if (!shouldStream || !cfg.Enabled || cfg.Group <= 0)
                    continue;      // left alone entirely - no forced-off color, just nothing sent

                int index = kv.Value.Index;
                if (_customModeSet.Add(index))
                {
                    try { _client.SetCustomMode(index); }
                    catch (Exception e) { LastError = e.Message; DisconnectClient(); return; }
                }

                int rgb = engine != null ? engine.GetGroupRgb(cfg.Group) : 0;
                var color = new Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
                var colors = new Color[kv.Value.LedCount];
                for (int i = 0; i < colors.Length; i++) colors[i] = color;

                try { _client.UpdateLeds(index, colors); }
                catch (Exception e) { LastError = e.Message; DisconnectClient(); return; }
            }
        }

        void TryConnect(Config config)
        {
            try
            {
                var client = new OpenRgbClient(config.OpenRgbHost, config.OpenRgbPort, "LightSync", false, 1000);
                client.Connect();

                var found = client.GetAllControllerData();
                _devices.Clear();
                _customModeSet.Clear();
                var discovered = new List<OpenRgbDeviceInfo>();
                for (int i = 0; i < found.Length; i++)
                {
                    var d = found[i];
                    int ledCount = d.Leds?.Length ?? 0;
                    if (ledCount <= 0 || string.IsNullOrEmpty(d.Name)) continue;
                    _devices[d.Name] = new Handle { Index = i, LedCount = ledCount };
                    discovered.Add(new OpenRgbDeviceInfo { Name = d.Name, LedCount = ledCount });
                }

                DiscoveredDevices = discovered;
                _client = client;
                Connected = true;
                LastError = null;
                Log.Info("OpenRGB: connected, " + discovered.Count + " device(s) with LEDs found.");
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Connected = false;
                _nextConnectAttempt = DateTime.UtcNow.AddSeconds(10);
            }
        }

        void DisconnectClient()
        {
            var client = _client;
            _client = null;
            Connected = false;
            _devices.Clear();
            _customModeSet.Clear();
            DiscoveredDevices = new List<OpenRgbDeviceInfo>();
            if (client != null) { try { client.Dispose(); } catch { } }
        }

        public void Dispose() { Stop(); }
    }
}
