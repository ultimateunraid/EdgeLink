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
        static int Main(string[] args)
        {
            // Setup helpers: run by the installer and uninstaller, no tray icon and no single-instance check.
            if (args.Length > 0)
            {
                string a = args[0].ToLowerInvariant();
                if (a == "--install-addon" || a == "--uninstall-addon")
                {
                    StringBuilder report = new StringBuilder();
                    int done = a == "--install-addon" ? TrayContext.InstallAddonFiles(report) : TrayContext.RemoveAddonFiles(report);
                    Log(a + ": " + report.ToString().Replace(Environment.NewLine, " | "));
                    // 0 = at least one game folder handled, 2 = nothing found or copied
                    return done > 0 ? 0 : 2;
                }
            }
            RunTray();
            return 0;
        }

        static void RunTray()
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
        public bool CheckUpdates = true;
        public int IntervalMs = 33;
        public string UpdateFeed;                // testing / mirror only (https); normally empty

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
                else if (k == "checkupdates") s.CheckUpdates = !(v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0");
                else if (k == "intervalms" && int.TryParse(v, out n) && n >= 10 && n <= 1000) s.IntervalMs = n;
                else if (k == "updatefeed" && v.Length > 0) s.UpdateFeed = v;
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
        readonly ToolStripMenuItem statusItem, startupItem, inputItem, updateItem;
        readonly ToolStripSeparator updateSep;
        readonly System.Windows.Forms.Timer timer;
        System.Threading.Timer updateTimer;
        SynchronizationContext ui;
        readonly Updater updater;
        Updater.Release updateRelease;

        public TrayContext()
        {
            settings = Settings.Load();
            Directory.CreateDirectory(App.DataDir);
            App.Log("Starting EdgeLink " + Application.ProductVersion + " from " + App.ExeDir + " on port " + settings.Port);

            bridge = new Bridge();
            bridge.AppVersion = Application.ProductVersion;
            bridge.Log = App.Log;
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
            updater = new Updater
            {
                Current = new Version(Application.ProductVersion), ExeDir = App.ExeDir, DataDir = App.DataDir,
                Feed = settings.UpdateFeed ?? Updater.DefaultFeed, Log = App.Log,
                ConfirmInstall = ConfirmUpdate
            };
            bridge.UpdateJson = updater.StateJson;
            bridge.UpdateAction = delegate (string action, string version)
            {
                if (action == "install") return updater.StartInstall(version);
                ThreadPool.QueueUserWorkItem(delegate { updater.Check(); });
                return "{\"ok\":true,\"status\":\"checking\"}";
            };
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

            updateItem = new ToolStripMenuItem("Update available") { Visible = false };
            updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
            updateItem.Click += delegate { OfferUpdate(); };
            updateSep = new ToolStripSeparator { Visible = false };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(updateItem);
            menu.Items.Add(updateSep);
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

            // Update check: GitHub's latest release, a minute after start and then twice a day (CheckUpdates=false turns it off)
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            updater.OnAvailable = delegate (Updater.Release r)
            {
                ui.Post(delegate
                {
                    updateRelease = r;
                    updateItem.Text = "Update available: " + r.Tag;
                    updateItem.Visible = updateSep.Visible = true;
                    tray.ShowBalloonTip(8000, "EdgeLink " + r.Tag + " is available",
                        "See what's new and upgrade from the version tag on the Edge, or right-click this icon.", ToolTipIcon.Info);
                }, null);
            };
            updater.BeforeInstall = delegate { ui.Post(delegate { ExitThread(); }, null); };
            if (settings.CheckUpdates)
                updateTimer = new System.Threading.Timer(delegate { updater.Check(); }, null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));

            string firstRun = Path.Combine(App.DataDir, "firstrun.done");
            if (!File.Exists(firstRun))
            {
                tray.ShowBalloonTip(6000, "EdgeLink is running",
                    "It lives here in the system tray. Right-click for options, such as installing the WoW addon and the iCUE widget.", ToolTipIcon.Info);
                try { File.WriteAllText(firstRun, DateTime.Now.ToString("o")); } catch { }
            }
        }

        // Kept for the existing test: "v1.7.0" newer than the running 1.6.0.0?
        internal static bool IsNewer(string tag, Version current) { return Updater.IsNewer(tag, current); }

        // Every install, whether started from the widget or the tray, is approved here on the desktop.
        // (Web pages can reach the local bridge, so a request from the widget is never enough by itself.)
        static bool ConfirmUpdate(Updater.Release r)
        {
            string notes = (r.Body ?? "").Trim();
            if (notes.Length > 1500) notes = notes.Substring(0, 1500) + "...";
            return MessageBox.Show("Install EdgeLink " + r.Tag + "?\n\n" + notes +
                "\n\nEdgeLink downloads the setup program from GitHub, checks its checksum and code signature, then installs it and restarts. /reload in WoW afterwards.",
                "EdgeLink update", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
                MessageBoxOptions.DefaultDesktopOnly) == DialogResult.Yes;
        }

        // Tray menu: install (the updater asks for confirmation), or open the download page for a copy that cannot self-update
        void OfferUpdate()
        {
            Updater.Release r = updateRelease;
            if (r == null) { OpenUrl(Updater.ReleasesPage); return; }
            string notes = (r.Body ?? "").Trim();
            if (notes.Length > 1500) notes = notes.Substring(0, 1500) + "...";
            if (!updater.CanInstall())
            {
                if (MessageBox.Show("EdgeLink " + r.Tag + " is available.\n\n" + notes + "\n\nOpen the download page?", "EdgeLink update",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenUrl(r.HtmlUrl);
                return;
            }
            App.Log("Update from tray: " + updater.StartInstall(r.Version));
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

        // Copies addon\EdgeLink into every WoW flavor found. Returns how many copies succeeded; -1 = addon files missing.
        internal static int InstallAddonFiles(StringBuilder report)
        {
            string src = Path.Combine(App.ExeDir, "addon", "EdgeLink");
            if (!Directory.Exists(src)) { report.AppendLine("Addon files not found next to EdgeLink.exe"); return -1; }
            int ok = 0;
            foreach (string flavor in FindWowFlavors())
            {
                string dst = Path.Combine(flavor, "Interface", "AddOns", "EdgeLink");
                try { CopyDir(src, dst); ok++; report.AppendLine("Installed:  " + dst); }
                catch (Exception ex) { report.AppendLine("Failed:  " + dst + "\n   " + ex.Message); }
            }
            return ok;
        }

        // Removes Interface\AddOns\EdgeLink from every WoW flavor found (saved variables are left alone).
        internal static int RemoveAddonFiles(StringBuilder report)
        {
            int ok = 0;
            foreach (string flavor in FindWowFlavors())
            {
                string dst = Path.Combine(flavor, "Interface", "AddOns", "EdgeLink");
                if (!Directory.Exists(dst)) continue;
                try { Directory.Delete(dst, true); ok++; report.AppendLine("Removed:  " + dst); }
                catch (Exception ex) { report.AppendLine("Failed:  " + dst + "\n   " + ex.Message); }
            }
            return ok;
        }

        void InstallAddon()
        {
            StringBuilder report = new StringBuilder();
            int n = InstallAddonFiles(report);
            App.Log("Addon install: " + report.ToString().Replace(Environment.NewLine, " | "));
            if (n < 0)
            {
                MessageBox.Show("The addon files were not found next to EdgeLink.exe (expected addon\\EdgeLink).", "EdgeLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (report.Length == 0)
            {
                MessageBox.Show("No World of Warcraft installation was found. Copy the addon\\EdgeLink folder into your game's Interface\\AddOns folder by hand.", "EdgeLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
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
            if (updateTimer != null) updateTimer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            bridge.Stop();
            base.ExitThreadCore();
        }
    }
}
