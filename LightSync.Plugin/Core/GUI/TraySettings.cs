#nullable disable
using System.Drawing;
using Newtonsoft.Json;

namespace Ambilight.GUI
{
    /// <summary>
    /// Stand-in for the standalone app's GUI.TraySettings (which is wired to WinForms/System.Configuration and
    /// not portable as-is): same namespace and member names, so every ported Logic/Lights/DesktopDuplication file
    /// that reads a "TraySettings" needs no changes at all. Backed by plain settable properties for now - real
    /// persistence (and a settings page) comes once the engine itself is confirmed working inside the host.
    /// </summary>
    public class TraySettings
    {
        /// <summary>Master switch for the whole Razer/laptop-keyboard side (Yeelight/Govee has its own master via
        /// Engine.Enabled). Every *Enabled property below is ANDed with this live, so flipping it off pauses
        /// everything at once without losing which individual devices were turned on, and flipping it back on
        /// restores exactly that previous state.</summary>
        public bool MasterEnabled { get; set; } = true;

        public int Tickrate { get; set; } = 30;
        public float Saturation { get; set; } = 1f;
        public int KeyboardWidth { get; set; } = 22;
        public int KeyboardHeight { get; set; } = 6;

        [JsonIgnore] public bool KeyboardEnabled { get => MasterEnabled && _keyboardEnabled; set => _keyboardEnabled = value; }
        [JsonProperty("KeyboardEnabled")] private bool _keyboardEnabled = true;

        [JsonIgnore] public bool MouseEnabled { get => MasterEnabled && _mouseEnabled; set => _mouseEnabled = value; }
        [JsonProperty("MouseEnabled")] private bool _mouseEnabled = true;

        [JsonIgnore] public bool LinkEnabled { get => MasterEnabled && _linkEnabled; set => _linkEnabled = value; }
        [JsonProperty("LinkEnabled")] private bool _linkEnabled = true;

        [JsonIgnore] public bool PadEnabled { get => MasterEnabled && _padEnabled; set => _padEnabled = value; }
        [JsonProperty("PadEnabled")] private bool _padEnabled = true;

        [JsonIgnore] public bool HeadsetEnabled { get => MasterEnabled && _headsetEnabled; set => _headsetEnabled = value; }
        [JsonProperty("HeadsetEnabled")] private bool _headsetEnabled = true;

        [JsonIgnore] public bool KeypadEnabeled { get => MasterEnabled && _keypadEnabeled; set => _keypadEnabeled = value; }
        [JsonProperty("KeypadEnabeled")] private bool _keypadEnabeled = true;

        public bool AmbiModeEnabled { get; set; }
        public bool UltrawideModeEnabled { get; set; }

        /// <summary>Kept as a raw field (not a property) to match the original exactly - it is written from a
        /// foreign thread in DesktopDuplicatorReader. [JsonProperty] is needed for persistence since Newtonsoft
        /// only serializes properties by default, not fields.</summary>
        [JsonProperty]
        public volatile bool KeepEffectDuringScreensaver = true;

        [JsonIgnore] public bool LaptopKeyboardEnabled { get => MasterEnabled && _laptopKeyboardEnabled; set => _laptopKeyboardEnabled = value; }
        [JsonProperty("LaptopKeyboardEnabled")] private bool _laptopKeyboardEnabled = true;

        public int LaptopBrightness { get; set; } = 100;
        public int DeviceBrightness { get; set; } = 100;
        public Ambilight.Logic.LaptopKeyboardEffect LaptopEffect { get; set; } = Ambilight.Logic.LaptopKeyboardEffect.ScreenAmbilight;
        public int LaptopEffectSpeed { get; set; } = 50;
        public bool LaptopEffectReverse { get; set; }
        public Color LaptopColor { get; set; } = Color.FromArgb(255, 255, 45, 45);
        public int SelectedMonitor { get; set; }
    }
}
