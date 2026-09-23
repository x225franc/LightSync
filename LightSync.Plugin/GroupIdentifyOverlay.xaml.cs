using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace LightSync.Plugin;

/// <summary>
/// A "which zone is where" overlay, like Windows' own monitor Identify: four colored, labeled bands are shown over
/// the captured screen for a few seconds. They match the four equal left-to-right columns LinkLogic actually
/// samples for Chroma "Group 1".."Group 4" (see Core/Logic/LinkLogic.cs's ApplyImageToGrid) - and the same colors
/// the existing group color test uses, so the two stay recognizable as the same four zones.
/// </summary>
public partial class GroupIdentifyOverlay : Window
{
    static readonly (string Label, Color Color)[] Groups =
    {
        ("Group 1", Color.FromRgb(255, 255, 255)),
        ("Group 2", Color.FromRgb(255,   0,   0)),
        ("Group 3", Color.FromRgb(  0, 255,   0)),
        ("Group 4", Color.FromRgb(  0, 110, 255)),
    };

    GroupIdentifyOverlay()
    {
        InitializeComponent();

        for (int i = 0; i < Groups.Length; i++)
        {
            var (label, color) = Groups[i];
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(110, color.R, color.G, color.B)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
            };
            var text = new TextBlock
            {
                Text = label,
                FontSize = 42,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Color = Colors.Black, Opacity = 0.9 },
            };
            border.Child = text;
            Grid.SetColumn(border, i);
            RootGrid.Children.Add(border);
        }
    }

    /// <summary>Shows the overlay over the given screen for a few seconds, then closes itself (also closes on a
    /// click or key press so it never lingers on top of everything).</summary>
    public static void Show(System.Drawing.Rectangle bounds, TimeSpan duration)
    {
        var overlay = new GroupIdentifyOverlay
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
        };

        void CloseNow(object? sender, EventArgs e) => overlay.Close();

        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (s, e) => { timer.Stop(); CloseNow(s, e); };
        overlay.MouseLeftButtonDown += (s, e) => { timer.Stop(); CloseNow(s, e); };
        overlay.KeyDown += (s, e) => { timer.Stop(); CloseNow(s, e); };

        overlay.Show();
        overlay.Activate();
        timer.Start();
    }
}
