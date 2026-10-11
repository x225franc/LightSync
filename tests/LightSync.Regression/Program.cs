using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Ambilight.Lights;
using Ambilight.Logic;

internal static class Program
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Invoke(Engine engine, string method, params object[] args) =>
        typeof(Engine).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine, args);

    private static Engine Create(string kind)
    {
        var config = new Config();
        config.Bulbs.Add(new BulbConfig { Id = "test", Kind = kind });
        var engine = new Engine(config); // Never Start(): no discovery/capture threads or real devices.
        engine.SetLocalZoneRgb(0x008000, 0xFF0000, 0x0000FF, 0xFFFF00);
        engine.Bulbs[0].Ip = "127.0.0.1";
        return engine;
    }

    private static void TestDiscoveryLoss()
    {
        using (var engine = Create(Kinds.Govee))
        {
            var bulb = engine.Bulbs[0];
            var now = DateTime.UtcNow;
            bulb.GoveeReady = bulb.GoveeStreaming = true;
            bulb.BrightnessDirty = false;
            bulb.LastSeenTicks = now.AddMinutes(-2).Ticks;
            Invoke(engine, "TickGovee", bulb, 1, now);
            Assert(bulb.GoveeReady && bulb.GoveeStreaming, "Lost discovery reset the established stream");
            Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "frame:32768" }), "Lost discovery did not preserve color-only streaming");
            Assert(bulb.Status == BulbStatus.Unreachable, "Missing discovery must remain visible in status");

            GoveeNet.Latest.Calls.Clear();
            bulb.LastSeenTicks = now.Ticks;
            Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(50));
            Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "mode:True", "frame:32768" }), "Recovery reset power/brightness or did not resend colors");
            Assert(bulb.Status == BulbStatus.Connected && bulb.Error == null, "Discovery recovery did not clear the warning");

            GoveeNet.Latest.Calls.Clear();
            engine.SetGroup(bulb, 2);
            Invoke(engine, "TickGovee", bulb, 2, now.AddMilliseconds(100));
            Assert(bulb.GoveeReady && GoveeNet.Latest.Calls.SequenceEqual(new[] { "frame:32768" }), "Group change unnecessarily restarted the stream");
        }
        using (var engine = Create(Kinds.Govee))
        {
            var bulb = engine.Bulbs[0];
            bulb.LastSeenTicks = DateTime.UtcNow.AddMinutes(-2).Ticks;
            Invoke(engine, "TickGovee", bulb, 1, DateTime.UtcNow);
            Assert(GoveeNet.Latest.Calls.Count == 0, "An undiscovered/uninitialized device was started");
        }
    }

    private static void TestGoveeStartup()
    {
        using (var engine = Create(Kinds.Govee))
        {
            var bulb = engine.Bulbs[0];
            var now = DateTime.UtcNow;
            bulb.LastSeenTicks = now.Ticks;
            Invoke(engine, "TickGovee", bulb, 1, now);
            Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(200));
            Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "brightness:1" }), "Power-on raced the dimming command");
            Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(400));
            GoveeNet.Latest.FailMode = true;
            Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(800));
            Assert(!bulb.GoveeStreaming && bulb.GoveeInitStep == 2, "Failed stream-mode command was treated as success");
            GoveeNet.Latest.FailMode = false;
            Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(1200));
            Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(1600));
            Assert(bulb.GoveeReady && GoveeNet.Latest.Calls.Last() == "frame:32768", "Startup failed to deliver the first color");
            Assert(!GoveeNet.Latest.Calls.Skip(1).Any(c => c.StartsWith("brightness:")), "Brightness rose before the first color frame");
        }
    }

    private static void TestYeelightReconnect()
    {
        using (var engine = Create(Kinds.Yeelight))
        {
            var bulb = engine.Bulbs[0];
            Invoke(engine, "ConnectYeelight", bulb);
            Assert(bulb.Link != null && bulb.Link.Calls.Count == 0, "Reconnect changed power/brightness before a color was ready");
            ScreenState.CapturePaused = true;
            Invoke(engine, "TickYeelight", bulb, 1, DateTime.UtcNow);
            Assert(bulb.Link.Calls.Count == 0, "Paused capture woke the bulb during reconnect");
            ScreenState.CapturePaused = false;
            bulb.Config.UseScreenPosition = true;
            Invoke(engine, "TickYeelight", bulb, 1, DateTime.UtcNow);
            Assert(bulb.Link.Calls.Count == 0, "Reconnect changed lighting before the first canvas color");
            bulb.Config.UseScreenPosition = false;
            Invoke(engine, "TickYeelight", bulb, 1, DateTime.UtcNow);
            Assert(bulb.Link.Calls.SequenceEqual(new[] { "scene:65280:40" }), "The first command did not apply color and scene brightness atomically");
        }
    }

    private static void TestGoveeWaitForColor()
    {
        using (var engine = Create(Kinds.Govee))
        {
            var bulb = engine.Bulbs[0];
            var now = DateTime.UtcNow;
            bulb.Config.UseScreenPosition = true;
            bulb.LastSeenTicks = now.Ticks;
            foreach (int ms in new[] { 0, 400, 800, 1200, 5000 })
                Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(ms));
            Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "brightness:1", "power:True", "mode:True" }), "Brightness rose before a canvas color was available");
            engine.SetScreenColor(bulb, 0x008000);
            Invoke(engine, "TickGovee", bulb, 1, now.AddMilliseconds(6000));
            Assert(GoveeNet.Latest.Calls.Last() == "frame:32768" && bulb.GoveeRamp == 0, "First canvas frame was not sent at minimum brightness");
        }
    }

    private static void TestChromaWrites()
    {
        int active = 0, finished = 0;
        var writes = Enumerable.Range(0, 12).Select(_ => Task.Run(() => ChromaFrameSender.Send(async () =>
        {
            Assert(Interlocked.Increment(ref active) == 1, "SDK writes overlapped");
            await Task.Delay(5).ConfigureAwait(false);
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref finished);
        }))).ToArray();
        Task.WaitAll(writes);
        Assert(finished == 12 && active == 0, "Send returned before the SDK finished");
        var expected = new InvalidOperationException("SDK failure");
        try { ChromaFrameSender.Send(async () => { await Task.Delay(5); throw expected; }); throw new Exception("SDK failure was swallowed"); }
        catch (InvalidOperationException ex) { Assert(ReferenceEquals(ex, expected), "SDK exception was wrapped"); }
        ChromaFrameSender.Send(() => Task.CompletedTask); // Lock must be released after failure.
    }

    private static void TestKeyboardResume()
    {
        var recovery = new LampArrayRecovery();
        Assert(!recovery.ShouldReopen(TimeSpan.FromHours(1), false), "Normal priority loss must not reopen the keyboard");
        recovery.Begin(TimeSpan.Zero);
        Assert(!recovery.ShouldReopen(TimeSpan.FromSeconds(30), false), "Windows was not allowed time to grant control");
        Assert(recovery.ShouldReopen(TimeSpan.FromSeconds(45), false), "A stale post-resume handle was never reopened");
        Assert(!recovery.ShouldReopen(TimeSpan.FromSeconds(46), false), "Rediscovery retries caused a tight loop");
        Assert(!recovery.ShouldReopen(TimeSpan.FromSeconds(50), true), "An available keyboard was unnecessarily reopened");
        Assert(!recovery.ShouldReopen(TimeSpan.FromSeconds(51), false), "A transient foreground priority change triggered rediscovery");
        Assert(recovery.ShouldReopen(TimeSpan.FromSeconds(96), false), "Foreground-only availability prevented recovery of background control");
        Assert(!recovery.ShouldReopen(TimeSpan.FromMinutes(4), false), "Retries continued indefinitely against Windows priority");
        recovery.Begin(TimeSpan.FromMinutes(5));
        Assert(recovery.ShouldReopen(TimeSpan.FromSeconds(345), false), "A second hibernation did not reset recovery");
    }

    private static void TestFrozenLights()
    {
        var now = DateTime.UtcNow;
        try
        {
            using (var engine = Create(Kinds.Govee))
            {
                var bulb = engine.Bulbs[0];
                bulb.GoveeReady = bulb.GoveeStreaming = true;
                bulb.GoveeRamp = 3;
                bulb.LastSeenTicks = now.Ticks;
                bulb.Config.UseScreenPosition = true;
                bulb.ScreenRgb = 0x008000;
                bulb.ScreenColors = new[] { 0xFF0000, 0x0000FF };
                Invoke(engine, "TickGovee", bulb, 1, now);
                ScreenState.CapturePaused = true;
                bulb.ScreenColors[0] = 0xFFFFFF;
                bulb.ScreenRgb = 0xFFFFFF;
                GoveeNet.Latest.Calls.Clear();
                Invoke(engine, "TickGovee", bulb, 1, now.AddSeconds(1));
                Assert(GoveeNet.Latest.LastGradient.SequenceEqual(new[] { 0xFF0000, 0x0000FF }), "Pause lost or changed the accepted gradient");
                Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "gradient" }), "Paused keepalive changed brightness/power");
                bulb.Status = BulbStatus.Unreachable;
                GoveeNet.Latest.Calls.Clear();
                Invoke(engine, "TickGovee", bulb, 1, now.AddSeconds(2));
                Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "mode:True", "gradient" }), "Discovery recovery did not immediately restore the frozen frame");
                ScreenState.CapturePaused = false;
                Invoke(engine, "TickGovee", bulb, 1, now.AddSeconds(3));
                Assert(GoveeNet.Latest.LastGradient[0] == 0xFFFFFF, "Resume did not apply the live frame");
            }
            using (var engine = Create(Kinds.Yeelight))
            {
                var bulb = engine.Bulbs[0];
                Invoke(engine, "ConnectYeelight", bulb);
                Invoke(engine, "TickYeelight", bulb, 1, now);
                int frozen = bulb.LastYeelightScene;
                ScreenState.CapturePaused = true;
                engine.SetLocalZoneRgb(0xFFFFFF, 0, 0, 0);
                bulb.Link.Calls.Clear();
                Invoke(engine, "TickYeelight", bulb, 1, now.AddSeconds(11));
                Assert(bulb.Link.Calls.SequenceEqual(new[] { $"scene:{frozen & 0xFFFFFF}:{(frozen >> 24) & 0x7F}" }), "Yeelight pause did not preserve color and brightness");
                Invoke(engine, "ConnectYeelight", bulb);
                Invoke(engine, "TickYeelight", bulb, 1, now.AddSeconds(12));
                Assert(bulb.Link.Calls.Count == 1 && bulb.LastSentRgb == frozen, "Reconnect lost the frozen Yeelight scene");
                bulb.YeelightOff = true;
                bulb.Link.Calls.Clear();
                Invoke(engine, "TickYeelight", bulb, 1, now.AddSeconds(30));
                Assert(bulb.Link.Calls.Count == 0, "Paused keepalive woke an off bulb");
            }
        }
        finally { ScreenState.CapturePaused = false; }
    }

    private static void TestPowerLifecycle()
    {
        using (var engine = Create(Kinds.Govee))
        {
            var bulb = engine.Bulbs[0];
            bulb.LastSeenTicks = DateTime.UtcNow.Ticks;
            bulb.GoveeReady = bulb.GoveeStreaming = true;
            Assert(!engine.TurnOffOnSuspendOrShutdown && !engine.TurnOnAtStartupOrResume, "New power options must be opt-in");
            Invoke(engine, "SuspendLights");
            Invoke(engine, "Tick");
            Assert(GoveeNet.Latest.Calls.Count == 0, "Disabled power option sent commands during suspend");
            Invoke(engine, "ResumeLights");
            engine.TurnOffOnSuspendOrShutdown = true;
            Invoke(engine, "SuspendLights");
            Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "power:False" }), "Suspend did not turn off Govee");
            GoveeNet.Latest.Calls.Clear();
            Invoke(engine, "ResumeLights");
            Invoke(engine, "Tick");
            Assert(GoveeNet.Latest.Calls.Count == 0, "Resume woke lights when automatic power on was disabled");
            engine.TurnOnAtStartupOrResume = true;
            Invoke(engine, "ResumeLights");
            Invoke(engine, "Tick");
            Assert(GoveeNet.Latest.Calls.Count == 0, "Wake raced network/desktop recovery");
            typeof(Engine).GetField("_resumeLightsAt", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(engine, DateTime.MinValue);
            Invoke(engine, "Tick");
            Assert(GoveeNet.Latest.Calls.SequenceEqual(new[] { "brightness:1" }), "Resume did not restart safe Govee initialization");
            GoveeNet.Latest.Calls.Clear();
            bulb.Config.Enabled = false;
            Invoke(engine, "SuspendLights");
            Assert(GoveeNet.Latest.Calls.Count == 0, "Lifecycle powered off an excluded light");
        }
        using (var engine = Create(Kinds.Yeelight))
        {
            MusicLink.PowerOffCalls.Clear();
            var bulb = engine.Bulbs[0];
            engine.TurnOffOnSuspendOrShutdown = true;
            Invoke(engine, "OnSessionEnding", null, new Microsoft.Win32.SessionEndingEventArgs(Microsoft.Win32.SessionEndReasons.Logoff));
            Assert(MusicLink.PowerOffCalls.Count == 0, "Signing out unexpectedly powered off the lights");
            Invoke(engine, "OnSessionEnding", null, new Microsoft.Win32.SessionEndingEventArgs(Microsoft.Win32.SessionEndReasons.SystemShutdown));
            Assert(MusicLink.PowerOffCalls.SequenceEqual(new[] { "127.0.0.1" }), "Yeelight without a music connection was not switched off");
            engine.TurnOnAtStartupOrResume = true;
            Invoke(engine, "ResumeLights");
            Invoke(engine, "ConnectYeelight", bulb);
            ScreenState.CapturePaused = false;
            Invoke(engine, "TickYeelight", bulb, 1, DateTime.UtcNow);
            Assert(bulb.Link.Calls.SequenceEqual(new[] { "scene:65280:40" }), "Wake did not restore Yeelight color/brightness atomically");
        }
    }

    private static void TestSessionRelease()
    {
        var engine = Create(Kinds.Yeelight);
        var bulb = engine.Bulbs[0];
        var releasing = new TaskCompletionSource<bool>();
        bulb.YeelightRelease = releasing.Task;
        Invoke(engine, "TickYeelight", bulb, 1, DateTime.UtcNow);
        Assert(!bulb.Connecting && bulb.Link == null, "Reconnect overlapped an unfinished previous release");
        releasing.SetResult(true);
        Invoke(engine, "ConnectYeelight", bulb);
        int before = MusicLink.Released;
        Invoke(engine, "SuspendLights");
        Assert(bulb.Link == null && MusicLink.Released > before, "Suspend returned before release with power-off disabled");
        Invoke(engine, "ResumeLights");
        Invoke(engine, "ConnectYeelight", bulb);
        before = MusicLink.Released;
        engine.Dispose();
        Assert(MusicLink.Released > before, "Engine disposal did not await Yeelight release");
        Invoke(engine, "ConnectYeelight", bulb);
        Assert(bulb.Link == null, "A connection reacquired music mode after shutdown");
    }

    private static void TestLightEffects()
    {
        Assert(LightEffects.Color(LightEffect.SolidColor, 0x123456, 50, false, 200, 0, 1) == 0x123456, "Solid color drifted with time");
        Assert(LightEffects.Color(LightEffect.ColorCycle, 0, 0, false, 0, 0, 1) == 0xFF0000, "Color cycle initial hue");
        Assert(LightEffects.Color(LightEffect.ColorCycle, 0, 0, false, 4, 0, 1) == 0xFFFF00, "Color cycle does not follow elapsed time");
        int dim = LightEffects.Color(LightEffect.Breathing, 0xFF0000, 0, false, 0, 0, 1);
        Assert(dim > 0 && dim < 0xFF0000 && LightEffects.Color(LightEffect.Breathing, 0xFF0000, 0, false, 3, 0, 1) == 0xFF0000, "Breathing intensity/period incorrect");
        Assert(LightEffects.Color(LightEffect.RainbowWave, 0, 50, false, 0.1, 1, 4) != LightEffects.Color(LightEffect.RainbowWave, 0, 50, true, 0.1, 1, 4), "Reverse has no effect");
        Assert(LightEffects.Color(LightEffect.RainbowWheel, 0, 0, false, 0, 3, 4) != LightEffects.Color(LightEffect.RainbowWave, 0, 0, false, 0, 3, 4), "Ring topology duplicates wave endpoints");
        using (var engine = Create(Kinds.Govee))
        {
            var bulb = engine.Bulbs[0];
            var now = DateTime.UtcNow;
            bulb.GoveeReady = bulb.GoveeStreaming = true;
            bulb.LastSeenTicks = now.Ticks;
            bulb.Config.Group = 0;
            bulb.Config.UseScreenPosition = true;
            bulb.ScreenRgb = 0x0000FF;
            engine.SetEffect(bulb, LightEffect.SolidColor, 0xFF0000, 50, false);
            Assert(bulb.Config.Controlled, "An independent effect requires a screen group");
            Invoke(engine, "TickGovee", bulb, 0, now);
            Assert(GoveeNet.Latest.LastGradient.All(c => c == 0xFF0000), "Screen canvas overwrote solid effect");
            int calls = GoveeNet.Latest.Calls.Count;
            Invoke(engine, "TickGovee", bulb, 0, now.AddMilliseconds(1));
            Assert(GoveeNet.Latest.Calls.Count == calls, "Effect bypassed Govee frame limit");
            engine.SetEffect(bulb, LightEffect.RainbowWave, 0, 50, false);
            Invoke(engine, "TickGovee", bulb, 0, now.AddSeconds(1));
            var gradient = GoveeNet.Latest.LastGradient.ToArray();
            Assert(gradient.Length == 4 && gradient.Distinct().Count() > 1, "Rainbow lost physical segment colors");
            ScreenState.CapturePaused = true;
            Invoke(engine, "TickGovee", bulb, 0, now.AddSeconds(2));
            Assert(GoveeNet.Latest.LastGradient.SequenceEqual(gradient), "Screensaver did not freeze the generated effect");
            ScreenState.CapturePaused = false;
            engine.SetEffect(bulb, LightEffect.ScreenAmbilight, 0, 50, false);
            Invoke(engine, "TickGovee", bulb, 0, now.AddSeconds(3));
            Assert(bulb.LastStreamRgb == 0x0000FF, "Returning to screen effect lost the canvas mapping");
        }
        using (var engine = Create(Kinds.Yeelight))
        {
            var bulb = engine.Bulbs[0];
            bulb.Link = new MusicLink(bulb.Ip, bulb.Port, bulb.LocalIp);
            engine.YeelightOffBelow = 50;
            engine.SetEffect(bulb, LightEffect.SolidColor, 0x010000, 50, false);
            Invoke(engine, "TickYeelight", bulb, 1, DateTime.UtcNow);
            Assert(bulb.Link.Calls.SequenceEqual(new[] { "scene:16711680:1" }), "Dark autonomous color triggered screen dark/off behavior");
            var before = bulb.Link.Calls.Count;
            Invoke(engine, "TickYeelight", bulb, 1, DateTime.UtcNow.AddMilliseconds(1));
            Assert(bulb.Link.Calls.Count == before, "Solid effect flooded Yeelight");
        }
        Console.WriteLine("PASS: independent effects, elapsed time, segment mapping, reverse, screen return, screensaver freeze and send pacing.");
    }

    private static void TestAllLightsEffect()
    {
        var config = new Config();
        config.Bulbs.Add(new BulbConfig { Id = "one", Kind = Kinds.Govee, Group = 1 });
        config.Bulbs.Add(new BulbConfig { Id = "two", Kind = Kinds.Govee, Group = 3, UseScreenPosition = true });
        config.Bulbs.Add(new BulbConfig { Id = "off", Kind = Kinds.Yeelight, Enabled = false });
        using (var engine = new Engine(config))
        {
            engine.SetAllEffects(LightEffect.ColorCycle, 0x123456, 100, true, 42);
            Assert(config.AllLightsEffect == LightEffect.ColorCycle && config.AllLightsBrightness == 42, "Global preset was not retained for saving");
            Assert(engine.Bulbs.All(b => b.Config.Effect == LightEffect.ColorCycle && b.Config.EffectColor == 0x123456 && b.Config.EffectSpeed == 100 && b.Config.EffectReverse && b.Config.Brightness == 42), "Global preset skipped lights or parameters");
            Assert(!engine.Bulbs[2].Config.Enabled && engine.Bulbs[1].Config.UseScreenPosition && engine.Bulbs[1].Config.Group == 3, "Global preset changed power or mapping choices");
            int index = 0;
            foreach (var bulb in engine.Bulbs.Take(2))
            {
                bulb.Ip = "127.0.0." + (++index);
                bulb.LastSeenTicks = DateTime.UtcNow.Ticks;
                bulb.GoveeReady = bulb.GoveeStreaming = true;
            }
            GoveeNet.Latest.SendDelayMs = 30;
            Invoke(engine, "Tick");
            Assert(GoveeNet.Latest.Frames["127.0.0.1"].SequenceEqual(GoveeNet.Latest.Frames["127.0.0.2"]), "Slow network sends caused per-light phase drift within a frame");
            engine.SetEffect(engine.Bulbs[0], LightEffect.SolidColor, 0x00FF00, 0, false);
            Assert(engine.Bulbs[1].Config.Effect == LightEffect.ColorCycle, "Individual edit changed another light");
            engine.Bulbs[0].LastStreamColors = new[] { 0x00FF00, 0xFF0000 };
            engine.Bulbs[2].YeelightFading = engine.Bulbs[2].YeelightOff = true;
            engine.SetAllEffects(LightEffect.ScreenAmbilight, 0, 200, false, 0);
            Assert(engine.Bulbs.All(b => b.Config.Effect == LightEffect.ScreenAmbilight), "Global apply preserved an individual effect override");
            Assert(engine.Bulbs.All(b => b.LastSentRgb == -1 && b.LastStreamColors == null && !b.YeelightFading && !b.YeelightOff), "Global apply retained a previous send/fade state");
            Assert(config.AllLightsSpeed == 100 && config.AllLightsBrightness == 1, "Global controls were not clamped");
            Assert(engine.Bulbs[1].Config.UseScreenPosition && engine.Bulbs[1].Config.Group == 3, "Return to screen lost mappings");
        }
        Console.WriteLine("PASS: all-light preset, shared frame phase despite delayed sends, brightness, disabled-light preservation and individual overrides.");
    }

    public sealed class WheelColorSource : System.ComponentModel.INotifyPropertyChanged
    {
        private System.Drawing.Color _color;
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        public System.Drawing.Color Color
        {
            get => _color;
            set { _color = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Color))); }
        }
    }

    private static void TestWheelBinding()
    {
        var source = new WheelColorSource { Color = System.Drawing.Color.FromArgb(20, 30, 40) };
        var wheel = new Ambilight.GUI.ColorWheel();
        int events = 0;
        wheel.ColorChanged += (s, e) => events++;
        System.Windows.Data.BindingOperations.SetBinding(wheel, Ambilight.GUI.ColorWheel.BoundColorProperty,
            new System.Windows.Data.Binding(nameof(WheelColorSource.Color)) { Source = source });
        Assert(wheel.SelectedColor.ToArgb() == source.Color.ToArgb(), "Saved color did not load into wheel");
        source.Color = System.Drawing.Color.Blue;
        Assert(wheel.SelectedColor.ToArgb() == source.Color.ToArgb() && events == 0, "External color update caused UI feedback or stale marker");
        wheel.Measure(new System.Windows.Size(140, 140));
        wheel.Arrange(new System.Windows.Rect(0, 0, 140, 140));
        typeof(Ambilight.GUI.ColorWheel).GetMethod("UpdateFromPoint", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(wheel, new object[] { new System.Windows.Point(139, 70) });
        Assert(source.Color.R > 240 && source.Color.G < 10 && source.Color.B < 10 && events == 1, "Wheel selection did not update the bound setting");
        Console.WriteLine("PASS: color wheel saved-color loading, external resync, two-way selection and no feedback events.");
    }

    [STAThread]
    public static void Main()
    {
        TestWheelBinding();
        TestAllLightsEffect();
        TestLightEffects();
        TestSessionRelease();
        TestPowerLifecycle();
        TestFrozenLights();
        TestKeyboardResume();
        TestDiscoveryLoss();
        TestGoveeStartup();
        TestGoveeWaitForColor();
        TestYeelightReconnect();
        TestChromaWrites();
        Console.WriteLine("PASS: power lifecycle, frozen light keepalive, keyboard resume recovery, discovery loss/recovery, Govee startup pacing/failure, Yeelight reconnect/pause, Chroma serialization/errors.");
    }
}
