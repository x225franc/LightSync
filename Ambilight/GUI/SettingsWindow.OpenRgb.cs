using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using LightsConfig = Ambilight.Lights.Config;

namespace Ambilight.GUI
{
    /// <summary>The "OpenRGB" section on the Razer devices page.</summary>
    public partial class SettingsWindow
    {
        private readonly ObservableCollection<OpenRgbDeviceViewModel> _openRgbDevices = new ObservableCollection<OpenRgbDeviceViewModel>();
        private readonly DispatcherTimer _openRgbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        private void InitOpenRgb()
        {
            OpenRgbDevicesList.ItemsSource = _openRgbDevices;

            _openRgbTimer.Tick += (s, e) => RefreshOpenRgb();
            Loaded += (s, e) => { LoadOpenRgbValues(); RefreshOpenRgb(); _openRgbTimer.Start(); };
            Closed += (s, e) => _openRgbTimer.Stop();
        }

        private LightsConfig OpenRgbCfg { get { return Lights.LightsService.Config; } }

        private void LoadOpenRgbValues()
        {
            var cfg = OpenRgbCfg;
            if (cfg == null) return;

            _isLoading = true;
            OpenRgbEnabledToggle.IsChecked = cfg.OpenRgbEnabled;
            OpenRgbForceToggle.IsChecked = cfg.OpenRgbForce;
            OpenRgbHostBox.Text = cfg.OpenRgbHost;
            OpenRgbPortBox.Text = cfg.OpenRgbPort.ToString();
            _isLoading = false;
        }

        private void RefreshOpenRgb()
        {
            var cfg = OpenRgbCfg;
            var logicManager = Program.LogicManager;
            if (cfg == null || logicManager == null || !cfg.OpenRgbEnabled)
            {
                _openRgbDevices.Clear();
                OpenRgbStatusText.Text = cfg != null && cfg.OpenRgbEnabled ? "" : "OpenRGB is off.";
                OpenRgbNoDevicesText.Visibility = Visibility.Collapsed;
                return;
            }

            bool connected = logicManager.OpenRgbConnected;
            OpenRgbStatusText.Text = connected
                ? "Connected to the OpenRGB server."
                : "Not connected" + (string.IsNullOrEmpty(logicManager.OpenRgbLastError) ? " yet." : ": " + logicManager.OpenRgbLastError);

            var found = logicManager.OpenRgbDiscoveredDevices;

            var known = new System.Collections.Generic.HashSet<string>();
            foreach (var vm in _openRgbDevices) known.Add(vm.Name);
            var stillThere = new System.Collections.Generic.HashSet<string>();
            foreach (var d in found) stillThere.Add(d.Name);

            for (int i = _openRgbDevices.Count - 1; i >= 0; i--)
                if (!stillThere.Contains(_openRgbDevices[i].Name)) _openRgbDevices.RemoveAt(i);

            foreach (var d in found)
                if (!known.Contains(d.Name)) _openRgbDevices.Add(new OpenRgbDeviceViewModel(cfg, d.Name, d.LedCount));

            OpenRgbNoDevicesText.Visibility = connected && _openRgbDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenRgbEnabledToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || OpenRgbCfg == null) return;
            OpenRgbCfg.OpenRgbEnabled = OpenRgbEnabledToggle.IsChecked == true;
            OpenRgbCfg.Save();
            RefreshOpenRgb();
        }

        private void OpenRgbForceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || OpenRgbCfg == null) return;
            OpenRgbCfg.OpenRgbForce = OpenRgbForceToggle.IsChecked == true;
            OpenRgbCfg.Save();
        }

        private void OpenRgbHostBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading || OpenRgbCfg == null) return;
            string host = OpenRgbHostBox.Text.Trim();
            if (host.Length == 0) { OpenRgbHostBox.Text = OpenRgbCfg.OpenRgbHost; return; }
            OpenRgbCfg.OpenRgbHost = host;
            OpenRgbCfg.Save();
        }

        private void OpenRgbPortBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading || OpenRgbCfg == null) return;
            int port;
            if (!int.TryParse(OpenRgbPortBox.Text.Trim(), out port) || port <= 0 || port > 65535)
            {
                OpenRgbPortBox.Text = OpenRgbCfg.OpenRgbPort.ToString();
                return;
            }
            OpenRgbCfg.OpenRgbPort = port;
            OpenRgbCfg.Save();
        }
    }
}
