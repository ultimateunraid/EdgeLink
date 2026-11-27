using System;
using System.Drawing;

namespace EdgeLinkNative {
    // Pure geometry, no process access or game data.
    public static class Geometry {
        public static Rectangle Canvas(Rectangle primary, Rectangle edge) {
            if (primary.Width <= 0 || primary.Height <= 0) throw new ArgumentException("Invalid primary");
            if (edge.Width != 2560 || edge.Height != 720) throw new ArgumentException("Expected landscape Xeneon Edge");
            if (primary.IntersectsWith(edge)) throw new ArgumentException("Displays overlap");
            return Rectangle.Union(primary, edge);
        }
        public static Rectangle Relative(Rectangle display, Rectangle canvas) {
            if (!canvas.Contains(display)) throw new ArgumentException("Display outside canvas");
            return new Rectangle(display.X-canvas.X, display.Y-canvas.Y, display.Width, display.Height);
        }
    }
}
