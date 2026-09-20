using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ambilight.GUI;
using Ambilight.Util;
using NLog;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using WinColor = Windows.UI.Color;

namespace Ambilight.Logic
{
    public enum LaptopKeyboardEffect
    {
        ScreenAmbilight = 0,
        SolidColor = 1,
        Breathing = 2,
        RainbowWave = 3,
        ColorCycle = 4,
        RainbowWheel = 5
    }

    /// <summary>
    /// Drives a Windows Dynamic Lighting (HID LampArray) keyboard - the laptop's built-in
    /// one - which the Razer Chroma SDK cannot reach. Razer devices are skipped on purpose:
    /// they are already handled through Chroma. Nothing here depends on Razer software.
    ///
    /// Windows only hands a LampArray to the foreground app, or to an "ambient" app that
    /// has package identity, declares the com.microsoft.windows.lighting extension (see the
    /// Package folder) and is prioritised by the user in Settings > Personalization >
    /// Dynamic Lighting. Until then IsAvailable stays false; frames are still written, as
    /// Microsoft's AutoRGB sample does, and simply ignored by Windows.
    ///
    /// A dedicated thread sends every frame so that animated effects keep running when the
    /// screen is static (screen capture only delivers frames when the screen changes).
    /// </summary>
    public class LampArrayLogic : IDeviceLogic
    {
        private const ushort RazerVendorId = 0x1532;
        private const int GridWidth = 44;
        private const int GridHeight = 16;
        private static readonly TimeSpan RetryDiscoveryInterval = TimeSpan.FromSeconds(30);

        /// <summary>Human readable state, shown in the settings window.</summary>
        public static volatile string CurrentStatus = "Starting...";

        private readonly Logger _log = LogManager.GetCurrentClassLogger();
        private readonly TraySettings _settings;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private LampArray _lampArray;
        private Vector2[] _normalizedPositions;
        private int[] _indices;
        private TimeSpan _minUpdateInterval = TimeSpan.FromMilliseconds(33);
        private volatile float _aspect = 1.3f;
        private DateTime _nextDiscovery = DateTime.MinValue;
        private bool _lastLoggedAvailability;
        private bool _loggedFirstFrame;

        // Latest colors computed from the screen (before brightness), read by the sender thread.
        private volatile WinColor[] _screenColors;

        public LampArrayLogic(TraySettings settings)
        {
            _settings = settings;

            new Thread(SenderLoop) { IsBackground = true, Name = "LampArrayLogic" }.Start();
        }

        /// <summary>Called for each captured frame; only prepares screen colors.</summary>
        public void Process(Bitmap newImage)
        {
            var positions = _normalizedPositions;
            if (positions == null || _settings.LaptopEffect != LaptopKeyboardEffect.ScreenAmbilight)
                return;

            var colors = new WinColor[positions.Length];

            Bitmap map = ImageManipulation.ResizeImage(newImage, GridWidth, GridHeight, _settings.UltrawideModeEnabled);
            Bitmap saturatedMap = ImageManipulation.ApplySaturation(map, _settings.Saturation);
            if (saturatedMap != map)
                map.Dispose();

            try
            {
                using (var fast = new FastBitmap(saturatedMap))
                {
                    for (int i = 0; i < positions.Length; i++)
                    {
                        int x = Clamp((int)(positions[i].X * GridWidth), GridWidth);
                        int y = _settings.AmbiModeEnabled
                            ? GridHeight - 1
                            : Clamp((int)(positions[i].Y * GridHeight), GridHeight);

                        Color c = fast.GetPixel(x, y);
                        colors[i] = WinColor.FromArgb(255, c.R, c.G, c.B);
                    }
                }
            }
            finally
            {
                saturatedMap.Dispose();
            }

            _screenColors = colors;
        }

