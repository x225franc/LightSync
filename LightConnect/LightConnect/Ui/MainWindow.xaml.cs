using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using LightConnect.Core;

namespace LightConnect.Ui
{
    public partial class MainWindow : FluentWindow
    {
        readonly Engine _engine;
        readonly Config _config;
        readonly ObservableCollection<BulbViewModel> _bulbs = new ObservableCollection<BulbViewModel>();
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        bool _isLoading;
        bool _firewallOk = true;

        /// <summary>Set by the application before it shuts down; otherwise closing the window only hides it.</summary>
        public bool AllowClose { get; set; }

        public MainWindow(Engine engine, Config config)
        {
            _engine = engine;
            _config = config;

            InitializeComponent();

            // One section per kind of device ("Govee", "Yeelight"), each with its own header.
            var view = new ListCollectionView(_bulbs);
            view.GroupDescriptions.Add(new PropertyGroupDescription("KindLabel"));
            view.SortDescriptions.Add(new SortDescription("KindLabel", ListSortDirection.Ascending));
            BulbList.ItemsSource = view;

            // Follow the Windows light/dark setting.
            var systemTheme = ApplicationThemeManager.GetSystemTheme();
            var theme = systemTheme == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
            ApplicationThemeManager.Apply(this);
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);

            _timer.Tick += (s, e) => RefreshView();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { LoadStaticValues(); RefreshView(); _timer.Start(); }
                else _timer.Stop();
            };
            Closing += (s, e) =>
            {
                if (AllowClose) return;
                e.Cancel = true;      // the app lives in the notification area
                Hide();
            };
        }

        void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (DevicesPage == null || SettingsPage == null) return;     // fires once during InitializeComponent
            bool settings = NavList.SelectedIndex == 1;
            DevicesPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
            SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        }

        void LoadStaticValues()
        {
            _isLoading = true;
            AutoStartToggle.IsChecked = AutoStart.IsEnabled;
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

        void AllowFirewallButton_Click(object sender, RoutedEventArgs e)
        {
            if (Firewall.AddAllowRule()) _firewallOk = Firewall.HasAllowRule();
            _engine.Rescan();
            RefreshView();
        }

        void GoveeFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GoveeFpsText == null) return;
            GoveeFpsText.Text = (int)e.NewValue + " fps";
            if (!_isLoading) _engine.GoveeFps = (int)e.NewValue;
        }

        void YeelightFadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (YeelightFadeText == null) return;
            int v = (int)e.NewValue;
            YeelightFadeText.Text = v == 0 ? "instant" : v + " ms";
            if (!_isLoading) _engine.YeelightFadeMs = v;
        }

        void YeelightOffSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (YeelightOffText == null) return;
            int v = (int)e.NewValue;
            YeelightOffText.Text = v == 0 ? "never off" : "off below " + v + " %";
            if (!_isLoading) _engine.YeelightOffBelow = v;
        }

        void InterpolateToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _engine.Interpolate = InterpolateToggle.IsChecked == true;
        }

        void YeelightFpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (YeelightFpsText == null) return;
            YeelightFpsText.Text = (int)e.NewValue + " fps";
            if (!_isLoading) _engine.YeelightFps = (int)e.NewValue;
        }

        void ControlToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _engine.Enabled = ControlToggle.IsChecked == true;
            RefreshView();
        }

        void RefreshView()
        {
            // Devices found after the window was built get a card of their own.
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

            FirewallBanner.Visibility = _firewallOk ? Visibility.Collapsed : Visibility.Visible;
            YeelightBanner.Visibility = AutoStart.IsRunning(AutoStart.YeelightOfficial) ? Visibility.Visible : Visibility.Collapsed;
            GoveeBanner.Visibility = AutoStart.IsRunning(AutoStart.GoveeDesktop) ? Visibility.Visible : Visibility.Collapsed;
            RestoreCard.Visibility = _config.SavedRunValues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        void RescanButton_Click(object sender, RoutedEventArgs e) { _engine.Rescan(); }

        void AutoStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            try { AutoStart.SetEnabled(AutoStartToggle.IsChecked == true); }
            catch (Exception ex)
            {
                Log.Error("Cannot change autostart.", ex);
                AutoStartToggle.IsChecked = AutoStart.IsEnabled;
            }
        }

        void DisableYeelightButton_Click(object sender, RoutedEventArgs e) { TakeOver(AutoStart.YeelightOfficial); }
        void DisableGoveeButton_Click(object sender, RoutedEventArgs e) { TakeOver(AutoStart.GoveeDesktop); }

        void TakeOver(ExternalApp app)
        {
            string previous = AutoStart.Disable(app);
            if (!string.IsNullOrEmpty(previous))
            {
                _config.SavedRunValues[app.RunValueName] = previous;
                _config.Save();
            }
            _engine.Rescan();
            RefreshView();
        }

        void RestoreOriginals_Click(object sender, RoutedEventArgs e)
        {
            foreach (var kv in _config.SavedRunValues) AutoStart.RestoreRunValue(kv.Key, kv.Value);
            _config.SavedRunValues.Clear();
            _config.Save();
            RefreshView();
        }

        void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(Config.DataDir, "logs");
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }
    }
}
