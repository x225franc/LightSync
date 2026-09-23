#nullable disable
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Ambilight.Lights
{
    /// <summary>
    /// A Yeelight bulb in "music mode" connects back to this PC, so Windows Firewall needs an inbound
    /// allow rule for this exe. Windows normally asks on first use, but that prompt can be missed or
    /// suppressed; this checks for the rule and can create it (one administrator prompt).
    /// </summary>
    static class Firewall
    {
        const string RuleName = "LightSync";

        static string ExePath { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        /// <summary>
        /// True when an enabled inbound allow rule exists for this exe and nothing blocks it (a block rule always
        /// wins over an allow rule, and Windows creates one on its own when its first-use prompt is dismissed).
        /// Also true when the check itself is impossible, so we never nag for nothing.
        /// </summary>
        public static bool HasAllowRule()
        {
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return true;
                dynamic policy = Activator.CreateInstance(t);
                string exe = ExePath;
                bool allowed = false, blocked = false;
                foreach (dynamic rule in policy.Rules)
                {
                    if (!(bool)rule.Enabled || (int)rule.Direction != 1) continue;      // 1 = inbound
                    string app = rule.ApplicationName as string;
                    if (app == null || !string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((int)rule.Action == 1) allowed = true;                          // 1 = allow
                    else blocked = true;
                }
                return allowed && !blocked;
            }
            catch (Exception e)
            {
                Log.Warn("Could not read the firewall rules: " + e.Message);
                return true;
            }
        }

        /// <summary>Creates the rule through an elevated netsh (Windows shows a UAC prompt). Returns false if declined or failed.</summary>
        public static bool AddAllowRule()
        {
            try
            {
                // Remove every existing inbound rule for this exe (including the block rules Windows adds by itself),
                // then add a single allow rule - all in one elevated command, so there is one prompt.
                string exe = ExePath;
                string delete = "netsh advfirewall firewall delete rule name=all dir=in program=\"" + exe + "\"";
                string add = "netsh advfirewall firewall add rule name=\"" + RuleName + "\" dir=in action=allow enable=yes profile=any program=\"" + exe + "\"";
                var psi = new ProcessStartInfo("cmd.exe", "/c \"" + delete + " & " + add + "\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    Log.Info("Firewall rule creation finished with code " + p.ExitCode);
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception) { Log.Info("The administrator prompt was declined."); return false; }
            catch (Exception e) { Log.Warn("Could not create the firewall rule: " + e.Message); return false; }
        }
    }
}
