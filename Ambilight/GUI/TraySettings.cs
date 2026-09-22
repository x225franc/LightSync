using IWshRuntimeLibrary;
using Microsoft.Win32;
using NLog;
using System;
using System.Configuration;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Colore.Effects.Keyboard;

namespace Ambilight.GUI
{
    /// <summary>
    /// This class handles the settings, as well as the tray icon
    /// </summary>
    public class TraySettings
    {
        public int Tickrate { get; private set; }
        public float Saturation { get; private set; }
        public int KeyboardWidth { get; private set; }
        public int KeyboardHeight { get; private set; }
        public bool KeyboardEnabled { get; private set; }
        public bool MouseEnabled { get; private set; }
        public bool LinkEnabled { get; private set; }
        public bool PadEnabled { get; private set; }
        public bool HeadsetEnabled { get; private set; }
        public bool KeypadEnabeled { get; private set; }
        public bool AmbiModeEnabled { get; private set; }
        public bool UltrawideModeEnabled { get; private set; }
        /// <summary>Keep capturing (and so keep the lighting effect going) while the Windows screensaver runs.</summary>
        public volatile bool KeepEffectDuringScreensaver;
        public bool AutostartEnabled { get; private set; }
        public bool LaptopKeyboardEnabled { get; private set; }
        public int LaptopBrightness { get; private set; } = 100;
        /// <summary>Global gain (percent) applied to the colors sent to the Razer devices, and so to Chroma Connect lights.</summary>
        public int DeviceBrightness { get; private set; } = 100;
        public Logic.LaptopKeyboardEffect LaptopEffect { get; private set; }
        public int LaptopEffectSpeed { get; private set; } = 50;
        public bool LaptopEffectReverse { get; private set; }
        public Color LaptopColor { get; private set; } = Color.FromArgb(255, 45, 45);
        public int SelectedMonitor { get; set; }

        // Sliders and the color wheel change values continuously while dragging; persisting on
        // every change would hammer the disk, so saves are coalesced.
        private System.Threading.Timer _saveTimer;

        private NotifyIcon notifyIcon;
        private ContextMenuStrip _trayMenu;
        private Icon _trayIcon;
        private Control _uiMarshal;
        private volatile bool _rebuildPending;
        private DateTime _lastRebuildAt = DateTime.MinValue;
        // Resume, the lock screen's unlock and a display change can each fire this separately for the very same
        // wake-up (the unlock in particular can land well over 3 s after resume - however long the user takes to
        // type their password), so a short "one trigger at a time" guard is not enough: without this, each one
        // rebuilds the icon on its own, and every extra rebuild is another chance to leave a stale ghost behind in
        // Windows' own tray icon cache. One rebuild is enough to fix the stale GDI/DWM resources; a second or third
        // for the same wake-up only adds more ghosts.
        private static readonly TimeSpan RebuildCooldown = TimeSpan.FromSeconds(20);

        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        public event EventHandler SettingsRequested;

        public TraySettings()
        {
            KeyboardWidth = KeyboardConstants.MaxColumns;
            KeyboardHeight = KeyboardConstants.MaxRows;
            loadConfig();
            MigrateAutostartShortcut();
            Thread trayThread = new Thread(InitializeTray);
            trayThread.SetApartmentState(ApartmentState.STA);
            trayThread.Start();
        }

