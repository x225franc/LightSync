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

        /// <summary>
        /// Entry point. Checks for updates and initializes the software
        /// </summary>
        /// <param name="args"></param>
        private static void Main(string[] args)
        {

            System.AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                logger.Fatal(e.ExceptionObject as System.Exception, "Unhandled exception, process terminating.");
                LogManager.Flush();
            };

            if (!AcquireSingleInstance(System.Array.IndexOf(args, "--restarted") >= 0))
            {
                logger.Warn("Another Ambilight instance is already running, exiting.");
                return;
            }

            logger.Info("\n\n\n --- Razer Ambilight Version 3.0.0 ----");
            // AutoUpdater désactivé pour éviter les interruptions
            // AutoUpdater.Start("https://nicojeske.de/ambi/ambi.xml");

            GUI.TraySettings tray = new GUI.TraySettings();
            logger.Info("Tray Created");
            _logicManager = new Logic.LogicManager(tray);

            tray.SettingsRequested += (sender, args2) => OpenSettingsWindow(tray);

            if (System.Array.IndexOf(args, "--show-settings") >= 0)
                tray.RequestShowSettings();
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
                _singleInstanceMutex = new System.Threading.Mutex(false, @"Local\RazerAmbilight.SingleInstance", out bool createdNew);
                if (createdNew)
                    return true;

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;

                if (System.DateTime.UtcNow >= deadline)
                    return false;

                System.Threading.Thread.Sleep(250);
            }
        }

        private static void OpenSettingsWindow(GUI.TraySettings tray)
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new GUI.SettingsWindow(tray);
            _settingsWindow.Closed += (sender, args) => _settingsWindow = null;
            _settingsWindow.ShowDialog();
        }
    }
}