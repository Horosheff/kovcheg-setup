using System.Drawing;
using System.Windows.Forms;

namespace KovchegVPN;

// Розово-тёмный рендерер меню трея: никакого белого.
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Color.FromArgb(0xFF, 0xE4, 0xEC);
        base.OnRenderItemText(e);
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x2A, 0x10, 0x1C);
        private static readonly Color Sel = Color.FromArgb(0x5A, 0x22, 0x38);
        private static readonly Color Border = Color.FromArgb(0x6A, 0x2A, 0x44);

        public override Color MenuStripGradientBegin => Bg;
        public override Color MenuStripGradientEnd => Bg;
        public override Color ToolStripDropDownBackground => Bg;
        public override Color ToolStripBorder => Border;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Sel;
        public override Color MenuItemSelected => Sel;
        public override Color MenuItemSelectedGradientBegin => Sel;
        public override Color MenuItemSelectedGradientEnd => Sel;
        public override Color MenuItemPressedGradientBegin => Sel;
        public override Color MenuItemPressedGradientEnd => Sel;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
