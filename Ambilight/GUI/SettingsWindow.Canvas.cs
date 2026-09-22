using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ambilight.Lights;
using Wpf.Ui.Controls;
using LightsConfig = Ambilight.Lights.Config;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Ambilight.GUI
{
    /// <summary>One device that can be placed on the shared screen canvas - a Yeelight/Govee light or a Razer
    /// device - kept separate from the "Lights" page code so that page stays about lights, not the canvas.</summary>
    interface ICanvasItem
    {
        string Title { get; }
        Brush Color { get; }
        /// <summary>Grid of small cells the box is drawn as, matching how many LEDs the device really has
        /// (1x1 for a single-color bulb, WxH for a keyboard, 1xN for an LED strip...).</summary>
        int CellColumns { get; }
        int CellRows { get; }
        bool UseScreenPosition { get; }
        double PosX { get; }
        double PosY { get; }
        double PosW { get; }
        double PosH { get; }
        void Commit(double x, double y, double w, double h);
    }

    sealed class BulbCanvasItem : ICanvasItem
    {
        readonly Engine _engine;
        readonly BulbViewModel _vm;
        public BulbCanvasItem(Engine engine, BulbViewModel vm) { _engine = engine; _vm = vm; }

        public string Title { get { return _vm.Title; } }
        public Brush Color { get { return _vm.Swatch; } }
        public int CellColumns { get { return Math.Max(1, GoveeSegments()); } }
        public int CellRows { get { return 1; } }
        public bool UseScreenPosition { get { return _vm.UseScreenPosition; } }
        public double PosX { get { return _vm.State.Config.PosX; } }
        public double PosY { get { return _vm.State.Config.PosY; } }
        public double PosW { get { return _vm.State.Config.PosW; } }
        public double PosH { get { return _vm.State.Config.PosH; } }
        public void Commit(double x, double y, double w, double h) { _engine.SetScreenPosition(_vm.State, x, y, w, h); }

        int GoveeSegments()
        {
            return _vm.State.Kind == DeviceKind.Govee ? GoveeCatalog.SegmentCount(_vm.State.Config.Id, _vm.State.Model) : 1;
        }
    }

    /// <summary>A Razer device (keyboard, mouse, mousepad, headset, keypad) positioned on the canvas. There is no
    /// live color for these in the settings window (their Logic classes run in a different process context), so
    /// the box is shown in a fixed, neutral accent color - a schematic placement aid, not a live preview.</summary>
    sealed class RazerCanvasItem : ICanvasItem
    {
        readonly LightsConfig _config;
        readonly string _device;
        public RazerCanvasItem(LightsConfig config, string device, string title, int cols, int rows)
        {
            _config = config; _device = device; Title = title; CellColumns = cols; CellRows = rows;
        }

        public string Title { get; private set; }
        public Brush Color { get { return RazerAccent; } }
        public int CellColumns { get; private set; }
        public int CellRows { get; private set; }
        DeviceCanvasPosition Pos { get { return _config.GetRazerCanvas(_device); } }
        public bool UseScreenPosition { get { return Pos.UseScreenPosition; } }
        public double PosX { get { return Pos.PosX; } }
        public double PosY { get { return Pos.PosY; } }
        public double PosW { get { return Pos.PosW; } }
        public double PosH { get { return Pos.PosH; } }

        public void Commit(double x, double y, double w, double h)
        {
            var pos = Pos;
            pos.PosX = Math.Max(0, Math.Min(1 - w, x));
            pos.PosY = Math.Max(0, Math.Min(1 - h, y));
            pos.PosW = Math.Max(0.02, Math.Min(1, w));
            pos.PosH = Math.Max(0.02, Math.Min(1, h));
            _config.Save();
        }

        static readonly SolidColorBrush RazerAccent = Frozen(0x3B, 0x82, 0xC4);
        static SolidColorBrush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); br.Freeze(); return br; }
    }

    public partial class SettingsWindow
    {
        // A fixed-size preview (matches the 16:9 the sampling itself assumes) with one draggable, resizable box per
        // device placed on it. Boxes are created once and then only moved by the user's own drag - SyncCanvas() must
        // never reset an existing box's position/size from the saved config, or a drag in progress (or one that just
        // committed) would visibly snap back.
        private readonly Dictionary<string, FrameworkElement> _canvasBoxes = new Dictionary<string, FrameworkElement>();
        private readonly Dictionary<string, ICanvasItem> _canvasItems = new Dictionary<string, ICanvasItem>();
        private const double CanvasW = 480, CanvasH = 270;
        private const double LabelHeight = 16;

        // Cols/Rows here are the defaults; the keyboard's are read live from its own size setting instead (below),
        // since that one is user-configurable.
        private static readonly (string Device, string Title, int Cols, int Rows)[] RazerDevices =
        {
            ("Keyboard", "Keyboard", Colore.Effects.Keyboard.KeyboardConstants.MaxColumns, Colore.Effects.Keyboard.KeyboardConstants.MaxRows),
            ("Mouse", "Mouse", Colore.Effects.Mouse.MouseConstants.MaxColumns, Colore.Effects.Mouse.MouseConstants.MaxRows),
            ("Mousepad", "Mousepad", Colore.Effects.Mousepad.MousepadConstants.MaxLeds, 1),
            ("Headset", "Headset", 2, 1),
            ("Keypad", "Keypad", Colore.Effects.Keypad.KeypadConstants.MaxColumns, Colore.Effects.Keypad.KeypadConstants.MaxRows),
        };

        private void InitCanvas()
        {
            Loaded += (s, e) => LoadCanvasValues();
        }

        /// <summary>The canvas keeps its 480x270 logical coordinate space (all the position/size math above is in
        /// that space) but is visually scaled to fill the available width, via a LayoutTransform - so it grows and
        /// shrinks with the window instead of always taking the same fixed pixels.</summary>
        private void CanvasScaleHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (CanvasScaleHost.ActualWidth <= 0) return;
            double scale = Math.Max(0.5, Math.Min(2.0, CanvasScaleHost.ActualWidth / CanvasW));
            CanvasBorder.LayoutTransform = new ScaleTransform(scale, scale);
        }

        private void LoadCanvasValues()
        {
            var cfg = LightsService.Config;
            if (cfg == null) return;

            _isLoading = true;
            CanvasKeyboardToggle.IsChecked = cfg.GetRazerCanvas("Keyboard").UseScreenPosition;
            CanvasMouseToggle.IsChecked = cfg.GetRazerCanvas("Mouse").UseScreenPosition;
            CanvasMousepadToggle.IsChecked = cfg.GetRazerCanvas("Mousepad").UseScreenPosition;
            CanvasHeadsetToggle.IsChecked = cfg.GetRazerCanvas("Headset").UseScreenPosition;
            CanvasKeypadToggle.IsChecked = cfg.GetRazerCanvas("Keypad").UseScreenPosition;
            _isLoading = false;
        }

        private void CanvasDeviceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var cfg = LightsService.Config;
            if (cfg == null) return;

            var toggle = (ToggleSwitch)sender;
            string device = toggle == CanvasKeyboardToggle ? "Keyboard"
                : toggle == CanvasMouseToggle ? "Mouse"
                : toggle == CanvasMousepadToggle ? "Mousepad"
                : toggle == CanvasHeadsetToggle ? "Headset"
                : "Keypad";

            cfg.GetRazerCanvas(device).UseScreenPosition = toggle.IsChecked == true;
            cfg.Save();
            SyncCanvas();
        }

        // ---- shared canvas rendering ----

        private void SyncCanvas()
        {
            if (PositionCanvas == null) return;

            var wanted = new HashSet<string>();
            var cfg = LightsService.Config;

            if (_engine != null)
                foreach (var vm in _bulbs)
                {
                    if (!vm.UseScreenPosition) continue;
                    string key = "bulb:" + vm.State.Config.Id;
                    wanted.Add(key);
                    SyncBox(key, new BulbCanvasItem(_engine, vm));
                }

            if (cfg != null)
                foreach (var d in RazerDevices)
                {
                    var pos = cfg.GetRazerCanvas(d.Device);
                    if (!pos.UseScreenPosition) continue;
                    string key = "razer:" + d.Device;
                    wanted.Add(key);
                    // The keyboard's own grid can be resized by the user (Settings page), the others are fixed.
                    int cols = d.Device == "Keyboard" ? _settings.KeyboardWidth : d.Cols;
                    int rows = d.Device == "Keyboard" ? _settings.KeyboardHeight : d.Rows;
                    SyncBox(key, new RazerCanvasItem(cfg, d.Device, d.Title, cols, rows));
                }

            foreach (var key in new List<string>(_canvasBoxes.Keys))
            {
                if (wanted.Contains(key)) continue;
                PositionCanvas.Children.Remove(_canvasBoxes[key]);
                _canvasBoxes.Remove(key);
                _canvasItems.Remove(key);
            }

            CanvasHintText.Visibility = _canvasBoxes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (CanvasHintText.Visibility == Visibility.Visible)
                CanvasHintText.Text = "Nothing is on the canvas yet - turn on 'Screen canvas' on a light, or on a Razer device below.";
        }

        private void SyncBox(string key, ICanvasItem item)
        {
            _canvasItems[key] = item;

            FrameworkElement stack;
            if (!_canvasBoxes.TryGetValue(key, out stack))
            {
                stack = CreateCanvasBox(key);
                _canvasBoxes[key] = stack;
                PositionCanvas.Children.Add(stack);
                ApplyStoredPosition(key, item);
            }

            var cellsHost = (Border)((StackPanel)stack).Children[0];
            var label = (TextBlock)((StackPanel)stack).Children[1];
            label.Text = item.Title;
            cellsHost.Background = item.Color;

            RebuildCells(cellsHost, item.CellColumns, item.CellRows);
        }

        private FrameworkElement CreateCanvasBox(string key)
        {
            var cellsHost = new Border
            {
                BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
                ClipToBounds = true
            };

            var moveThumb = new Thumb { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            moveThumb.DragDelta += (s, e) => MoveCanvasBox(key, e.HorizontalChange, e.VerticalChange);
            moveThumb.DragCompleted += (s, e) => CommitCanvasPosition(key);

            var resizeThumb = new Thumb
            {
                Width = 12, Height = 12, Background = Brushes.White, Opacity = 0.8,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE
            };
            resizeThumb.DragDelta += (s, e) => ResizeCanvasBox(key, e.HorizontalChange, e.VerticalChange);
            resizeThumb.DragCompleted += (s, e) => CommitCanvasPosition(key);

            var overlay = new Grid();
            overlay.Children.Add(moveThumb);
            overlay.Children.Add(resizeThumb);
            cellsHost.Child = overlay;

            // A permanent caption under the box (not a hover tooltip), so it is clear at a glance which device each
            // box on the canvas is, even with several of them side by side.
            var label = new TextBlock
            {
                FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Height = LabelHeight
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(cellsHost);
            stack.Children.Add(label);
            return stack;
        }

        /// <summary>The cell grid is rebuilt (not just recolored) whenever the LED count changes - e.g. a Govee
        /// device's real segment count becomes known once it answers the network, or the keyboard grid size changes.</summary>
        private void RebuildCells(Border cellsHost, int cols, int rows)
        {
            var overlay = (Grid)cellsHost.Child;
            var existing = overlay.Children.Count > 0 ? overlay.Children[0] as UniformGrid : null;
            if (existing != null && existing.Columns == cols && existing.Rows == rows) return;

            if (existing != null) overlay.Children.RemoveAt(0);

            var cells = new UniformGrid { Rows = rows, Columns = cols, IsHitTestVisible = false };
            var cellBorder = new Thickness(0.5);
            var cellStroke = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            for (int i = 0; i < rows * cols; i++)
                cells.Children.Add(new Border { BorderBrush = cellStroke, BorderThickness = cellBorder });
            overlay.Children.Insert(0, cells);
        }

        private void MoveCanvasBox(string key, double dx, double dy)
        {
            var stack = _canvasBoxes[key];
            var cellsHost = (Border)((StackPanel)stack).Children[0];
            double left = Math.Max(0, Math.Min(CanvasW - cellsHost.Width, Canvas.GetLeft(stack) + dx));
            double top = Math.Max(0, Math.Min(CanvasH - cellsHost.Height - LabelHeight, Canvas.GetTop(stack) + dy));
            Canvas.SetLeft(stack, left);
            Canvas.SetTop(stack, top);
        }

        private void ResizeCanvasBox(string key, double dx, double dy)
        {
            var stack = _canvasBoxes[key];
            var cellsHost = (Border)((StackPanel)stack).Children[0];
            double w = Math.Max(20, Math.Min(CanvasW - Canvas.GetLeft(stack), cellsHost.Width + dx));
            double h = Math.Max(16, Math.Min(CanvasH - LabelHeight - Canvas.GetTop(stack), cellsHost.Height + dy));
            cellsHost.Width = w;
            cellsHost.Height = h;
            ((StackPanel)stack).Width = w;
        }

        private void CommitCanvasPosition(string key)
        {
            var stack = _canvasBoxes[key];
            var cellsHost = (Border)((StackPanel)stack).Children[0];
            ICanvasItem item;
            if (_canvasItems.TryGetValue(key, out item))
                item.Commit(Canvas.GetLeft(stack) / CanvasW, Canvas.GetTop(stack) / CanvasH, cellsHost.Width / CanvasW, cellsHost.Height / CanvasH);
        }

        private void ApplyStoredPosition(string key, ICanvasItem item)
        {
            var stack = _canvasBoxes[key];
            var cellsHost = (Border)((StackPanel)stack).Children[0];
            double w = Math.Max(20, item.PosW * CanvasW), h = Math.Max(16, item.PosH * CanvasH);
            cellsHost.Width = w;
            cellsHost.Height = h;
            ((StackPanel)stack).Width = w;
            Canvas.SetLeft(stack, item.PosX * CanvasW);
            Canvas.SetTop(stack, item.PosY * CanvasH);
        }
    }
}
