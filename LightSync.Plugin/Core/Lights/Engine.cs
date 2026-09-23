#nullable disable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Ambilight.Lights
{
    public enum DeviceKind { Govee, Yeelight }

    public enum BulbStatus { NotControlled, Paused, Searching, Connecting, Connected, Unreachable }

    /// <summary>Runtime state of one device, shared between the engine threads and the UI (read-only there).</summary>
    public class BulbState
    {
        public BulbConfig Config;
        public volatile string Ip;
        public int Port = 55443;
        public IPAddress LocalIp;
        public string Model, DeviceName, Error;
        public volatile BulbStatus Status = BulbStatus.Searching;
        public volatile int CurrentRgb;
        /// <summary>Set by the screen canvas feed each frame for a device positioned on it, 0xRRGGBB; -1 = no frame sampled yet.
        /// Bypasses Razer Chroma Connect entirely - read only when <see cref="Config"/>.UseScreenPosition is on.</summary>
        internal volatile int ScreenRgb = -1;
        /// <summary>Govee only, canvas mode: one color per real segment, sampled left-to-right across the device's
        /// canvas rectangle - a true gradient read straight off the screen. Null/1 element = use ScreenRgb instead.</summary>
        internal volatile int[] ScreenColors;

        public DeviceKind Kind { get { return Config.Kind == Kinds.Govee ? DeviceKind.Govee : DeviceKind.Yeelight; } }

        internal MusicLink Link;              // Yeelight only: the music-mode session
        internal bool Connecting;
        internal DateTime NextAttempt = DateTime.MinValue;
        internal int Failures;
        internal int FpsCap = int.MaxValue;   // Yeelight: lowered when this bulb keeps closing its session (a weak bulb or Wi-Fi link)
        internal DateTime LinkSince;
        internal int LastSentRgb = -1;       // Govee: last color; Yeelight: last (color, brightness) pair as one number
        internal bool YeelightOff;            // Yeelight: turned off because the mapped part of the screen is black
        internal bool YeelightFading;         // Yeelight: fading out just before switching off
        internal DateTime YeelightFadeStart;
        internal int YeelightFadeBright, YeelightFadeColor, YeelightFadeLastStep;
        internal int LastNormColor = 0xFFFFFF; // Yeelight: hue/saturation of the last non-black color (kept while the scene is black)
        internal DateTime LastSentAt = DateTime.MinValue;
        internal volatile bool BrightnessDirty;
        internal volatile bool Identifying;   // a preview (blink) is running: the color loop leaves this device alone
        internal long LastSeenTicks;          // Govee only: last time the device answered the network search
        internal bool GoveeReady;             // Govee only: powered on and brightness applied since control started
        internal bool GoveeStreaming;         // Govee only: the device is currently in Razer stream mode
        internal int LastStreamRgb = -1;      // Govee only: the last color streamed (average for a gradient), frozen when control stops
        internal int GoveeRamp = 3;           // Govee only: brightness fade-in progress after a start (3 = done)
        internal DateTime GoveeRampAt;
        internal int GoveeSegmentCount;       // Govee only: looked up once (0 = not yet)
        internal int GoveeInitStep;           // Govee only: 0 = not started, 1 = power/brightness sent, 2 = stream mode set
        internal DateTime GoveeInitAt;        // Govee only: when the last init step was sent (steps are spaced out)
    }

    /// <summary>
    /// Keeps the devices found, connected and fed with the colors Razer Chroma Connect broadcasts.
    /// Everything that can fail (Synapse not started yet, a device rebooting, Wi-Fi drop, a changed IP)
    /// is retried on its own, so nothing needs a restart or a manual toggle in a phone app.
    /// </summary>
    public class Engine : IDisposable
    {
        const int TickMs = 16;                                       // ~60 loop iterations per second, so a new color is never left waiting
        static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);
        public const int SpreadGroup = 6;    // Govee only: "all zones" instead of a single one

        int SegmentsOf(BulbState b)
        {
            if (b.GoveeSegmentCount == 0) b.GoveeSegmentCount = GoveeCatalog.SegmentCount(b.Config.Id, b.Model);
            return b.GoveeSegmentCount;
        }

        /// <summary>Blends the four Chroma groups (Group 1..4 = CL2..CL5) into n colors: a smooth gradient along the device.</summary>
        int[] SpreadColors(int n)
        {
            var zones = new int[4];
            for (int i = 0; i < 4; i++) zones[i] = ZoneRgb(i + 1);
            var result = new int[n];
            for (int i = 0; i < n; i++)
            {
                double t = n == 1 ? 1.5 : i * 3.0 / (n - 1);
                int lo = (int)Math.Floor(t), hi = Math.Min(3, lo + 1);
                double f = t - lo;
                result[i] = (Lerp(zones[lo] >> 16, zones[hi] >> 16, f) << 16) | (Lerp(zones[lo] >> 8, zones[hi] >> 8, f) << 8) | Lerp(zones[lo], zones[hi], f);
            }
            return result;
        }

        static int Lerp(int a, int b, double f)
        {
            a &= 0xFF; b &= 0xFF;
            return (int)Math.Round(a + (b - a) * f);
        }
        static readonly TimeSpan GoveeAliveWindow = TimeSpan.FromSeconds(45);

        readonly Config _config;
        readonly RazerBroadcast _razer = new RazerBroadcast();
        readonly GoveeNet _govee;
        readonly List<BulbState> _bulbs = new List<BulbState>();
        readonly Thread _loop, _scanner;
        readonly ManualResetEventSlim _scanNow = new ManualResetEventSlim(false);
        readonly Timer _saveTimer;
        volatile bool _stop;
        DateTime _nextRazerTry = DateTime.MinValue;

        // Interpolation: the five Chroma zones eased towards their latest value (RGB per zone).
        readonly double[] _smooth = new double[15];
        bool _smoothInit;
        DateTime _smoothAt;
        const double SmoothTau = 0.04;     // seconds: about two Chroma frames

        public volatile bool Scanning;
        public DateTime LastScan { get; private set; }

        public Engine(Config config)
        {
            _config = config;
            _govee = new GoveeNet(OnGoveeSeen);
            _saveTimer = new Timer(_ => _config.Save(), null, Timeout.Infinite, Timeout.Infinite);

            lock (_config.Bulbs)
                foreach (var b in _config.Bulbs)
                    _bulbs.Add(new BulbState { Config = b, Status = b.Controlled ? BulbStatus.Searching : BulbStatus.NotControlled, BrightnessDirty = true });

            _loop = new Thread(Loop) { IsBackground = true, Name = "Light color loop" };
            _scanner = new Thread(ScanLoop) { IsBackground = true, Name = "Light discovery" };
        }

        public void Start() { _loop.Start(); _scanner.Start(); }

        public List<BulbState> Bulbs { get { lock (_bulbs) return new List<BulbState>(_bulbs); } }

        /// <summary>Master switch. Turning it off releases every device; turning it on reconnects them.</summary>
        public bool Enabled
        {
            get { return _config.ControlEnabled; }
            set
            {
                if (_config.ControlEnabled == value) return;
                _config.ControlEnabled = value;
                Log.Info(value ? "Light control resumed." : "Light control paused.");
                if (value) _scanNow.Set();
                ScheduleSave();
            }
        }

        public string ChromaStatus
        {
            get
            {
                if (!Enabled) return "Light control is off - the lights keep their last color";
                if (!_razer.IsStarted) return _razer.LastError ?? "Waiting for Razer Synapse...";
                if (_razer.Live) return "Receiving Chroma colors";
                if (_razer.Events == 0) return "Connected to Razer - no lighting data yet (is Chroma Connect enabled for Yeelight in Synapse?)";
                return "Connected to Razer - Chroma Connect is paused";
            }
        }
        public bool ChromaLive { get { return _razer.Live; } }

        /// <summary>Diagnostic only: the five raw Broadcast colors (CL1..CL5) as 0xRRGGBB, straight from Razer.</summary>
        public int[] RawZoneColors { get { return new[] { _razer.GetRgb(0), _razer.GetRgb(1), _razer.GetRgb(2), _razer.GetRgb(3), _razer.GetRgb(4) }; } }

        public void Rescan() { _scanNow.Set(); }

        /// <summary>Blend between Chroma frames so colors glide instead of stepping ~20 times a second.</summary>
        public bool Interpolate
        {
            get { return _config.Interpolate; }
            set { _config.Interpolate = value; _smoothInit = false; ScheduleSave(); }
        }

        void UpdateSmoothing(DateTime now)
        {
            if (!_config.Interpolate || _razer.Events == 0) { _smoothInit = false; return; }
            double dt = _smoothInit ? (now - _smoothAt).TotalSeconds : 0;
            double a = _smoothInit ? 1 - Math.Exp(-dt / SmoothTau) : 1;     // first frame: jump straight to the color
            _smoothAt = now;
            for (int z = 0; z < 5; z++)
            {
                int raw = _razer.GetRgb(z);
                for (int c = 0; c < 3; c++)
                {
                    double target = (raw >> (16 - 8 * c)) & 0xFF;
                    _smooth[z * 3 + c] += (target - _smooth[z * 3 + c]) * a;
                }
                if (!_smoothInit) for (int c = 0; c < 3; c++) _smooth[z * 3 + c] = (raw >> (16 - 8 * c)) & 0xFF;
            }
            _smoothInit = true;
        }

        /// <summary>Current color of a Chroma zone (0..4): the smoothed one when interpolation is on.</summary>
        int ZoneRgb(int zone)
        {
            if (!_config.Interpolate || !_smoothInit) return _razer.GetRgb(zone);
            int r = (int)Math.Round(_smooth[zone * 3]), g = (int)Math.Round(_smooth[zone * 3 + 1]), b = (int)Math.Round(_smooth[zone * 3 + 2]);
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>Same group numbering a bulb's Group uses (1..4 = Chroma group 1..4): the color a Razer device
        /// assigned to that group should be painted, as 0xRRGGBB. Lets a keyboard/mouse/mousepad/headset/keypad
        /// mirror the same 4 zones a Yeelight/Govee light can be assigned to instead of sampling its own screen
        /// region.</summary>
        public int GetGroupRgb(int group) { return ZoneRgb(Math.Min(group, 5) - 1); }

        /// <summary>Scene brightness (percent) below which Yeelight bulbs switch off; 0 disables that.</summary>
        public int YeelightOffBelow
        {
            get { return Math.Max(0, Math.Min(25, _config.YeelightOffBelow)); }
            set { _config.YeelightOffBelow = Math.Max(0, Math.Min(25, value)); ScheduleSave(); }
        }

        /// <summary>How long (ms) a Yeelight bulb fades out before switching off in the dark; 0 = at once.</summary>
        public int YeelightFadeMs
        {
            get { return Math.Max(0, Math.Min(1500, _config.YeelightFadeMs)); }
            set { _config.YeelightFadeMs = Math.Max(0, Math.Min(1500, value)); ScheduleSave(); }
        }

        /// <summary>Color updates per second sent to each Yeelight bulb. Capped well below the bulb's real LAN
        /// Control quota (60 commands/min, ~1/s, per TCP session) - going much above it gets the session killed by
        /// the bulb itself after a few seconds, not just throttled.</summary>
        public int YeelightFps
        {
            get { return Math.Max(1, Math.Min(10, _config.YeelightFps)); }
            set { _config.YeelightFps = Math.Max(1, Math.Min(10, value)); ScheduleSave(); }
        }

        /// <summary>Color updates per second sent to each Govee device (Govee devices start to choke above ~25/s).</summary>
        public int GoveeFps
        {
            get { return Math.Max(1, Math.Min(30, _config.GoveeFps)); }
            set { _config.GoveeFps = Math.Max(1, Math.Min(30, value)); ScheduleSave(); }
        }

        // ---- changes made from the UI ----

        public void SetGroup(BulbState bulb, int group)
        {
            bulb.Config.Group = Math.Max(0, Math.Min(bulb.Kind == DeviceKind.Govee ? 6 : 5, group));
            ApplyControlledChange(bulb);
            ScheduleSave();
        }

        /// <summary>Per-light on/off toggle: turning it off releases the device right away but keeps its Chroma
        /// group, so turning it back on resumes on the same group instead of it having to be picked again.</summary>
        public void SetEnabled(BulbState bulb, bool enabled)
        {
            bulb.Config.Enabled = enabled;
            ApplyControlledChange(bulb);
            ScheduleSave();
        }

        void ApplyControlledChange(BulbState bulb)
        {
            if (!bulb.Config.Controlled) bulb.Status = BulbStatus.NotControlled;
            else if (bulb.Status == BulbStatus.NotControlled) { bulb.Status = bulb.Ip == null ? BulbStatus.Searching : BulbStatus.Connecting; bulb.NextAttempt = DateTime.MinValue; }
            bulb.LastSentRgb = -1;
            bulb.GoveeReady = false; bulb.GoveeInitStep = 0;
        }

        public void SetBrightness(BulbState bulb, int percent)
        {
            bulb.Config.Brightness = Math.Max(1, Math.Min(100, percent));
            bulb.BrightnessDirty = true;
            ScheduleSave();
        }

        /// <summary>Switches a device between following its Chroma group and following a rectangle on the lights canvas.</summary>
        public void SetUseScreenPosition(BulbState bulb, bool value)
        {
            bulb.Config.UseScreenPosition = value;
            bulb.ScreenRgb = -1;
            ApplyControlledChange(bulb);
            ScheduleSave();
        }

        /// <summary>Moves/resizes a device's rectangle on the lights canvas (fractions of the screen, 0-1).</summary>
        public void SetScreenPosition(BulbState bulb, double x, double y, double w, double h)
        {
            w = Math.Max(0.02, Math.Min(1, w));
            h = Math.Max(0.02, Math.Min(1, h));
            bulb.Config.PosX = Math.Max(0, Math.Min(1 - w, x));
            bulb.Config.PosY = Math.Max(0, Math.Min(1 - h, y));
            bulb.Config.PosW = w;
            bulb.Config.PosH = h;
            ScheduleSave();
        }

        /// <summary>Called once per captured frame, for a device positioned on the lights canvas, with the dominant
        /// color of its rectangle - bypasses Razer Chroma Connect entirely (see <see cref="BulbState.ScreenRgb"/>).</summary>
        public void SetScreenColor(BulbState bulb, int rgb) { bulb.ScreenRgb = rgb; }

        /// <summary>Same, but one color per real Govee segment (a true gradient sampled straight off the screen).</summary>
        public void SetScreenColors(BulbState bulb, int[] rgbs) { bulb.ScreenColors = rgbs; bulb.ScreenRgb = rgbs.Length > 0 ? rgbs[0] : -1; }

        public void SetName(BulbState bulb, string name)
        {
            bulb.Config.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            ScheduleSave();
        }

        void ScheduleSave() { _saveTimer.Change(500, Timeout.Infinite); }

        // ---- preview ----

        /// <summary>
        /// Preview: blinks the device white a few times so it can be told apart from the others, then puts it
        /// back as it was. Works for devices that are controlled (the color loop pauses on this one) and
        /// for ones set to "Off".
        /// </summary>
        public void Identify(BulbState bulb)
        {
            if (bulb.Ip == null || bulb.Identifying) return;
            bulb.Identifying = true;
            Task.Run(() => { if (bulb.Kind == DeviceKind.Govee) IdentifyGovee(bulb); else IdentifyYeelight(bulb); });
        }

        void IdentifyYeelight(BulbState b)
        {
            MusicLink temp = null;
            try
            {
                var link = b.Link;
                MusicLink.Snapshot before = null;
                if (link == null || !link.IsAlive)
                {
                    temp = new MusicLink(b.Ip, b.Port, b.LocalIp);
                    if (!temp.Open()) { Log.Warn("Preview: cannot reach " + b.Ip + " (" + temp.LastReply + ")"); return; }
                    before = temp.QueryState();
                    link = temp;
                }

                // White, then bright/dim alternating (a black color is ignored by the bulbs, brightness is not).
                link.SetPower(true);
                link.SetRgb(0xFFFFFF);
                Breathe(v => link.SetBrightness(v), 40);

                if (temp != null && before != null)
                {
                    link.SetBrightness(before.Brightness);
                    if (before.ColorMode == 2) link.SetCt(before.Ct); else link.SetRgb(before.Rgb);
                    link.SetPower(before.On);
                    Thread.Sleep(150);
                }
            }
            catch (Exception e) { Log.Warn("Preview of " + b.Ip + " failed: " + e.Message); }
            finally
            {
                if (temp != null) temp.Dispose();
                EndIdentify(b);
            }
        }

        void IdentifyGovee(BulbState b)
        {
            try
            {
                var before = _govee.QueryStatus(b.Ip, b.LocalIp, 1500);
                _govee.Turn(b.Ip, b.LocalIp, true);
                _govee.Color(b.Ip, b.LocalIp, 255, 255, 255, 0);
                Breathe(v => _govee.Brightness(b.Ip, b.LocalIp, v), 90);      // Govee devices cope with ~11 commands a second

                // Controlled devices go back to the Chroma color on their own; the others get their old state back.
                if (before != null && (!b.Config.Controlled || !_config.ControlEnabled))
                {
                    _govee.Brightness(b.Ip, b.LocalIp, before.Brightness);
                    _govee.Color(b.Ip, b.LocalIp, before.R, before.G, before.B, before.Kelvin);
                    _govee.Turn(b.Ip, b.LocalIp, before.On);
                }
            }
            catch (Exception e) { Log.Warn("Preview of " + b.Ip + " failed: " + e.Message); }
            finally { EndIdentify(b); }
        }

        /// <summary>Three slow breaths (100% down to ~10% and back), so the preview is easy on the eyes but still unmistakable.</summary>
        static void Breathe(Action<int> setBrightness, int stepMs)
        {
            const double period = 1400;     // ms per breath
            const int breaths = 3;
            int steps = (int)(period * breaths / stepMs);
            for (int i = 0; i <= steps; i++)
            {
                double t = i * stepMs / period;
                int v = (int)Math.Round(55 + 45 * Math.Cos(2 * Math.PI * t));       // 100 -> 10 -> 100
                setBrightness(Math.Max(1, v));
                Thread.Sleep(stepMs);
            }
        }

        static void EndIdentify(BulbState b)
        {
            b.YeelightOff = false;         // the preview switched the bulb on
            b.YeelightFading = false;
            b.LastSentRgb = -1;            // resend the current Chroma color right away
            b.BrightnessDirty = true;      // and the configured brightness
            b.Identifying = false;
        }

        // ---- discovery ----

        void ScanLoop()
        {
            while (!_stop)
            {
                try
                {
                    Scanning = true;
                    _govee.Refresh();
                    _govee.Scan();                              // Govee answers arrive on their own threads...
                    var found = Discovery.Scan(3000);           // ...while Yeelight is searched (blocks ~3 s)
                    _govee.Scan();
                    Scanning = false;
                    LastScan = DateTime.Now;
                    Merge(found);
                }
                catch (Exception e) { Scanning = false; Log.Error("Discovery failed.", e); }

                // Search often while something is missing; once everything is connected keep looking
                // every 15 s so a device that is added (or comes back) shows up almost immediately.
                int waitMs = AnythingMissing() ? 4000 : 15000;
                _scanNow.Wait(waitMs);
                _scanNow.Reset();
            }
        }

        bool AnythingMissing()
        {
            foreach (var b in Bulbs)
                if (b.Config.Controlled && b.Status != BulbStatus.Connected) return true;
            return false;
        }

        BulbState FindState(string id)
        {
            foreach (var b in Bulbs)
                if (string.Equals(b.Config.Id, id, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        BulbState AddNew(string id, string kind)
        {
            // Starts off (the toggle) with a sensible group already picked, ready for one click.
            var cfg = new BulbConfig { Id = id, Kind = kind, Group = 1, Enabled = false, Brightness = 100 };
            lock (_config.Bulbs) _config.Bulbs.Add(cfg);
            var state = new BulbState { Config = cfg, Status = BulbStatus.NotControlled, BrightnessDirty = true };
            lock (_bulbs) _bulbs.Add(state);
            ScheduleSave();
            return state;
        }

        void Merge(List<DiscoveredBulb> found)
        {
            foreach (var d in found)
            {
                var state = FindState(d.Id);
                if (state == null)
                {
                    state = AddNew(d.Id, Kinds.Yeelight);
                    Log.Info("New Yeelight found: " + d.Id + " at " + d.Ip + " (" + d.Model + ")");
                }

                if (state.Ip != d.Ip)
                {
                    if (state.Ip != null) Log.Info("Bulb " + d.Id + " moved from " + state.Ip + " to " + d.Ip);
                    state.Ip = d.Ip;
                    state.NextAttempt = DateTime.MinValue;
                    DropLink(state);
                }
                state.Port = d.Port; state.LocalIp = d.LocalIp; state.Model = d.Model; state.DeviceName = d.Name;
                if (state.Status == BulbStatus.Searching) state.Status = BulbStatus.Connecting;
            }
        }

        /// <summary>Called on a Govee receive thread each time a device answers the network search.</summary>
        void OnGoveeSeen(GoveeSeen s)
        {
            BulbState state;
            bool isNew = false;
            lock (_bulbs)
            {
                state = FindState(s.Mac);
                if (state == null) { state = AddNew(s.Mac, Kinds.Govee); isNew = true; }
            }

            if (state.Ip != s.Ip)
            {
                if (state.Ip != null) Log.Info("Govee " + s.Mac + " moved from " + state.Ip + " to " + s.Ip);
                state.Ip = s.Ip;
                state.GoveeReady = false; state.GoveeInitStep = 0;
            }
            state.LocalIp = s.Local;
            state.Model = s.Sku;
            Interlocked.Exchange(ref state.LastSeenTicks, DateTime.UtcNow.Ticks);
            if (state.Status == BulbStatus.Searching) state.Status = BulbStatus.Connecting;

            if (isNew)
            {
                Log.Info("New Govee found: " + s.Mac + " at " + s.Ip + " (" + s.Sku + ")");
                // Start from the brightness the device has now, so adding it does not suddenly change it.
                Task.Run(() =>
                {
                    try
                    {
                        var st = _govee.QueryStatus(s.Ip, s.Local, 2000);
                        if (st != null) { state.Config.Brightness = Math.Max(1, Math.Min(100, st.Brightness)); ScheduleSave(); }
                    }
                    catch (Exception e)
                    {
                        // Fire-and-forget: an unguarded exception here would otherwise surface much later (and
                        // confusingly) via the finalizer thread once this Task is garbage collected unobserved.
                        Log.Warn("Could not query initial status for Govee " + s.Ip + ": " + e.Message);
                    }
                });
            }
        }

        // ---- color loop ----

        void Loop()
        {
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception e) { Log.Error("Color loop error.", e); }
                Thread.Sleep(TickMs);
            }
        }

        void Tick()
        {
            DateTime now = DateTime.UtcNow;
            if (!_razer.IsStarted && now >= _nextRazerTry)
            {
                _razer.TryStart();
                _nextRazerTry = now.AddSeconds(5);
            }

            UpdateSmoothing(now);
            bool enabled = _config.ControlEnabled;
            foreach (var b in Bulbs)
            {
                bool controlled = b.Config.Controlled;
                if (!enabled || !controlled)
                {
                    if (b.Link != null) DropLink(b);
                    ReleaseGovee(b);
                    b.Status = controlled ? BulbStatus.Paused : BulbStatus.NotControlled;
                    continue;
                }
                if (b.Ip == null) { b.Status = BulbStatus.Searching; continue; }

                int group = b.Config.Group;
                if (b.Kind == DeviceKind.Govee) TickGovee(b, group, now);
                else TickYeelight(b, group, now);
            }
        }

        void TickYeelight(BulbState b, int group, DateTime now)
        {
            var link = b.Link;
            if (link == null || !link.IsAlive)
            {
                if (link != null)
                {
                    double lived = (DateTime.UtcNow - b.LinkSince).TotalSeconds;
                    // A bulb that closes its session again and again is being sent more than it can take: ease off for it.
                    if (lived < 300 && Math.Min(b.FpsCap, YeelightFps) > 1) b.FpsCap = Math.Max(1, Math.Min(b.FpsCap, YeelightFps) * 2 / 3);
                    Log.Info("Lost the connection to " + b.Ip + " after " + (int)lived + " s (" + link.CloseReason + "), sending at most " + Math.Min(b.FpsCap, YeelightFps) + " updates/s from now on");
                    DropLink(b);
                    // A bulb that just killed a session (quota exceeded, or it is still settling the old one) needs a
                    // moment before it accepts a new one - reconnecting instantly turns one bad session into a
                    // connect/disconnect storm instead of letting it recover.
                    b.NextAttempt = DateTime.UtcNow.AddSeconds(lived < 10 ? 3 : 0);
                }
                if (!b.Connecting && now >= b.NextAttempt)
                {
                    b.Connecting = true;
                    b.Status = BulbStatus.Connecting;
                    Task.Run(() => ConnectYeelight(b));
                }
                return;
            }

            if (b.Identifying) return;

            // Screen capture itself is paused (screensaver, lock, suspend): freeze at the last color instead of
            // reacting to whatever Chroma Connect/the canvas does or doesn't send meanwhile. The reconnect logic
            // above still runs, so the session stays warm; only the color (and the dark/off fade) stops moving.
            if (ScreenState.CapturePaused) return;

            if (b.BrightnessDirty) { b.BrightnessDirty = false; b.LastSentRgb = -1; }    // the new setting is applied with the next color

            int rgb;
            if (b.Config.UseScreenPosition)
            {
                if (b.ScreenRgb < 0) return;      // no frame sampled yet
                rgb = b.ScreenRgb;
            }
            else
            {
                if (_razer.Events == 0) return;
                rgb = ZoneRgb(Math.Min(group, 5) - 1);
            }
            b.CurrentRgb = rgb;

            // A bulb only takes hue and saturation from a color and keeps its own brightness, and it refuses pure black.
            // So the color is brought to full intensity and how bright the scene is becomes the bulb's brightness:
            // a dark scene gives a dim bulb, and black gives the minimum instead of being ignored.
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, bl = rgb & 0xFF;
            int level = Math.Max(r, Math.Max(g, bl));
            if (level > 0)
                b.LastNormColor = ((r * 255 + level / 2) / level << 16) | ((g * 255 + level / 2) / level << 8) | ((bl * 255 + level / 2) / level);
            int bright = Math.Max(1, (int)Math.Round(level / 255.0 * b.Config.Brightness));
            int key = (bright << 24) | b.LastNormColor;

            // Black (the mapped part of the screen is black): switch the bulb off, like the official connector does.
            // Two thresholds so a scene hovering around the limit does not make the bulb flicker.
            int offLevel = (int)Math.Round(YeelightOffBelow * 255 / 100.0);       // switch off at or below this level...
            int onLevel = offLevel + Math.Max(4, offLevel / 2);                    // ...and back on only once clearly above it
            bool dark = offLevel > 0 && (b.YeelightOff ? level < onLevel : level <= offLevel);
            if (dark)
            {
                if (b.YeelightOff) return;

                // Fade the last color to the minimum over a short moment first, so going dark is smooth rather than a cut.
                if (!b.YeelightFading)
                {
                    b.YeelightFading = true;
                    b.YeelightFadeStart = now;
                    b.YeelightFadeLastStep = -1;
                    b.YeelightFadeBright = b.LastSentRgb == -1 ? 0 : (b.LastSentRgb >> 24) & 0x7F;
                    b.YeelightFadeColor = b.LastSentRgb == -1 ? b.LastNormColor : b.LastSentRgb & 0xFFFFFF;
                }

                int fadeMs = YeelightFadeMs;
                double t = fadeMs <= 0 ? 1 : (now - b.YeelightFadeStart).TotalMilliseconds / fadeMs;
                if (t >= 1 || b.YeelightFadeBright <= 1)
                {
                    // The last step (1% to off) is the one the eye reads as a cut, so the bulb finishes it with its own fade.
                    if (link.SetPowerOffSmooth(Math.Min(fadeMs, 400))) { b.YeelightOff = true; b.YeelightFading = false; b.LastSentRgb = -1; b.LastSentAt = now; }
                    else DropLink(b);
                }
                else if ((now - b.LastSentAt).TotalMilliseconds >= 1000.0 / Math.Min(b.FpsCap, YeelightFps))
                {
                    int step = Math.Max(1, (int)Math.Round(b.YeelightFadeBright * (1 - t)));
                    if (step != b.YeelightFadeLastStep)
                    {
                        if (link.SetColorAndBrightness(b.YeelightFadeColor, step)) { b.YeelightFadeLastStep = step; b.LastSentRgb = -1; b.LastSentAt = now; }
                        else DropLink(b);
                    }
                }
                return;
            }
            b.YeelightFading = false;   // the scene is bright again: cancel any fade in progress
            b.YeelightOff = false;      // the next set_scene switches the bulb back on

            if ((key != b.LastSentRgb && (now - b.LastSentAt).TotalMilliseconds >= 1000.0 / Math.Min(b.FpsCap, YeelightFps)) || now - b.LastSentAt > KeepAlive)
            {
                if (link.SetColorAndBrightness(b.LastNormColor, bright)) { b.LastSentRgb = key; b.LastSentAt = now; }
                else DropLink(b);
            }
        }

        void TickGovee(BulbState b, int group, DateTime now)
        {
            // Govee LAN is connectionless: a device is "connected" while it keeps answering the network search.
            if (Interlocked.Read(ref b.LastSeenTicks) == 0) { b.Status = BulbStatus.Searching; return; }
            bool alive = now - new DateTime(Interlocked.Read(ref b.LastSeenTicks), DateTimeKind.Utc) < GoveeAliveWindow;
            if (!alive)
            {
                b.GoveeReady = false; b.GoveeInitStep = 0;
                b.GoveeStreaming = false;
                if (b.Status != BulbStatus.Unreachable)
                {
                    Log.Info("Govee " + b.Ip + " is not answering.");
                    b.Error = "no answer to the network search";
                    b.Status = BulbStatus.Unreachable;
                }
                return;
            }

            // Govee always uses the real-time Razer stream: the plain color command is far less smooth.
            if (!b.GoveeReady)
            {
                // The device drops commands that arrive back to back, so each step waits ~0.4 s for the previous one.
                if (b.GoveeInitStep == 0)
                {
                    // Wake the device at the minimum brightness: it comes back in its last state (often plain white), and
                    // that must not be visible at full brightness while the stream starts. The brightness is faded in below.
                    if (!_govee.Turn(b.Ip, b.LocalIp, true) || !_govee.Brightness(b.Ip, b.LocalIp, 1)) return;
                    b.GoveeInitStep = 1; b.GoveeInitAt = now;
                    return;
                }
                if ((now - b.GoveeInitAt).TotalMilliseconds < 400) return;
                if (b.GoveeInitStep == 1)
                {
                    _govee.RazerMode(b.Ip, b.LocalIp, true);
                    b.GoveeStreaming = true;
                    b.GoveeInitStep = 2; b.GoveeInitAt = now;
                    return;
                }
                b.GoveeReady = true;
                b.BrightnessDirty = false;
                b.GoveeRamp = 0;
                b.GoveeRampAt = now.AddMilliseconds(1200);      // let the first frames land, then fade the brightness in
                b.LastSentRgb = -1;
                b.Error = null;
                b.Status = BulbStatus.Connected;
                Log.Info("Controlling Govee " + b.Ip + " (" + b.Model + ", zone " + group + ", " + "Razer stream" + ")");
            }
            if (b.Identifying) return;

            // Screen capture itself is paused (screensaver, lock, suspend): freeze at the last color instead of
            // reacting to whatever Chroma Connect/the canvas does or doesn't send meanwhile.
            if (ScreenState.CapturePaused) return;

            if (b.GoveeRamp < 3 && now >= b.GoveeRampAt)
            {
                b.GoveeRamp++;                                   // 1/3, 2/3, then the full setting
                b.GoveeRampAt = now.AddMilliseconds(350);
                _govee.Brightness(b.Ip, b.LocalIp, Math.Max(1, b.Config.Brightness * b.GoveeRamp / 3));
            }

            if (b.BrightnessDirty)
            {
                b.BrightnessDirty = false;
                b.GoveeRamp = 3;                                 // an explicit change cancels the fade
                _govee.Brightness(b.Ip, b.LocalIp, b.Config.Brightness);
            }

            double sinceMs = (now - b.LastSentAt).TotalMilliseconds;
            bool due = sinceMs >= 1000.0 / GoveeFps;
            if (b.Config.UseScreenPosition)
            {
                if (b.ScreenRgb < 0) return;      // no frame sampled yet
                var colors = b.ScreenColors;
                b.CurrentRgb = b.ScreenRgb;
                if (due)
                {
                    bool sent = colors != null && colors.Length > 1
                        ? _govee.SegmentColors(b.Ip, b.LocalIp, colors)       // a true gradient sampled off the screen
                        : _govee.Segments(b.Ip, b.LocalIp, b.ScreenRgb, SegmentsOf(b));
                    if (sent) { b.LastSentRgb = b.ScreenRgb; b.LastSentAt = now; b.LastStreamRgb = b.ScreenRgb; }
                }
            }
            else if (_razer.Events == 0) return;
            else if (group == SpreadGroup)
            {
                // The four Chroma groups as a gradient along the device (first segment = Group 1 ... last = Group 4).
                int[] seg = SpreadColors(SegmentsOf(b));
                b.CurrentRgb = ZoneRgb(3);
                if (due && _govee.SegmentColors(b.Ip, b.LocalIp, seg)) { b.LastSentAt = now; b.LastStreamRgb = Average(seg); }
            }
            else
            {
                int rgb = ZoneRgb(group - 1);
                b.CurrentRgb = rgb;
                if (due && _govee.Segments(b.Ip, b.LocalIp, rgb, SegmentsOf(b))) { b.LastSentRgb = rgb; b.LastSentAt = now; b.LastStreamRgb = rgb; }   // a steady stream, like Govee Desktop
            }
        }

        static int Average(int[] colors)
        {
            long r = 0, g = 0, bl = 0;
            foreach (int c in colors) { r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; bl += c & 0xFF; }
            int n = Math.Max(1, colors.Length);
            return (int)(((r / n) << 16) | ((g / n) << 8) | (bl / n));
        }

        /// <summary>
        /// Leaves Razer stream mode. The device then falls back to its own state (usually plain white), so the
        /// last color that was streamed is applied right afterwards and stays until something else changes it.
        /// </summary>
        Task ReleaseGovee(BulbState b)
        {
            bool wasStreaming = b.GoveeStreaming;
            b.GoveeStreaming = false;
            b.GoveeReady = false; b.GoveeInitStep = 0;
            if (!wasStreaming || b.Ip == null) return Task.FromResult(0);

            string ip = b.Ip; var local = b.LocalIp; int last = b.LastStreamRgb;
            return Task.Run(() =>
            {
                try
                {
                    _govee.RazerMode(ip, local, false);
                    if (last >= 0)
                    {
                        Thread.Sleep(450);          // the device drops commands that follow each other too closely
                        _govee.Color(ip, local, last);
                    }
                }
                catch (Exception e)
                {
                    // The main call site (Tick) discards this Task fire-and-forget - an unguarded exception here
                    // would otherwise surface much later (and confusingly) via the finalizer thread instead.
                    Log.Warn("Could not release Govee stream mode for " + ip + ": " + e.Message);
                }
            });
        }

        void ConnectYeelight(BulbState b)
        {
            MusicLink link = null;
            try
            {
                link = new MusicLink(b.Ip, b.Port, b.LocalIp);
                if (link.Open())
                {
                    link.SetPower(true);
                    link.SetBrightness(b.Config.Brightness);
                    b.LastSentRgb = -1;
                    b.Failures = 0;
                    b.Error = null;
                    b.Link = link;
                    b.LinkSince = DateTime.UtcNow;
                    b.Status = BulbStatus.Connected;
                    Log.Info("Connected to " + b.Ip + " (zone " + b.Config.Group + ")");
                    link = null;
                    return;
                }

                b.Error = string.IsNullOrEmpty(link.LastReply) ? "no answer"
                        : link.LastReply.Contains("\"ok\"") ? "the bulb could not connect back - check the Windows Firewall"
                        : link.LastReply;
                Log.Warn("Could not open a music session on " + b.Ip + ": " + b.Error);
            }
            catch (Exception e) { b.Error = e.Message; Log.Warn("Connecting to " + b.Ip + " failed: " + e.Message); }
            finally
            {
                if (link != null) link.Dispose();
                b.Connecting = false;
            }

            b.Status = BulbStatus.Unreachable;
            b.Failures++;
            b.NextAttempt = DateTime.UtcNow.AddSeconds(Math.Min(10, 2 * b.Failures));
            if (b.Failures >= 2) _scanNow.Set();   // its IP may have changed

            // The bulb answers discovery (we have its current IP from a recent scan) but refuses the control port
            // itself, steadily, for a while - not a Wi-Fi blip. This is the known Yeelight firmware quirk where LAN
            // Control silently gets stuck off; nothing sent over the LAN can fix it, only the toggle in the Yeelight
            // app (off, then on again) resets it. Said once per bulb per bad patch, not on every retry.
            if (b.Failures == 6 && b.Error == "bulb did not accept the TCP connection")
            {
                b.Error = "LAN Control looks stuck off - toggle it off then on for this bulb in the Yeelight app";
                Log.Warn(b.Ip + " has refused the control port for a while despite answering discovery - " + b.Error + ".");
            }
        }

        void DropLink(BulbState b)
        {
            var link = b.Link;
            b.Link = null;
            if (link != null) Task.Run(() => { try { link.Dispose(); } catch { } });    // its cleanup waits a little: not on the color loop
            if (b.Config.Controlled && b.Status == BulbStatus.Connected) b.Status = BulbStatus.Connecting;
            b.LastSentRgb = -1;
            b.YeelightOff = false;
            b.YeelightFading = false;
        }

        public void Dispose()
        {
            _stop = true;
            _scanNow.Set();
            try { _loop.Join(1000); } catch { }
            _saveTimer.Dispose();
            _config.Save();
            var releasing = new List<Task>();
            foreach (var b in Bulbs) { DropLink(b); releasing.Add(ReleaseGovee(b)); }
            try { Task.WaitAll(releasing.ToArray(), 3000); } catch { }
            _govee.Dispose();
            _razer.Dispose();
        }
    }
}
