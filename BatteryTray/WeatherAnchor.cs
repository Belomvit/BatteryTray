using System.Drawing;
using System.Windows.Automation;

namespace BatteryTray;

/// Finds where the widget goes: just right of the taskbar's Widgets (weather) button, read
/// through UI Automation. Rides out brief glitches while Explorer animates, and falls back to
/// the taskbar's left edge only once the button has been gone for a while.
sealed class WeatherAnchor(IntPtr taskbar)
{
    const int MissesBeforeFallback = 5; // ~15 s at the 3 s watchdog: weather is really turned off
    static readonly TimeSpan SearchBackoff = TimeSpan.FromSeconds(60);

    AutomationElement? button;  // cached UI Automation element of the weather button
    int? lastGoodX;             // last trusted screen X right of the weather button
    int misses;                 // consecutive failed lookups
    DateTime nextSearch;        // when a full taskbar search may run again after finding nothing

    /// Screen X for the widget's left edge, or null to stay where it is.
    /// <paramref name="placed"/>: the widget already has a position it could keep.
    public int? AnchorX(Rectangle taskbarRect, float scale, bool placed)
    {
        if (ButtonRight(taskbarRect, scale) is int right)
        {
            lastGoodX = right;
            misses = 0;
            return right;
        }

        misses = Math.Min(misses + 1, MissesBeforeFallback);
        if (misses < MissesBeforeFallback)
        {
            if (lastGoodX is int keep) return keep; // a glitch: keep the last trusted spot
            if (placed) return null;                // nothing trusted yet: don't move
        }
        return taskbarRect.Left + (int)(8 * scale); // weather widget turned off
    }

    /// Screen X of the weather button's right edge, or null if it can't be read right now.
    int? ButtonRight(Rectangle tb, float scale)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (button == null)
                {
                    // A full search of Explorer's UI is expensive; if the weather widget is turned off,
                    // don't repeat it every 3 s — try again once a minute.
                    if (DateTime.UtcNow < nextSearch) return null;
                    button = AutomationElement.FromHandle(taskbar).FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
                    if (button == null) { nextSearch = DateTime.UtcNow + SearchBackoff; return null; }
                }
                var r = button.Current.BoundingRectangle;
                // Ignore transient rects while Explorer animates (empty, collapsed, or outside the taskbar).
                bool sane = !r.IsEmpty && r.Width >= 30 * scale && r.Left >= tb.Left - 1 && r.Right <= tb.Right
                            && r.Top >= tb.Top - 1 && r.Bottom <= tb.Bottom + 1;
                return sane ? (int)r.Right : null;
            }
            catch
            {
                button = null; // stale element (Explorer rebuilt it): look it up again once
            }
        }
        return null;
    }
}
