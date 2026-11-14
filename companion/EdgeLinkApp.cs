// EdgeLink companion: Windows tray app. Built with the .NET Framework compiler (see build.cmd);
// C# 5 syntax so the compiler that ships with Windows can build it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace EdgeLink
{
    static class App
    {
        public static string ExeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeLink");
        static readonly object logLock = new object();

        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(DataDir);
                    string path = Path.Combine(DataDir, "edgelink.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }

        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex m = new Mutex(true, "Local\\EdgeLinkCompanion", out created))
            {
                if (!created)
                {
                    MessageBox.Show("EdgeLink is already running. Look for its icon in the system tray.", "EdgeLink",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Native.MakeDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log("UI error: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log("Fatal: " + e.ExceptionObject); };
                TrayContext ctx;
                try { ctx = new TrayContext(); }
                catch (Exception ex)
                {
                    Log("Startup failed: " + ex);
                    MessageBox.Show("EdgeLink could not start:\n\n" + ex.Message, "EdgeLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Application.Run(ctx);
            }
        }
    }

    // EdgeLink.ini next to the exe:  Port=8787  ProcessNames=Wow  Icons=true  IntervalMs=33
    class Settings
    {
        public int Port = 8787;
        public string[] ProcessNames = new string[] { "Wow" };
        public bool Icons = true;
        public int IntervalMs = 33;

        public static Settings Load()
        {
            Settings s = new Settings();
            string path = Path.Combine(App.ExeDir, "EdgeLink.ini");
            if (!File.Exists(path)) return s;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                int n;
                if (k == "port" && int.TryParse(v, out n) && n > 0 && n < 65536) s.Port = n;
                else if (k == "processnames" && v.Length > 0) s.ProcessNames = v.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
                else if (k == "icons") s.Icons = !(v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0");
                else if (k == "intervalms" && int.TryParse(v, out n) && n >= 10 && n <= 1000) s.IntervalMs = n;
            }
            return s;
        }
    }

    class TrayContext : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly Settings settings;
        readonly Bridge bridge;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem, startupItem, inputItem;
        readonly System.Windows.Forms.Timer timer;

        public TrayContext()
        {
            settings = Settings.Load();
            Directory.CreateDirectory(App.DataDir);
            App.Log("Starting EdgeLink " + Application.ProductVersion + " from " + App.ExeDir + " on port " + settings.Port);

            bridge = new Bridge();
            bridge.Port = settings.Port;
            bridge.ProcessNames = settings.ProcessNames;
            bridge.IntervalMs = settings.IntervalMs;
            bridge.WidgetDir = Path.Combine(App.ExeDir, "widget");
            if (settings.Icons)
            {
                bridge.Icons = new IconCache(Path.Combine(App.DataDir, "cache", "icons"));
                bridge.Maps = new MapCache(Path.Combine(App.DataDir, "cache", "maps"));
            }
            bridge.Trails = new TrailStore(Path.Combine(App.DataDir, "trails"));
            bridge.Chars = new CharStore(Path.Combine(App.DataDir, "chars"));
            bridge.Certificate = FindCertificate();
            bridge.AllowInput = ReadAllowInput();
            try { bridge.Start(); }
            catch (System.Net.Sockets.SocketException ex)
            {
                throw new Exception("Port " + settings.Port + " is already in use (" + ex.Message + "). Close the other EdgeLink (for example the old PowerShell window) or set Port= in EdgeLink.ini.");
            }

            statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            inputItem = new ToolStripMenuItem("Let widget taps control WoW", null, delegate { ToggleInput(); });
            inputItem.Checked = bridge.AllowInput;
            inputItem.ToolTipText = "Taps on the Edge can track quests and set map waypoints in WoW (one tap = one action).";
            startupItem.Checked = IsStartupEnabled();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open widget in browser", null, delegate { OpenUrl("http://localhost:" + settings.Port + "/"); }));
            menu.Items.Add(new ToolStripMenuItem("Install widget in iCUE", null, delegate { InstallWidget(); }));
            menu.Items.Add(new ToolStripMenuItem("Install / update WoW addon", null, delegate { InstallAddon(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(inputItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripMenuItem("Open log folder", null, delegate { OpenUrl(App.DataDir); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));

            Icon icon;
            try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icon = SystemIcons.Application; }
            tray = new NotifyIcon { Icon = icon, Text = "EdgeLink", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { OpenUrl("http://localhost:" + settings.Port + "/"); };

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate { UpdateStatus(); };
            timer.Start();
            UpdateStatus();

            string firstRun = Path.Combine(App.DataDir, "firstrun.done");
            if (!File.Exists(firstRun))
            {
                tray.ShowBalloonTip(6000, "EdgeLink is running",
                    "It lives here in the system tray. Right-click for options, such as installing the WoW addon and the iCUE widget.", ToolTipIcon.Info);
                try { File.WriteAllText(firstRun, DateTime.Now.ToString("o")); } catch { }
            }
        }

        static X509Certificate2 FindCertificate()
        {
            try
            {
                X509Store store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2 best = null;
                foreach (X509Certificate2 c in store.Certificates)
                {
                    if (c.FriendlyName == "EdgeLink localhost" && c.HasPrivateKey && c.NotAfter > DateTime.Now && (best == null || c.NotAfter > best.NotAfter))
                        best = c;
                }
                store.Close();
                return best;
            }
            catch (Exception ex) { App.Log("Certificate lookup failed: " + ex.Message); return null; }
        }

        void UpdateStatus()
        {
            string s;
            double age = bridge.SnapshotAgeSeconds;
            if (!bridge.WowFound) s = "WoW not running";
            else if (age < 0) s = "WoW found - waiting for the EdgeLink addon";
            else if (age > 10) s = "Paused (no update for " + (int)age + " s)";
            else s = "Live";
            statusItem.Text = "Status: " + s;
            string tip = "EdgeLink - " + s;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        static void OpenUrl(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log("Open failed for " + target + ": " + ex.Message); }
        }

        void InstallWidget()
        {
            string pkg = Directory.GetFiles(App.ExeDir, "*.icuewidget").FirstOrDefault();
            if (pkg == null)
            {
                MessageBox.Show("No .icuewidget file was found next to EdgeLink.exe.", "EdgeLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            OpenUrl(pkg); // iCUE handles .icuewidget files
        }

        // WoW installs: registry InstallPath plus the default folders; game flavors are the _name_ subfolders.
        static List<string> FindWowFlavors()
        {
            List<string> roots = new List<string>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft"))
                {
                    string p = k == null ? null : k.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(p))
                    {
                        DirectoryInfo d = new DirectoryInfo(p.TrimEnd('\\'));
                        roots.Add(d.Name.StartsWith("_") && d.Parent != null ? d.Parent.FullName : d.FullName);
                    }
                }
            }
            catch { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "World of Warcraft"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "World of Warcraft"));
            List<string> flavors = new List<string>();
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root, "_*_"))
                {
                    if (Directory.Exists(Path.Combine(dir, "Interface")) || Directory.GetFiles(dir, "Wow*.exe").Length > 0)
                        if (!flavors.Contains(dir, StringComparer.OrdinalIgnoreCase)) flavors.Add(dir);
                }
            }
            return flavors;
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        void InstallAddon()
        {
            string src = Path.Combine(App.ExeDir, "addon", "EdgeLink");
            if (!Directory.Exists(src))
            {
                MessageBox.Show("The addon files were not found next to EdgeLink.exe (expected addon\\EdgeLink).", "EdgeLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            List<string> flavors = FindWowFlavors();
            if (flavors.Count == 0)
            {
                MessageBox.Show("No World of Warcraft installation was found. Copy the addon\\EdgeLink folder into your game's Interface\\AddOns folder by hand.", "EdgeLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            StringBuilder report = new StringBuilder();
            foreach (string flavor in flavors)
            {
                string dst = Path.Combine(flavor, "Interface", "AddOns", "EdgeLink");
                try { CopyDir(src, dst); report.AppendLine("Installed:  " + dst); App.Log("Addon installed to " + dst); }
                catch (Exception ex) { report.AppendLine("Failed:  " + dst + "\n   " + ex.Message); App.Log("Addon install failed for " + dst + ": " + ex.Message); }
            }
            report.AppendLine();
            report.AppendLine("Type /reload in game (or restart WoW) to load it.");
            MessageBox.Show(report.ToString(), "EdgeLink addon", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        const string SettingsKey = @"Software\EdgeLink";
        static bool ReadAllowInput()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey)) return k != null && Convert.ToInt32(k.GetValue("AllowInput", 0)) == 1; }
            catch { return false; }
        }

        void ToggleInput()
        {
            bridge.AllowInput = !bridge.AllowInput;
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("AllowInput", bridge.AllowInput ? 1 : 0, RegistryValueKind.DWord);
            inputItem.Checked = bridge.AllowInput;
            App.Log("Widget control of WoW " + (bridge.AllowInput ? "enabled" : "disabled"));
        }

        static bool IsStartupEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("EdgeLink") != null;
        }

        void ToggleStartup()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsStartupEnabled()) k.DeleteValue("EdgeLink", false);
                else k.SetValue("EdgeLink", "\"" + Application.ExecutablePath + "\"");
            }
            startupItem.Checked = IsStartupEnabled();
        }

        protected override void ExitThreadCore()
        {
            App.Log("Exiting");
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            bridge.Stop();
            base.ExitThreadCore();
        }
    }
}
