using System;

namespace Ambilight.Lights
{
    public enum LightEffect { ScreenAmbilight, SolidColor, Breathing, ColorCycle, RainbowWave, RainbowWheel }

    /// <summary>Procedural colors in 0xRRGGBB; no screen, Chroma or network dependency.</summary>
    public static class LightEffects
    {
        public static readonly string[] Names = { "Screen Ambilight", "Solid Color", "Breathing", "Color Cycle", "Rainbow Wave", "Rainbow Wheel" };

        public static int Color(LightEffect effect, int rgb, int speed, bool reverse, double seconds, int index, int count)
        {
            double rate = Math.Max(0, Math.Min(100, speed)) / 100.0;
            if (effect == LightEffect.SolidColor) return rgb & 0xFFFFFF;
            if (effect == LightEffect.Breathing)
            {
                double intensity = 0.04 + 0.96 * (1 - Math.Cos(2 * Math.PI * seconds / (6 - 5 * rate))) / 2;
                return ((int)(((rgb >> 16) & 255) * intensity) << 16)
                    | ((int)(((rgb >> 8) & 255) * intensity) << 8) | (int)((rgb & 255) * intensity);
            }
            double phase = seconds * (effect == LightEffect.ColorCycle ? 15 + 165 * rate : 30 + 330 * rate);
            if (effect != LightEffect.ColorCycle && !reverse) phase = -phase;
            // A strip has a linear topology: Wave spans a full rainbow, Wheel wraps a circular hue ring.
            double position = count <= 1 ? 0 : (effect == LightEffect.RainbowWheel ? (double)index / count : (double)index / (count - 1));
            double hue = (phase + (effect == LightEffect.ColorCycle ? 0 : position * 360)) % 360;
            if (hue < 0) hue += 360;
            double sector = hue / 60;
            int x = (int)Math.Round(255 * (1 - Math.Abs(sector % 2 - 1)));
            switch ((int)sector)
            {
                case 0: return (255 << 16) | (x << 8);
                case 1: return (x << 16) | (255 << 8);
                case 2: return (255 << 8) | x;
                case 3: return (x << 8) | 255;
                case 4: return (x << 16) | 255;
                default: return (255 << 16) | x;
            }
        }
    }
}
