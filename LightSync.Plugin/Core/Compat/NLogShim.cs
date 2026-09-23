#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using LenovoLegionToolkit.Lib.Station.Logging;

namespace NLog
{
    /// <summary>
    /// Drop-in replacement for the slice of the NLog API the ported LightSync engine code uses (GetCurrentClassLogger,
    /// Info/Warn/Error, the (Exception, string) overloads), so every ported file keeps calling the exact same NLog
    /// API it always has - zero per-call-site edits - while everything actually lands in the host's own
    /// <see cref="IExtensionLogger"/> instead of a real NLog target.
    /// </summary>
    public sealed class Logger
    {
        private readonly string _name;
        internal Logger(string name) => _name = name;

        public void Debug(string message) => LogManager.Write("DEBUG", _name, message, null);
        public void Info(string message) => LogManager.Write("INFO", _name, message, null);
        public void Warn(string message) => LogManager.Write("WARN", _name, message, null);
        public void Warn(Exception ex, string message) => LogManager.Write("WARN", _name, message, ex);
        public void Error(string message) => LogManager.Write("ERROR", _name, message, null);
        public void Error(Exception ex, string message) => LogManager.Write("ERROR", _name, message, ex);
    }

    public static class LogManager
    {
        /// <summary>Set once from LightSyncProvider.Initialize. Null (e.g. before that) just drops the log line.</summary>
        public static IExtensionLogger? Sink;

        public static Logger GetCurrentClassLogger([CallerFilePath] string? file = null) =>
            new(file is null ? "LightSync" : Path.GetFileNameWithoutExtension(file));

        public static Logger GetLogger(string name) => new(name);

        internal static void Write(string level, string logger, string message, Exception? ex)
        {
            var sink = Sink;
            if (sink is null)
                return;

            if (ex is null)
                sink.Trace($"[{level}] [{logger}] {message}");
            else
                sink.Error($"[{level}] [{logger}] {message}", ex);
        }
    }
}
