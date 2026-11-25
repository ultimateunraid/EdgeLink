// EdgeLink updater: finds newer GitHub releases, shows their notes on the Edge, and on request
// downloads the setup program, checks it and runs it silently.
// C# 5 syntax (built by the compiler that ships with Windows, see build.cmd).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace EdgeLink
{
    public class Updater
    {
        public const string DefaultFeed = "https://api.github.com/repos/ultimateunraid/EdgeLink/releases?per_page=20";
        public const string ReleasesPage = "https://github.com/ultimateunraid/EdgeLink/releases";
        const string InstallerAppId = "{6C0B6A0E-7C1F-4E0B-9D4E-ED6E11A0C5F1}_is1";

        public string Feed = DefaultFeed;      // UpdateFeed= in EdgeLink.ini (testing or a mirror)
        public string Token;                   // UpdateToken= in EdgeLink.ini (only while the repo is private)
        public Version Current;
        public string ExeDir;
        public string DataDir;
        public Action<string> Log = delegate { };
        public Action<Release> OnAvailable = delegate { };   // called once per newly seen version
        public Action BeforeInstall = delegate { };          // the tray app exits after this
        public bool TestMode;                                // tests only: treat this folder as installed

        public class Release
        {
            public string Tag, Version, Name, Body, Published, HtmlUrl;
            public string AssetName, AssetUrl, AssetApiUrl, Sha256;
            public long Size;
            public bool Prerelease;
        }

        readonly object gate = new object();
        string status = "idle";                // idle, checking, uptodate, available, downloading, verifying, installing, error
        string error;
        int progress;
        DateTime checkedAt;
        List<Release> newer = new List<Release>();
        string announced;

        // ---- pure helpers (tested) ---------------------------------------------------------------

        public static Version ParseTag(string tag)
        {
            string t = (tag ?? "").Trim().TrimStart('v', 'V');
            int dash = t.IndexOf('-'); if (dash >= 0) t = t.Substring(0, dash);
            Version v;
            if (!System.Version.TryParse(t, out v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        public static bool IsNewer(string tag, Version current)
        {
            Version got = ParseTag(tag);
            if (got == null || current == null) return false;
            return got > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o; return d != null && d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        // GitHub's /releases list -> releases newer than `current` that carry a setup program, newest first.
        public static List<Release> ParseReleases(string json, Version current)
        {
            List<Release> list = new List<Release>();
            JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            object root = js.DeserializeObject(json);
            object[] arr = root as object[];
            if (arr == null && root is Dictionary<string, object>) arr = new object[] { root };   // /releases/latest
            if (arr == null) return list;
            foreach (object o in arr)
            {
                Dictionary<string, object> r = o as Dictionary<string, object>;
                if (r == null) continue;
                if (Str(r, "draft") == "True" || Str(r, "prerelease") == "True") continue;
                string tag = Str(r, "tag_name");
                if (!IsNewer(tag, current)) continue;
                Release rel = new Release
                {
                    Tag = tag, Version = ParseTag(tag).ToString(3), Name = Str(r, "name") ?? tag, Body = Str(r, "body") ?? "",
                    Published = Str(r, "published_at") ?? "", HtmlUrl = Str(r, "html_url") ?? ReleasesPage
                };
                object[] assets = null; object ao;
                if (r.TryGetValue("assets", out ao)) assets = ao as object[];
                foreach (object a in assets ?? new object[0])
                {
                    Dictionary<string, object> ad = a as Dictionary<string, object>;
                    string name = Str(ad, "name") ?? "";
                    if (!name.StartsWith("EdgeLink-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    rel.AssetName = name;
                    rel.AssetUrl = Str(ad, "browser_download_url");
                    rel.AssetApiUrl = Str(ad, "url");
                    long size; long.TryParse(Str(ad, "size"), out size); rel.Size = size;
                    string digest = Str(ad, "digest");               // "sha256:<hex>" on assets uploaded since mid-2025
                    if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) rel.Sha256 = digest.Substring(7).ToLowerInvariant();
                }
                if (rel.AssetName == null) continue;
                list.Add(rel);
            }
            list.Sort(delegate (Release x, Release y) { return ParseTag(y.Tag).CompareTo(ParseTag(x.Tag)); });
            return list;
        }

        // Downloads only from GitHub (or from the host of a custom feed set in EdgeLink.ini)
        public static bool AllowedUrl(string url, string feed)
        {
            Uri u; if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return false;
            if (feed == null || feed == DefaultFeed)
                return u.Scheme == "https" && (u.Host == "github.com" || u.Host == "api.github.com");
            Uri f; if (!Uri.TryCreate(feed, UriKind.Absolute, out f)) return false;
            return u.Scheme == f.Scheme && u.Host == f.Host && u.Port == f.Port;
        }

        // ---- state for the widget ------------------------------------------------------------------

        static string J(string s) { return Json.Esc(s); }

        public string StateJson()
        {
            lock (gate)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"current\":").Append(J(Current == null ? "" : Current.ToString(3)));
                sb.Append(",\"status\":").Append(J(status));
                sb.Append(",\"error\":").Append(J(error));
                sb.Append(",\"progress\":").Append(progress);
                sb.Append(",\"checkedAt\":").Append(J(checkedAt == DateTime.MinValue ? null : checkedAt.ToUniversalTime().ToString("o")));
                sb.Append(",\"canInstall\":").Append(CanInstall() ? "true" : "false");
                sb.Append(",\"installHint\":").Append(J(CanInstall() ? null : "This copy of EdgeLink was not installed by the setup program, so updates open the download page instead."));
                sb.Append(",\"page\":").Append(J(newer.Count > 0 ? newer[0].HtmlUrl : ReleasesPage));
                sb.Append(",\"releases\":[");
                for (int i = 0; i < newer.Count; i++)
                {
                    Release r = newer[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"version\":").Append(J(r.Version)).Append(",\"tag\":").Append(J(r.Tag)).Append(",\"name\":").Append(J(r.Name))
                      .Append(",\"published\":").Append(J(r.Published)).Append(",\"notes\":").Append(J(r.Body))
                      .Append(",\"url\":").Append(J(r.HtmlUrl)).Append(",\"size\":").Append(r.Size)
                      .Append(",\"verified\":").Append(r.Sha256 != null ? "true" : "false").Append('}');
                }
                sb.Append("]}");
                return sb.ToString();
            }
        }

        // Installed by the setup program into this folder? (Then the setup can update it in place.)
        public bool CanInstall()
        {
            if (TestMode) return true;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + InstallerAppId))
                {
                    string loc = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(loc)) return false;
                    return string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), Path.GetFullPath(ExeDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        void Set(string s, string err, int pct) { lock (gate) { status = s; error = err; progress = pct; } }
        bool Busy() { lock (gate) { return status == "checking" || status == "downloading" || status == "verifying" || status == "installing"; } }

        // ---- check ---------------------------------------------------------------------------------

        HttpWebRequest Request(string url, string accept)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "EdgeLink/" + (Current == null ? "0" : Current.ToString(3)) + " (update)";
            req.Accept = accept;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 60000;
            if (!string.IsNullOrEmpty(Token) && new Uri(url).Host.EndsWith("github.com")) req.Headers["Authorization"] = "Bearer " + Token;
            return req;
        }

        public void Check()
        {
            if (Busy()) return;
            Set("checking", null, 0);
            try
            {
                string body;
                using (WebResponse resp = Request(Feed, "application/vnd.github+json").GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    body = sr.ReadToEnd();
                List<Release> list = ParseReleases(body, Current);
                Release top = null;
                lock (gate)
                {
                    newer = list; checkedAt = DateTime.Now;
                    status = list.Count > 0 ? "available" : "uptodate"; error = null;
                    if (list.Count > 0 && announced != list[0].Version) { announced = list[0].Version; top = list[0]; }
                }
                if (top != null) { Log("Update available: " + top.Tag); OnAvailable(top); }
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                string why = r != null && r.StatusCode == HttpStatusCode.NotFound
                    ? "GitHub has no public releases for EdgeLink yet (the repository may still be private)."
                    : "Could not reach GitHub: " + ex.Message;
                Log("Update check failed: " + why);
                lock (gate) { status = "error"; error = why; checkedAt = DateTime.Now; }
            }
            catch (Exception ex)
            {
                Log("Update check failed: " + ex.Message);
                lock (gate) { status = "error"; error = "Update check failed: " + ex.Message; checkedAt = DateTime.Now; }
            }
        }

        // ---- install -------------------------------------------------------------------------------

        // Starts the download and install on a worker thread. Returns a JSON answer for /update/install.
        public string StartInstall(string version)
        {
            Release r;
            lock (gate)
            {
                if (status == "downloading" || status == "verifying" || status == "installing") return "{\"ok\":true,\"status\":" + J(status) + "}";
                r = newer.FirstOrDefault();
                if (r == null) return "{\"ok\":false,\"error\":\"No update is available.\"}";
                if (!string.IsNullOrEmpty(version) && version != r.Version) return "{\"ok\":false,\"error\":\"That version is no longer the latest; check again.\"}";
            }
            if (!CanInstall()) return "{\"ok\":false,\"error\":\"This copy was not installed by the setup program. Download the update from GitHub instead.\"}";
            Set("downloading", null, 0);
            Thread t = new Thread(delegate () { Install(r); }) { IsBackground = true, Name = "EdgeLink update" };
            t.Start();
            return "{\"ok\":true,\"status\":\"downloading\"}";
        }

        void Install(Release r)
        {
            try
            {
                string url = !string.IsNullOrEmpty(Token) && r.AssetApiUrl != null ? r.AssetApiUrl : r.AssetUrl;
                if (!AllowedUrl(url, Feed)) throw new Exception("refusing to download from " + url);
                string dir = Path.Combine(DataDir, "updates");
                Directory.CreateDirectory(dir);
                foreach (string old in Directory.GetFiles(dir, "EdgeLink-Setup-*.exe")) { try { File.Delete(old); } catch { } }
                string file = Path.Combine(dir, Path.GetFileName(r.AssetName));
                string part = file + ".part";

                HttpWebRequest req = Request(url, url == r.AssetApiUrl ? "application/octet-stream" : "*/*");
                req.AllowAutoRedirect = true;    // GitHub redirects release downloads to its file storage
                using (WebResponse resp = req.GetResponse())
                using (Stream src = resp.GetResponseStream())
                using (FileStream dst = File.Create(part))
                {
                    long total = resp.ContentLength > 0 ? resp.ContentLength : r.Size, got = 0;
                    byte[] buf = new byte[81920]; int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        dst.Write(buf, 0, n); got += n;
                        if (total > 0) Set("downloading", null, (int)Math.Min(99, got * 100 / total));
                        if (got > 300L * 1024 * 1024) throw new Exception("download is unexpectedly large");
                    }
                }
                Set("verifying", null, 100);
                if (r.Size > 0 && new FileInfo(part).Length != r.Size) throw new Exception("download is incomplete (size does not match GitHub)");
                if (r.Sha256 != null)
                {
                    string hash;
                    using (FileStream fs = File.OpenRead(part)) using (SHA256 sha = SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                    if (hash != r.Sha256) throw new Exception("download does not match the checksum GitHub published");
                }
                else Log("Update: GitHub gave no checksum for " + r.AssetName + "; size check only.");
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);

                string why = Signature.CheckUpdate(file, Path.Combine(ExeDir, "EdgeLink.exe"));
                if (why != null) throw new Exception(why);

                Set("installing", null, 100);
                Log("Update: running " + file);
                string logFile = Path.Combine(dir, "setup-" + r.Version + ".log");
                // /EDGELINKUPDATE tells the setup to start EdgeLink again when it is done (see EdgeLink.iss)
                Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /EDGELINKUPDATE /LOG=\"" + logFile + "\"")
                    { UseShellExecute = false, WorkingDirectory = dir });
                Thread.Sleep(1500);              // let the widget see "installing" before the setup closes us
                BeforeInstall();
            }
            catch (Exception ex)
            {
                Log("Update failed: " + ex.Message);
                Set("error", "Update failed: " + ex.Message, 0);
            }
        }
    }

    // Authenticode rules for updates: once EdgeLink itself is signed, an update must be signed by the same publisher.
    public static class Signature
    {
        public static string CheckUpdate(string setupPath, string runningExe)
        {
            string mine = Publisher(runningExe);
            if (mine == null) return null;                       // unsigned build (development): nothing to compare
            if (!Trusted(setupPath)) return "the update is not signed with a trusted certificate";
            string theirs = Publisher(setupPath);
            if (theirs != mine) return "the update is signed by a different publisher (" + (theirs ?? "none") + ")";
            return null;
        }

        // Subject of a valid Authenticode signature, or null
        public static string Publisher(string path)
        {
            if (!File.Exists(path) || !Trusted(path)) return null;
            try { return new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).Subject; }
            catch { return null; }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfoW { public int cbStruct; public string path; public IntPtr hFile; public IntPtr known; }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public int cbStruct; public IntPtr policy; public IntPtr sip; public int ui; public int revocation; public int choice;
            public IntPtr file; public int action; public IntPtr state; public IntPtr url; public int provFlags; public int context; public IntPtr sig;
        }

        public static bool Trusted(string path)
        {
            Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            FileInfoW fi = new FileInfoW { cbStruct = Marshal.SizeOf(typeof(FileInfoW)), path = path };
            IntPtr pfi = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
            IntPtr ptd = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fi, pfi, false);
                TrustData td = new TrustData { cbStruct = Marshal.SizeOf(typeof(TrustData)), ui = 2 /* none */, revocation = 0, choice = 1 /* file */, file = pfi, action = 0 };
                ptd = Marshal.AllocHGlobal(Marshal.SizeOf(td));
                Marshal.StructureToPtr(td, ptd, false);
                return WinVerifyTrust(IntPtr.Zero, action, ptd) == 0;      // state action IGNORE: nothing to close
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pfi); if (ptd != IntPtr.Zero) Marshal.FreeHGlobal(ptd); }
        }
    }
}
