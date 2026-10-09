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

    public static void Main()
    {
        TestFrozenLights();
        TestKeyboardResume();
        TestDiscoveryLoss();
        TestGoveeStartup();
        TestGoveeWaitForColor();
        TestYeelightReconnect();
        TestChromaWrites();
        Console.WriteLine("PASS: keyboard resume recovery, discovery loss/recovery, Govee startup pacing/failure, Yeelight reconnect/pause, Chroma serialization/errors.");
    }
}
