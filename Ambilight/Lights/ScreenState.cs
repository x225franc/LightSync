namespace Ambilight.Lights
{
    /// <summary>
    /// Tiny shared flag: true while screen capture itself is paused (screensaver with "keep the effect" off, lock,
    /// suspend). Set by DesktopDuplicatorReader, read by Engine - lets the lights freeze at their last color for
    /// that whole window instead of reacting to whatever Chroma Connect (or the screen canvas) does or doesn't send
    /// while nothing is actually being captured, which is what was making them go dark instead of staying lit.
    /// </summary>
    public static class ScreenState
    {
        public static volatile bool CapturePaused;
    }
}
