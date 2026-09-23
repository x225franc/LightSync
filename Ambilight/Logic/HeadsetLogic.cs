using System;
using System.Drawing;
using Ambilight.GUI;
using Ambilight.Util;
using Colore;
using Colore.Effects.Headset;
using ColoreColor = Colore.Data.Color;

namespace Ambilight.Logic
{
    public class HeadsetLogic : IDeviceLogic
    {
        private TraySettings _settings;
        private IChroma _chroma;
        private CustomHeadsetEffect _headsetGrid = CustomHeadsetEffect.Create();

        public HeadsetLogic(TraySettings settings, IChroma chroma)
        {
            _settings = settings;
            _chroma = chroma;
        }

        public void Process(Bitmap newImage)
        {
            Bitmap mapHeadset = Lights.RazerCanvasFeed.Resize(newImage, "Headset", 2, 1, _settings.UltrawideModeEnabled);
            Bitmap saturatedMap = ImageManipulation.ApplySaturation(mapHeadset, _settings.Saturation, _settings.DeviceBrightness / 100f);

            // Dispose the resized map if saturation created a new bitmap
            if (saturatedMap != mapHeadset && mapHeadset != null)
            {
                mapHeadset.Dispose();
            }

            ApplyPictureToGrid(saturatedMap);
            _chroma.Headset.SetCustomAsync(_headsetGrid);

            // Clean up
            saturatedMap?.Dispose();
        }

        private void ApplyPictureToGrid(Bitmap map)
        {
            using (var fast = new FastBitmap(map))
            {
                _headsetGrid[0] = toColoreColor(fast.GetPixel(0, 0));
                _headsetGrid[1] = toColoreColor(fast.GetPixel(1, 0));
            }
        }

        private ColoreColor toColoreColor(Color color)
        {
            return new ColoreColor((byte)color.R, (byte)color.G, (byte)color.B);
        }
    }
}