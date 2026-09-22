using AutoUpdaterDotNET;
using NLog;

namespace Ambilight
{

    /// <summary>
    /// Entry point
    /// </summary>
    internal class Program
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private static GUI.SettingsWindow _settingsWindow;

        // Keeping this rooted for the process lifetime is not optional: it
        // transitively owns the Chroma SDK IChroma instance and every device
        // Logic object. Discarding the result of "new LogicManager(...)"
        // (as this used to do) left nothing referencing any of that once
        // Main() returned - only the tray thread's message loop was keeping
        // the process alive - so the GC was free to collect it at any point
        // during normal operation. Colore's ChromaImplementation finalizer
        // then called into the native SDK to tear down effects, which
        // crashed hard (AccessViolationException / 0xC0000005) because that
        // teardown was never meant to run mid-session. This is what was
        // silently killing the process (and with it, all device updates,
        // including Chroma Link/Chroma Connect) at unpredictable times.
        private static Logic.LogicManager _logicManager;

        /// <summary>Reached from the settings window (e.g. for the group color test) - null until Main() has run.</summary>
        public static Logic.LogicManager LogicManager { get { return _logicManager; } }

        /// <summary>
        /// Entry point. Checks for updates and initializes the software
        /// </summary>
        /// <param name="args"></param>
        private static void Main(string[] args)
        {
            // Settings live under the exe's name: bring over those of the former "Ambilight" before anything reads them.
            Util.SettingsMigration.Run();


            System.AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                logger.Fatal(e.ExceptionObject as System.Exception, "Unhandled exception, process terminating.");
                LogManager.Flush();
            };

            var showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\LightSync.ShowSettings");
            var exitEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\LightSync.Exit");

            if (!AcquireSingleInstance(System.Array.IndexOf(args, "--restarted") >= 0))
            {
                // A second launch never starts a second copy: it asks the running one to open its window (so the app can
                // always be reopened, even when the tray icon is hidden), or to quit for "--exit".
                if (System.Array.IndexOf(args, "--exit") >= 0)
                    exitEvent.Set();
                else if (System.Array.IndexOf(args, "--minimized") < 0)
                    showEvent.Set();

                logger.Warn("Another LightSync instance is already running, exiting.");
                return;
            }

            if (System.Array.IndexOf(args, "--exit") >= 0)
                return;     // nothing running: nothing to stop

            logger.Info("\n\n\n --- LightSync (Razer Ambilight + Light Connect) Version 3.0.0 ----");
            // AutoUpdater désactivé pour éviter les interruptions
            // AutoUpdater.Start("https://nicojeske.de/ambi/ambi.xml");

            GUI.TraySettings tray = new GUI.TraySettings();
            logger.Info("Tray Created");
            _logicManager = new Logic.LogicManager(tray);

            // Yeelight + Govee lights (formerly the separate "Light Connect" app), driven from the same Razer effects.
            Lights.LightsService.Start();

            tray.SettingsRequested += (sender, args2) => OpenSettingsWindow(tray);

            if (System.Array.IndexOf(args, "--show-settings") >= 0)
                tray.RequestShowSettings();

            // Wakes the window (or quits) when another launch asks for it.
            var listener = new System.Threading.Thread(() =>
            {
                var handles = new System.Threading.WaitHandle[] { showEvent, exitEvent };
                while (true)
                {
                    int which = System.Threading.WaitHandle.WaitAny(handles);
                    if (which == 0)
                        tray.RequestShowSettings();
                    else
                    {
                        tray.ExitApplication();
                        return;
                    }
                }
            })
            { IsBackground = true, Name = "Second-launch listener" };
            listener.Start();
        }

        // Only the existence of the named handle matters (not ownership, which is
        // thread-affine and would be released as soon as Main's thread ends), so it
        // is simply kept open in a static for the life of the process.
        private static System.Threading.Mutex _singleInstanceMutex;

        private static bool AcquireSingleInstance(bool waitForPreviousInstance)
        {
            // After "restart" from the settings window the old process may still be
            // shutting down for a moment, so retry briefly before giving up.
            var deadline = System.DateTime.UtcNow.AddSeconds(waitForPreviousInstance ? 8 : 0);
            while (true)
            {
                _singleInstanceMutex = new System.Threading.Mutex(false, @"Local\LightSync.SingleInstance", out bool createdNew);
                if (createdNew)
                    return true;

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;

                if (System.DateTime.UtcNow >= deadline)
                    return false;

                System.Threading.Thread.Sleep(250);
            }
        }

        /// <summary>
        /// The window is shown from the tray thread, which has no WPF Application. Without one the Fluent theme falls back
        /// to its default blue; with one holding the WPF-UI dictionaries, the accent color chosen in Windows is used.
        /// </summary>
        private static void EnsureWpfApplication()
        {
            if (System.Windows.Application.Current != null)
                return;

            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        }

        private static void OpenSettingsWindow(GUI.TraySettings tray)
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            EnsureWpfApplication();
            _settingsWindow = new GUI.SettingsWindow(tray);
            _settingsWindow.Closed += (sender, args) => _settingsWindow = null;
            _settingsWindow.ShowDialog();
        }
    }
}