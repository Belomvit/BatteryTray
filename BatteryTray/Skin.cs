using System.Drawing;

namespace BatteryTray;

/// The look chosen in the menu; its name is what gets saved in the settings.
enum Look { Windows11, Classic, Modern }

/// Everything a look changes: how the widget is drawn and whether it reacts to hover.
sealed class Skin(Look look, string name, ILookRenderer renderer, bool hoverEffect)
{
    public Look Look { get; } = look;
    public string Name { get; } = name;
    public ILookRenderer Renderer { get; } = renderer;
    public bool HoverEffect { get; } = hoverEffect;

    public static IReadOnlyList<Skin> All { get; } =
    [
        new(Look.Windows11, "Windows 11", new RingRenderer(RingRenderer.Windows11, Palette.Windows11), hoverEffect: true),
        new(Look.Modern, "Modern", new CapsuleRenderer(), hoverEffect: true),
        new(Look.Classic, "Classic", new RingRenderer(RingRenderer.Classic, Palette.Classic), hoverEffect: false),
    ];

    public static Skin For(Look look) => All.First(s => s.Look == look);
}

/// Draws the widget content for one look.
interface ILookRenderer
{
    /// Size of the frame needed for <paramref name="items"/> at the given DPI scale.
    Size Measure(IReadOnlyList<ItemView> items, float scale);
    /// Paints <paramref name="items"/> onto a frame of <paramref name="size"/>, already cleared.
    void Paint(Graphics g, Size size, IReadOnlyList<ItemView> items, float scale, bool hover, SystemTheme theme);
}
