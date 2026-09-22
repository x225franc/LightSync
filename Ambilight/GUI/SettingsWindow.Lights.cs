using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Ambilight.Lights;
using LightsConfig = Ambilight.Lights.Config;

namespace Ambilight.GUI
{
    /// <summary>The "Lights" page (Yeelight + Govee, formerly the standalone Light Connect app) and its settings.</summary>
    public partial class SettingsWindow
    {
        private Engine _engine;
        private LightsConfig _lightsConfig;
        private readonly ObservableCollection<BulbViewModel> _bulbs = new ObservableCollection<BulbViewModel>();
        private readonly DispatcherTimer _lightsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private bool _firewallOk = true;

        private void InitLights()
        {
            _engine = LightsService.Engine;
            _lightsConfig = LightsService.Config;

            if (_engine == null)
            {
                LightsContent.Visibility = Visibility.Collapsed;
                LightsUnavailableText.Visibility = Visibility.Visible;
                return;
            }

            // One section per kind of device ("Govee", "Yeelight"), each with its own header.
            var view = new ListCollectionView(_bulbs);
            view.GroupDescriptions.Add(new PropertyGroupDescription("KindLabel"));
            view.SortDescriptions.Add(new SortDescription("KindLabel", ListSortDirection.Ascending));
            BulbList.ItemsSource = view;

            _lightsTimer.Tick += (s, e) => RefreshLights();
            Loaded += (s, e) => { LoadLightsValues(); RefreshLights(); _lightsTimer.Start(); };
            Closed += (s, e) => _lightsTimer.Stop();
        }

        private void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (RazerPage == null || SettingsPage == null)
                return;     // fires once while the window is being built

            var pages = new FrameworkElement[] { RazerPage, LightsPage, CanvasPage, LaptopPage, CapturePage, SettingsPage };
            for (int i = 0; i < pages.Length; i++)
                pages[i].Visibility = i == NavList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoadLightsValues()
        {
            if (_engine == null)
                return;

            _isLoading = true;
            ControlToggle.IsChecked = _engine.Enabled;
            InterpolateToggle.IsChecked = _engine.Interpolate;
            YeelightFadeSlider.Value = _engine.YeelightFadeMs;
            YeelightFadeText.Text = _engine.YeelightFadeMs == 0 ? "instant" : _engine.YeelightFadeMs + " ms";
            YeelightOffSlider.Value = _engine.YeelightOffBelow;
            YeelightOffText.Text = _engine.YeelightOffBelow == 0 ? "never off" : "off below " + _engine.YeelightOffBelow + " %";
            GoveeFpsSlider.Value = _engine.GoveeFps;
            GoveeFpsText.Text = _engine.GoveeFps + " fps";
            YeelightFpsSlider.Value = _engine.YeelightFps;
            YeelightFpsText.Text = _engine.YeelightFps + " fps";
            _isLoading = false;
            _firewallOk = Firewall.HasAllowRule();
        }

        private void RefreshLights()
        {
            if (_engine == null)
                return;

            // Devices found after the window was opened get a card of their own.
            var known = new HashSet<BulbState>();
            foreach (var vm in _bulbs) known.Add(vm.State);
            foreach (var state in _engine.Bulbs)
                if (!known.Contains(state)) _bulbs.Add(new BulbViewModel(_engine, state));
            foreach (var vm in _bulbs) vm.Refresh();

            NoBulbsText.Visibility = _bulbs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChromaStatusText.Text = _engine.ChromaStatus;
            ScanStatusText.Text = _engine.Scanning
                ? "Searching for devices on all network adapters..."
                : (_engine.LastScan == default(DateTime) ? "Not searched yet." : "Last search: " + _engine.LastScan.ToString("HH:mm:ss"));

            bool testing = Program.LogicManager != null && Program.LogicManager.ColorTestRunning;
            ColorTestButton.Content = testing ? "Stop the test" : "Start the test";
            ColorTestStatusText.Text = testing ? "Running - see the log file for which zone is on now." : "";

            FirewallBanner.Visibility = _firewallOk ? Visibility.Collapsed : Visibility.Visible;
            LightConnectBanner.Visibility = ExternalApps.IsRunning(ExternalApps.LightConnect) ? Visibility.Visible : Visibility.Collapsed;
            YeelightBanner.Visibility = ExternalApps.IsRunning(ExternalApps.YeelightOfficial) ? Visibility.Visible : Visibility.Collapsed;
            GoveeBanner.Visibility = ExternalApps.IsRunning(ExternalApps.GoveeDesktop) ? Visibility.Visible : Visibility.Collapsed;
            RestoreCard.Visibility = _lightsConfig.SavedRunValues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            SyncCanvas();       // GUI/SettingsWindow.Canvas.cs
        }

