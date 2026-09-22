using System;

namespace Ambilight.Lights
{
    /// <summary>
    /// Owns the Yeelight / Govee engine for the life of the process: started once at launch, stopped cleanly on exit
    /// (Yeelight sessions closed, Govee devices left on their last color instead of falling back to plain white).
    /// </summary>
    public static class LightsService
    {
        static readonly object Gate = new object();
        static Engine _engine;
        static Config _config;

        public static Engine Engine { get { return _engine; } }
        public static Config Config { get { return _config; } }

        public static void Start()
        {
            lock (Gate)
            {
                if (_engine != null) return;
                try
                {
                    Log.Info("Starting the lights engine (Yeelight + Govee).");
                    _config = Config.Load();
                    _engine = new Engine(_config);
                    _engine.Start();
                }
                catch (Exception e)
                {
                    // Lights are an add-on: a failure here must never stop the keyboard/mouse effects from running.
                    Log.Error("The lights engine could not start.", e);
                    _engine = null;
                }
            }
        }

        public static void Stop()
        {
            Engine engine;
            lock (Gate) { engine = _engine; _engine = null; }
            if (engine == null) return;
            try { engine.Dispose(); }
            catch (Exception e) { Log.Error("Error while stopping the lights engine.", e); }
        }
    }
}
