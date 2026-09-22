using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Ambilight.Lights
{
    /// <summary>An application whose job this app takes over (and that must not run at the same time).</summary>
    public sealed class ExternalApp
    {
        public string DisplayName, RunValueName, ProcessName;
    }

    /// <summary>Closing the original Yeelight / Govee apps and turning off their per-user autostart (and restoring it).</summary>
    static class ExternalApps
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static readonly ExternalApp YeelightOfficial = new ExternalApp
        {
            DisplayName = "Yeelight Chroma Connector", RunValueName = "Yeelight Chroma Connector", ProcessName = "Yeelight Chroma Connector"
        };
        public static readonly ExternalApp GoveeDesktop = new ExternalApp
        {
            DisplayName = "Govee Desktop", RunValueName = "GoveeDesktop", ProcessName = "GoveeDesktop"
        };
        /// <summary>The standalone app this one replaces: two apps driving the same lights would fight over them.</summary>
        public static readonly ExternalApp LightConnect = new ExternalApp
        {
            DisplayName = "Light Connect", RunValueName = "LightConnect", ProcessName = "LightConnect"
        };

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
