using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace LightConnect.Core
{
    /// <summary>An application whose job Light Connect takes over (and that must not run at the same time).</summary>
    public sealed class ExternalApp
    {
        public string DisplayName, RunValueName, ProcessName;
    }

    /// <summary>Per-user Windows autostart (HKCU\...\Run), plus handling of the original apps' own entries.</summary>
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string OurName = "LightConnect";
        const string LegacyName = "YeelightConnect";      // name used before Govee support

        public static readonly ExternalApp YeelightOfficial = new ExternalApp
        {
            DisplayName = "Yeelight Chroma Connector", RunValueName = "Yeelight Chroma Connector", ProcessName = "Yeelight Chroma Connector"
        };
        public static readonly ExternalApp GoveeDesktop = new ExternalApp
        {
            DisplayName = "Govee Desktop", RunValueName = "GoveeDesktop", ProcessName = "GoveeDesktop"
        };

        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(OurName) != null;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) return;
                if (enabled)
                    key.SetValue(OurName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --minimized");
                else
                    key.DeleteValue(OurName, false);
            }
        }

        /// <summary>The app used to be called YeelightConnect: if it had autostart, carry that over to the new name.</summary>
        public static void MigrateLegacy()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null || key.GetValue(LegacyName) == null) return;
                    key.DeleteValue(LegacyName, false);
                }
                SetEnabled(true);
            }
            catch (Exception e) { Log.Warn("Could not migrate the old autostart entry: " + e.Message); }
        }

        public static bool IsRunning(ExternalApp app)
        {
            foreach (var p in Process.GetProcessesByName(app.ProcessName)) { p.Dispose(); return true; }
            return false;
        }

        /// <summary>Removes the app's autostart entry (returning its value so it can be restored) and closes it.</summary>
        public static string Disable(ExternalApp app)
        {
            string previous = null;
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key != null)
                {
                    previous = key.GetValue(app.RunValueName) as string;
                    key.DeleteValue(app.RunValueName, false);
                }
            }
            foreach (var p in Process.GetProcessesByName(app.ProcessName))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception e) { Log.Warn("Could not close " + app.DisplayName + ": " + e.Message); }
                p.Dispose();
            }
            return previous;
        }

        public static void RestoreRunValue(string runValueName, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                if (key != null) key.SetValue(runValueName, value);
        }
    }
}
