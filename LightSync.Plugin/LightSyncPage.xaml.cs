using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ambilight.GUI;
using Ambilight.Lights;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using LibEffect = Ambilight.Logic.LaptopKeyboardEffect;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace LightSync.Plugin;

public partial class LightSyncPage : UiPage
{
    private readonly TraySettings _settings = EngineHost.Settings;
    private DispatcherTimer? _timer;
    private bool _isUpdatingUi;

    // Setting a control's initial value in XAML (Slider Value=, ListBoxItem IsSelected=, ...) raises its change
    // event mid-parse, while InitializeComponent is still connecting the fields declared after it in the tree -
    // every one of those is still null at that point. Every handler below bails out immediately while this is
    // false, and it only flips true once the constructor - and so InitializeComponent - has fully returned.
    private bool _constructed;

    public LightSyncPage()
    {
        InitializeComponent();
        _constructed = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _nav.SelectedIndex = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUpdatingUi = true;

        _masterEnabledToggle.IsChecked = _settings.MasterEnabled;
        _ultrawideToggle.IsChecked = _settings.UltrawideModeEnabled;
        _ambiToggle.IsChecked = _settings.AmbiModeEnabled;
        _screensaverToggle.IsChecked = _settings.KeepEffectDuringScreensaver;
        _tickrateSlider.Value = _settings.Tickrate;
        _saturationSlider.Value = _settings.Saturation * 100.0;
        _deviceBrightnessSlider.Value = _settings.DeviceBrightness;
        _interpolateToggle.IsChecked = LightsService.Engine?.Interpolate ?? false;
        LoadMonitorCombo();

        var engineForLights = LightsService.Engine;
        _yeelightOffBelowSlider.Value = engineForLights?.YeelightOffBelow ?? 0;
        _yeelightFadeMsSlider.Value = engineForLights?.YeelightFadeMs ?? 400;
        _goveeFpsSlider.Value = engineForLights?.GoveeFps ?? 30;
        _yeelightFpsSlider.Value = engineForLights?.YeelightFps ?? 30;

        _keyboardToggle.IsChecked = _settings.KeyboardEnabled;
        _mouseToggle.IsChecked = _settings.MouseEnabled;
        _padToggle.IsChecked = _settings.PadEnabled;
        _headsetToggle.IsChecked = _settings.HeadsetEnabled;
        _keypadToggle.IsChecked = _settings.KeypadEnabeled;
        _linkToggle.IsChecked = _settings.LinkEnabled;
        _kbWidthSlider.Value = _settings.KeyboardWidth;
        _kbHeightSlider.Value = _settings.KeyboardHeight;

        _laptopEnabledToggle.IsChecked = _settings.LaptopKeyboardEnabled;
        _laptopEffectCombo.SelectedIndex = (int)_settings.LaptopEffect;
        SetLaptopColorControls(_settings.LaptopColor);
        _laptopReverseToggle.IsChecked = _settings.LaptopEffectReverse;
        _laptopBrightnessSlider.Value = _settings.LaptopBrightness;
        _laptopSpeedSlider.Value = _settings.LaptopEffectSpeed;

        UpdateLaptopEffectVisibility();
        RefreshBulbList(force: true);
        UpdateChromaStatus();

        _isUpdatingUi = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => { UpdateStatus(); UpdateChromaStatus(); RefreshBulbList(force: false); };
        _timer.Start();
        UpdateStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_constructed) return;

        if (_nav.SelectedItem is not ListBoxItem item)
            return;

        _generalPanel.Visibility = Visibility.Collapsed;
        _razerPanel.Visibility = Visibility.Collapsed;
        _lightsPanel.Visibility = Visibility.Collapsed;
        _canvasPanel.Visibility = Visibility.Collapsed;
        _laptopPanel.Visibility = Visibility.Collapsed;

