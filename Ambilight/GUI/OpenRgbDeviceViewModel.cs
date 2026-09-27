using System;
using System.ComponentModel;
using Ambilight.Lights;

namespace Ambilight.GUI
{
    /// <summary>Binds one OpenRGB-discovered device to its card in the window.</summary>
    public class OpenRgbDeviceViewModel : INotifyPropertyChanged
    {
        readonly Config _config;
        public string Name { get; private set; }
        public int LedCount { get; private set; }

        public OpenRgbDeviceViewModel(Config config, string name, int ledCount)
        {
            _config = config; Name = name; LedCount = ledCount;
        }

        OpenRgbDeviceConfig Cfg { get { return _config.GetOpenRgbDevice(Name); } }

        public string Subtitle { get { return LedCount + (LedCount == 1 ? " LED" : " LEDs"); } }

        public bool Enabled
        {
            get { return Cfg.Enabled; }
            set { Cfg.Enabled = value; _config.Save(); Raise("Enabled"); }
        }

        // Same "Group 1"/"Group 2" merge as BulbViewModel: Config.Group values 1 and 2 broadcast the exact
        // same color, so there is no separate combo entry for them.
        static readonly int[] GroupValues = { 1, 3, 4, 5 };

        /// <summary>0 = "Not controlled", 1..4 = Chroma group (index into <see cref="GroupValues"/> + 1).</summary>
        public int GroupIndex
        {
            get
            {
                int g = Cfg.Group == 2 ? 1 : Cfg.Group;
                if (g <= 0) return 0;
                int idx = Array.IndexOf(GroupValues, g);
                return idx >= 0 ? idx + 1 : 0;
            }
            set
            {
                int idx = value - 1;
                Cfg.Group = idx >= 0 && idx < GroupValues.Length ? GroupValues[idx] : 0;
                _config.Save();
                Raise("GroupIndex");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }
    }
}
