using System;
using System.Drawing;
using System.Linq;
using Ambilight.GUI;
using Ambilight.Util;
using Colore;
using Colore.Effects.ChromaLink;
using NLog;
using ColoreColor = Colore.Data.Color;


namespace Ambilight.Logic
{

    /// <summary>
    /// Handles the Ambilight Effect for the Link connection
    /// </summary>
    class LinkLogic : IDeviceLogic
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        private GUI.TraySettings _settings;
        private CustomChromaLinkEffect _linkGrid = CustomChromaLinkEffect.Create();
        private IChroma _chroma;
        private bool _loggedFirstFrame;

        public LinkLogic(TraySettings settings, IChroma chromaInstance)
        {
            this._settings = settings;
            this._chroma = chromaInstance;

            // Some third-party Chroma Connect accessories (reported: a Razer Chroma Addressable RGB Controller)
            // never actually light up on the very first color frame written after Chroma starts - only a distinct
            // second write does, which is why manually toggling "Global Brightness" off/on in the Razer Chroma app
            // fixes it (it forces a second write). Sending one deliberate black frame here, before the real first
            // frame from Process() follows a moment later, reproduces that same off-then-on transition automatically.
            Kick();
        }

        /// <summary>Sends one all-black frame so the next real frame is a distinct second write - see the
        /// constructor comment. Also exposed for a manual "nudge" button, for when the automatic kick at startup
        /// was not enough (e.g. the accessory was plugged in or woken up after Chroma had already started).</summary>
        public void Kick()
        {
            for (int i = 0; i < ChromaLinkConstants.MaxLeds; i++)
                _linkGrid[i] = new ColoreColor((byte)0, (byte)0, (byte)0);

            Send();
        }

        /// <summary>
        /// Processes a ScreenShot and creates an Ambilight Effect for the chroma link
        /// </summary>
        /// <param name="newImage">ScreenShot</param>
        public void Process(Bitmap newImage)
        {
            if (RunColorTestStep()) return;

            // MEASURED, not assumed (see the group color test): writing Chroma Link LED index 0/1/2/3 is what
            // actually arrives as Razer's Broadcast CL1/CL3/CL4/CL5 - LED index 0 always arrives as BOTH CL1 and
            // CL2 (Razer forces "Global" and "Group 1" to be the same color, we cannot make them independent), and
            // LED index 4 (the 5th LED) never arrives anywhere - it is dead. So there are only 4 broadcast values we
            // can actually control, and the four equal screen columns (left to right) use exactly those four: index
            // 0 is also what bulbs set to "Global" or "Group 1" both see.
            bool crop = _settings.UltrawideModeEnabled;
            Bitmap map = ImageManipulation.ResizeImage(newImage, 4, 1, crop);
            Bitmap saturatedMap = ImageManipulation.ApplySaturation(map, _settings.Saturation, _settings.DeviceBrightness / 100f);
            if (saturatedMap != map && map != null)
                map.Dispose();

            using (var fast = new FastBitmap(saturatedMap))
                ApplyImageToGrid(fast);
            _linkGrid[4] = new ColoreColor((byte)0, (byte)0, (byte)0);    // dead LED - never reaches Razer, left dark

            if (!_loggedFirstFrame)
            {
                _log.Info($"ChromaLink: sending first frame, colors=[{string.Join(", ", Enumerable.Range(0, ChromaLinkConstants.MaxLeds).Select(i => _linkGrid[i].ToString()))}]");
                _loggedFirstFrame = true;
            }

            Send();

            // Clean up
            saturatedMap?.Dispose();
        }

        /// <summary>The four equal screen columns (left to right) onto the four LED indices that actually reach Razer.</summary>
        private void ApplyImageToGrid(FastBitmap map)
        {
            for (int i = 0; i < 4; i++)
            {
                Color color = map.GetPixel(i, 0);
                _linkGrid[i] = new ColoreColor((byte)color.R, (byte)color.G, (byte)color.B);
            }
        }

