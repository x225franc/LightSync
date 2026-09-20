using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace Ambilight.Util
{
    /// <summary>
    /// Locks a bitmap once and reads pixels via a direct pointer instead of
    /// Bitmap.GetPixel, which round-trips into native GDI+ on every single call
    /// and is the dominant CPU cost when sampling dozens of pixels per frame
    /// across multiple device grids.
    /// </summary>
    sealed class FastBitmap : IDisposable
    {
        private readonly Bitmap _bitmap;
        private readonly BitmapData _data;
        private readonly int _bytesPerPixel;

        public FastBitmap(Bitmap bitmap)
        {
            _bitmap = bitmap;
            _bytesPerPixel = Image.GetPixelFormatSize(bitmap.PixelFormat) / 8;
            _data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        }

        public unsafe Color GetPixel(int x, int y)
        {
            byte* row = (byte*)_data.Scan0 + y * _data.Stride;
            byte* pixel = row + x * _bytesPerPixel;

            // GDI+ 32/24bpp RGB formats are stored as BGR(A) in memory.
            return _bytesPerPixel >= 4
                ? Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0])
                : Color.FromArgb(255, pixel[2], pixel[1], pixel[0]);
        }

        public void Dispose()
        {
            _bitmap.UnlockBits(_data);
        }
    }
}