        /// <summary>
        /// Loads stored config values from storage.
        /// </summary>
        private void loadConfig()
        {
            try
            {
                Tickrate = Math.Abs(Properties.Settings.Default.tickrate);
                Saturation = Properties.Settings.Default.saturation;
                int _keyboardHeightProperty = Properties.Settings.Default.keyboardHeight;
                int _keyboardWidthProperty = Properties.Settings.Default.keyboardWidth;
                AutostartEnabled = Properties.Settings.Default.autostartEnabled;
                SelectedMonitor = Properties.Settings.Default.monitor;

                KeyboardEnabled = Properties.Settings.Default.keyboardEnabled;
                MouseEnabled = Properties.Settings.Default.mouseEnabled;
                PadEnabled = Properties.Settings.Default.mousematEnabled;
                HeadsetEnabled = Properties.Settings.Default.headsetEnabled;
                KeypadEnabeled = Properties.Settings.Default.keypadEnabled;
                LinkEnabled = Properties.Settings.Default.linkEnabled;
                AmbiModeEnabled = Properties.Settings.Default.ambiEnabled;
                UltrawideModeEnabled = Properties.Settings.Default.ultrawideEnabled;
                KeepEffectDuringScreensaver = Properties.Settings.Default.keepEffectDuringScreensaver;
                LaptopKeyboardEnabled = Properties.Settings.Default.laptopKeyboardEnabled;
                LaptopBrightness = Math.Max(0, Math.Min(100, Properties.Settings.Default.laptopBrightness));
                DeviceBrightness = Math.Max(10, Math.Min(300, Properties.Settings.Default.deviceBrightness));
                LaptopEffectSpeed = Math.Max(1, Math.Min(100, Properties.Settings.Default.laptopEffectSpeed));
                LaptopEffectReverse = Properties.Settings.Default.laptopEffectReverse;
                LaptopEffect = Enum.IsDefined(typeof(Logic.LaptopKeyboardEffect), Properties.Settings.Default.laptopEffect)
                    ? (Logic.LaptopKeyboardEffect)Properties.Settings.Default.laptopEffect
                    : Logic.LaptopKeyboardEffect.ScreenAmbilight;
                try
                {
                    LaptopColor = ColorTranslator.FromHtml(Properties.Settings.Default.laptopColor);
                }
                catch (Exception)
                {
                    logger.Warn("Invalid laptopColor, using the default.");
                }

                if (_keyboardWidthProperty >= 0 && _keyboardWidthProperty < KeyboardConstants.MaxColumns)
                {
                    KeyboardWidth = _keyboardWidthProperty;
                } else
                {
                    logger.Warn("Invalid keyboardWidth changing back to default value");
                    KeyboardWidth = KeyboardConstants.MaxColumns;
                }

                if (_keyboardHeightProperty >= 0 && _keyboardHeightProperty < KeyboardConstants.MaxRows)
                {
                    KeyboardHeight = _keyboardHeightProperty;
                } else
                {
                    logger.Warn("Invalid keyboardHeight changing back to default value");
                    KeyboardHeight = KeyboardConstants.MaxRows;
                }
            }
            catch (SettingsPropertyNotFoundException)
            {
                Tickrate = 5;
                Saturation = 1f;
            }

            logger.Info("Autostart: " + AutostartEnabled);
            logger.Info("Keyboard width: " + KeyboardWidth);
            logger.Info("Keyboard height: " + KeyboardHeight);
            logger.Info("Max FPS: " + Tickrate);
            logger.Info("Saturation: " + Saturation);
        }

        /// <summary>
        /// Initializes the tray icon and its (minimal) context menu. All actual
        /// configuration now happens in the WPF-UI SettingsWindow.
        /// </summary>
        private void InitializeTray()
        {
            // An exception thrown while painting the tray menu (typically right
            // after resuming from hibernation, when GDI/DWM resources are stale)
            // used to escape through the native window procedure and terminate
            // the whole process (0xC000041D). Log and swallow instead.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) =>
                logger.Error(e.Exception, "Unhandled exception on the tray UI thread (ignored).");

            // Hidden control whose only job is to give this thread something to
            // marshal work onto from SystemEvents' own thread.
            _uiMarshal = new Control();
            var forceHandle = _uiMarshal.Handle;

            BuildTrayIcon();

            SystemEvents.PowerModeChanged += (sender, e) =>
            {
                if (e.Mode == PowerModes.Resume)
                    RebuildTrayIconSoon();
            };
            SystemEvents.SessionSwitch += (sender, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock)
                    RebuildTrayIconSoon();
            };
            SystemEvents.DisplaySettingsChanged += (sender, e) => RebuildTrayIconSoon();

            logger.Info("Keyboard Enabled: " + KeyboardEnabled);
            logger.Info("Mouse Enabled: " + MouseEnabled);
            logger.Info("Mousemat Enabled: " + PadEnabled);
            logger.Info("Headset Enabled: " + HeadsetEnabled);
            logger.Info("Keypad Enabled: " + KeypadEnabeled);
            logger.Info("ChromaLink Enabled: " + LinkEnabled);
            logger.Info("Ambilight mode: " + AmbiModeEnabled);
            logger.Info("Ultrawide mode: " + UltrawideModeEnabled);

            Application.Run();
        }

