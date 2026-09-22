using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;
using LightConnect.Core;

namespace LightConnect.Ui
{
    /// <summary>
    /// Notification-area icon, modeled on Razer Ambilight's: dark "Settings / Exit" menu, double-click
    /// opens the window, and the icon and menu are rebuilt after resume, unlock or a display change
    /// (stale GDI/DWM resources otherwise leave a blank menu). NotifyIcon also re-adds itself when
    /// Explorer restarts, which is what the official connector was missing.
    /// </summary>
    sealed class TrayIcon : IDisposable
    {
        readonly Action _open, _restart, _exit;
        readonly Dispatcher _ui;
        NotifyIcon _icon;
        ContextMenuStrip _menu;
        Icon _image;
        bool _rebuildPending, _disposed;

        public TrayIcon(Dispatcher ui, Action open, Action restart, Action exit)
        {
            _ui = ui; _open = open; _restart = restart; _exit = exit;
            Build();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        void OnPowerModeChanged(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) RebuildSoon(); }
        void OnSessionSwitch(object s, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionUnlock) RebuildSoon(); }
        void OnDisplayChanged(object s, EventArgs e) { RebuildSoon(); }

        void Build()
        {
            var menu = new ContextMenuStrip { Renderer = new DarkContextMenuRenderer(), ShowImageMargin = false };
            menu.Items.Add("Settings", null, (s, e) => _open());
            menu.Items.Add("Restart", null, (s, e) => _restart());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => _exit());

            _menu = menu;
            _image = LoadIcon();
            _icon = new NotifyIcon { Icon = _image, Text = "Light Connect", Visible = true, ContextMenuStrip = menu };
            _icon.DoubleClick += (s, e) => _open();
        }

        void RebuildSoon()
        {
            if (_rebuildPending || _disposed) return;
            _rebuildPending = true;
            _ui.BeginInvoke(new Action(() =>
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _rebuildPending = false;
                    Rebuild();
                };
                timer.Start();
            }));
        }

        void Rebuild()
        {
            try
            {
                if (_disposed || (_menu != null && _menu.Visible)) return;
                Log.Info("Rebuilding the tray icon and menu.");

                var oldIcon = _icon; var oldMenu = _menu; var oldImage = _image;
                Build();
                if (oldIcon != null) { oldIcon.Visible = false; oldIcon.Dispose(); }
                if (oldMenu != null) oldMenu.Dispose();
                if (oldImage != null) oldImage.Dispose();
            }
            catch (Exception e) { Log.Error("Failed to rebuild the tray icon.", e); }
        }

        static Icon LoadIcon()
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                Icon icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
            catch { }
            return (Icon)SystemIcons.Application.Clone();
        }

        public void Dispose()
        {
            _disposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
            if (_menu != null) _menu.Dispose();
            if (_image != null) _image.Dispose();
        }
    }
}
