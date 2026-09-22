using System.Drawing;
using Ambilight.GUI;
using Ambilight.Util;
using Colore;
using Colore.Effects.Keyboard;
using ColoreColor = Colore.Data.Color;

namespace Ambilight.Logic
{

    /// <summary>
    /// Handles the Ambilight Effect for the mouse
    /// </summary>
    class KeyboardLogic : IDeviceLogic
    {
        private readonly GUI.TraySettings _settings;
        private CustomKeyboardEffect _keyboardGrid = Colore.Effects.Keyboard.CustomKeyboardEffect.Create();
        private IChroma _chroma;

        public KeyboardLogic(TraySettings settings, IChroma chromaInstance)
        {
            this._settings = settings;
            this._chroma = chromaInstance;
        }

        /// <summary>
        /// Processes a ScreenShot and creates an Ambilight Effect for the keyboard
        /// </summary>
        /// <param name="newImage">ScreenShot</param>
        public void Process(Bitmap newImage)
        {
            Bitmap map = Lights.RazerCanvasFeed.Resize(newImage, "Keyboard", _settings.KeyboardWidth, _settings.KeyboardHeight, _settings.UltrawideModeEnabled);
            Bitmap saturatedMap = ImageManipulation.ApplySaturation(map, _settings.Saturation, _settings.DeviceBrightness / 100f);

            // Dispose the resized map if saturation created a new bitmap
            if (saturatedMap != map && map != null)
            {
                map.Dispose();
            }

            ApplyPictureToGrid(saturatedMap);
            _chroma.Keyboard.SetCustomAsync(_keyboardGrid);

            // Clean up
            saturatedMap?.Dispose();
        }

        /// <summary>
        /// From a given resized screenshot, an ambilight effect will be created for the keyboard
        /// </summary>
        /// <param name="map">resized screenshot</param>
        /// <returns>EffectGrid</returns>
        private void ApplyPictureToGrid(Bitmap map)
        {
            using (var fast = new FastBitmap(map))
            {
                //Iterating over each key and set it to the corrosponding color of the resized Screenshot
                for (var r = 0; r < _settings.KeyboardHeight; r++)
                {
                    for (var c = 0; c < _settings.KeyboardWidth; c++)
                    {
                        System.Drawing.Color color;

                        if (_settings.AmbiModeEnabled)
                        {
                            color = fast.GetPixel(c, _settings.KeyboardHeight - 1);
                        }
                        else
                        {
                            color = fast.GetPixel(c, r);
                        }

                        _keyboardGrid[r, c] = new ColoreColor((byte)color.R, (byte)color.G, (byte)color.B);
                    }
                }
            }
        }
    }
}
