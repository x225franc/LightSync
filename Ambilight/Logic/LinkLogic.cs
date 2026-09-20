using System.Drawing;
using System.Linq;
using Ambilight.GUI;
using Ambilight.Util;
using Colore;
using Colore.Effects.ChromaLink;
using NLog;
using ColoreColor = Colore.Data.Color;


namespace Ambilight.Logic
{

    /// <summary>
    /// Handles the Ambilight Effect for the Link connection
    /// </summary>
    class LinkLogic : IDeviceLogic
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        private GUI.TraySettings _settings;
        private CustomChromaLinkEffect _linkGrid = CustomChromaLinkEffect.Create();
        private IChroma _chroma;
        private bool _loggedFirstFrame;

        public LinkLogic(TraySettings settings, IChroma chromaInstance)
        {
            this._settings = settings;
            this._chroma = chromaInstance;
        }

        /// <summary>
        /// Processes a ScreenShot and creates an Ambilight Effect for the chroma link
        /// </summary>
        /// <param name="newImage">ScreenShot</param>
        public void Process(Bitmap newImage)
        {
            Bitmap map = ImageManipulation.ResizeImage(newImage, 4, 1);
            Bitmap saturatedMap = ImageManipulation.ApplySaturation(map, _settings.Saturation);

            // Dispose the resized map if saturation created a new bitmap
            if (saturatedMap != map && map != null)
            {
                map.Dispose();
            }

            using (var fast = new FastBitmap(saturatedMap))
            {
                ApplyC1(fast);
                ApplyImageToGrid(fast);
            }

            if (!_loggedFirstFrame)
            {
                _log.Info($"ChromaLink: sending first frame, colors=[{string.Join(", ", Enumerable.Range(0, ChromaLinkConstants.MaxLeds).Select(i => _linkGrid[i].ToString()))}]");
                _loggedFirstFrame = true;
            }

            try
            {
                var task = _chroma.ChromaLink.SetCustomAsync(_linkGrid);
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _log.Error(t.Exception, "ChromaLink.SetCustomAsync failed.");
                });
            }
            catch (System.Exception ex)
            {
                _log.Error(ex, "ChromaLink.SetCustomAsync threw synchronously.");
            }

            // Clean up
            saturatedMap?.Dispose();
        }

        private void ApplyC1(FastBitmap map)
        {
            Color color = map.GetPixel(0, 0);
            _linkGrid[0] = new ColoreColor((byte)color.R, (byte)color.G, (byte)color.B);
        }

        /// <summary>
        /// From a given resized screenshot, an ambilight effect will be created for the keyboard
        /// </summary>
        /// <param name="map">resized screenshot</param>
        private void ApplyImageToGrid(FastBitmap map)
        {
            //Iterating over each key and set it to the corrosponding color of the resized Screenshot
            for (int i = 1; i < Colore.Effects.ChromaLink.ChromaLinkConstants.MaxLeds; i++)
            {
                Color color = map.GetPixel(i-1,0);
                _linkGrid[i] = new ColoreColor((byte)color.R, (byte)color.G, (byte)color.B);
            }
        }
    }
}
