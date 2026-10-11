using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ambilight.Lights;

namespace Ambilight.GUI
{
    /// <summary>One preset applied to all known lights; individual enabled switches are preserved.</summary>
    public sealed class LightsEffectEditor : Expander
    {
        public event EventHandler Applied = delegate { };

        public LightsEffectEditor(Engine engine, Config config)
        {
            Header = "Global effect";
            var panel = new StackPanel { Margin = new Thickness(12) };
            var effect = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = LightEffects.Names, SelectedIndex = Enum.IsDefined(typeof(LightEffect), config.AllLightsEffect) ? (int)config.AllLightsEffect : 0 };
            int rgb = config.AllLightsColor;
            var wheel = new ColorWheel { Width = 140, Height = 140, Margin = new Thickness(0, 4, 0, 8),
                SelectedColor = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255) };
            var swatch = new Border { Width = 140, Height = 20, CornerRadius = new CornerRadius(4) };
            var color = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            color.Children.Add(wheel);
            color.Children.Add(swatch);
            void UpdateSwatch() { var c = wheel.SelectedColor; swatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B)); }
            wheel.ColorChanged += (s, e) => UpdateSwatch();
            UpdateSwatch();
            var speed = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Max(0, Math.Min(100, config.AllLightsSpeed)) };
            var reverse = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = config.AllLightsReverse, HorizontalAlignment = HorizontalAlignment.Left };
            var brightness = new Slider { Minimum = 1, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Max(1, Math.Min(100, config.AllLightsBrightness)) };
            var brightnessText = new TextBlock { Width = 45, VerticalAlignment = VerticalAlignment.Center };
            brightnessText.Text = ((int)brightness.Value) + "%";
            brightness.ValueChanged += (s, e) => brightnessText.Text = ((int)e.NewValue) + "%";
            var brightnessPanel = new DockPanel();
            DockPanel.SetDock(brightnessText, Dock.Right);
            brightnessPanel.Children.Add(brightnessText);
            brightnessPanel.Children.Add(brightness);
            panel.Children.Add(Row("Effect", effect));
            var colorRow = Row("Color", color);
            var speedRow = Row("Speed", speed);
            var reverseRow = Row("Reverse", reverse);
            panel.Children.Add(colorRow);
            panel.Children.Add(speedRow);
            panel.Children.Add(reverseRow);
            panel.Children.Add(Row("Brightness", brightnessPanel));
            void UpdateRows()
            {
                var selected = (LightEffect)effect.SelectedIndex;
                colorRow.Visibility = selected == LightEffect.SolidColor || selected == LightEffect.Breathing ? Visibility.Visible : Visibility.Collapsed;
                speedRow.Visibility = selected == LightEffect.ScreenAmbilight || selected == LightEffect.SolidColor ? Visibility.Collapsed : Visibility.Visible;
                reverseRow.Visibility = selected == LightEffect.RainbowWave || selected == LightEffect.RainbowWheel ? Visibility.Visible : Visibility.Collapsed;
            }
            effect.SelectionChanged += (s, e) => UpdateRows();
            UpdateRows();
            panel.Children.Add(new TextBlock { Text = "Apply the same settings and restart animations together. Disabled lights stay disabled; individual settings can be changed afterwards.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 8) });
            var apply = new Wpf.Ui.Controls.Button { Content = "Apply to all lights", HorizontalAlignment = HorizontalAlignment.Left };
            apply.Click += (s, e) =>
            {
                if (effect.SelectedIndex < 0) return;
                var c = wheel.SelectedColor;
                engine.SetAllEffects((LightEffect)effect.SelectedIndex, (c.R << 16) | (c.G << 8) | c.B, (int)speed.Value, reverse.IsChecked == true, (int)brightness.Value);
                Applied?.Invoke(this, EventArgs.Empty);
            };
            panel.Children.Add(apply);
            Content = panel;
        }

        private static Grid Row(string label, UIElement control)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }
    }
}
