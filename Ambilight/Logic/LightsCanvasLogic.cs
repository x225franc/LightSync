using System.Drawing;
using Ambilight.GUI;
using Ambilight.Lights;
using Ambilight.Util;

namespace Ambilight.Logic
{
    /// <summary>
    /// Feeds Yeelight/Govee devices positioned on the lights canvas directly from this frame - bypassing Razer
    /// Chroma Connect entirely, so none of its 4-zone limit or forced Global/Group-1 mirroring applies, and
    /// positions are not limited to 4 fixed columns. Runs even when Razer Synapse/Chroma is not available.
    /// A multi-segment Govee device gets a true gradient: its rectangle is split into as many equal columns as it
    /// has real segments, each sampled and streamed to its own segment, left to right.
    /// </summary>
    class LightsCanvasLogic : IDeviceLogic
    {
        private readonly TraySettings _settings;

        public LightsCanvasLogic(TraySettings settings) { _settings = settings; }

        public void Process(Bitmap newImage)
        {
            var engine = LightsService.Engine;
            if (engine == null) return;

            bool crop = _settings.UltrawideModeEnabled;
            float saturation = _settings.Saturation, brightness = _settings.DeviceBrightness / 100f;

            foreach (var b in engine.Bulbs)
            {
                if (!b.Config.UseScreenPosition || !b.Config.Enabled) continue;

                int segments = b.Kind == DeviceKind.Govee ? GoveeCatalog.SegmentCount(b.Config.Id, b.Model) : 1;
                if (segments <= 1)
                {
                    engine.SetScreenColor(b, Sample(newImage, b.Config.PosX, b.Config.PosY, b.Config.PosW, b.Config.PosH, crop, saturation, brightness));
                    continue;
                }

                var colors = new int[segments];
                double colW = b.Config.PosW / segments;
                for (int i = 0; i < segments; i++)
                    colors[i] = Sample(newImage, b.Config.PosX + i * colW, b.Config.PosY, colW, b.Config.PosH, crop, saturation, brightness);
                engine.SetScreenColors(b, colors);
            }
        }

        static int Sample(Bitmap image, double x, double y, double w, double h, bool crop, float saturation, float brightness)
        {
            Bitmap sample = ImageManipulation.ResizeRegion(image, x, y, w, h, 1, 1, crop);
            Bitmap saturated = ImageManipulation.ApplySaturation(sample, saturation, brightness);

            Color c;
            using (var fast = new FastBitmap(saturated)) c = fast.GetPixel(0, 0);
            if (saturated != sample) saturated.Dispose();
            sample.Dispose();

            return (c.R << 16) | (c.G << 8) | c.B;
        }
    }
}