        private void ControlToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _engine == null) return;
            _engine.Enabled = ControlToggle.IsChecked == true;
            RefreshLights();
        }

        private void RescanButton_Click(object sender, RoutedEventArgs e) { if (_engine != null) _engine.Rescan(); }

        private void ColorTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (Program.LogicManager == null) return;
            if (Program.LogicManager.ColorTestRunning) Program.LogicManager.StopColorTest();
            else Program.LogicManager.StartColorTest();
            RefreshLights();
        }

        private void AllowFirewallButton_Click(object sender, RoutedEventArgs e)
        {
            if (Firewall.AddAllowRule()) _firewallOk = Firewall.HasAllowRule();
            if (_engine != null) _engine.Rescan();
            RefreshLights();
        }

        private void DisableLightConnectButton_Click(object sender, RoutedEventArgs e) { TakeOver(ExternalApps.LightConnect); }
        private void DisableYeelightButton_Click(object sender, RoutedEventArgs e) { TakeOver(ExternalApps.YeelightOfficial); }
        private void DisableGoveeButton_Click(object sender, RoutedEventArgs e) { TakeOver(ExternalApps.GoveeDesktop); }

        private void TakeOver(ExternalApp app)
        {
            string previous = ExternalApps.Disable(app);
            if (!string.IsNullOrEmpty(previous))
            {
                _lightsConfig.SavedRunValues[app.RunValueName] = previous;
                _lightsConfig.Save();
            }
            if (_engine != null) _engine.Rescan();
            RefreshLights();
        }

        private void RestoreOriginals_Click(object sender, RoutedEventArgs e)
        {
            foreach (var kv in _lightsConfig.SavedRunValues) ExternalApps.RestoreRunValue(kv.Key, kv.Value);
            _lightsConfig.SavedRunValues.Clear();
            _lightsConfig.Save();
            RefreshLights();
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        private void InterpolateToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _engine == null) return;
            _engine.Interpolate = InterpolateToggle.IsChecked == true;
        }

        private void YeelightFadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (YeelightFadeText == null) return;
            int v = (int)e.NewValue;
            YeelightFadeText.Text = v == 0 ? "instant" : v + " ms";
            if (!_isLoading && _engine != null) _engine.YeelightFadeMs = v;
        }

        private void YeelightOffSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (YeelightOffText == null) return;
            int v = (int)e.NewValue;
            YeelightOffText.Text = v == 0 ? "never off" : "off below " + v + " %";
            if (!_isLoading && _engine != null) _engine.YeelightOffBelow = v;
        }

        private void GoveeFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GoveeFpsText == null) return;
            GoveeFpsText.Text = (int)e.NewValue + " fps";
            if (!_isLoading && _engine != null) _engine.GoveeFps = (int)e.NewValue;
        }

        private void YeelightFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (YeelightFpsText == null) return;
            YeelightFpsText.Text = (int)e.NewValue + " fps";
            if (!_isLoading && _engine != null) _engine.YeelightFps = (int)e.NewValue;
        }
    }
}
