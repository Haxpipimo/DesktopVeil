using System;
using System.Drawing;

namespace DesktopVeil
{
    internal enum DockSide { Left, Right }

    // Geometry and idle timing are independent of Windows input and Explorer state.
    internal sealed class EdgeDock
    {
        internal const int HideDelay = 1000;
        internal DockSide Side { get; private set; }
        internal bool Collapsed { get; private set; }
        internal bool Initialized { get; private set; }
        internal Rectangle ExpandedBounds { get; private set; }
        private int peekWidth;
        private long lastActivity;

        internal Rectangle Bounds
        {
            get
            {
                Rectangle b = ExpandedBounds;
                if (!Collapsed) return b;
                // Shrink within the monitor instead of moving onto an adjacent display.
                return new Rectangle(Side == DockSide.Left ? b.Left : b.Right - peekWidth,
                    b.Top, peekWidth, b.Height);
            }
        }

        internal void Configure(Rectangle area, int diameter, int peek, Point position, DockSide? preferred, long now)
        {
            int size = Math.Max(1, Math.Min(diameter, Math.Min(area.Width, area.Height)));
            Side = preferred ?? ((position.X + diameter / 2.0 < area.Left + area.Width / 2.0) ? DockSide.Left : DockSide.Right);
            int y = Math.Max(area.Top, Math.Min(position.Y, area.Bottom - size));
            ExpandedBounds = new Rectangle(Side == DockSide.Left ? area.Left : area.Right - size, y, size, size);
            peekWidth = Math.Max(1, Math.Min(peek, size));
            Collapsed = false;
            Initialized = true;
            lastActivity = now;
        }

        internal void Expand(long now) { Collapsed = false; lastActivity = now; }

        internal void Update(long now, bool pointerInside, bool suppress)
        {
            if (!Initialized) return;
            if (pointerInside) { Expand(now); return; }
            if (suppress) { lastActivity = now; return; }
            if (now - lastActivity >= HideDelay) Collapsed = true;
        }
    }
}