        private void Send()
        {
            try
            {
                var task = _chroma.ChromaLink.SetCustomAsync(_linkGrid);
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _log.Error(t.Exception, "ChromaLink.SetCustomAsync failed.");
                });
            }
            catch (System.Exception ex)
            {
                _log.Error(ex, "ChromaLink.SetCustomAsync threw synchronously.");
            }
        }

        // ---- group color test: lights exactly one Chroma zone at a time (everything else stays dark), so which
        // physical Yeelight/Govee bulb reacts can be read off unambiguously, without relying on Synapse's own
        // "which bubble is which" bookkeeping or on mixed colors being hard to tell apart. ----

        static readonly TimeSpan TestStepDuration = TimeSpan.FromSeconds(6);

        // Only 4 of the 5 Chroma Link LEDs actually reach Razer's broadcast (see Process()), so there are only 4
        // steps: LED index 0 lights whatever is set to "Global" AND "Group 1" (Razer ties them together), 1/2/3
        // light "Group 2"/"Group 3"/"Group 4".
        static readonly (string Label, byte R, byte G, byte B)[] TestSteps =
        {
            ("Group 1",   255, 255, 255),
            ("Group 2", 255,   0,   0),
            ("Group 3",   0, 255,   0),
            ("Group 4",   0, 110, 255),
        };

        volatile int _testIndex = -1;      // TestSteps index currently shown, -1 = not testing
        DateTime _testStepStarted;
        DateTime _testStepEnds;
        bool _testReadBackDone;

        public bool ColorTestRunning { get { return _testIndex >= 0; } }

        /// <summary>The group currently lit by the running test (null once it is done, or before the very first
        /// step has been processed).</summary>
        public string CurrentTestStepLabel
        {
            get
            {
                int i = _testIndex - 1;
                return i >= 0 && i < TestSteps.Length ? TestSteps[i].Label : null;
            }
        }

        /// <summary>Starts (or restarts) the test: Global, then Group 1..4, each shown alone for a few seconds. The
        /// app also reads back what Razer actually broadcasts a couple of seconds into each step and logs the exact
        /// match, so the LED-index-to-CL-slot mapping does not have to be inferred from watching physical bulbs.</summary>
        public void StartColorTest()
        {
            _testIndex = 0;
            _testStepStarted = DateTime.MinValue;
            _testStepEnds = DateTime.MinValue;     // makes the next Process() call announce step 1 right away
            _testReadBackDone = true;
            _log.Info("Color test starting (ChromaLinkConstants.MaxLeds=" + ChromaLinkConstants.MaxLeds + "): each Chroma " +
                      "zone lights up alone for " + TestStepDuration.TotalSeconds + " s - watch which bulb reacts, and see " +
                      "the \"Color test read-back\" lines below for what Razer actually broadcasts.");
        }

        public void StopColorTest()
        {
            if (_testIndex < 0) return;
            _testIndex = -1;
            _log.Info("Color test stopped.");
        }

        /// <summary>Drives the test instead of the screen while one is running. Returns false once it is done.</summary>
        bool RunColorTestStep()
        {
            if (_testIndex < 0) return false;

            var now = DateTime.UtcNow;
            if (now >= _testStepEnds)
            {
                if (_testIndex >= TestSteps.Length)
                {
                    _testIndex = -1;
                    _log.Info("Color test finished - back to the normal screen colors.");
                    return false;
                }

                var step = TestSteps[_testIndex];
                for (int i = 0; i < ChromaLinkConstants.MaxLeds; i++)
                    _linkGrid[i] = new ColoreColor((byte)0, (byte)0, (byte)0);
                _linkGrid[_testIndex] = new ColoreColor(step.R, step.G, step.B);

                _log.Info($"Color test: step {_testIndex + 1}/{TestSteps.Length} - \"{step.Label}\" (LED index {_testIndex}) only is lit, everything else is dark.");
                _testStepStarted = now;
                _testStepEnds = now + TestStepDuration;
                _testReadBackDone = false;
                _testIndex++;
            }
            else if (!_testReadBackDone && now - _testStepStarted >= TimeSpan.FromSeconds(2))
            {
                LogReadBack(_testIndex - 1, TestSteps[_testIndex - 1]);
                _testReadBackDone = true;
            }

            Send();
            return true;
        }

        /// <summary>Reads Ambilight.Lights' own view of the Broadcast colors back and logs which CL slot(s), if any,
        /// picked up the color this step wrote - the ground truth for how our LED index maps onto Razer's CL1..CL5.</summary>
        void LogReadBack(int ledIndex, (string Label, byte R, byte G, byte B) step)
        {
            try
            {
                var engine = Ambilight.Lights.LightsService.Engine;
                if (engine == null)
                {
                    _log.Warn("Color test: the lights engine is not running, cannot read back what Razer broadcasts.");
                    return;
                }

                int[] raw = engine.RawZoneColors;      // CL1..CL5 as 0xRRGGBB
                var hits = new System.Collections.Generic.List<string>();
                for (int cl = 0; cl < raw.Length; cl++)
                {
                    int r = (raw[cl] >> 16) & 0xFF, g = (raw[cl] >> 8) & 0xFF, b = raw[cl] & 0xFF;
                    if (r > 40 || g > 40 || b > 40)    // clearly lit, not just black / noise
                        hits.Add($"CL{cl + 1}=#{r:X2}{g:X2}{b:X2}");
                }

                string where = hits.Count == 0 ? "NOTHING - this color never arrived at any CL slot" : string.Join(", ", hits);
                _log.Info($"Color test read-back: wrote LED index {ledIndex} (\"{step.Label}\") -> Razer is broadcasting: {where}");
            }
            catch (Exception e) { _log.Warn(e, "Color test read-back failed."); }
        }
    }
}
