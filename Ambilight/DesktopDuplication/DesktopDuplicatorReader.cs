using Microsoft.Win32;
using NLog;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ambilight.DesktopDuplication
{
    internal class DesktopDuplicatorReader : IDesktopDuplicatorReader
    {
        private readonly Logger _log = LogManager.GetCurrentClassLogger();
        private readonly Logic.LogicManager _logic;
        private readonly GUI.TraySettings settings;

        // Set while the machine is suspending/locked so the capture loop stops
        // touching DXGI entirely instead of spinning against a dead device
        // (this is both a crash source and a needless CPU sink).
        private volatile bool _suspended;

        // The screensaver engaging/disengaging doesn't reliably raise
        // SystemEvents.SessionSwitch/PowerModeChanged when this app has no
        // foreground window (the usual case for a tray app), which is exactly
        // the scenario that was crashing: screensaver kicks in, DXGI's
        // desktop duplication surface goes away, then the very next
        // AcquireNextFrame/CopySubresourceRegion call on mouse-move can hit
        // the lost device hard enough to bring down the process before a
        // managed exception even has a chance to be caught. Polling the
        // actual OS screensaver state directly - instead of relying on a
        // push notification - closes that gap.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

        private const uint SPI_GETSCREENSAVERRUNNING = 0x0072;
        private bool _screensaverSuspended;

        public DesktopDuplicatorReader(Logic.LogicManager logic, GUI.TraySettings settings)
        {
            this._logic = logic;
            this.settings = settings;

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            RefreshCapturingState();

            _log.Info($"DesktopDuplicatorReader created.");
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _log.Debug("System suspending, pausing desktop capture.");
                _suspended = true;
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _log.Debug("System resumed, resuming desktop capture.");
                RequestReinitialize();
                _suspended = false;
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.ConsoleDisconnect:
                    _log.Debug($"Session switch ({e.Reason}), pausing desktop capture.");
                    _suspended = true;
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.RemoteConnect:
                case SessionSwitchReason.ConsoleConnect:
                    _log.Debug($"Session switch ({e.Reason}), resuming desktop capture.");
                    RequestReinitialize();
                    _suspended = false;
                    break;
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            // Resolution/monitor topology changed (e.g. a monitor was turned off,
            // unplugged, or DPI scaling changed). The current duplicator's
            // description is now stale, so force a clean recreation on the next
            // frame rather than letting the next AcquireNextFrame call fail.
            _log.Debug("Display settings changed, forcing desktop duplicator reinitialization.");
            RequestReinitialize();
        }

        // SystemEvents handlers run on a foreign thread. Disposing the D3D device
        // from there while the capture thread is mid-frame is a use-after-dispose
        // on native COM objects (access violation), so they only raise this flag
        // and the capture thread does the actual teardown at the top of its loop.
        private int _reinitRequested;

        private void RequestReinitialize()
        {
            Interlocked.Exchange(ref _reinitRequested, 1);
        }

        private void ForceReinitialize()
        {
            var duplicator = _desktopDuplicator;
            _desktopDuplicator = null;
            duplicator?.Dispose();
        }

        private void UpdateScreensaverState()
        {
            bool isRunning = false;
            try
            {
                if (!SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, ref isRunning, 0))
                    return;
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "Failed to query screensaver state.");
                return;
            }

            if (isRunning && !_screensaverSuspended)
            {
                _log.Debug("Screensaver engaged, pausing desktop capture.");
                _screensaverSuspended = true;
            }
            else if (!isRunning && _screensaverSuspended)
            {
                _log.Debug("Screensaver dismissed, reinitializing desktop capture.");
                ForceReinitialize();
                _screensaverSuspended = false;
            }
        }

        public bool IsRunning { get; private set; } = false;
        private CancellationTokenSource _cancellationTokenSource;


        private void RefreshCapturingState()
        {
            var isRunning = _cancellationTokenSource != null && IsRunning;

            if (isRunning)
            {
                //stop it!
                _log.Debug("stopping the capturing");
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource = null;
            }
            else if (!isRunning)
            {
                //start it
                _log.Debug("starting the capturing");
                _cancellationTokenSource = new CancellationTokenSource();
                var thread = new Thread(() => Run(_cancellationTokenSource.Token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Normal,
                    Name = "DesktopDuplicatorReader"
                };
                thread.Start();
            }
        }

        private DesktopDuplicator _desktopDuplicator;

        public void Run(CancellationToken token)
        {
            if (IsRunning) throw new Exception(nameof(DesktopDuplicatorReader) + " is already running!");

            IsRunning = true;
            _log.Debug("Started Desktop Duplication Reader.");
            Bitmap image = null;
            int consecutiveErrors = 0;
            const int maxConsecutiveErrors = 20; // Augmenté de 10 à 20 pour réduire les réinitialisations

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (Interlocked.Exchange(ref _reinitRequested, 0) == 1)
                        ForceReinitialize();

                    UpdateScreensaverState();

                    if (_suspended || _screensaverSuspended)
                    {
                        // Machine is suspending/locked, or the screensaver is
                        // active: don't touch DXGI at all. Cheap poll,
                        // effectively zero CPU while parked here.
                        Thread.Sleep(250);
                        continue;
                    }

                    try
                    {
                        var frameTime = Stopwatch.StartNew();
                        var newImage = GetNextFrame(image);
                        if (newImage == null)
                        {
                            //there was a timeout before there was the next frame, simply retry!
                            Thread.Sleep(100);
                            consecutiveErrors++;

                            if (consecutiveErrors > maxConsecutiveErrors)
                            {
                                _log.Warn("Too many consecutive errors. Reinitializing desktop duplicator...");
                                ForceReinitialize();
                                Thread.Sleep(1000);
                                consecutiveErrors = 0;
                            }
                            continue;
                        }

                        // Reset error counter on success
                        consecutiveErrors = 0;
                        image = newImage;

                        _logic.ProcessNewImage(newImage);

                        // Frame limiter: cap to the user-configured tickrate (min 30 FPS
                        // worth of spacing) so the idle capture thread doesn't spin faster
                        // than needed. No extra multiplier here - it previously forced huge,
                        // unpredictable sleeps (up to ~1s) whenever a single frame briefly
                        // took longer to process, causing visible stutter independent of
                        // the user's chosen FPS setting.
                        var elapsedMs = (int)frameTime.ElapsedMilliseconds;
                        int minFrameTimeInMs = Math.Max(33, 1000 / Math.Max(1, settings.Tickrate));
                        int sleepNeeded = minFrameTimeInMs - elapsedMs;

                        if (sleepNeeded > 0)
                        {
                            Thread.Sleep(sleepNeeded);
                        }
                    }
                    catch (DesktopDuplicationException ex)
                    {
                        // DXGI context is confirmed dead (access lost / device removed /
                        // invalid call, or duplicator construction failed). Recreate
                        // immediately instead of waiting for the consecutive-error
                        // threshold - retrying against a known-dead device wastes CPU
                        // and, if left long enough, is what causes the app to crash.
                        _log.Warn(ex, "DXGI capture context lost. Reinitializing immediately.");
                        ForceReinitialize();
                        Thread.Sleep(500);
                        consecutiveErrors = 0;
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Error in capture loop. Attempting to continue...");
                        Thread.Sleep(200);

                        consecutiveErrors++;
                        if (consecutiveErrors > maxConsecutiveErrors)
                        {
                            _log.Warn("Too many consecutive errors. Reinitializing resources...");
                            ForceReinitialize();

                            if (image != null)
                            {
                                image.Dispose();
                                image = null;
                            }

                            Thread.Sleep(1500);
                            consecutiveErrors = 0;
                        }
                    }
                }
            }
            finally
            {
                image?.Dispose();

                _desktopDuplicator?.Dispose();
                _desktopDuplicator = null;

                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

                _log.Debug("Stopped Desktop Duplication Reader.");
                IsRunning = false;
            }
        }

        private Bitmap GetNextFrame(Bitmap reusableBitmap)
        {
            if (_desktopDuplicator == null)
            {
                _desktopDuplicator = new DesktopDuplicator(0, settings.SelectedMonitor);
            }

            return _desktopDuplicator.GetLatestFrame(reusableBitmap);
        }
    }
}
