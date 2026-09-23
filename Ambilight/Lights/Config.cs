using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Ambilight.Lights
{
    public static class Kinds
    {
        public const string Yeelight = "yeelight";
        public const string Govee = "govee";
    }

    /// <summary>What we remember about one device (matched by its id: Yeelight device id or Govee MAC).</summary>
    public class BulbConfig
    {
        public string Id { get; set; }
        /// <summary>"yeelight" or "govee". Missing in configs written before Govee support, which were all Yeelight.</summary>
        public string Kind { get; set; } = Kinds.Yeelight;
        /// <summary>Optional friendly name typed by the user.</summary>
        public string Name { get; set; }
        /// <summary>Chroma color (1..5 = CL1..CL5 of the Chroma Broadcast effect); 0 = not controlled.
        /// 1 is "Global" and 2..5 are "Group 1..4". Measured fact: Razer's Chroma Link -> Chroma Connect Broadcast
        /// path only relays 4 independent colors, not 5 - CL1 and CL2 ("Global" and "Group 1") are always the exact
        /// same color, forced together by Razer itself; Group 2/3/4 (CL3/CL4/CL5) are the three that are genuinely
        /// independent, plus CL1/CL2. LinkLogic fills these four with the four equal screen columns, left to right.
        /// 6 = Govee only: groups 1-4 spread as a gradient over the device's segments.</summary>
        public int Group { get; set; }
        /// <summary>Brightness in percent, sent to the device itself (1..100).</summary>
        public int Brightness { get; set; } = 100;
        /// <summary>Per-light on/off switch, independent of Group (which is kept while this is off, so turning the
        /// light back on resumes on the same Chroma group instead of asking it to be picked again).</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Whether this device is actually driven right now: switched on, and assigned to a group or a screen position.</summary>
        [JsonIgnore] public bool Controlled { get { return Enabled && (Group > 0 || UseScreenPosition); } }

        /// <summary>When on, this device ignores Group/Chroma Connect entirely and is fed straight from a rectangle of the
        /// screen the user placed on the lights canvas (Pos*, fractions of the screen 0-1) - no 4-zone limit, no Razer
        /// dependency, any position/size. Group is kept meanwhile, so switching back off resumes on the same group.</summary>
        public bool UseScreenPosition { get; set; }
        public double PosX { get; set; } = 0.4;
        public double PosY { get; set; } = 0.4;
        public double PosW { get; set; } = 0.17;
        public double PosH { get; set; } = 0.17;
    }

    /// <summary>Position of a Razer device (keyboard, mouse, mousepad, headset, keypad) on the shared canvas,
    /// keyed by device name in <see cref="Config.RazerCanvas"/>. Mirrors BulbConfig's Pos* fields.</summary>
    public class DeviceCanvasPosition
    {
        /// <summary>When on, this device is fed from this rectangle of the screen instead of the whole screen.</summary>
        public bool UseScreenPosition { get; set; }
        public double PosX { get; set; } = 0.1;
        public double PosY { get; set; } = 0.1;
        public double PosW { get; set; } = 0.255;
        public double PosH { get; set; } = 0.255;

        /// <summary>0 = Default (whole screen, or the canvas rectangle above when <see cref="UseScreenPosition"/> is
        /// on); 1-4 = the device is painted a single solid color instead, taken from that Chroma group - the same
        /// four zones a Yeelight/Govee bulb can be assigned to (see BulbConfig.Group). Wins over
        /// <see cref="UseScreenPosition"/> when set, exactly like a bulb's Group wins over its own canvas toggle.</summary>
        public int RazerGroup { get; set; }
    }

    public class Config
    {
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightSync");
        // The standalone "Light Connect" app (and, before it, "Yeelight Connect") kept the same settings elsewhere:
        // they are picked up once, so devices, names and groups carry over.
        static readonly string[] LegacyDataDirs =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightConnect"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YeelightConnect")
        };
        static readonly string FilePath = Path.Combine(DataDir, "config.json");
        static readonly object Gate = new object();

        public List<BulbConfig> Bulbs { get; set; } = new List<BulbConfig>();
        /// <summary>Razer devices (keyboard, mouse, mousepad, headset, keypad) positioned on the shared canvas,
        /// keyed by a fixed device name ("Keyboard", "Mouse", "Mousepad", "Headset", "Keypad").</summary>
        public Dictionary<string, DeviceCanvasPosition> RazerCanvas { get; set; } = new Dictionary<string, DeviceCanvasPosition>();

        /// <summary>Gets (creating if needed) the canvas position of a Razer device.</summary>
        public DeviceCanvasPosition GetRazerCanvas(string device)
        {
            DeviceCanvasPosition pos;
            if (RazerCanvas.TryGetValue(device, out pos)) return pos;
            pos = new DeviceCanvasPosition();
            RazerCanvas[device] = pos;
            return pos;
        }
        /// <summary>Autostart entries of the original apps that we removed (Run value name -> command), so they can be restored.</summary>
        public Dictionary<string, string> SavedRunValues { get; set; } = new Dictionary<string, string>();
        /// <summary>Master switch: when off, no device is controlled (they keep their last color).</summary>
        public bool ControlEnabled { get; set; } = true;
        /// <summary>Maximum color updates per second sent to each Govee device (Chroma itself only produces ~20/s).</summary>
        public int GoveeFps { get; set; } = 30;
        /// <summary>Maximum color updates per second sent to each Yeelight bulb (music mode has no limit of its own).</summary>
        public int YeelightFps { get; set; } = 8;
        /// <summary>Yeelight bulbs switch off when the scene is darker than this many percent (0 = never switch off).</summary>
        public int YeelightOffBelow { get; set; } = 6;
        /// <summary>How long a Yeelight bulb takes to fade out before switching off in the dark (0 = switch off at once).</summary>
        public int YeelightFadeMs { get; set; } = 400;
        /// <summary>Blend between two Chroma frames (which arrive ~20 times a second) so colors move smoothly at the update rate.</summary>
        public bool Interpolate { get; set; }

        // Written by the first builds; folded into SavedRunValues on load, never written back.
        public string OfficialRunValue { get; set; }
        public bool ShouldSerializeOfficialRunValue() { return false; }

        [JsonIgnore] public bool IsNew { get; private set; }

        public static Config Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    foreach (string dir in LegacyDataDirs)
                    {
                        string legacy = Path.Combine(dir, "config.json");
                        if (!File.Exists(legacy)) continue;
                        Directory.CreateDirectory(DataDir);
                        File.Copy(legacy, FilePath);
                        Log.Info("Imported the lights configuration from " + dir);
                        break;
                    }
                }

                if (File.Exists(FilePath))
                {
                    var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(FilePath));
                    if (cfg != null)
                    {
                        if (cfg.Bulbs == null) cfg.Bulbs = new List<BulbConfig>();
                        if (cfg.SavedRunValues == null) cfg.SavedRunValues = new Dictionary<string, string>();
                        if (cfg.RazerCanvas == null) cfg.RazerCanvas = new Dictionary<string, DeviceCanvasPosition>();
                        foreach (var b in cfg.Bulbs)
                        {
                            if (string.IsNullOrEmpty(b.Kind)) b.Kind = Kinds.Yeelight;
                            // Configs written before the on/off toggle used Group = 0 for "off". The combo box no
                            // longer has that entry, so give it a real group to show and keep it off with the toggle.
                            if (b.Group == 0) { b.Group = 1; b.Enabled = false; }
                            // The canvas box's default size shrank (0.2 -> 0.17, ~15%); a box still at the exact old
                            // default was never resized by hand, so it shrinks along with the new default too.
                            if (b.PosW == 0.2 && b.PosH == 0.2) { b.PosW = 0.17; b.PosH = 0.17; }
                        }
                        foreach (var pos in cfg.RazerCanvas.Values)
                            if (pos.PosW == 0.3 && pos.PosH == 0.3) { pos.PosW = 0.255; pos.PosH = 0.255; }
                        if (!string.IsNullOrEmpty(cfg.OfficialRunValue))
                        {
                            cfg.SavedRunValues["Yeelight Chroma Connector"] = cfg.OfficialRunValue;
                            cfg.OfficialRunValue = null;
                        }
                        return cfg;
                    }
                }
            }
            catch (Exception e) { Log.Error("Cannot read config.json, starting fresh.", e); }

            var fresh = new Config { IsNew = true };
            fresh.ImportOfficialConnector();
            fresh.Save();
            return fresh;
        }

        public void Save()
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DataDir);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    File.Move(tmp, FilePath);
                }
            }
            catch (Exception e) { Log.Error("Cannot save config.json.", e); }
        }

        public BulbConfig Find(string id)
        {
            lock (Bulbs)
            {
                foreach (var b in Bulbs)
                    if (string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) return b;
            }
            return null;
        }

        /// <summary>
        /// First run: reuse the bulbs and zones of the official Yeelight Chroma Connector
        /// (%LOCALAPPDATA%\YeelightChromaConnector\light_cfg.ini), so nothing has to be reconfigured.
        /// </summary>
        void ImportOfficialConnector()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"YeelightChromaConnector\light_cfg.ini");
                if (!File.Exists(path)) return;

                string did = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("did=", StringComparison.OrdinalIgnoreCase))
                        did = line.Substring(4);
                    else if (line.StartsWith("select_group=", StringComparison.OrdinalIgnoreCase) && did != null)
                    {
                        int group;
                        if (int.TryParse(line.Substring(13), out group) && group >= 1 && group <= 5 && Find(did) == null)
                            Bulbs.Add(new BulbConfig { Id = did, Kind = Kinds.Yeelight, Group = group, Brightness = 100 });
                    }
                }
                Log.Info("Imported " + Bulbs.Count + " bulb(s) from the official connector's light_cfg.ini.");
            }
            catch (Exception e) { Log.Error("Could not import the official connector's configuration.", e); }
        }
    }
}
