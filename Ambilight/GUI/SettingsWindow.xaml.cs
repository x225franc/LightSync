using System;
using System.Windows;
using System.Windows.Forms;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Ambilight.GUI
{
    /// <summary>
    /// Consolidated Fluent/Mica settings window, replacing the old scattered
    /// WinForms popup dialogs (FpsControl, SaturationControl, KeyboardSizeControl, Monitor).
    /// </summary>
    public partial class SettingsWindow : FluentWindow
    {
        private const string SetupCommand = "& \"C:\\LightSync\\Package\\Install-LightingProvider.ps1\"";

        // Govee LAN discovery is multicast; Windows sends it through the adapter with the lowest
        // metric, which on a PC with VPN/virtual adapters is often not the Wi-Fi one.
        private const string GoveeFixCommand = "Set-NetIPInterface -InterfaceAlias \"Wi-Fi\" -AutomaticMetric Disabled -InterfaceMetric 1";
        private const string GoveeRevertCommand = "Set-NetIPInterface -InterfaceAlias \"Wi-Fi\" -AutomaticMetric Enabled";

        // Display order of the effect list; independent of the enum values, which are what gets
        // saved in the user's settings.
        private static readonly (Logic.LaptopKeyboardEffect Effect, string Name)[] LaptopEffects =
        {
            (Logic.LaptopKeyboardEffect.ScreenAmbilight, "Screen ambilight"),
            (Logic.LaptopKeyboardEffect.SolidColor, "Solid color"),
            (Logic.LaptopKeyboardEffect.Breathing, "Breathing"),
            (Logic.LaptopKeyboardEffect.ColorCycle, "Color cycle"),
            (Logic.LaptopKeyboardEffect.RainbowWave, "Rainbow wave"),
            (Logic.LaptopKeyboardEffect.RainbowWheel, "Rainbow wheel")
        };

        private static Logic.LaptopKeyboardEffect EffectAt(int index) =>
            LaptopEffects[Math.Max(0, Math.Min(LaptopEffects.Length - 1, index))].Effect;

        private readonly TraySettings _settings;
        private readonly System.Windows.Threading.DispatcherTimer _statusTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private bool _isLoading;

        public SettingsWindow(TraySettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            InitializeComponent();
            InitLights();
            InitCanvas();

            SetupCommandBox.Text = SetupCommand;
            GoveeFixCommandBox.Text = GoveeFixCommand;
            GoveeRevertCommandBox.Text = GoveeRevertCommand;
            LaptopColorWheel.ColorChanged += (sender, e) =>
            {
                if (!_isLoading)
                    ApplyLaptopColor(LaptopColorWheel.SelectedColor, ColorSource.Wheel);
            };

            _statusTimer.Tick += (sender, e) => LaptopStatusText.Text = Logic.LampArrayLogic.CurrentStatus;
            _statusTimer.Start();
            Closed += (sender, e) => _statusTimer.Stop();

            // Match the current Windows light/dark setting so the card
            // backgrounds/text use the correct contrast (defaults to Light
            // otherwise, which reads as washed-out grey boxes over the dark
            // Mica backdrop).
            var systemTheme = ApplicationThemeManager.GetSystemTheme();
            var initialTheme = systemTheme == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(initialTheme, WindowBackdropType.Mica, true);
            ApplicationThemeManager.Apply(this);
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);

            // Use the accent color chosen in Windows (buttons, sliders, toggles, the selected menu entry) instead of the
            // default blue, and pick up a change made in Windows Settings the next time this window gets the focus.
            ApplicationAccentColorManager.ApplySystemAccent();
            Activated += (sender, e) => ApplicationAccentColorManager.ApplySystemAccent();

            // NumberBox only reflects a programmatically-set Value in its visible
            // text once its control template has been applied, which happens
            // during the layout pass after the window is shown - not yet at this
            // point in the constructor. Loading here left FPS/Saturation/keyboard
            // size boxes visually blank even though the value was set correctly.
            Loaded += (sender, e) => LoadFromSettings();
        }

        private void LoadFromSettings()
        {
            _isLoading = true;

            KeyboardToggle.IsChecked = _settings.KeyboardEnabled;
            MouseToggle.IsChecked = _settings.MouseEnabled;
            PadToggle.IsChecked = _settings.PadEnabled;
            HeadsetToggle.IsChecked = _settings.HeadsetEnabled;
            KeypadToggle.IsChecked = _settings.KeypadEnabeled;
            LinkToggle.IsChecked = _settings.LinkEnabled;
            LaptopKeyboardToggle.IsChecked = _settings.LaptopKeyboardEnabled;

            LaptopEffectCombo.Items.Clear();
            foreach (var entry in LaptopEffects)
                LaptopEffectCombo.Items.Add(entry.Name);
            LaptopEffectCombo.SelectedIndex = Array.FindIndex(LaptopEffects, entry => entry.Effect == _settings.LaptopEffect);
            LaptopBrightnessSlider.Value = _settings.LaptopBrightness;
            LaptopBrightnessText.Text = _settings.LaptopBrightness + "%";
            LaptopSpeedSlider.Value = _settings.LaptopEffectSpeed;
            LaptopSpeedText.Text = _settings.LaptopEffectSpeed + "%";
            ApplyLaptopColor(_settings.LaptopColor, ColorSource.Load);
            LaptopReverseToggle.IsChecked = _settings.LaptopEffectReverse;
            LaptopStatusText.Text = Logic.LampArrayLogic.CurrentStatus;
            UpdateLaptopEffectCards();

            TickrateSlider.Value = Math.Max(1, Math.Min(60, _settings.Tickrate));
            TickrateText.Text = Math.Max(1, Math.Min(60, _settings.Tickrate)) + " fps";
            int saturation = (int)Math.Round(Math.Max(-100, Math.Min(300, _settings.Saturation * 100)) / 5.0) * 5;
            SaturationSlider.Value = saturation;
            SaturationText.Text = saturation + " %";
            DeviceBrightnessSlider.Value = _settings.DeviceBrightness;
            DeviceBrightnessText.Text = _settings.DeviceBrightness + "%";
            AmbiToggle.IsChecked = _settings.AmbiModeEnabled;
            UltrawideToggle.IsChecked = _settings.UltrawideModeEnabled;
            KeepScreensaverToggle.IsChecked = _settings.KeepEffectDuringScreensaver;

            KeyboardWidthBox.Text = _settings.KeyboardWidth.ToString();
            KeyboardHeightBox.Text = _settings.KeyboardHeight.ToString();

            AutostartToggle.IsChecked = _settings.AutostartEnabled;

            MonitorCombo.Items.Clear();
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                MonitorCombo.Items.Add($"Monitor {i + 1}");
            }
            MonitorCombo.SelectedIndex = Math.Min(_settings.SelectedMonitor, MonitorCombo.Items.Count - 1);

            _isLoading = false;
        }

        private void KeyboardToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetKeyboardEnabled(KeyboardToggle.IsChecked ?? false);

        private void MouseToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetMouseEnabled(MouseToggle.IsChecked ?? false);

        private void PadToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetPadEnabled(PadToggle.IsChecked ?? false);

        private void HeadsetToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetHeadsetEnabled(HeadsetToggle.IsChecked ?? false);

        private void KeypadToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetKeypadEnabled(KeypadToggle.IsChecked ?? false);

        private void LinkToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetLinkEnabled(LinkToggle.IsChecked ?? false);

        private void NudgeChromaLinkButton_Click(object sender, RoutedEventArgs e) =>
            Program.LogicManager?.KickChromaLink();

        private void LaptopKeyboardToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetLaptopKeyboardEnabled(LaptopKeyboardToggle.IsChecked ?? false);

        private void LaptopEffectCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isLoading || LaptopEffectCombo.SelectedIndex < 0)
                return;

            _settings.SetLaptopEffect(EffectAt(LaptopEffectCombo.SelectedIndex));
            UpdateLaptopEffectCards();
        }

        // Speed only matters for animated effects, the color only for the ones based on one.
        private void UpdateLaptopEffectCards()
        {
            var effect = EffectAt(LaptopEffectCombo.SelectedIndex);

            LaptopSpeedCard.Visibility = effect == Logic.LaptopKeyboardEffect.Breathing
                                         || effect == Logic.LaptopKeyboardEffect.RainbowWave
                                         || effect == Logic.LaptopKeyboardEffect.ColorCycle
                                         || effect == Logic.LaptopKeyboardEffect.RainbowWheel
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Direction only means something for the effects that travel across the keyboard.
            LaptopReverseCard.Visibility = effect == Logic.LaptopKeyboardEffect.RainbowWave
                                           || effect == Logic.LaptopKeyboardEffect.RainbowWheel
                ? Visibility.Visible
                : Visibility.Collapsed;

            LaptopColorCard.Visibility = effect == Logic.LaptopKeyboardEffect.SolidColor
                                         || effect == Logic.LaptopKeyboardEffect.Breathing
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private enum ColorSource { Load, Wheel, Rgb, Hex }

        private bool _syncingColor;

        // The wheel, the R/G/B boxes, the hex box and the swatch all show the same color; whichever
        // one the user edits is the source and every other one is updated from it.
        private void ApplyLaptopColor(System.Drawing.Color color, ColorSource source)
        {
            if (_syncingColor)
                return;

            _syncingColor = true;
            try
            {
                if (source != ColorSource.Wheel)
                    LaptopColorWheel.SelectedColor = color;

                if (source != ColorSource.Rgb)
                {
                    LaptopRedBox.Text = color.R.ToString();
                    LaptopGreenBox.Text = color.G.ToString();
                    LaptopBlueBox.Text = color.B.ToString();
                }

                if (source != ColorSource.Hex)
                    LaptopHexBox.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";

                LaptopColorSwatch.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));

                if (source != ColorSource.Load)
                    _settings.SetLaptopColor(color);
            }
            finally
            {
                _syncingColor = false;
            }
        }

        private static bool TryParseChannel(string text, out int value)
        {
            if (!int.TryParse(text, out value) || value < 0)
                return false;

            value = Math.Min(255, value);
            return true;
        }

        private void LaptopRgbBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isLoading || _syncingColor)
                return;

            if (TryParseChannel(LaptopRedBox.Text, out int r)
                && TryParseChannel(LaptopGreenBox.Text, out int g)
                && TryParseChannel(LaptopBlueBox.Text, out int b))
            {
                ApplyLaptopColor(System.Drawing.Color.FromArgb(255, r, g, b), ColorSource.Rgb);
            }
        }

        private void LaptopHexBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isLoading || _syncingColor)
                return;

            string hex = LaptopHexBox.Text.Trim().TrimStart('#');
            if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            {
                ApplyLaptopColor(System.Drawing.Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF), ColorSource.Hex);
            }
        }

        private void LaptopReverseToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetLaptopEffectReverse(LaptopReverseToggle.IsChecked ?? false);

        private void LaptopBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LaptopBrightnessText == null)
                return;

            LaptopBrightnessText.Text = (int)e.NewValue + "%";
            if (!_isLoading)
                _settings.SetLaptopBrightness((int)e.NewValue);
        }

        private void DeviceBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DeviceBrightnessText == null)
                return;

            DeviceBrightnessText.Text = (int)e.NewValue + "%";
            if (!_isLoading)
                _settings.SetDeviceBrightness((int)e.NewValue);
        }

        private void LaptopSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LaptopSpeedText == null)
                return;

            LaptopSpeedText.Text = (int)e.NewValue + "%";
            if (!_isLoading)
                _settings.SetLaptopEffectSpeed((int)e.NewValue);
        }

        private void CopyGoveeFixCommand_Click(object sender, RoutedEventArgs e) =>
            CopyToClipboard(GoveeFixCommand);

        private void CopyGoveeRevertCommand_Click(object sender, RoutedEventArgs e) =>
            CopyToClipboard(GoveeRevertCommand);

        private void CopyToClipboard(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                StatusText.Text = "Command copied to the clipboard.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not copy: " + ex.Message;
            }
        }

        private void CopySetupCommand_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(SetupCommand);
                StatusText.Text = "Setup command copied to the clipboard.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not copy: " + ex.Message;
            }
        }

        private void AmbiToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetAmbiModeEnabled(AmbiToggle.IsChecked ?? false);

        private void KeepScreensaverToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetKeepEffectDuringScreensaver(KeepScreensaverToggle.IsChecked ?? false);

        private void UltrawideToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetUltrawideModeEnabled(UltrawideToggle.IsChecked ?? false);

        private void AutostartToggle_Click(object sender, RoutedEventArgs e) =>
            _settings.SetAutostartEnabled(AutostartToggle.IsChecked ?? false);

        private void TickrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TickrateText == null)
                return;

            int value = (int)e.NewValue;
            TickrateText.Text = value + " fps";
            if (!_isLoading)
                _settings.SetTickrate(value);
        }

        private void SaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SaturationText == null)
                return;

            int value = (int)e.NewValue;
            SaturationText.Text = value + " %";
            if (!_isLoading)
                _settings.SetSaturation((float)(value / 100.0));
        }

        private void ApplyKeyboardSize_Click(object sender, RoutedEventArgs e)
        {
            int.TryParse(KeyboardWidthBox.Text, out var width);
            int.TryParse(KeyboardHeightBox.Text, out var height);

            if (!_settings.SetKeyboardSize(width, height))
            {
                StatusText.Text = "Invalid keyboard size.";
                return;
            }

            StatusText.Text = "Keyboard size applied.";
        }

        private void MonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isLoading || MonitorCombo.SelectedIndex < 0 || MonitorCombo.SelectedIndex == _settings.SelectedMonitor)
                return;

            _settings.SetMonitor(MonitorCombo.SelectedIndex);

            var result = MessageBox.Show(
                "The application must be restarted to apply this change. Do you want to restart now?",
                "Restart required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _settings.RestartApplication();
            }
        }

        private void IdentifyGroupsButton_Click(object sender, RoutedEventArgs e)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int idx = Math.Max(0, Math.Min(MonitorCombo.SelectedIndex, screens.Length - 1));
            GroupIdentifyOverlay.Show(screens[idx].Bounds, TimeSpan.FromSeconds(5));
        }
    }
}
