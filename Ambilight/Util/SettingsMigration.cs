using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;

namespace Ambilight.Util
{
    /// <summary>
    /// User settings are stored under the exe's name, so after the rename to LightSync they would start from scratch.
    /// This copies the newest previous user.config (from the old location, or from the old package's private copy, since a
    /// packaged app has its %LOCALAPPDATA% writes redirected there) to the new place, once, before anything reads a setting.
    /// </summary>
    internal static class SettingsMigration
    {
        public static void Run()
        {
            try
            {
                string target = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
                if (File.Exists(target))
                    return;

                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var candidates = new List<FileInfo>();
                Collect(Path.Combine(local, "Ambilight"), candidates);

                string packages = Path.Combine(local, "Packages");
                if (Directory.Exists(packages))
                {
                    foreach (string package in Directory.GetDirectories(packages, "RazerAmbilight.Lighting_*"))
                        Collect(Path.Combine(package, "LocalCache", "Local", "Ambilight"), candidates);
                }

                var newest = candidates.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest == null)
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(newest.FullName, target);
            }
            catch (Exception)
            {
                // Migration is a convenience: never let it stop the app from starting.
            }
        }

        private static void Collect(string root, List<FileInfo> into)
        {
            if (!Directory.Exists(root))
                return;

            foreach (string file in Directory.GetFiles(root, "user.config", SearchOption.AllDirectories))
                into.Add(new FileInfo(file));
        }
    }
}
