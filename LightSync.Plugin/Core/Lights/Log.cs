#nullable disable
using System;
using NLog;

namespace Ambilight.Lights
{
    /// <summary>The lights engine logs through Ambilight's NLog target, so everything lands in one log file.</summary>
    static class Log
    {
        static readonly Logger L = LogManager.GetLogger("Lights");

        public static void Info(string message) { L.Info(message); }
        public static void Warn(string message) { L.Warn(message); }
        public static void Error(string message, Exception e = null) { if (e == null) L.Error(message); else L.Error(e, message); }
    }
}
