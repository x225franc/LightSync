#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Ambilight.Lights
{
    /// <summary>How many independently colorable segments a Govee device has (needed to spread the Chroma zones over it).</summary>
    static class GoveeCatalog
    {
        const int DefaultSegments = 10;

        // Segment counts of the models seen so far; anything unknown falls back to 10 (most Govee strips and panels).
        static readonly Dictionary<string, int> BySku = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "H6056", 6 },     // RGBIC TV light bars
            { "H6061", 10 },    // Glide Hexa
            { "H61A0", 10 },    // 3 m RGBIC neon rope light
        };

        static Dictionary<string, int> _byMac;

        /// <summary>
        /// Govee Desktop keeps a plain-text cache of the devices it knows, including their segment counts. It is
        /// only read (never written) and only used when present, so Govee Desktop does not have to be installed.
        /// </summary>
        static Dictionary<string, int> LoadCache()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"GoveeDesktop\config\device_info.ini");
                if (!File.Exists(path)) return map;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (!line.StartsWith("Device=", StringComparison.Ordinal)) continue;
                    foreach (JObject d in JArray.Parse(line.Substring(7)))
                    {
                        string id = (string)d["DeviceId"];
                        int n = (int?)d["SegmentNums"] ?? 0;
                        if (id != null && n > 0) map[id] = n;
                    }
                }
            }
            catch (Exception e) { Log.Warn("Could not read Govee Desktop's device cache: " + e.Message); }
            return map;
        }

        public static int SegmentCount(string mac, string sku)
        {
            if (_byMac == null) _byMac = LoadCache();
            int n;
            if (mac != null && _byMac.TryGetValue(mac, out n)) return Math.Min(n, 255);
            if (sku != null && BySku.TryGetValue(sku, out n)) return n;
            return DefaultSegments;
        }
    }
}
