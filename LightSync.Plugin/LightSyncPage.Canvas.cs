using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ambilight.Lights;
using Wpf.Ui.Controls;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace LightSync.Plugin;

public partial class LightSyncPage
{
    // A fixed-size logical canvas (matches the 16:9 the sampling itself assumes), scaled to whatever screen the
    // window is on only by the border's own fixed pixel size - simpler than the standalone app's LayoutTransform
    // scaling, acceptable for a first pass here.
    private const double CanvasW = 480, CanvasH = 270;
    private const double LabelHeight = 16;

    private readonly Dictionary<string, FrameworkElement> _canvasBoxes = new();
    private readonly Dictionary<string, CanvasEntry> _canvasItems = new();

    private static readonly (string Device, string Title, int Cols, int Rows)[] RazerDevices =
    {
        ("Keyboard", "Keyboard", Colore.Effects.Keyboard.KeyboardConstants.MaxColumns, Colore.Effects.Keyboard.KeyboardConstants.MaxRows),
        ("Mouse", "Mouse", Colore.Effects.Mouse.MouseConstants.MaxColumns, Colore.Effects.Mouse.MouseConstants.MaxRows),
        ("Mousepad", "Mousepad", Colore.Effects.Mousepad.MousepadConstants.MaxLeds, 1),
        ("Headset", "Headset", 2, 1),
        ("Keypad", "Keypad", Colore.Effects.Keypad.KeypadConstants.MaxColumns, Colore.Effects.Keypad.KeypadConstants.MaxRows),
    };

    private sealed class CanvasEntry
    {
        public required string Title;
        public required WpfBrush Color;
        public required int Cols;
        public required int Rows;
        public required double PosX, PosY, PosW, PosH;
        public required Action<double, double, double, double> Commit;
    }