        switch (item.Tag as string)
        {
            case "General": _generalPanel.Visibility = Visibility.Visible; UpdateStatus(); break;
            case "Razer": _razerPanel.Visibility = Visibility.Visible; break;
            case "Lights": _lightsPanel.Visibility = Visibility.Visible; UpdateChromaStatus(); RefreshBulbList(force: true); break;
            case "Canvas": _canvasPanel.Visibility = Visibility.Visible; SyncCanvas(); break;
            case "Laptop": _laptopPanel.Visibility = Visibility.Visible; UpdateStatus(); break;
        }
    }

    // ---- General ----

    private void MasterEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;

        var on = _masterEnabledToggle.IsChecked ?? false;
        _settings.MasterEnabled = on;
        var engine = LightsService.Engine;
        if (engine != null) engine.Enabled = on;
        EngineHost.SaveSettings();
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        EngineHost.Restart();
        UpdateStatus();
    }

    private void UltrawideToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        _settings.UltrawideModeEnabled = _ultrawideToggle.IsChecked ?? false;
        EngineHost.SaveSettings();
    }

    private void AmbiToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        _settings.AmbiModeEnabled = _ambiToggle.IsChecked ?? false;
        EngineHost.SaveSettings();
    }

    private void ScreensaverToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        _settings.KeepEffectDuringScreensaver = _screensaverToggle.IsChecked ?? false;
        EngineHost.SaveSettings();
    }

    private void TickrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _tickrateValue.Text = $"{e.NewValue:F0}";
        if (_isUpdatingUi) return;
        _settings.Tickrate = (int)e.NewValue;
        EngineHost.SaveSettings();
    }

    private void SaturationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _saturationValue.Text = $"{e.NewValue:F0}%";
        if (_isUpdatingUi) return;
        _settings.Saturation = (float)(e.NewValue / 100.0);
        EngineHost.SaveSettings();
    }

    private void DeviceBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _deviceBrightnessValue.Text = $"{e.NewValue:F0}%";
        if (_isUpdatingUi) return;
        _settings.DeviceBrightness = (int)e.NewValue;
        EngineHost.SaveSettings();
    }

    private void InterpolateToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        var engine = LightsService.Engine;
        if (engine != null) engine.Interpolate = _interpolateToggle.IsChecked ?? false;
    }

    private static string LogFilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LenovoLegionToolkit", "log", "plugin_LightSync.Plugin.LightSyncProvider.log");

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.IO.File.Exists(LogFilePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not open the LightSync log file.");
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(LogFilePath);
            if (folder != null && System.IO.Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not open the log folder.");
        }
    }

    private void YeelightOffBelowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _yeelightOffBelowValue.Text = e.NewValue <= 0 ? "never" : $"off below {e.NewValue:F0}%";
        if (_isUpdatingUi) return;
        var engine = LightsService.Engine;
        if (engine != null) engine.YeelightOffBelow = (int)e.NewValue;
    }

    private void YeelightFadeMsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _yeelightFadeMsValue.Text = $"{e.NewValue:F0} ms";
        if (_isUpdatingUi) return;
        var engine = LightsService.Engine;
        if (engine != null) engine.YeelightFadeMs = (int)e.NewValue;
    }

    private void GoveeFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _goveeFpsValue.Text = $"{e.NewValue:F0} fps";
        if (_isUpdatingUi) return;
        var engine = LightsService.Engine;
        if (engine != null) engine.GoveeFps = (int)e.NewValue;
    }

    private void YeelightFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _yeelightFpsValue.Text = $"{e.NewValue:F0} fps";
        if (_isUpdatingUi) return;
        var engine = LightsService.Engine;
        if (engine != null) engine.YeelightFps = (int)e.NewValue;
    }

    private void LoadMonitorCombo()
    {
        _monitorCombo.Items.Clear();
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            _monitorCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{i}: {s.DeviceName} ({s.Bounds.Width}x{s.Bounds.Height}){(s.Primary ? " - primary" : "")}",
            });
        }
        _monitorCombo.SelectedIndex = Math.Max(0, Math.Min(screens.Length - 1, _settings.SelectedMonitor));
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        _settings.SelectedMonitor = _monitorCombo.SelectedIndex;
        EngineHost.SaveSettings();
    }

    // ---- Razer devices ----

    private void RazerDeviceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;

        _settings.KeyboardEnabled = _keyboardToggle.IsChecked ?? false;
        _settings.MouseEnabled = _mouseToggle.IsChecked ?? false;
        _settings.PadEnabled = _padToggle.IsChecked ?? false;
        _settings.HeadsetEnabled = _headsetToggle.IsChecked ?? false;
        _settings.KeypadEnabeled = _keypadToggle.IsChecked ?? false;
        _settings.LinkEnabled = _linkToggle.IsChecked ?? false;
        EngineHost.SaveSettings();
    }

    private void KeyboardWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _kbWidthValue.Text = $"{e.NewValue:F0}";
        if (_isUpdatingUi) return;
        _settings.KeyboardWidth = (int)e.NewValue;
        EngineHost.SaveSettings();
    }

    private void KeyboardHeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _kbHeightValue.Text = $"{e.NewValue:F0}";
        if (_isUpdatingUi) return;
        _settings.KeyboardHeight = (int)e.NewValue;
        EngineHost.SaveSettings();
    }

    // ---- Laptop keyboard ----

    private void LaptopEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        _settings.LaptopKeyboardEnabled = _laptopEnabledToggle.IsChecked ?? false;
        EngineHost.SaveSettings();
    }

    private void LaptopEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_constructed) return;
        UpdateLaptopEffectVisibility();
        if (_isUpdatingUi) return;
        _settings.LaptopEffect = (LibEffect)_laptopEffectCombo.SelectedIndex;
        EngineHost.SaveSettings();
    }

    private void UpdateLaptopEffectVisibility()
    {
        var effect = (LibEffect)_laptopEffectCombo.SelectedIndex;
        _laptopColorCard.Visibility = effect is LibEffect.SolidColor or LibEffect.Breathing ? Visibility.Visible : Visibility.Collapsed;
        _laptopReverseCard.Visibility = effect is LibEffect.RainbowWave or LibEffect.RainbowWheel ? Visibility.Visible : Visibility.Collapsed;
        _laptopSpeedCard.Visibility = effect == LibEffect.ScreenAmbilight || effect == LibEffect.SolidColor ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool _isEditingLaptopColor;

    private void LaptopColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        SetLaptopColorControls(_settings.LaptopColor);
        _laptopColorPopup.IsOpen = true;
    }

    private void SetLaptopColorControls(System.Drawing.Color c)
    {
        _isEditingLaptopColor = true;
        _laptopColorSquarePicker.SelectedColor = WpfColor.FromRgb(c.R, c.G, c.B);
        _laptopRedSlider.Value = c.R;
        _laptopGreenSlider.Value = c.G;
        _laptopBlueSlider.Value = c.B;
        _laptopHexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}";
        _laptopColorSwatch.Background = new WpfSolidColorBrush(WpfColor.FromRgb(c.R, c.G, c.B));
        _isEditingLaptopColor = false;
    }

    private void ApplyLaptopColor(byte r, byte g, byte b)
    {
        _settings.LaptopColor = System.Drawing.Color.FromArgb(255, r, g, b);
        EngineHost.SaveSettings();
        SetLaptopColorControls(_settings.LaptopColor);
    }

    private void LaptopSquarePicker_ColorChanged(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isEditingLaptopColor) return;
        var c = _laptopColorSquarePicker.SelectedColor;
        ApplyLaptopColor(c.R, c.G, c.B);
    }

    private void LaptopRgbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed || _isEditingLaptopColor) return;
        ApplyLaptopColor((byte)_laptopRedSlider.Value, (byte)_laptopGreenSlider.Value, (byte)_laptopBlueSlider.Value);
    }

    private void LaptopHexBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isEditingLaptopColor) return;

        var text = _laptopHexBox.Text.TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var argb))
        {
            SetLaptopColorControls(_settings.LaptopColor);
            return;
        }

        ApplyLaptopColor((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
    }

    private void LaptopReverseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        _settings.LaptopEffectReverse = _laptopReverseToggle.IsChecked ?? false;
        EngineHost.SaveSettings();
    }

    private void LaptopBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _laptopBrightnessValue.Text = $"{e.NewValue:F0}%";
        if (_isUpdatingUi) return;
        _settings.LaptopBrightness = (int)e.NewValue;
        EngineHost.SaveSettings();
    }

    private void LaptopSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_constructed) return;
        _laptopSpeedValue.Text = $"{e.NewValue:F0}%";
        if (_isUpdatingUi) return;
        _settings.LaptopEffectSpeed = (int)e.NewValue;
        EngineHost.SaveSettings();
    }

    // ---- Lights ----

    private void LightsMasterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;
        var engine = LightsService.Engine;
        if (engine != null) engine.Enabled = _lightsMasterToggle.IsChecked ?? false;
    }

    private void ColorTestButton_Click(object sender, RoutedEventArgs e)
    {
        var logicManager = EngineHost.LogicManagerInstance;
        if (logicManager is null) return;

        if (logicManager.ColorTestRunning)
        {
            logicManager.StopColorTest();
            _colorTestButton.Content = "Start test";
        }
        else
        {
            logicManager.StartColorTest();
            _colorTestButton.Content = "Stop test";
        }
    }

    private void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        LightsService.Engine?.Rescan();
        RefreshBulbList(force: true);
    }

    private void UpdateChromaStatus()
    {
        var engine = LightsService.Engine;
        if (engine is null) return;

        if (!_isUpdatingUi) _lightsMasterToggle.IsChecked = engine.Enabled;
        _chromaStatusText.Text = engine.ChromaStatus;
        _scanStatusText.Text = engine.Scanning
            ? "Searching for devices on all network adapters..."
            : (engine.LastScan == default ? "Not searched yet." : $"Last search: {engine.LastScan:HH:mm:ss}");

        UpdateConflictBanners();
    }

    private void UpdateConflictBanners()
    {
        var config = LightsService.Config;

        _firewallBanner.Visibility = Firewall.HasAllowRule() ? Visibility.Collapsed : Visibility.Visible;
        _yeelightConflictBanner.Visibility = ExternalApps.IsRunning(ExternalApps.YeelightOfficial) ? Visibility.Visible : Visibility.Collapsed;
        _goveeConflictBanner.Visibility = ExternalApps.IsRunning(ExternalApps.GoveeDesktop) ? Visibility.Visible : Visibility.Collapsed;
        _lightConnectBanner.Visibility = ExternalApps.IsRunning(ExternalApps.LightConnect) ? Visibility.Visible : Visibility.Collapsed;
        _restoreAppsBanner.Visibility = config != null && config.SavedRunValues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddFirewallRuleButton_Click(object sender, RoutedEventArgs e)
    {
        Firewall.AddAllowRule();
        UpdateConflictBanners();
    }

    private void DisableYeelightButton_Click(object sender, RoutedEventArgs e) => TakeOverExternalApp(ExternalApps.YeelightOfficial);
    private void DisableGoveeButton_Click(object sender, RoutedEventArgs e) => TakeOverExternalApp(ExternalApps.GoveeDesktop);
    private void DisableLightConnectButton_Click(object sender, RoutedEventArgs e) => TakeOverExternalApp(ExternalApps.LightConnect);

    private void TakeOverExternalApp(ExternalApp app)
    {
        var config = LightsService.Config;
        var previous = ExternalApps.Disable(app);
        if (config != null && !string.IsNullOrEmpty(previous))
        {
            config.SavedRunValues[app.RunValueName] = previous;
            config.Save();
        }
        LightsService.Engine?.Rescan();
        UpdateConflictBanners();
    }

    private void RestoreAppsButton_Click(object sender, RoutedEventArgs e)
    {
        var config = LightsService.Config;
        if (config is null) return;

        foreach (var kv in config.SavedRunValues)
            ExternalApps.RestoreRunValue(kv.Key, kv.Value);
        config.SavedRunValues.Clear();
        config.Save();
        UpdateConflictBanners();
    }

    /// <summary>Live-updatable pieces of a bulb card, kept around across ticks. Only these get touched on a
    /// periodic refresh - the interactive controls (toggles, combo, slider, name box) are built once and then
    /// left alone, so clicking one is never fought by a rebuild landing mid-interaction (that was the toggle
    /// flicker: full rebuild every 2s meant a fresh ToggleSwitch replaced the one the user had just clicked,
    /// snapping it back to whatever the last-read backend state was at that instant).</summary>
    private sealed class BulbRow
    {
        public required Ellipse Swatch;
        public required Ellipse StatusDot;
        public required TextBlock StatusText;
        public required TextBlock ModelLine;
        public required ToggleSwitch EnabledToggle;
        public required ComboBox GroupCombo;
        public required ToggleSwitch CanvasToggle;
    }

    private readonly Dictionary<string, BulbRow> _bulbRows = new();

    private void RefreshBulbList(bool force)
    {
        var engine = LightsService.Engine;
        var bulbs = engine?.Bulbs ?? new List<BulbState>();
        var ids = new HashSet<string>(bulbs.Select(b => b.Config.Id));

        if (force || !ids.SetEquals(_bulbRows.Keys))
        {
            _bulbRows.Clear();

            var rows = new StackPanel();
            foreach (var group in bulbs.GroupBy(b => b.Kind).OrderBy(g => g.Key))
            {
                rows.Children.Add(new TextBlock
                {
                    Margin = new Thickness(0, 8, 0, 8),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Text = $"{group.Key} ({group.Count()})",
                });
                foreach (var bulb in group)
                {
                    var (card, row) = BuildBulbRow(engine!, bulb);
                    _bulbRows[bulb.Config.Id] = row;
                    rows.Children.Add(card);
                }
            }

            if (bulbs.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "No Yeelight/Govee device found yet.",
                    Foreground = (WpfBrush)FindResource("TextFillColorSecondaryBrush"),
                    Margin = new Thickness(0, 8, 0, 0),
                });
            }

            _bulbList.Items.Clear();
            _bulbList.Items.Add(rows);
            return;
        }

        // Same set of devices as last time: just refresh what changes on its own (color, connection status),
        // and gently resync the interactive controls in case something changed from elsewhere (e.g. the Canvas
        // page flipping "Screen canvas" for this bulb) - guarded so that doesn't fire their own click handlers.
        if (engine is null) return;

        var wasUpdating = _isUpdatingUi;
        _isUpdatingUi = true;
        try
        {
            foreach (var bulb in bulbs)
            {
                if (!_bulbRows.TryGetValue(bulb.Config.Id, out var row)) continue;

                row.Swatch.Fill = new WpfSolidColorBrush(bulb.Config.Controlled ? RgbToWpfColor(bulb.CurrentRgb) : WpfColor.FromRgb(0x55, 0x55, 0x55));
                row.StatusDot.Fill = new WpfSolidColorBrush(StatusColor(bulb, engine));
                row.StatusText.Text = StatusText(bulb, engine);
                row.ModelLine.Text = $"{(string.IsNullOrWhiteSpace(bulb.Model) ? "unknown model" : bulb.Model)} - {bulb.Ip ?? "not found yet"}";

                if (row.EnabledToggle.IsChecked != bulb.Config.Enabled) row.EnabledToggle.IsChecked = bulb.Config.Enabled;
                var groupIndex = GroupToIndex(bulb.Config.Group);
                if (row.GroupCombo.SelectedIndex != groupIndex) row.GroupCombo.SelectedIndex = groupIndex;
                if (row.CanvasToggle.IsChecked != bulb.Config.UseScreenPosition) row.CanvasToggle.IsChecked = bulb.Config.UseScreenPosition;
            }
        }
        finally
        {
            _isUpdatingUi = wasUpdating;
        }
    }

    private (Border Card, BulbRow Row) BuildBulbRow(Engine engine, BulbState bulb)
    {
        var secondaryBrush = (WpfBrush)FindResource("TextFillColorSecondaryBrush");

        // ---- header row: swatch, name/model/status stack, enabled + preview on the right ----

        var swatch = new Ellipse
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 12, 0),
            Fill = new WpfSolidColorBrush(bulb.Config.Controlled ? RgbToWpfColor(bulb.CurrentRgb) : WpfColor.FromRgb(0x55, 0x55, 0x55)),
        };

        var nameText = new TextBlock
        {
            FontWeight = FontWeights.Medium,
            Text = string.IsNullOrWhiteSpace(bulb.Config.Name) ? (bulb.DeviceName ?? bulb.Config.Id) : bulb.Config.Name,
        };
        var modelLine = new TextBlock
        {
            FontSize = 11,
            Foreground = secondaryBrush,
            Text = $"{(string.IsNullOrWhiteSpace(bulb.Model) ? "unknown model" : bulb.Model)} - {bulb.Ip ?? "not found yet"}",
        };
        var statusDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new WpfSolidColorBrush(StatusColor(bulb, engine)),
        };
        var statusTextBlock = new TextBlock { FontSize = 11, Foreground = secondaryBrush, Text = StatusText(bulb, engine) };
        var statusLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        statusLine.Children.Add(statusDot);
        statusLine.Children.Add(statusTextBlock);

        var identityColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identityColumn.Children.Add(nameText);
        identityColumn.Children.Add(modelLine);
        identityColumn.Children.Add(statusLine);

        var enabledToggle = new ToggleSwitch { IsChecked = bulb.Config.Enabled, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        enabledToggle.Click += (_, _) =>
        {
            if (_isUpdatingUi) return;
            engine.SetEnabled(bulb, enabledToggle.IsChecked ?? false);
        };

        var previewButton = new Wpf.Ui.Controls.Button { Content = "Preview", VerticalAlignment = VerticalAlignment.Center };
        previewButton.Click += (_, _) => engine.Identify(bulb);

        var actionsColumn = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actionsColumn.Children.Add(enabledToggle);
        actionsColumn.Children.Add(previewButton);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(identityColumn, 1);
        Grid.SetColumn(actionsColumn, 2);
        header.Children.Add(swatch);
        header.Children.Add(identityColumn);
        header.Children.Add(actionsColumn);

        // ---- labeled rows: Name / Chroma group / Screen canvas / Brightness ----

        var nameBox = new Wpf.Ui.Controls.TextBox { Text = nameText.Text };
        nameBox.LostFocus += (_, _) =>
        {
            if (_isUpdatingUi) return;
            engine.SetName(bulb, nameBox.Text);
            nameText.Text = nameBox.Text;
        };

        var groupCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, Width = 190 };
        groupCombo.Items.Add(new ComboBoxItem { Content = "Default" });
        groupCombo.Items.Add(new ComboBoxItem { Content = "Group 1" });
        groupCombo.Items.Add(new ComboBoxItem { Content = "Group 2" });
        groupCombo.Items.Add(new ComboBoxItem { Content = "Group 3" });
        groupCombo.Items.Add(new ComboBoxItem { Content = "Group 4" });
        if (bulb.Kind == DeviceKind.Govee)
            groupCombo.Items.Add(new ComboBoxItem { Content = "Groups 1-4 (gradient)" });
        groupCombo.SelectedIndex = GroupToIndex(bulb.Config.Group);
        groupCombo.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingUi) return;
            engine.SetGroup(bulb, IndexToGroup(groupCombo.SelectedIndex));
        };

        var canvasToggle = new ToggleSwitch { IsChecked = bulb.Config.UseScreenPosition, HorizontalAlignment = HorizontalAlignment.Left };
        canvasToggle.Click += (_, _) =>
        {
            if (_isUpdatingUi) return;
            engine.SetUseScreenPosition(bulb, canvasToggle.IsChecked ?? false);
            SyncCanvas();
        };

        var brightnessSlider = new Slider { Minimum = 1, Maximum = 100, Value = bulb.Config.Brightness, VerticalAlignment = VerticalAlignment.Center };
        var brightnessValue = new TextBlock { Width = 40, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Text = $"{bulb.Config.Brightness}%" };
        brightnessSlider.ValueChanged += (_, e) =>
        {
            brightnessValue.Text = $"{e.NewValue:F0}%";
            if (_isUpdatingUi) return;
            engine.SetBrightness(bulb, (int)e.NewValue);
        };
        var brightnessRow = new Grid();
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brightnessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(brightnessSlider, 0);
        Grid.SetColumn(brightnessValue, 1);
        brightnessRow.Children.Add(brightnessSlider);
        brightnessRow.Children.Add(brightnessValue);

        var content = new StackPanel();
        content.Children.Add(LabeledRow("Name", nameBox));
        content.Children.Add(LabeledRow("Chroma group", groupCombo));
        content.Children.Add(LabeledRow("Screen canvas", canvasToggle));
        content.Children.Add(LabeledRow("Brightness", brightnessRow));

        var card = new StackPanel();
        card.Children.Add(header);
        card.Children.Add(content);

        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(16),
            Background = (WpfBrush)FindResource("ControlFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(8),
            Child = card,
        };

        var row = new BulbRow
        {
            Swatch = swatch,
            StatusDot = statusDot,
            StatusText = statusTextBlock,
            ModelLine = modelLine,
            EnabledToggle = enabledToggle,
            GroupCombo = groupCombo,
            CanvasToggle = canvasToggle,
        };

        return (border, row);
    }

    /// <summary>One label-left / control-right row, matching the standalone app's device card layout.</summary>
    private static Grid LabeledRow(string label, UIElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(control, 1);
        row.Children.Add(labelBlock);
        row.Children.Add(control);
        return row;
    }

    private static WpfColor StatusColor(BulbState bulb, Engine engine)
    {
        if (!engine.Enabled || !bulb.Config.Enabled) return WpfColor.FromRgb(0x88, 0x88, 0x88);
        return bulb.Status switch
        {
            BulbStatus.Connected => WpfColor.FromRgb(0x4C, 0xAF, 0x50),
            BulbStatus.Connecting or BulbStatus.Searching => WpfColor.FromRgb(0xFF, 0xB3, 0x00),
            BulbStatus.Unreachable => WpfColor.FromRgb(0xE5, 0x39, 0x35),
            _ => WpfColor.FromRgb(0x88, 0x88, 0x88),
        };
    }

    private static string StatusText(BulbState bulb, Engine engine)
    {
        if (!engine.Enabled) return "Paused (light control is off)";
        if (!bulb.Config.Enabled) return "Disabled";
        return bulb.Status switch
        {
            BulbStatus.Connected => "Connected",
            BulbStatus.Connecting => "Connecting...",
            BulbStatus.Searching => "Searching on the network...",
            BulbStatus.Unreachable => "Unreachable" + (string.IsNullOrEmpty(bulb.Error) ? "" : $" ({bulb.Error})"),
            BulbStatus.NotControlled => "Not controlled",
            _ => bulb.Status.ToString(),
        };
    }

    private static WpfColor RgbToWpfColor(int rgb) => WpfColor.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    private static int GroupToIndex(int group)
    {
        var g = group == 2 ? 1 : group;
        return g switch { 1 => 0, 3 => 1, 4 => 2, 5 => 3, 6 => 4, _ => 0 };
    }

    private static int IndexToGroup(int index) => index switch { 0 => 0, 1 => 1, 2 => 3, 3 => 4, 4 => 5, 5 => 6, _ => 0 };

    // ---- Status ----

    private void UpdateStatus()
    {
        var engine = LightsService.Engine;
        var logicManager = EngineHost.LogicManagerInstance;

        _diagnosticsText.Text =
            $"Engine: {(engine is not null ? "running" : "not running")}\n" +
            $"Chroma logic: {(logicManager is not null ? "initialized" : "not initialized")}\n" +
            $"Devices discovered: {engine?.Bulbs.Count ?? 0}";

        _laptopStatus.Text = Ambilight.Logic.LampArrayLogic.CurrentStatus;
    }
}
