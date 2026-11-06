using System;
using System.Drawing;

namespace SnipStudio
{
    // Inputs are all screen pixels from the same monitor/DPI coordinate space.
    // Keeping monitor choice outside this helper makes adjacent displays and
    // negative desktop origins irrelevant to the placement calculation.
    internal static class MenuPopupPlacement
    {
        internal static Point Calculate(Rectangle anchor, Size popup, Rectangle work)
        {
            if (popup.Width < 1 || popup.Height < 1)
                throw new ArgumentOutOfRangeException("popup");
            if (work.Width < 1 || work.Height < 1)
                throw new ArgumentOutOfRangeException("work");

            long right = (long)work.Left + work.Width;
            long bottom = (long)work.Top + work.Height;
            long x = (long)anchor.Left + anchor.Width - popup.Width;
            long below = (long)anchor.Top + anchor.Height;
            long y = below + popup.Height <= bottom ? below : (long)anchor.Top - popup.Height;

            // Oversized menus start at the owner working area's origin; the
            // normal WinForms menu scrolling remains responsible for reachability.
            x = Math.Max(work.Left, Math.Min(x, Math.Max((long)work.Left, right - popup.Width)));
            y = Math.Max(work.Top, Math.Min(y, Math.Max((long)work.Top, bottom - popup.Height)));
            return new Point((int)x, (int)y);
        }
    }
}
