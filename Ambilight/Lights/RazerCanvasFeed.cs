using System.Drawing;

namespace Ambilight.Lights
{
    /// <summary>
    /// Lets a Razer device (keyboard, mouse, mousepad, headset, keypad) be positioned on the same canvas as the
    /// Yeelight/Govee lights, instead of always reading the whole screen. Each Logic class calls this exactly where
    /// it used to call <see cref="ImageManipulation.ResizeImage"/> directly; behavior is unchanged (whole screen)
    /// until the device is placed on the canvas.
    /// </summary>
    public static class RazerCanvasFeed
    {
        public static Bitmap Resize(Image image, string device, int width, int height, bool cropSides)
        {
            var cfg = LightsService.Config;
            var pos = cfg == null ? null : cfg.GetRazerCanvas(device);
            return pos != null && pos.UseScreenPosition
                ? ImageManipulation.ResizeRegion(image, pos.PosX, pos.PosY, pos.PosW, pos.PosH, width, height, cropSides)
                : ImageManipulation.ResizeImage(image, width, height, cropSides);
        }
    }
}