    /// <summary>The canvas keeps its 480x270 logical coordinate space (all the position/size math above is in that
    /// space) but is visually scaled to fill the available width, via a LayoutTransform - so it grows and shrinks
    /// with the window instead of always taking the same fixed pixels.</summary>
    private void CanvasScaleHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_canvasScaleHost.ActualWidth <= 0) return;
        var scale = Math.Max(0.5, Math.Min(2.0, _canvasScaleHost.ActualWidth / CanvasW));
        _canvasBorder.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void LoadCanvasToggles()
    {
        var cfg = LightsService.Config;
        if (cfg is null) return;

        _canvasKeyboardToggle.IsChecked = cfg.GetRazerCanvas("Keyboard").UseScreenPosition;
        _canvasMouseToggle.IsChecked = cfg.GetRazerCanvas("Mouse").UseScreenPosition;
        _canvasMousepadToggle.IsChecked = cfg.GetRazerCanvas("Mousepad").UseScreenPosition;
        _canvasHeadsetToggle.IsChecked = cfg.GetRazerCanvas("Headset").UseScreenPosition;
        _canvasKeypadToggle.IsChecked = cfg.GetRazerCanvas("Keypad").UseScreenPosition;
    }

    private void CanvasDeviceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_constructed || _isUpdatingUi) return;

        var cfg = LightsService.Config;
        if (cfg is null) return;

        var toggle = (ToggleSwitch)sender;
        var device = toggle == _canvasKeyboardToggle ? "Keyboard"
            : toggle == _canvasMouseToggle ? "Mouse"
            : toggle == _canvasMousepadToggle ? "Mousepad"
            : toggle == _canvasHeadsetToggle ? "Headset"
            : "Keypad";

        cfg.GetRazerCanvas(device).UseScreenPosition = toggle.IsChecked == true;
        cfg.Save();
        SyncCanvas();
    }

    private void SyncCanvas()
    {
        var wanted = new HashSet<string>();
        var engine = LightsService.Engine;
        var cfg = LightsService.Config;

        if (engine != null)
            foreach (var bulb in engine.Bulbs)
            {
                if (!bulb.Config.UseScreenPosition) continue;
                var key = "bulb:" + bulb.Config.Id;
                wanted.Add(key);
                SyncBox(key, new CanvasEntry
                {
                    Title = string.IsNullOrWhiteSpace(bulb.Config.Name) ? (bulb.DeviceName ?? bulb.Config.Id) : bulb.Config.Name,
                    Color = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xC4)),
                    Cols = 1,
                    Rows = 1,
                    PosX = bulb.Config.PosX,
                    PosY = bulb.Config.PosY,
                    PosW = bulb.Config.PosW,
                    PosH = bulb.Config.PosH,
                    Commit = (x, y, w, h) => engine.SetScreenPosition(bulb, x, y, w, h),
                });
            }

        if (cfg != null)
            foreach (var d in RazerDevices)
            {
                var pos = cfg.GetRazerCanvas(d.Device);
                if (!pos.UseScreenPosition) continue;
                var key = "razer:" + d.Device;
                wanted.Add(key);
                var cols = d.Device == "Keyboard" ? _settings.KeyboardWidth : d.Cols;
                var rows = d.Device == "Keyboard" ? _settings.KeyboardHeight : d.Rows;
                SyncBox(key, new CanvasEntry
                {
                    Title = d.Title,
                    Color = new WpfSolidColorBrush(WpfColor.FromRgb(0xC4, 0x82, 0x3B)),
                    Cols = cols,
                    Rows = rows,
                    PosX = pos.PosX,
                    PosY = pos.PosY,
                    PosW = pos.PosW,
                    PosH = pos.PosH,
                    Commit = (x, y, w, h) =>
                    {
                        pos.PosX = Math.Max(0, Math.Min(1 - w, x));
                        pos.PosY = Math.Max(0, Math.Min(1 - h, y));
                        pos.PosW = Math.Max(0.02, Math.Min(1, w));
                        pos.PosH = Math.Max(0.02, Math.Min(1, h));
                        cfg.Save();
                    },
                });
            }

        foreach (var key in _canvasBoxes.Keys.ToList())
        {
            if (wanted.Contains(key)) continue;
            _positionCanvas.Children.Remove(_canvasBoxes[key]);
            _canvasBoxes.Remove(key);
            _canvasItems.Remove(key);
        }

        _canvasHintText.Visibility = _canvasBoxes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadCanvasToggles();
    }

    private void SyncBox(string key, CanvasEntry item)
    {
        _canvasItems[key] = item;

        if (!_canvasBoxes.TryGetValue(key, out var stack))
        {
            stack = CreateCanvasBox(key);
            _canvasBoxes[key] = stack;
            _positionCanvas.Children.Add(stack);
            ApplyStoredPosition(key, item);
        }

        var cellsHost = (Border)((StackPanel)stack).Children[0];
        var label = (TextBlock)((StackPanel)stack).Children[1];
        label.Text = item.Title;
        cellsHost.Background = item.Color;

        RebuildCells(cellsHost, item.Cols, item.Rows);
    }

    private FrameworkElement CreateCanvasBox(string key)
    {
        var cellsHost = new Border
        {
            BorderBrush = WpfBrushes.White,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
        };

        var moveThumb = new Thumb { Background = WpfBrushes.Transparent, Cursor = Cursors.SizeAll };
        moveThumb.DragDelta += (_, e) => MoveCanvasBox(key, e.HorizontalChange, e.VerticalChange);
        moveThumb.DragCompleted += (_, _) => CommitCanvasPosition(key);

        var resizeThumb = new Thumb
        {
            Width = 12,
            Height = 12,
            Background = WpfBrushes.White,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
        };
        resizeThumb.DragDelta += (_, e) => ResizeCanvasBox(key, e.HorizontalChange, e.VerticalChange);
        resizeThumb.DragCompleted += (_, _) => CommitCanvasPosition(key);

        var overlay = new Grid();
        overlay.Children.Add(moveThumb);
        overlay.Children.Add(resizeThumb);
        cellsHost.Child = overlay;

        var label = new TextBlock
        {
            FontSize = 10,
            Foreground = WpfBrushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Height = LabelHeight,
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(cellsHost);
        stack.Children.Add(label);
        return stack;
    }

    private static void RebuildCells(Border cellsHost, int cols, int rows)
    {
        var overlay = (Grid)cellsHost.Child;
        var existing = overlay.Children.Count > 0 ? overlay.Children[0] as UniformGrid : null;
        if (existing != null && existing.Columns == cols && existing.Rows == rows) return;

        if (existing != null) overlay.Children.RemoveAt(0);

        var cells = new UniformGrid { Rows = rows, Columns = cols, IsHitTestVisible = false };
        var cellBorder = new Thickness(0.5);
        var cellStroke = new WpfSolidColorBrush(WpfColor.FromArgb(70, 0, 0, 0));
        for (var i = 0; i < rows * cols; i++)
            cells.Children.Add(new Border { BorderBrush = cellStroke, BorderThickness = cellBorder });
        overlay.Children.Insert(0, cells);
    }

    private void MoveCanvasBox(string key, double dx, double dy)
    {
        var stack = _canvasBoxes[key];
        var cellsHost = (Border)((StackPanel)stack).Children[0];
        var left = Math.Max(0, Math.Min(CanvasW - cellsHost.Width, Canvas.GetLeft(stack) + dx));
        var top = Math.Max(0, Math.Min(CanvasH - cellsHost.Height - LabelHeight, Canvas.GetTop(stack) + dy));
        Canvas.SetLeft(stack, left);
        Canvas.SetTop(stack, top);
    }

    private void ResizeCanvasBox(string key, double dx, double dy)
    {
        var stack = _canvasBoxes[key];
        var cellsHost = (Border)((StackPanel)stack).Children[0];
        var w = Math.Max(20, Math.Min(CanvasW - Canvas.GetLeft(stack), cellsHost.Width + dx));
        var h = Math.Max(16, Math.Min(CanvasH - LabelHeight - Canvas.GetTop(stack), cellsHost.Height + dy));
        cellsHost.Width = w;
        cellsHost.Height = h;
        ((StackPanel)stack).Width = w;
    }

    private void CommitCanvasPosition(string key)
    {
        var stack = _canvasBoxes[key];
        var cellsHost = (Border)((StackPanel)stack).Children[0];
        if (_canvasItems.TryGetValue(key, out var item))
            item.Commit(Canvas.GetLeft(stack) / CanvasW, Canvas.GetTop(stack) / CanvasH, cellsHost.Width / CanvasW, cellsHost.Height / CanvasH);
    }

    private void ApplyStoredPosition(string key, CanvasEntry item)
    {
        var stack = _canvasBoxes[key];
        var cellsHost = (Border)((StackPanel)stack).Children[0];
        var w = Math.Max(20, item.PosW * CanvasW);
        var h = Math.Max(16, item.PosH * CanvasH);
        cellsHost.Width = w;
        cellsHost.Height = h;
        ((StackPanel)stack).Width = w;
        Canvas.SetLeft(stack, item.PosX * CanvasW);
        Canvas.SetTop(stack, item.PosY * CanvasH);
    }
}
