#nullable disable
using System;
using System.Drawing;

namespace Ambilight.Util
{
    internal static class ColorUtil
    {
        /// <param name="hue">Degrees, any value (wrapped into 0..360).</param>
        /// <param name="saturation">0..1</param>
        /// <param name="value">0..1</param>
        public static Color FromHsv(double hue, double saturation, double value)
        {
            hue = ((hue % 360) + 360) % 360;
            saturation = Math.Max(0, Math.Min(1, saturation));
            value = Math.Max(0, Math.Min(1, value));

            double c = value * saturation;
            double x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
            double m = value - c;

            double r, g, b;
            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromArgb(255, ToByte(r + m), ToByte(g + m), ToByte(b + m));
        }

        public static void ToHsv(Color color, out double hue, out double saturation, out double value)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            if (delta == 0) hue = 0;
            else if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
            if (hue < 0) hue += 360;

            saturation = max == 0 ? 0 : delta / max;
            value = max;
        }

        private static int ToByte(double component) =>
            Math.Max(0, Math.Min(255, (int)Math.Round(component * 255)));
    }
}
