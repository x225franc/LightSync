using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ambilight.Util;
using DrawingColor = System.Drawing.Color;

namespace Ambilight.GUI
{
    /// <summary>
    /// Hue/saturation color wheel: angle picks the hue, distance from the center the
    /// saturation. Brightness is deliberately not part of it (there is a dedicated slider).
    /// </summary>
    public class ColorWheel : FrameworkElement
    {
        private const int BitmapSize = 240;

        private static readonly ImageSource WheelImage = CreateWheelImage();

        private double _hue;
        private double _saturation = 1;

        // The wheel only edits hue and saturation; the brightness of the current color is kept
        // as is, so a dark color typed in the RGB boxes isn't reset to full brightness by a drag.
        private double _value = 1;
        private bool _dragging;

        public event EventHandler ColorChanged;

        public DrawingColor SelectedColor
        {
            get => ColorUtil.FromHsv(_hue, _saturation, _value);
            set
            {
                ColorUtil.ToHsv(value, out double hue, out double saturation, out double brightness);

                // A grey/black color has no meaningful hue; keep the marker where it was.
                if (saturation > 0 && brightness > 0)
                    _hue = hue;
                if (brightness > 0)
                    _saturation = saturation;
                _value = brightness;

                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0)
                return;

            double radius = size / 2;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawImage(WheelImage, new Rect(center.X - radius, center.Y - radius, size, size));

            double angle = _hue * Math.PI / 180;
            var marker = new Point(
                center.X + Math.Cos(angle) * _saturation * (radius - 1),
                center.Y - Math.Sin(angle) * _saturation * (radius - 1));

            dc.DrawEllipse(null, new Pen(Brushes.Black, 3), marker, 8, 8);
            dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 8, 8);
        }

        // The wheel sits inside a card that is itself a button. Without marking these events as
        // handled, that parent button also grabs the mouse on press, so this element never sees
        // the release and stays "dragging" - the color then followed the cursor around.
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _dragging = true;
            CaptureMouse();
            UpdateFromPoint(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_dragging)
                return;

            // Safety net: if a release was ever missed, stop as soon as no button is held.
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragging = false;
                return;
            }

            UpdateFromPoint(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _dragging = false;
        }

        private void UpdateFromPoint(Point p)
        {
            double radius = Math.Min(ActualWidth, ActualHeight) / 2;
            if (radius <= 0)
                return;

            double dx = p.X - ActualWidth / 2;
            double dy = ActualHeight / 2 - p.Y;

            _hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            _saturation = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / radius);

            InvalidateVisual();
            ColorChanged?.Invoke(this, EventArgs.Empty);
        }

        private static ImageSource CreateWheelImage()
        {
            var bitmap = new WriteableBitmap(BitmapSize, BitmapSize, 96, 96, PixelFormats.Pbgra32, null);
            var pixels = new int[BitmapSize * BitmapSize];
            double radius = BitmapSize / 2.0;

            for (int y = 0; y < BitmapSize; y++)
            {
                for (int x = 0; x < BitmapSize; x++)
                {
                    double dx = x + 0.5 - radius;
                    double dy = radius - (y + 0.5);
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    // One pixel of anti-aliasing on the rim; transparent outside the disc.
                    double alpha = Math.Max(0, Math.Min(1, radius - distance));
                    if (alpha <= 0)
                        continue;

                    double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                    var c = ColorUtil.FromHsv(hue, Math.Min(1, distance / radius), 1);

                    int a = (int)Math.Round(alpha * 255);
                    int r = c.R * a / 255, g = c.G * a / 255, b = c.B * a / 255;
                    pixels[y * BitmapSize + x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }

            bitmap.WritePixels(new Int32Rect(0, 0, BitmapSize, BitmapSize), pixels, BitmapSize * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