        // Thread.Sleep only has the system timer's granularity (15.6 ms by default), so a 33 ms
        // sleep really lasts ~47 ms: ~21 fps instead of ~30, which made animations stutter.
        // Asking for a 1 ms timer resolution and pacing against a fixed schedule fixes that.
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        private void SenderLoop()
        {
            timeBeginPeriod(1);

            var nextTick = _clock.Elapsed;

            while (true)
            {
                TimeSpan interval = TimeSpan.FromMilliseconds(100);

                try
                {
                    var lampArray = _lampArray;
                    if (lampArray == null)
                    {
                        CurrentStatus = "Keyboard not found";
                        if (DateTime.UtcNow >= _nextDiscovery)
                            DiscoverAsync().GetAwaiter().GetResult();
                    }
                    else if (_settings.LaptopKeyboardEnabled)
                    {
                        SendFrame(lampArray);

                        // The keyboard's own minimum update interval is the fastest it accepts.
                        interval = _settings.LaptopEffect == LaptopKeyboardEffect.SolidColor
                            ? TimeSpan.FromMilliseconds(250)
                            : _minUpdateInterval;
                    }
                    else
                    {
                        CurrentStatus = "Disabled";
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(ex, "Dynamic Lighting keyboard error, will rediscover it.");
                    _lampArray = null;
                    _nextDiscovery = DateTime.UtcNow + RetryDiscoveryInterval;
                }

                nextTick += interval;
                var wait = nextTick - _clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
                else
                    nextTick = _clock.Elapsed; // fell behind: don't try to catch up with a burst
            }
        }

        private void SendFrame(LampArray lampArray)
        {
            LogAvailabilityChange(lampArray.IsAvailable);

            var positions = _normalizedPositions;
            WinColor[] colors = _settings.LaptopEffect == LaptopKeyboardEffect.ScreenAmbilight
                ? _screenColors
                : RenderEffect(positions, _clock.Elapsed.TotalSeconds);

            // Screen mode before the first captured frame.
            if (colors == null || colors.Length != positions.Length)
                return;

            double brightness = _settings.LaptopBrightness / 100.0;
            var scaled = new WinColor[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                scaled[i] = WinColor.FromArgb(255,
                    (byte)Math.Round(colors[i].R * brightness),
                    (byte)Math.Round(colors[i].G * brightness),
                    (byte)Math.Round(colors[i].B * brightness));
            }

            lampArray.SetColorsForIndices(scaled, _indices);

            if (!_loggedFirstFrame)
            {
                _loggedFirstFrame = true;
                _log.Info($"First Dynamic Lighting frame sent to {positions.Length} lamps.");
            }
        }

        private WinColor[] RenderEffect(Vector2[] positions, double t)
        {
            var colors = new WinColor[positions.Length];
            double speed = _settings.LaptopEffectSpeed / 100.0;
            Color baseColor = _settings.LaptopColor;

            switch (_settings.LaptopEffect)
            {
                case LaptopKeyboardEffect.SolidColor:
                    Fill(colors, baseColor);
                    break;

                case LaptopKeyboardEffect.Breathing:
                {
                    // One breath every 6 s at the slowest setting, every 1 s at the fastest.
                    double period = 6.0 - 5.0 * speed;
                    double intensity = (1 - Math.Cos(2 * Math.PI * t / period)) / 2;
                    intensity = 0.04 + 0.96 * intensity;
                    Fill(colors, Color.FromArgb(255,
                        (int)(baseColor.R * intensity),
                        (int)(baseColor.G * intensity),
                        (int)(baseColor.B * intensity)));
                    break;
                }

                case LaptopKeyboardEffect.RainbowWave:
                {
                    // Default: the rainbow travels from left to right.
                    double degreesPerSecond = 30 + 330 * speed;
                    double phase = (_settings.LaptopEffectReverse ? 1 : -1) * t * degreesPerSecond;
                    for (int i = 0; i < colors.Length; i++)
                        colors[i] = ToWin(ColorUtil.FromHsv(positions[i].X * 360 + phase, 1, 1));
                    break;
                }

                case LaptopKeyboardEffect.RainbowWheel:
                {
                    // Hue follows the angle around the keyboard's center (aspect corrected so the
                    // wheel is round); default rotation is clockwise as seen on screen.
                    double degreesPerSecond = 30 + 330 * speed;
                    double phase = (_settings.LaptopEffectReverse ? 1 : -1) * t * degreesPerSecond;
                    double aspect = _aspect;
                    for (int i = 0; i < colors.Length; i++)
                    {
                        double dx = (positions[i].X - 0.5) * aspect;
                        double dy = positions[i].Y - 0.5;
                        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                        colors[i] = ToWin(ColorUtil.FromHsv(angle + phase, 1, 1));
                    }
                    break;
                }

                case LaptopKeyboardEffect.ColorCycle:
                {
                    double degreesPerSecond = 15 + 165 * speed;
                    Fill(colors, ColorUtil.FromHsv(t * degreesPerSecond, 1, 1));
                    break;
                }
            }

            return colors;
        }

        private static void Fill(WinColor[] colors, Color color)
        {
            var winColor = ToWin(color);
            for (int i = 0; i < colors.Length; i++)
                colors[i] = winColor;
        }

        private static WinColor ToWin(Color c) => WinColor.FromArgb(255, c.R, c.G, c.B);

        private void LogAvailabilityChange(bool available)
        {
            CurrentStatus = available
                ? "Active - Windows gave control of the keyboard to Razer Ambilight"
                : "Waiting for Windows to grant control (can take about 30 seconds after start)";

            if (available == _lastLoggedAvailability)
                return;

            _lastLoggedAvailability = available;
            _log.Info((available
                ? "Laptop keyboard is now under our control (Dynamic Lighting)."
                : "Laptop keyboard control lost (another app has priority in Windows Dynamic Lighting settings).")
                + " Foreground app: " + DescribeForegroundApp());
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static string DescribeForegroundApp()
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                using (var process = System.Diagnostics.Process.GetProcessById((int)pid))
                    return process.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }

        private static int Clamp(int value, int size) => Math.Max(0, Math.Min(value, size - 1));

        private async Task DiscoverAsync()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector());

