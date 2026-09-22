using System;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using LightConnect.Core;

namespace LightConnect.Ui
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

        public string StatusText
        {
            get
            {
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
            set { if (value < 0 || value == State.Config.Group) return; _engine.SetGroup(State, value); Raise("Group"); Refresh(); }
        }

        public double Brightness
        {
            get { return State.Config.Brightness; }
            set { _engine.SetBrightness(State, (int)Math.Round(value)); Raise("Brightness"); Raise("BrightnessText"); }
        }

        public string BrightnessText { get { return State.Config.Brightness + "%"; } }

        public void Refresh()
        {
            Raise("Title"); Raise("Subtitle"); Raise("StatusText"); Raise("StatusBrush"); Raise("Swatch");
            Raise("Brightness"); Raise("BrightnessText");     // a new Govee device starts from its own brightness
        }

        static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
