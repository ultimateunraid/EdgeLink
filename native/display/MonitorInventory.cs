using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
namespace EdgeLinkNative {
    public sealed class MonitorInfo {
        public string DeviceName { get; private set; }
        public Rectangle Bounds { get; private set; }
        public bool Primary { get; private set; }
        public bool EdgeCandidate { get { return Bounds.Width == 2560 && Bounds.Height == 720; } }
        public MonitorInfo(string name, Rectangle bounds, bool primary) {
            DeviceName=name; Bounds=bounds; Primary=primary;
        }
    }
    public static class MonitorInventory {
        // Host must establish per-monitor DPI awareness before calling.
        // Resolution is a candidate filter, not proof of manufacturer/model.
        public static IList<MonitorInfo> Enumerate() {
            var result = new List<MonitorInfo>();
            foreach (var screen in Screen.AllScreens)
                result.Add(new MonitorInfo(screen.DeviceName, screen.Bounds, screen.Primary));
            return result.AsReadOnly();
        }
    }
}
