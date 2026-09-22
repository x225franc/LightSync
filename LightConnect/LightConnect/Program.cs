using System;
using System.Threading;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using LightConnect.Core;
using LightConnect.Ui;

namespace LightConnect
{
    static class Program
    {
        // Keeps the "already running" handshake alive for the whole process lifetime.
        static Mutex _instanceMutex;

        [STAThread]
        static int Main(string[] args)
        {
            bool minimized = Array.IndexOf(args, "--minimized") >= 0;

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Error("Unhandled exception, process terminating.", e.ExceptionObject as Exception);

            // One instance only, without any dialog. A second launch either does nothing (autostart)
            // or asks the running instance to show its window (so it can always be reopened).
            // After a restart the previous process may still be shutting down, so wait for it briefly.
            bool restarted = Array.IndexOf(args, "--restarted") >= 0;
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LightConnect.Show");
            var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LightConnect.Exit");
            var restartEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LightConnect.Restart");

            bool created;
            DateTime deadline = DateTime.UtcNow.AddSeconds(restarted ? 10 : 0);
            while (true)
            {
                _instanceMutex = new Mutex(true, @"Local\LightConnect.SingleInstance", out created);
                if (created) break;
                _instanceMutex.Dispose();
                _instanceMutex = null;

                if (restarted && DateTime.UtcNow < deadline) { Thread.Sleep(250); continue; }
                if (Array.IndexOf(args, "--exit") >= 0) exitEvent.Set();               // ask the running instance to quit
                else if (Array.IndexOf(args, "--restart") >= 0) restartEvent.Set();    // ...or to restart itself
                else if (!minimized) showEvent.Set();
                return 0;
            }
            if (Array.IndexOf(args, "--exit") >= 0 || Array.IndexOf(args, "--restart") >= 0) return 0;   // nothing running: nothing to stop

            Log.Info("--- Light Connect starting ---");
            AutoStart.MigrateLegacy();

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new ControlsDictionary());

            var config = Config.Load();
            var engine = new Engine(config);
            engine.Start();

            MainWindow window = null;
            Action show = () =>
            {
                if (window == null) window = new MainWindow(engine, config);
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            };
            Action exit = () =>
            {
                if (window != null) window.AllowClose = true;
                app.Shutdown();
            };

            bool restartRequested = false;
            Action restart = () =>
            {
                restartRequested = true;
                exit();
            };

            var tray = new TrayIcon(app.Dispatcher, show, restart, exit);

            // Wakes the window (or quits) when another launch asks for it.
            var waiter = new Thread(() =>
            {
                while (true)
                {
                    int which = WaitHandle.WaitAny(new WaitHandle[] { showEvent, exitEvent, restartEvent });
                    if (which == 0) app.Dispatcher.BeginInvoke(show);
                    else if (which == 1) { app.Dispatcher.BeginInvoke(exit); return; }
                    else { app.Dispatcher.BeginInvoke(restart); return; }
                }
            }) { IsBackground = true, Name = "Show-window listener" };
            waiter.Start();

            app.SessionEnding += (s, e) => exit();

            // First run has nothing to show in the tray yet, so open the window; later autostarts stay hidden.
            if (!minimized || config.IsNew) app.Dispatcher.BeginInvoke(show);

            app.Run();

            Log.Info(restartRequested ? "Restarting." : "Shutting down.");
            tray.Dispose();
            engine.Dispose();

            if (restartRequested)
            {
                // The new process waits until this one has released the single-instance mutex.
                _instanceMutex.Dispose();
                System.Diagnostics.Process.Start(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, "--restarted --minimized");
            }
            return 0;
        }
    }
}
