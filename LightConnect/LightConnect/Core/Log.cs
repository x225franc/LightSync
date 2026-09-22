using System;
using System.IO;

namespace LightConnect.Core
{
    /// <summary>Tiny thread-safe file logger (%LOCALAPPDATA%\LightConnect\logs).</summary>
    static class Log
    {
        static readonly object Gate = new object();
        static readonly string Dir = Path.Combine(Config.DataDir, "logs");
        static readonly string PathMain = Path.Combine(Dir, "lightconnect.log");

        public static void Info(string message) { Write("INFO ", message); }
        public static void Warn(string message) { Write("WARN ", message); }
        public static void Error(string message, Exception e = null) { Write("ERROR", e == null ? message : message + " : " + e); }

        static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    var info = new FileInfo(PathMain);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        string old = PathMain + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(PathMain, old);
                    }
                    File.AppendAllText(PathMain, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message + Environment.NewLine);
                }
            }
            catch { /* logging must never take the app down */ }
        }
    }
}
