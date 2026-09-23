#nullable disable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using Ambilight.DesktopDuplication;
using Ambilight.GUI;
using Colore;
using Colore.Data;
using NLog;
using Color = Colore.Data.Color;

namespace Ambilight.Logic
{
    /// <summary>
    /// This Class manages the Logic of the software. Handling the settings, Image Manipulation and logic functions
    /// </summary>
    class LogicManager
    {
        private static readonly TimeSpan ChromaRetryInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ErrorLogInterval = TimeSpan.FromSeconds(30);

        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private KeyboardLogic _keyboardLogic;
        private MousePadLogic _mousePadLogic;
        private MouseLogic _mouseLogic;
        private LinkLogic _linkLogic;
        private HeadsetLogic _headsetLogic;
        private KeypadLogic _keypadLogic;
        private LampArrayLogic _lampArrayLogic;
        private LightsCanvasLogic _lightsCanvasLogic;
        private DesktopDuplicatorReader _reader;

        // Kept as a field on purpose: the IChroma instance has a finalizer that calls into the
        // native SDK, which must never run while the app is alive.
        private IChroma _chroma;

        private readonly Dictionary<string, DateTime> _lastErrorLog = new Dictionary<string, DateTime>();

        private readonly TraySettings settings;

        public LogicManager(TraySettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

            this.StartLogic(settings);
        }

        private async void StartLogic(TraySettings settings)
        {
            try
            {
                // Everything that does not depend on Razer software starts first, so that the
                // laptop keyboard (Windows Dynamic Lighting) works even when Synapse / the
                // Chroma SDK is missing or not running. The lights canvas is the same story for
                // Yeelight/Govee devices positioned on it: no Razer dependency at all.
                _lampArrayLogic = new LampArrayLogic(settings);
                _lightsCanvasLogic = new LightsCanvasLogic(settings);
                _reader = new DesktopDuplicatorReader(this, settings);

                await InitializeChromaWithRetryAsync();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Unexpected error while starting the logic.");
            }
        }

        private async Task InitializeChromaWithRetryAsync()
        {
            while (true)
            {
                IChroma chroma = null;
                try
                {
                    chroma = await ColoreProvider.CreateNativeAsync();
                    AppInfo appInfo = new AppInfo(
                        "Ambilight for Razer devices",
                        "Shows an ambilight effect on your Razer Chroma devices",
                        "Nico Jeske",
                        "ambilight@nicojeske.de",
                        new[]
                        {
                            ApiDeviceType.Headset,
                            ApiDeviceType.Keyboard,
                            ApiDeviceType.Keypad,
                            ApiDeviceType.Mouse,
                            ApiDeviceType.Mousepad,
                            ApiDeviceType.ChromaLink
                        },
                        Category.Application);
                    await chroma.InitializeAsync(appInfo);

                    _chroma = chroma;
                    _keyboardLogic = new KeyboardLogic(settings, chroma);
                    _mousePadLogic = new MousePadLogic(settings, chroma);
                    _mouseLogic = new MouseLogic(settings, chroma);
                    _linkLogic = new LinkLogic(settings, chroma);
                    _headsetLogic = new HeadsetLogic(settings, chroma);
                    _keypadLogic = new KeypadLogic(settings, chroma);

                    logger.Info("Razer Chroma SDK initialized.");
                    return;
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"Razer Chroma SDK not available (is Razer Synapse running?), retrying in {ChromaRetryInterval.TotalSeconds:0} s.");

                    // A half-initialized instance must not be finalized: its finalizer would
                    // call into the native SDK and crash the process.
                    if (chroma != null)
                        GC.SuppressFinalize(chroma);
                }

                await Task.Delay(ChromaRetryInterval);
            }
        }

        /// <summary>
        /// Processes a captured Screenshot and create an Ambilight effect for the selected devices
        /// </summary>
        /// <param name="img"></param>
        public void ProcessNewImage(Bitmap img)
        {
            // Runs regardless of every other toggle below: lights on the canvas have nothing to do with Razer Chroma
            // or the other devices, and must keep working even if every one of those is turned off.
            SafeProcess("lights canvas", _lightsCanvasLogic, img);

            // Skip the rest if no Razer/laptop-keyboard device is enabled
            if (!settings.KeyboardEnabled && !settings.PadEnabled && !settings.MouseEnabled &&
                !settings.LinkEnabled && !settings.HeadsetEnabled && !settings.KeypadEnabeled &&
                !settings.LaptopKeyboardEnabled)
            {
                return;
            }

            // No need to create a copy - the bitmap is already managed by the reader
            // and each Process call creates its own resized copies.
            // Each device is isolated so that a failure on one (e.g. Synapse closed) can never
            // stop the others - in particular the laptop keyboard, which is independent of Razer.
            if (settings.KeyboardEnabled)
                SafeProcess("keyboard", _keyboardLogic, img);
            if (settings.PadEnabled)
                SafeProcess("mousepad", _mousePadLogic, img);
            if (settings.MouseEnabled)
                SafeProcess("mouse", _mouseLogic, img);
            if (settings.LinkEnabled)
                SafeProcess("chroma link", _linkLogic, img);
            if (settings.HeadsetEnabled)
                SafeProcess("headset", _headsetLogic, img);
            if (settings.KeypadEnabeled)
                SafeProcess("keypad", _keypadLogic, img);
            if (settings.LaptopKeyboardEnabled)
                SafeProcess("laptop keyboard", _lampArrayLogic, img);
        }

        /// <summary>Starts the group color test (see <see cref="LinkLogic"/>); does nothing if Chroma is not ready yet.</summary>
        public void StartColorTest() { _linkLogic?.StartColorTest(); }
        public void StopColorTest() { _linkLogic?.StopColorTest(); }
        public bool ColorTestRunning { get { return _linkLogic != null && _linkLogic.ColorTestRunning; } }

        /// <summary>Stops the capture thread so a fresh LogicManager can be created (e.g. a plugin restart)
        /// without fighting this one over the single DXGI desktop duplication session. The Chroma connection
        /// itself is left to become unreferenced along with everything else once this instance is dropped.</summary>
        public void Stop() { _reader?.Stop(); }

        private void SafeProcess(string device, IDeviceLogic logic, Bitmap img)
        {
            // Null until the Chroma SDK has been initialized.
            if (logic == null)
                return;

            try
            {
                logic.Process(img);
            }
            catch (Exception ex)
            {
                // Rate limited: this runs once per captured frame.
                var now = DateTime.UtcNow;
                if (!_lastErrorLog.TryGetValue(device, out var last) || now - last > ErrorLogInterval)
                {
                    _lastErrorLog[device] = now;
                    logger.Error(ex, $"Error processing image for {device}");
                }
            }
        }
    }
}