        private void BuildTrayIcon()
        {
            var contextMenu = new ContextMenuStrip
            {
                Renderer = new DarkContextMenuRenderer(),
                ShowImageMargin = false
            };

            contextMenu.Items.Add("Settings", null, (sender, args) => RaiseSettingsRequested());
            contextMenu.Items.Add("Restart", null, (sender, args) => RestartApplication());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Exit", null, (sender, args) => ExitApplication());

            _trayMenu = contextMenu;
            _trayIcon = new Icon(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LightSync.ico"));

            notifyIcon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "LightSync",
                Visible = true,
                ContextMenuStrip = contextMenu
            };
            notifyIcon.DoubleClick += (sender, args) => RaiseSettingsRequested();
        }

        /// <summary>
        /// After hibernation/resume the taskbar and the GPU-backed drop-down
        /// windows can come back in a broken state (blank white menu, then a
        /// crash on click). Recreating the icon and menu from scratch once the
        /// shell has settled avoids ever touching the stale ones.
        /// </summary>
        private void RebuildTrayIconSoon()
        {
            if (_uiMarshal == null || !_uiMarshal.IsHandleCreated || _rebuildPending)
                return;
            if (DateTime.UtcNow - _lastRebuildAt < RebuildCooldown)
                return;     // already rebuilt for this wake-up - a later resume/unlock/display event needs no second one

            _rebuildPending = true;
            _uiMarshal.BeginInvoke(new Action(() =>
            {
                var timer = new System.Windows.Forms.Timer { Interval = 3000 };
                timer.Tick += (sender, args) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    _rebuildPending = false;
                    RebuildTrayIcon();
                };
                timer.Start();
            }));
        }

        private void RebuildTrayIcon()
        {
            try
            {
                if (_trayMenu != null && _trayMenu.Visible)
                    return;

                _lastRebuildAt = DateTime.UtcNow;
                logger.Debug("Rebuilding tray icon and menu.");

                var oldIcon = notifyIcon;
                var oldMenu = _trayMenu;
                var oldTrayIcon = _trayIcon;

                BuildTrayIcon();

                if (oldIcon != null)
                {
                    oldIcon.Visible = false;
                    oldIcon.Dispose();
                }
                oldMenu?.Dispose();
                oldTrayIcon?.Dispose();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to rebuild the tray icon.");
            }
        }

        /// <summary>Opens the settings window as soon as the tray thread is up (used by --show-settings).</summary>
        public void RequestShowSettings()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 100 && (_uiMarshal == null || !_uiMarshal.IsHandleCreated); i++)
                    Thread.Sleep(100);

                _uiMarshal?.BeginInvoke(new Action(RaiseSettingsRequested));
            });
        }

        private void RaiseSettingsRequested()
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Restarts the application. Used after changing settings (like the
        /// selected monitor) that can't be applied without recreating the
        /// desktop duplicator from scratch.
        /// </summary>
        public void RestartApplication()
        {
            Properties.Settings.Default.Save();
            Lights.LightsService.Stop();          // close the Yeelight sessions and leave Govee on its last color
            notifyIcon.Dispose();
            System.Diagnostics.Process.Start(Application.ExecutablePath, "--restarted");
            Environment.Exit(0);
        }

        public void ExitApplication()
        {
            Properties.Settings.Default.Save();
            Lights.LightsService.Stop();          // close the Yeelight sessions and leave Govee on its last color
            notifyIcon.Dispose();
            Environment.Exit(0);
        }

        public void SetKeyboardEnabled(bool value)
        {
            KeyboardEnabled = value;
            Properties.Settings.Default.keyboardEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetMouseEnabled(bool value)
        {
            MouseEnabled = value;
            Properties.Settings.Default.mouseEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetPadEnabled(bool value)
        {
            PadEnabled = value;
            Properties.Settings.Default.mousematEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetHeadsetEnabled(bool value)
        {
            HeadsetEnabled = value;
            Properties.Settings.Default.headsetEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetKeypadEnabled(bool value)
        {
            KeypadEnabeled = value;
            Properties.Settings.Default.keypadEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetLinkEnabled(bool value)
        {
            LinkEnabled = value;
            Properties.Settings.Default.linkEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetLaptopKeyboardEnabled(bool value)
        {
            LaptopKeyboardEnabled = value;
            Properties.Settings.Default.laptopKeyboardEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetLaptopBrightness(int value)
        {
            LaptopBrightness = Math.Max(0, Math.Min(100, value));
            Properties.Settings.Default.laptopBrightness = LaptopBrightness;
            ScheduleSave();
        }

        public void SetDeviceBrightness(int value)
        {
            DeviceBrightness = Math.Max(10, Math.Min(300, value));
            Properties.Settings.Default.deviceBrightness = DeviceBrightness;
            ScheduleSave();
        }

        public void SetLaptopEffect(Logic.LaptopKeyboardEffect value)
        {
            LaptopEffect = value;
            Properties.Settings.Default.laptopEffect = (int)value;
            ScheduleSave();
        }

        public void SetLaptopEffectSpeed(int value)
        {
            LaptopEffectSpeed = Math.Max(1, Math.Min(100, value));
            Properties.Settings.Default.laptopEffectSpeed = LaptopEffectSpeed;
            ScheduleSave();
        }

        public void SetLaptopEffectReverse(bool value)
        {
            LaptopEffectReverse = value;
            Properties.Settings.Default.laptopEffectReverse = value;
            ScheduleSave();
        }

        public void SetLaptopColor(Color value)
        {
            LaptopColor = Color.FromArgb(255, value.R, value.G, value.B);
            Properties.Settings.Default.laptopColor = ColorTranslator.ToHtml(LaptopColor);
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (_saveTimer == null)
                _saveTimer = new System.Threading.Timer(_ => Properties.Settings.Default.Save(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            _saveTimer.Change(600, System.Threading.Timeout.Infinite);
        }

        public void SetAmbiModeEnabled(bool value)
        {
            AmbiModeEnabled = value;
            Properties.Settings.Default.ambiEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetUltrawideModeEnabled(bool value)
        {
            UltrawideModeEnabled = value;
            Properties.Settings.Default.ultrawideEnabled = value;
            Properties.Settings.Default.Save();
        }

        public void SetKeepEffectDuringScreensaver(bool value)
        {
            KeepEffectDuringScreensaver = value;
            Properties.Settings.Default.keepEffectDuringScreensaver = value;
            Properties.Settings.Default.Save();
        }

        public void SetTickrate(int value)
        {
            Tickrate = value;
            Properties.Settings.Default["tickrate"] = value;
            Properties.Settings.Default.Save();
        }

        public void SetSaturation(float value)
        {
            Saturation = value;
            Properties.Settings.Default.saturation = value;
            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// Sets the monitor to capture. Takes effect after a restart, since the
        /// desktop duplicator is built once per adapter/output at startup.
        /// </summary>
        public void SetMonitor(int index)
        {
            SelectedMonitor = index;
            Properties.Settings.Default.monitor = index;
            Properties.Settings.Default.Save();
        }

        public bool SetKeyboardSize(int width, int height)
        {
            if (width < 0 || width > KeyboardConstants.MaxColumns || height < 0 || height > KeyboardConstants.MaxRows)
                return false;

            KeyboardWidth = width;
            KeyboardHeight = height;
            Properties.Settings.Default.keyboardWidth = width;
            Properties.Settings.Default.keyboardHeight = height;
            Properties.Settings.Default.Save();
            return true;
        }

        public void SetAutostartEnabled(bool value)
        {
            if (value == AutostartEnabled)
                return;

            if (value)
                CreateAutostartShortcut();
            else
                RemoveAutostartShortcut();

            AutostartEnabled = value;
            Properties.Settings.Default.autostartEnabled = value;
            Properties.Settings.Default.Save();
        }

        private static string AutostartShortcutPath =>
            Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "/LightSync.lnk";

        private void CreateAutostartShortcut()
        {
            WshShell shell = new WshShell();
            IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(AutostartShortcutPath);
            shortcut.Description = "LightSync: Razer, Yeelight, Govee and laptop keyboard lighting";
            shortcut.TargetPath = Application.ExecutablePath;
            shortcut.Arguments = "--minimized";
            shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            shortcut.Save();
        }

        /// <summary>
        /// The app used to autostart through an "Ambilight.lnk" shortcut. When autostart is on, replace it with the
        /// LightSync one, so the old exe is not started at logon next to this one.
        /// </summary>
        private void MigrateAutostartShortcut()
        {
            try
            {
                string legacy = Environment.GetFolderPath(Environment.SpecialFolder.Startup) + "/Ambilight.lnk";
                bool hadLegacy = System.IO.File.Exists(legacy);
                if (hadLegacy)
                    System.IO.File.Delete(legacy);

                if (AutostartEnabled && (hadLegacy || !System.IO.File.Exists(AutostartShortcutPath)))
                    CreateAutostartShortcut();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Could not migrate the autostart shortcut.");
            }
        }

        private void RemoveAutostartShortcut()
        {
            if (System.IO.File.Exists(AutostartShortcutPath))
                System.IO.File.Delete(AutostartShortcutPath);
        }
    }
}
