using System;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using Ambilight.Lights;

namespace Ambilight.GUI
{
    sealed class RelayCommand : ICommand
    {
        readonly Action _run;
        public RelayCommand(Action run) { _run = run; }
        public bool CanExecute(object parameter) { return true; }
        public void Execute(object parameter) { _run(); }
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    /// <summary>Binds one device (config + live state) to its card in the window.</summary>
    public class BulbViewModel : INotifyPropertyChanged
    {
        readonly Engine _engine;
        public BulbState State { get; private set; }

        public BulbViewModel(Engine engine, BulbState state)
        {
            _engine = engine; State = state;
            PreviewCommand = new RelayCommand(() => _engine.Identify(State));
        }

        /// <summary>Blinks the device so it can be told apart from the others.</summary>
        public ICommand PreviewCommand { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }

        /// <summary>Govee devices offer the extra "all groups" choice, Yeelight bulbs (a single color) do not.</summary>
        public System.Windows.Visibility GoveeChoices { get { return State.Kind == DeviceKind.Govee ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed; } }
        public System.Windows.Visibility YeelightChoices { get { return State.Kind == DeviceKind.Govee ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible; } }

        /// <summary>Section header the card is listed under.</summary>
        public string KindLabel { get { return State.Kind == DeviceKind.Govee ? "Govee" : "Yeelight"; } }

        public string Title
        {
            get
            {
                if (!string.IsNullOrEmpty(State.Config.Name)) return State.Config.Name;
                if (!string.IsNullOrEmpty(State.DeviceName)) return State.DeviceName;
                string tail = State.Ip != null ? " (" + State.Ip.Substring(State.Ip.LastIndexOf('.') + 1) + ")" : "";
                return KindLabel + " " + (State.Model ?? "device") + tail;
            }
        }

        public string Name
        {
            get { return State.Config.Name ?? ""; }
            set { _engine.SetName(State, value); Raise("Name"); Raise("Title"); }
        }

        public string Subtitle
        {
            get
            {
                string model = State.Model ?? "unknown model";
                return State.Ip != null ? model + "  -  " + State.Ip : model + "  -  not found yet";
            }
        }

        /// <summary>Per-light on/off switch. Turning it off keeps the chosen Chroma group, so turning it back on
        /// resumes there instead of it having to be picked again.</summary>
        public bool Enabled
        {
            get { return State.Config.Enabled; }
            set { _engine.SetEnabled(State, value); Raise("Enabled"); Refresh(); }
        }

        public string StatusText
        {
            get
            {
                if (!State.Config.Enabled) return "Off";
                switch (State.Status)
                {
                    case BulbStatus.NotControlled: return "Not controlled";
                    case BulbStatus.Paused: return "Paused (light control is off)";
                    case BulbStatus.Searching: return "Searching on the network...";
                    case BulbStatus.Connecting: return "Connecting...";
                    case BulbStatus.Connected: return "Connected";
                    default: return "Unreachable" + (string.IsNullOrEmpty(State.Error) ? "" : " (" + State.Error + ")");
                }
            }
        }

        public Brush StatusBrush
        {
            get
            {
                switch (State.Status)
                {
                    case BulbStatus.Connected: return Frozen(0x3F, 0xB9, 0x50);
                    case BulbStatus.Connecting:
                    case BulbStatus.Searching: return Frozen(0xE3, 0xA0, 0x08);
                    case BulbStatus.Unreachable: return Frozen(0xE5, 0x48, 0x4D);
                    default: return Frozen(0x8A, 0x8A, 0x8A);
                }
            }
        }

        public Brush Swatch
        {
            get
            {
                if (State.Status != BulbStatus.Connected) return Frozen(0x40, 0x40, 0x40);
                int rgb = State.CurrentRgb;
                return Frozen((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
        }

        /// <summary>0 = not controlled, 1..5 = Chroma zone.</summary>
        public int Group
        {
            get { return State.Config.Group; }
            set { if (value < 0 || value == State.Config.Group) return; _engine.SetGroup(State, value); Raise("Group"); Raise("GroupIndex"); Refresh(); }
        }

        // Config.Group values 1 and 2 ("Global" and "Group 1") always broadcast the exact same color - Razer itself
        // ties CL1 and CL2 together, so there is no point offering them as two separate combo entries. The combo
        // shows one "Group 1" entry (index 0) that maps to Group = 1; a config saved with the old Group = 2 still
        // works exactly the same (same broadcast color) and is just shown as "Group 1" too.
        static readonly int[] GroupValues = { 1, 3, 4, 5 };

        /// <summary>0-based index for the "Chroma group" combo (4 entries: "Group 1".."Group 4", no separate
        /// "Global" or "Off" - <see cref="Enabled"/> is the on/off toggle now).</summary>
        public int GroupIndex
        {
            get
            {
                int g = State.Config.Group == 2 ? 1 : State.Config.Group;
                int idx = Array.IndexOf(GroupValues, g);
                return idx >= 0 ? idx : (State.Kind == DeviceKind.Govee && g == 6 ? GroupValues.Length : 0);
            }
            set
            {
                int idx = Math.Max(0, value);
                Group = idx < GroupValues.Length ? GroupValues[idx] : 6;      // past the end = Govee's gradient entry
            }
        }

        public double Brightness
        {
            get { return State.Config.Brightness; }
            set { _engine.SetBrightness(State, (int)Math.Round(value)); Raise("Brightness"); Raise("BrightnessText"); }
        }

        public string BrightnessText { get { return State.Config.Brightness + "%"; } }

        /// <summary>When on, this device ignores the Chroma group entirely and is fed straight from a rectangle
        /// the user positions on the lights canvas - no 4-zone limit, no Razer dependency, any position/size.</summary>
        public bool UseScreenPosition
        {
            get { return State.Config.UseScreenPosition; }
            set { _engine.SetUseScreenPosition(State, value); Raise("UseScreenPosition"); Raise("GroupEnabled"); Refresh(); }
        }

        /// <summary>Whether the Chroma group picker should be enabled (on, and not using the canvas instead).</summary>
        public bool GroupEnabled { get { return State.Config.Enabled && !State.Config.UseScreenPosition; } }

        public void Refresh()
        {
            Raise("Title"); Raise("Subtitle"); Raise("StatusText"); Raise("StatusBrush"); Raise("Swatch");
            Raise("Brightness"); Raise("BrightnessText");     // a new Govee device starts from its own brightness
            Raise("Enabled"); Raise("UseScreenPosition"); Raise("GroupEnabled");
        }

        static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
