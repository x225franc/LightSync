using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Ambilight.Util;
using NLog;

namespace Ambilight
{
    class ImageManipulation
    {

        private static Logger _log = LogManager.GetCurrentClassLogger();
        private static ImageAttributes _cachedImageAttributes;
        private static ColorMatrix _cachedColorMatrix;
        /// <summary>
        /// Resize an image to the specified width and height.
        /// </summary>
        /// <param name="image">The image to resize.</param>
        /// <param name="width">The width to resize to.</param>
        /// <param name="height">The height to resize to.</param>
        /// <returns>The resized image.</returns>
        public static Bitmap ResizeImage(Image image, int width, int height, bool cropSides = false)
        {
            try
            {
                // Cuts down a 21:9 image to a 16:9 image by removing the outer sides
                var source = cropSides
                    ? new Rectangle(Convert.ToInt32((image.Width / 21) * 2.5), 0, (image.Width / 21) * 16, image.Height)
                    : new Rectangle(0, 0, image.Width, image.Height);

                return AverageResize((Bitmap)image, source, width, height);
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Error while resizing the image. width: {width} height: {height} cropSides: {cropSides}");
            }

            return new Bitmap(image, width, height);
        }

        /// <summary>Same averaging resize, but only for a region of the image given as fractions (0-1) of its width and height.</summary>
        public static Bitmap ResizeRegion(Image image, double left, double top, double regionWidth, double regionHeight, int width, int height, bool cropSides = false)
        {
            int baseX = 0, baseW = image.Width;
            if (cropSides)
            {
                baseX = Convert.ToInt32((image.Width / 21) * 2.5);
                baseW = (image.Width / 21) * 16;
            }

            var source = new Rectangle(baseX + (int)(baseW * left), (int)(image.Height * top),
                                       Math.Max(1, (int)(baseW * regionWidth)), Math.Max(1, (int)(image.Height * regionHeight)));
            return AverageResize((Bitmap)image, source, width, height);
        }

        /// <summary>
        /// Shrinks the source area to width x height by averaging what lies under each output pixel.
        /// "new Bitmap(image, w, h)" only samples a couple of source pixels per output pixel (biased towards the
        /// top-left corner), so a light in the middle or the bottom of the screen was simply ignored; the full
        /// high-quality GDI+ resize is exact but ~15x slower. This samples up to 16 x 16 points per output pixel.
        /// </summary>
        private static unsafe Bitmap AverageResize(Bitmap image, Rectangle source, int width, int height)
        {
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var src = image.LockBits(source, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dst = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                double cellW = (double)source.Width / width, cellH = (double)source.Height / height;
                int stepsX = Math.Max(1, Math.Min(16, (int)cellW)), stepsY = Math.Max(1, Math.Min(16, (int)cellH));

                for (int y = 0; y < height; y++)
                {
                    uint* outRow = (uint*)((byte*)dst.Scan0 + y * dst.Stride);
                    for (int x = 0; x < width; x++)
                    {
                        long r = 0, g = 0, b = 0;
                        for (int sy = 0; sy < stepsY; sy++)
                        {
                            int py = Math.Min(source.Height - 1, (int)((y + (sy + 0.5) / stepsY) * cellH));
                            byte* row = (byte*)src.Scan0 + py * src.Stride;
                            for (int sx = 0; sx < stepsX; sx++)
                            {
                                int px = Math.Min(source.Width - 1, (int)((x + (sx + 0.5) / stepsX) * cellW));
                                byte* p = row + px * 4;
                                b += p[0]; g += p[1]; r += p[2];
                            }
                        }

                        int n = stepsX * stepsY;
                        outRow[x] = 0xFF000000u | ((uint)(r / n) << 16) | ((uint)(g / n) << 8) | (uint)(b / n);
                    }
                }
            }
            finally
            {
                image.UnlockBits(src);
                result.UnlockBits(dst);
            }

            return result;
        }

        /// <summary>
        /// Applies a given saturation value to a Bitmap.
        /// </summary>
        /// <param name="srcBitmap">Bitmap</param>
        /// <param name="saturation">Saturation Value</param>
        /// <param name="brightness">Gain applied to every channel (1 = unchanged, above 1 brightens; results are clamped)</param>
        /// <returns></returns>
        public static Bitmap ApplySaturation(Bitmap srcBitmap, float saturation, float brightness = 1f)
        {
            // Skip processing if neither saturation nor brightness changes anything
            if (Math.Abs(saturation - 1.0f) < 0.001f && Math.Abs(brightness - 1.0f) < 0.001f)
            {
                return srcBitmap;
            }

            float rWeight = 0.3086f;
            float gWeight = 0.6094f;
            float bWeight = 0.0820f;

            float a = (1.0f - saturation) * rWeight + saturation;
            float b = (1.0f - saturation) * rWeight;
            float c = (1.0f - saturation) * rWeight;
            float d = (1.0f - saturation) * gWeight;
            float e = (1.0f - saturation) * gWeight + saturation;
            float f = (1.0f - saturation) * gWeight;
            float g = (1.0f - saturation) * bWeight;
            float h = (1.0f - saturation) * bWeight;
            float i = (1.0f - saturation) * bWeight + saturation;

            Bitmap returnBitmap = new Bitmap(srcBitmap.Width, srcBitmap.Height);

            // Create a Graphics
            using (Graphics gr = Graphics.FromImage(returnBitmap))
            {
                // Reuse cached ColorMatrix and ImageAttributes if possible
                if (_cachedColorMatrix == null)
                {
                    _cachedColorMatrix = new ColorMatrix();
                }
                if (_cachedImageAttributes == null)
                {
                    _cachedImageAttributes = new ImageAttributes();
                }

                // Update ColorMatrix values
                _cachedColorMatrix.Matrix00 = a * brightness;
                _cachedColorMatrix.Matrix01 = b * brightness;
                _cachedColorMatrix.Matrix02 = c * brightness;
                _cachedColorMatrix.Matrix10 = d * brightness;
                _cachedColorMatrix.Matrix11 = e * brightness;
                _cachedColorMatrix.Matrix12 = f * brightness;
                _cachedColorMatrix.Matrix20 = g * brightness;
                _cachedColorMatrix.Matrix21 = h * brightness;
                _cachedColorMatrix.Matrix22 = i * brightness;

                // Set color matrix
                _cachedImageAttributes.SetColorMatrix(_cachedColorMatrix,
                    ColorMatrixFlag.Default,
                    ColorAdjustType.Default);

                // Draw Image with image attributes
                gr.DrawImage(srcBitmap,
                    new Rectangle(0, 0, srcBitmap.Width, srcBitmap.Height),
                    0, 0, srcBitmap.Width, srcBitmap.Height,
                    GraphicsUnit.Pixel, _cachedImageAttributes);
            }

            return returnBitmap;
        }
    }
}