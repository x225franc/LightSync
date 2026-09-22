using System.Drawing;
using System.Windows.Forms;

namespace LightConnect.Ui
{
    /// <summary>
    /// The classic WinForms tray context menu never follows Windows' dark mode on its own.
    /// Owner-drawing it with a dark color table keeps it consistent with the Fluent window
    /// (same renderer as Razer Ambilight's tray menu).
    /// </summary>
    internal sealed class DarkContextMenuColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(32, 32, 32);
        private static readonly Color Hover = Color.FromArgb(61, 61, 61);
        private static readonly Color Border = Color.FromArgb(69, 69, 69);

        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    internal sealed class DarkContextMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkContextMenuRenderer() : base(new DarkContextMenuColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.WhiteSmoke;
            base.OnRenderItemText(e);
        }
    }
}
