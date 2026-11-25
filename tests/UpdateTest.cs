// Update flow test against a fake GitHub on localhost: check, notes JSON, download, checksum, setup launch.
// Built and run by tests\update_test.ps1 (with ..\companion\Updater.cs and EdgeLinkCore.cs).
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using EdgeLink;

static class UpdateTest
{
    static int fails;
    static void Ok(bool cond, string what) { Console.WriteLine((cond ? "  ok   " : "  FAIL ") + what); if (!cond) fails++; }

    static int Main(string[] args)
    {
        string setupExe = args[0];                       // dummy "setup" that records its arguments
        string work = Path.Combine(Path.GetTempPath(), "edgelink-update-test");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        byte[] setup = File.ReadAllBytes(setupExe);
        string sha = BitConverter.ToString(SHA256.Create().ComputeHash(setup)).Replace("-", "").ToLowerInvariant();
        string badSha = new string('0', 64);
        const string Base = "http://localhost:8899";

        Func<string, string, string> rel = delegate (string tag, string extra)
        {
            return "{\"tag_name\":\"" + tag + "\",\"name\":\"EdgeLink " + tag.TrimStart('v') + "\",\"draft\":false,\"prerelease\":false," +
                   "\"published_at\":\"2026-12-01T10:00:00Z\",\"html_url\":\"https://github.com/ultimateunraid/EdgeLink/releases/tag/" + tag + "\"," +
                   "\"body\":\"### New\\n- Thing in " + tag + "\\n- **Bold** `code`\"," + extra + "}";
        };
        Func<string, string, string> asset = delegate (string name, string digest)
        {
            return "\"assets\":[{\"name\":\"" + name + "\",\"size\":" + setup.Length + ",\"browser_download_url\":\"" + Base + "/dl/" + name + "\"," +
                   "\"url\":\"" + Base + "/api/" + name + "\",\"digest\":\"sha256:" + digest + "\"}]";
        };
        string feedOk = "[" + string.Join(",", new[] {
            rel("v1.9.0", asset("EdgeLink-Setup-1.9.0.exe", sha)),
            rel("v2.0.0-beta", "\"assets\":[]").Replace("\"prerelease\":false", "\"prerelease\":true"),
            rel("v1.8.5", asset("EdgeLink-Setup-1.8.5.exe", sha)),
            rel("v1.8.9", "\"assets\":[{\"name\":\"notes.txt\",\"size\":1}]"),          // no setup program: ignored
            rel("v1.7.0", asset("EdgeLink-Setup-1.7.0.exe", sha)),                       // older: ignored
        }) + "]";
        string feedBad = "[" + rel("v1.9.0", asset("EdgeLink-Setup-1.9.0.exe", badSha)) + "]";
        string feed = feedOk;

        HttpListener http = new HttpListener();
        http.Prefixes.Add(Base + "/");
        http.Start();
        new Thread(delegate ()
        {
            while (http.IsListening)
            {
                HttpListenerContext c;
                try { c = http.GetContext(); } catch { return; }
                byte[] body = c.Request.Url.AbsolutePath.StartsWith("/dl/") ? setup : Encoding.UTF8.GetBytes(feed);
                c.Response.OutputStream.Write(body, 0, body.Length);
                c.Response.Close();
            }
        }) { IsBackground = true }.Start();

        Console.WriteLine("pure helpers");
        Ok(Updater.IsNewer("v1.9.0", new Version(1, 8, 0, 0)), "v1.9.0 newer than 1.8.0.0");
        Ok(!Updater.IsNewer("v1.8.0", new Version(1, 8, 0, 0)), "same version not newer");
        Ok(Updater.AllowedUrl("https://github.com/ultimateunraid/EdgeLink/releases/download/v1/x.exe", Updater.DefaultFeed), "github download allowed");
        Ok(!Updater.AllowedUrl("https://evil.example.com/x.exe", Updater.DefaultFeed), "other host refused");
        Ok(!Updater.AllowedUrl("http://github.com/x.exe", Updater.DefaultFeed), "plain http refused");
        Ok(Updater.AllowedUrl(Base + "/dl/x.exe", Base + "/releases"), "custom feed host allowed");

        Console.WriteLine("check");
        bool exited = false;
        Updater u = new Updater
        {
            Feed = Base + "/releases", Current = new Version(1, 8, 0, 0), ExeDir = work, DataDir = work, TestMode = true,
            Log = delegate (string s) { Console.WriteLine("    log: " + s); }, BeforeInstall = delegate { exited = true; }
        };
        string announced = null;
        u.OnAvailable = delegate (Updater.Release r) { announced = r.Version; };
        u.Check();
        string st = u.StateJson();
        Ok(st.Contains("\"status\":\"available\""), "status available");
        Ok(announced == "1.9.0", "announced newest (1.9.0), got " + announced);
        Ok(st.IndexOf("\"version\":\"1.9.0\"") >= 0 && st.IndexOf("\"version\":\"1.8.5\"") > st.IndexOf("\"version\":\"1.9.0\""), "1.9.0 then 1.8.5, newest first");
        Ok(!st.Contains("1.8.9") && !st.Contains("2.0.0") && !st.Contains("\"version\":\"1.7.0\""), "skips no-setup, prerelease and older");
        Ok(st.Contains("Thing in v1.9.0") && st.Contains("\"verified\":true"), "notes and checksum carried");

        Console.WriteLine("install");
        string argsFile = Path.Combine(work, "updates", "setup-args.txt");
        string ans = u.StartInstall("1.9.0");
        Ok(ans.Contains("\"ok\":true"), "install started: " + ans);
        for (int i = 0; i < 100 && !exited; i++) Thread.Sleep(100);
        Ok(exited, "app asked to exit for the setup");
        for (int i = 0; i < 50 && !File.Exists(argsFile); i++) Thread.Sleep(100);
        string got = File.Exists(argsFile) ? File.ReadAllText(argsFile) : "";
        Ok(got.Contains("/SILENT") && got.Contains("/EDGELINKUPDATE"), "setup ran silently with /EDGELINKUPDATE: " + got.Trim());
        Ok(u.StateJson().Contains("\"status\":\"installing\""), "status installing");

        Console.WriteLine("tampered download");
        feed = feedBad;
        Updater u2 = new Updater { Feed = Base + "/releases", Current = new Version(1, 8, 0, 0), ExeDir = work, DataDir = Path.Combine(work, "b"), TestMode = true };
        u2.Check();
        Ok(u2.StartInstall("1.9.0").Contains("\"ok\":true"), "install started");
        for (int i = 0; i < 100 && !u2.StateJson().Contains("\"status\":\"error\""); i++) Thread.Sleep(100);
        string st2 = u2.StateJson();
        Ok(st2.Contains("\"status\":\"error\"") && st2.Contains("checksum"), "checksum mismatch stops the install");
        Ok(!File.Exists(Path.Combine(work, "b", "updates", "setup-args.txt")), "bad setup never ran");
        Ok(u2.StartInstall("1.8.0").Contains("no longer the latest"), "stale version refused");

        Console.WriteLine("not installed by setup");
        Updater u3 = new Updater { Feed = Base + "/releases", Current = new Version(1, 8, 0, 0), ExeDir = work, DataDir = work };
        feed = feedOk; u3.Check();
        Ok(u3.StateJson().Contains("\"canInstall\":false"), "dev copy cannot self-install");
        Ok(u3.StartInstall("1.9.0").Contains("\"ok\":false"), "install refused for a dev copy");

        http.Stop();
        Console.WriteLine(fails == 0 ? "UPDATE OK" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
