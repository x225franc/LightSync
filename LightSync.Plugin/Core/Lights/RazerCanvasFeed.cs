#nullable disable
using System.Drawing;
using System.Drawing.Imaging;

namespace Ambilight.Lights
{
    /// <summary>
    /// Lets a Razer device (keyboard, mouse, mousepad, headset, keypad) be positioned on the same canvas as the
    /// Yeelight/Govee lights, instead of always reading the whole screen, or assigned to one of the same 4 Chroma
    /// groups a Yeelight/Govee light can use (see BulbConfig.Group) so it can be mapped to wherever that zone sits
    /// in a Chroma Connect setup instead of sampling its own screen region. Each Logic class calls this exactly
    /// where it used to call <see cref="ImageManipulation.ResizeImage"/> directly; behavior is unchanged (whole
    /// screen) until the device is placed on the canvas or assigned to a group.
    /// </summary>
    public static class RazerCanvasFeed
    {
        public static Bitmap Resize(Image image, string device, int width, int height, bool cropSides)
        {
            var cfg = LightsService.Config;
            var pos = cfg == null ? null : cfg.GetRazerCanvas(device);

            // Same priority a bulb uses between its own canvas toggle and its Group: the canvas rectangle, when on,
            // wins over a group assignment.
            if (pos != null && pos.UseScreenPosition)
                return ImageManipulation.ResizeRegion(image, pos.PosX, pos.PosY, pos.PosW, pos.PosH, width, height, cropSides);

            if (pos != null && pos.RazerGroup > 0)
            {
                var engine = LightsService.Engine;
                int rgb = engine != null ? engine.GetGroupRgb(pos.RazerGroup) : 0;
                return SolidFill(width, height, rgb);
            }

            return ImageManipulation.ResizeImage(image, width, height, cropSides);
        }

        static Bitmap SolidFill(int width, int height, int rgb)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF)))
                g.FillRectangle(brush, 0, 0, width, height);
            return bmp;
        }
    }
}