                foreach (var device in devices)
                {
                    LampArray candidate;
                    try
                    {
                        candidate = await LampArray.FromIdAsync(device.Id);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(ex, $"Could not open LampArray device '{device.Name}'.");
                        continue;
                    }

                    if (candidate == null || candidate.LampArrayKind != LampArrayKind.Keyboard)
                        continue;

                    if (candidate.HardwareVendorId == RazerVendorId)
                        continue;

                    var count = candidate.LampCount;
                    var bounds = candidate.BoundingBox;
                    var positions = new Vector2[count];
                    var indices = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        var p = candidate.GetLampInfo(i).Position;
                        positions[i] = new Vector2(
                            bounds.X > 0 ? p.X / bounds.X : 0.5f,
                            bounds.Y > 0 ? p.Y / bounds.Y : 0.5f);
                        indices[i] = i;
                    }

                    candidate.IsEnabled = true;
                    _minUpdateInterval = candidate.MinUpdateInterval > TimeSpan.Zero
                        ? candidate.MinUpdateInterval
                        : TimeSpan.FromMilliseconds(33);
                    _aspect = bounds.Y > 0 ? bounds.X / bounds.Y : 1.3f;
                    _normalizedPositions = positions;
                    _indices = indices;
                    _lampArray = candidate;

                    _log.Info($"Dynamic Lighting keyboard found: '{device.Name}' ({candidate.HardwareVendorId:x4}:{candidate.HardwareProductId:x4}), {count} lamps, available={candidate.IsAvailable}. " +
                        "Windows typically hands background control to a freshly started app after about 30 seconds.");
                    return;
                }

                _log.Info("No non-Razer Dynamic Lighting keyboard found.");
                _nextDiscovery = DateTime.UtcNow + RetryDiscoveryInterval;
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "Dynamic Lighting discovery failed.");
                _nextDiscovery = DateTime.UtcNow + RetryDiscoveryInterval;
            }
        }
    }
}
