// EdgeLink companion core. Compiled at runtime by Start-EdgeLink.ps1 (Add-Type).
// Kept to C# 5 syntax so Windows PowerShell 5.1 can compile it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace EdgeLink
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        const uint KEYUP = 0x0002;
        public const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_F1 = 0x70;

        public static void Key(byte vk, bool down)
        {
            keybd_event(vk, (byte)MapVirtualKey(vk, 0), down ? 0 : KEYUP, UIntPtr.Zero);
        }

        public static void MakeDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }
    }

    public class Page
    {
        public int SnapshotId, Index, Count;
        public byte[] Payload;
        // v2 live position
        public bool HasPos;
        public int MapId, Facing, Flags;
        public double X, Y;
    }

    public static class Protocol
    {
        public const int Magic1 = 0xE7, Magic2 = 0x1D, Version = 1;
        public const int Blocks = 160, BlockPx = 4, Header = 10;

        public static int Fletcher16(byte[] data, int start, int len)
        {
            int s1 = 0, s2 = 0;
            for (int i = start; i < start + len; i++)
            {
                s1 = (s1 + data[i]) % 255;
                s2 = (s2 + s1) % 255;
            }
            return s2 * 256 + s1;
        }

        // Returns null when the strip is absent; sets error on a bad checksum or version.
        public static Page Decode(Bitmap bmp, int ox, int oy, out string error)
        {
            error = null;
            int c = BlockPx / 2;
            byte[] d = new byte[Blocks * 3];
            for (int i = 0; i < Blocks; i++)
            {
                int x = ox + i * BlockPx + c, y = oy + c;
                if (x >= bmp.Width || y >= bmp.Height) return null;
                Color px = bmp.GetPixel(x, y);
                d[i * 3] = px.R; d[i * 3 + 1] = px.G; d[i * 3 + 2] = px.B;
                if (i == 0 && (d[0] != Magic1 || d[1] != Magic2)) return null;
            }
            int ver = d[2];
            if (ver != 1 && ver != 2) { error = "version " + ver; return null; }
            int header = ver == 1 ? 10 : 18;
            int plen = d[6] * 256 + d[7];
            if (plen > Blocks * 3 - header) { error = "length"; return null; }
            int want = d[8] * 256 + d[9];
            // v1: checksum covers the payload; v2: position bytes 10..17 + payload
            int sumStart = ver == 1 ? 10 : 10;
            int sumLen = ver == 1 ? plen : 8 + plen;
            if (Fletcher16(d, sumStart, sumLen) != want) { error = "checksum"; return null; }
            Page p = new Page();
            p.SnapshotId = d[3]; p.Index = d[4]; p.Count = d[5];
            p.Payload = new byte[plen];
            Buffer.BlockCopy(d, header, p.Payload, 0, plen);
            if (ver == 2)
            {
                p.HasPos = true;
                p.MapId = d[10] * 256 + d[11];
                p.X = (d[12] * 256 + d[13]) / 65535.0;
                p.Y = (d[14] * 256 + d[15]) / 65535.0;
                p.Facing = d[16];
                p.Flags = d[17];
            }
            return p;
        }
    }

    public class Assembler
    {
        int currentId = -1, currentCount = 0;
        Dictionary<int, byte[]> pages = new Dictionary<int, byte[]>();

        // Returns the full payload text when a snapshot completes, otherwise null.
        public string Add(Page p)
        {
            if (p.SnapshotId != currentId || p.Count != currentCount)
            {
                currentId = p.SnapshotId; currentCount = p.Count; pages.Clear();
            }
            pages[p.Index] = p.Payload;
            if (pages.Count < currentCount) return null;
            MemoryStream ms = new MemoryStream();
            for (int i = 0; i < currentCount; i++)
            {
                byte[] b;
                if (!pages.TryGetValue(i, out b)) return null;
                ms.Write(b, 0, b.Length);
            }
            pages.Clear();
            currentId = -1;
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public static class Json
    {
        public static string Esc(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Num(string s)
        {
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v.ToString(CultureInfo.InvariantCulture);
            return "null";
        }
    }

    // Converts the addon's tab-separated snapshot into JSON for the widget.
    public static class SnapshotParser
    {
        // "12,34" -> "12,34" as JSON numbers (empty parts dropped)
        static string NumList(string csv)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string v in csv.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(Json.Num(v));
            }
            return sb.ToString();
        }

        static string F(string[] f, int i) { return i < f.Length ? f[i] : ""; }

        public static string ToJson(string text)
        {
            StringBuilder player = new StringBuilder("null"), map = new StringBuilder("null");
            List<string> bags = new List<string>();
            List<string> gear = new List<string>();
            List<string> quests = new List<string>();
            List<string> maps = new List<string>();
            List<string> pins = new List<string>();
            List<string> areas = new List<string>();
            List<string> skills = new List<string>();
            List<string> nodes = new List<string>();
            List<string> stats = new List<string>();
            List<string> nearby = new List<string>();
            List<string> friends = new List<string>();
            string addonVersion = "";
            List<string> reps = new List<string>();
            List<string> bank = new List<string>();
            List<string> recipes = new List<string>();
            List<string> tree = new List<string>();
            string session = "null", charName = null, charRealm = "", navJson = "null", polestarJson = "null", statusJson = "null";
            List<string> services = null;
            string caps = null;
            string error = null;
            StringBuilder bagItems = null; string bagHead = null;
            string group = "";

            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                switch (f[0])
                {
                    case "P":
                        charName = F(f, 1); charRealm = F(f, 2);
                        player = new StringBuilder();
                        player.Append("{\"name\":").Append(Json.Esc(F(f, 1)))
                              .Append(",\"realm\":").Append(Json.Esc(F(f, 2)))
                              .Append(",\"level\":").Append(Json.Num(F(f, 3)))
                              .Append(",\"class\":").Append(Json.Esc(F(f, 4)))
                              .Append(",\"race\":").Append(Json.Esc(F(f, 5)))
                              .Append(",\"money\":").Append(Json.Num(F(f, 6)))
                              .Append(",\"zone\":").Append(Json.Esc(F(f, 7)))
                              .Append(",\"subzone\":").Append(Json.Esc(F(f, 8)))
                              .Append(",\"faction\":").Append(Json.Esc(F(f, 9))).Append('}');
                        break;
                    case "S":
                        if (bagHead != null) bags.Add(bagHead + bagItems.ToString() + "]}");
                        bagHead = "{\"bag\":" + Json.Num(F(f, 1)) + ",\"slots\":" + Json.Num(F(f, 2)) +
                                  ",\"free\":" + Json.Num(F(f, 3)) + ",\"name\":" + Json.Esc(F(f, 4)) + ",\"items\":[";
                        bagItems = new StringBuilder();
                        break;
                    case "B":
                        if (bagItems == null) break;
                        if (bagItems.Length > 0) bagItems.Append(',');
                        bagItems.Append("{\"slot\":").Append(Json.Num(F(f, 2)))
                                .Append(",\"id\":").Append(Json.Num(F(f, 3)))
                                .Append(",\"count\":").Append(Json.Num(F(f, 4)))
                                .Append(",\"q\":").Append(Json.Num(F(f, 5)))
                                .Append(",\"name\":").Append(Json.Esc(F(f, 6)))
                                .Append(",\"icon\":").Append(Json.Num(F(f, 7)))
                                .Append(",\"cls\":").Append(Json.Num(F(f, 8)))
                                .Append(",\"sub\":").Append(Json.Num(F(f, 9)))
                                .Append(",\"sell\":").Append(Json.Num(F(f, 10)))
                                .Append(",\"ah\":").Append(Json.Num(F(f, 11))).Append('}');
                        break;
                    case "E":
                        gear.Add("{\"slot\":" + Json.Num(F(f, 1)) + ",\"id\":" + Json.Num(F(f, 2)) +
                                 ",\"q\":" + Json.Num(F(f, 3)) + ",\"name\":" + Json.Esc(F(f, 4)) +
                                 ",\"dur\":" + Json.Num(F(f, 5)) + ",\"durMax\":" + Json.Num(F(f, 6)) +
                                 ",\"icon\":" + Json.Num(F(f, 7)) + "}");
                        break;
                    case "G":
                        group = F(f, 1);
                        break;
                    case "Q":
                        StringBuilder objs = new StringBuilder();
                        string raw = F(f, 5);
                        if (raw.Length > 0)
                        {
                            foreach (string o in raw.Split('\u001f'))
                            {
                                if (o.Length == 0) continue;
                                if (objs.Length > 0) objs.Append(',');
                                objs.Append("{\"done\":").Append(o[0] == '1' ? "true" : "false")
                                    .Append(",\"text\":").Append(Json.Esc(o.Substring(1))).Append('}');
                            }
                        }
                        quests.Add("{\"id\":" + Json.Num(F(f, 1)) + ",\"level\":" + Json.Num(F(f, 2)) +
                                   ",\"complete\":" + Json.Num(F(f, 3)) + ",\"title\":" + Json.Esc(F(f, 4)) +
                                   ",\"group\":" + Json.Esc(group) + ",\"tag\":" + Json.Esc(F(f, 6)) + ",\"objectives\":[" + objs + "]}");
                        break;
                    case "M":
                        // v2: M id widthYards heightYards name mapType minX maxX minY maxY (current map first, then parents)
                        string m = "{\"id\":" + Json.Num(F(f, 1)) +
                                   ",\"yw\":" + Json.Num(F(f, 2)) + ",\"yh\":" + Json.Num(F(f, 3)) +
                                   ",\"name\":" + Json.Esc(F(f, 4)) + ",\"type\":" + Json.Num(F(f, 5)) +
                                   ",\"rect\":" + (F(f, 6).Length > 0 && F(f, 9).Length > 0
                                       ? "[" + Json.Num(F(f, 6)) + "," + Json.Num(F(f, 7)) + "," + Json.Num(F(f, 8)) + "," + Json.Num(F(f, 9)) + "]"
                                       : "null") + "}";
                        if (maps.Count == 0) map = new StringBuilder(m);
                        maps.Add(m);
                        int mid;
                        if (MapNames != null && int.TryParse(F(f, 1), out mid))
                            lock (MapNames) { MapNames[mid] = F(f, 4) + "\t" + F(f, 5); }
                        break;
                    case "N":
                        pins.Add("{\"map\":" + Json.Num(F(f, 1)) + ",\"kind\":" + Json.Esc(F(f, 2)) +
                                 ",\"x\":" + Json.Num(F(f, 3)) + ",\"y\":" + Json.Num(F(f, 4)) +
                                 ",\"name\":" + Json.Esc(F(f, 5)) + ",\"state\":" + Json.Num(F(f, 6)) +
                                 ",\"faction\":" + Json.Num(F(f, 7)) + "}");
                        break;
                    case "Z":
                        caps = F(f, 1);
                        break;
                    case "K":
                        skills.Add("{\"name\":" + Json.Esc(F(f, 1)) + ",\"rank\":" + Json.Num(F(f, 2)) +
                                   ",\"max\":" + Json.Num(F(f, 3)) + ",\"id\":" + Json.Num(F(f, 4)) + "}");
                        break;
                    case "W":
                        StringBuilder pts = new StringBuilder();
                        foreach (string pt in F(f, 3).Split(';'))
                        {
                            string[] v = pt.Split(',');
                            if (v.Length < 3) continue;
                            if (pts.Length > 0) pts.Append(',');
                            pts.Append('[').Append(Json.Num(v[0])).Append(',').Append(Json.Num(v[1])).Append(',').Append(Json.Num(v[2])).Append(']');
                        }
                        nodes.Add("{\"kind\":" + Json.Esc(F(f, 1)) + ",\"name\":" + Json.Esc(F(f, 2)) + ",\"pts\":[" + pts + "]}");
                        break;
                    case "R":
                        areas.Add("{\"quest\":" + Json.Num(F(f, 1)) + ",\"map\":" + Json.Num(F(f, 2)) +
                                  ",\"w\":" + Json.Num(F(f, 3)) + ",\"h\":" + Json.Num(F(f, 4)) +
                                  ",\"runs\":" + Json.Esc(F(f, 5)) + "}");
                        break;
                    case "T":
                        stats.Add("{\"group\":" + Json.Esc(F(f, 1)) + ",\"label\":" + Json.Esc(F(f, 2)) +
                                  ",\"value\":" + Json.Esc(F(f, 3)) + ",\"color\":" + Json.Esc(F(f, 4)) + "}");
                        break;

                    case "Y":
                        session = "{\"start\":" + Json.Num(F(f, 1)) + ",\"xp\":" + Json.Num(F(f, 2)) + ",\"xpMax\":" + Json.Num(F(f, 3)) +
                                  ",\"rested\":" + Json.Num(F(f, 4)) + ",\"xpGained\":" + Json.Num(F(f, 5)) + ",\"moneyStart\":" + Json.Num(F(f, 6)) +
                                  ",\"herbs\":" + Json.Num(F(f, 7)) + ",\"ore\":" + Json.Num(F(f, 8)) + ",\"fish\":" + Json.Num(F(f, 9)) +
                                  ",\"chests\":" + Json.Num(F(f, 10)) + ",\"fps\":" + Json.Num(F(f, 11)) + ",\"ms\":" + Json.Num(F(f, 12)) +
                                  ",\"durLow\":" + Json.Num(F(f, 13)) + "}";
                        break;
                    case "V":
                        nearby.Add("{\"name\":" + Json.Esc(F(f, 1)) + ",\"map\":" + Json.Num(F(f, 2)) + ",\"x\":" + Json.Num(F(f, 3)) +
                                   ",\"y\":" + Json.Num(F(f, 4)) + ",\"atlas\":" + Json.Esc(F(f, 5)) + ",\"id\":" + Json.Num(F(f, 6)) + "}");
                        break;
                    case "H":
                        addonVersion = F(f, 2);
                        break;
                    case "F":
                        // F  kind  name  class  level  mapID  x  y  area  flags  tag  (friends and group)
                        friends.Add("{\"kind\":" + Json.Esc(F(f, 1)) + ",\"name\":" + Json.Esc(F(f, 2)) + ",\"cls\":" + Json.Esc(F(f, 3)) +
                                    ",\"level\":" + Json.Num(F(f, 4)) + ",\"map\":" + Json.Num(F(f, 5)) + ",\"x\":" + Json.Num(F(f, 6)) +
                                    ",\"y\":" + Json.Num(F(f, 7)) + ",\"area\":" + Json.Esc(F(f, 8)) + ",\"flags\":" + Json.Esc(F(f, 9)) +
                                    ",\"tag\":" + Json.Esc(F(f, 10)) + "}");
                        break;
                    case "U":
                        reps.Add("{\"name\":" + Json.Esc(F(f, 1)) + ",\"reaction\":" + Json.Num(F(f, 2)) + ",\"min\":" + Json.Num(F(f, 3)) +
                                 ",\"max\":" + Json.Num(F(f, 4)) + ",\"value\":" + Json.Num(F(f, 5)) + "}");
                        break;
                    case "O":
                        bank.Add("{\"id\":" + Json.Num(F(f, 1)) + ",\"count\":" + Json.Num(F(f, 2)) + ",\"q\":" + Json.Num(F(f, 3)) +
                                 ",\"name\":" + Json.Esc(F(f, 4)) + "}");
                        break;
                    case "C":
                        tree.Add("{\"p\":" + Json.Num(F(f, 1)) + ",\"id\":" + Json.Num(F(f, 2)) + ",\"name\":" + Json.Esc(F(f, 3)) +
                                 ",\"type\":" + Json.Num(F(f, 4)) + ",\"rect\":[" + Json.Num(F(f, 5)) + "," + Json.Num(F(f, 6)) + "," +
                                 Json.Num(F(f, 7)) + "," + Json.Num(F(f, 8)) + "]}");
                        int cid;
                        if (MapNames != null && int.TryParse(F(f, 2), out cid))
                            lock (MapNames) { if (!MapNames.ContainsKey(cid)) MapNames[cid] = F(f, 3) + "\t" + F(f, 4); }
                        break;
                    case "L":
                        StringBuilder np = new StringBuilder();
                        foreach (string pt in F(f, 6).Split(';'))
                        {
                            string[] xy = pt.Split(',');
                            if (xy.Length != 2) continue;
                            if (np.Length > 0) np.Append(',');
                            np.Append('[').Append(Json.Num(xy[0])).Append(',').Append(Json.Num(xy[1])).Append(']');
                        }
                        navJson = "{\"status\":" + Json.Esc(F(f, 1)) + ",\"yards\":" + Json.Num(F(f, 2)) + ",\"map\":" + Json.Num(F(f, 3)) +
                                  ",\"x\":" + Json.Num(F(f, 4)) + ",\"y\":" + Json.Num(F(f, 5)) + ",\"pts\":[" + np + "]}";
                        break;
                    case "A":
                        // trainers and services (from Polestar's NPC database): "-" marks the list as read
                        if (services == null) services = new List<string>();
                        if (F(f, 1) == "-") break;
                        services.Add("{\"cat\":" + Json.Esc(F(f, 1)) + ",\"x\":" + Json.Num(F(f, 2)) + ",\"y\":" + Json.Num(F(f, 3)) +
                                     ",\"name\":" + Json.Esc(F(f, 4)) + ",\"title\":" + Json.Esc(F(f, 5)) +
                                     ",\"items\":[" + NumList(F(f, 6)) + "]}");
                        break;
                    case "I":
                        // header status: hearth location, hearth ready-at (epoch, 0 = ready, "" = none), new mail, repair cost
                        statusJson = "{\"home\":" + Json.Esc(F(f, 1)) +
                                     ",\"hearthReadyAt\":" + (F(f, 2).Length > 0 ? Json.Num(F(f, 2)) : "null") +
                                     ",\"mail\":" + (F(f, 3) == "1" ? "true" : "false") +
                                     ",\"repair\":" + (F(f, 4).Length > 0 ? Json.Num(F(f, 4)) : "null") + "}";
                        break;
                    case "D":
                        // Polestar's active route: label, target (zone coords), legs "kind,cur,straight:x,y;x,y|..."
                        StringBuilder legsJ = new StringBuilder();
                        foreach (string leg in F(f, 4).Split('|'))
                        {
                            int colon = leg.IndexOf(':');
                            if (colon < 0) continue;
                            string[] head = leg.Substring(0, colon).Split(',');
                            if (head.Length < 3) continue;
                            StringBuilder lp = new StringBuilder();
                            foreach (string pt in leg.Substring(colon + 1).Split(';'))
                            {
                                string[] xy = pt.Split(',');
                                if (xy.Length != 2) continue;
                                if (lp.Length > 0) lp.Append(',');
                                lp.Append('[').Append(Json.Num(xy[0])).Append(',').Append(Json.Num(xy[1])).Append(']');
                            }
                            if (legsJ.Length > 0) legsJ.Append(',');
                            legsJ.Append("{\"kind\":").Append(Json.Esc(head[0])).Append(",\"current\":").Append(head[1] == "1" ? "true" : "false")
                                 .Append(",\"straight\":").Append(head[2] == "1" ? "true" : "false").Append(",\"pts\":[").Append(lp).Append("]}");
                        }
                        string ptx = F(f, 2), pty = F(f, 3);
                        polestarJson = "{\"label\":" + Json.Esc(F(f, 1)) +
                                       ",\"x\":" + (ptx.Length > 0 ? Json.Num(ptx) : "null") + ",\"y\":" + (pty.Length > 0 ? Json.Num(pty) : "null") +
                                       ",\"legs\":[" + legsJ + "]}";
                        break;
                    case "J":
                        StringBuilder rg = new StringBuilder();
                        foreach (string part in F(f, 6).Split('\u001f'))
                        {
                            string[] v = part.Split(new char[] { ',' }, 3);
                            if (v.Length < 2) continue;
                            if (rg.Length > 0) rg.Append(',');
                            rg.Append("{\"id\":").Append(Json.Num(v[0])).Append(",\"count\":").Append(Json.Num(v[1]))
                              .Append(",\"name\":").Append(Json.Esc(v.Length > 2 ? v[2] : "")).Append('}');
                        }
                        recipes.Add("{\"prof\":" + Json.Esc(F(f, 1)) + ",\"name\":" + Json.Esc(F(f, 2)) + ",\"diff\":" + Json.Num(F(f, 3)) +
                                    ",\"out\":" + Json.Num(F(f, 4)) + ",\"qty\":" + Json.Num(F(f, 5)) + ",\"reagents\":[" + rg + "]}");
                        break;
                    case "X":
                        error = F(f, 1);
                        break;
                }
            }
            if (bagHead != null) bags.Add(bagHead + bagItems.ToString() + "]}");
            if (!string.IsNullOrEmpty(charName))
            {
                LastCharKey = charName + "-" + charRealm;
                LastCharJson = "{\"key\":" + Json.Esc(LastCharKey) + ",\"player\":" + player +
                               ",\"bags\":[" + string.Join(",", bags.ToArray()) + "],\"bank\":[" + string.Join(",", bank.ToArray()) + "]}";
            }
            return "{\"player\":" + player + ",\"bags\":[" + string.Join(",", bags.ToArray()) +
                   "],\"gear\":[" + string.Join(",", gear.ToArray()) +
                   "],\"quests\":[" + string.Join(",", quests.ToArray()) +
                   "],\"map\":" + map + ",\"maps\":[" + string.Join(",", maps.ToArray()) +
                   "],\"pins\":[" + string.Join(",", pins.ToArray()) +
                   "],\"pinCaps\":" + (caps == null ? "null" : "[" + string.Join(",", Array.ConvertAll(caps.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries), new Converter<string, string>(Json.Esc))) + "]") +
                   ",\"areas\":[" + string.Join(",", areas.ToArray()) +
                   "],\"stats\":[" + string.Join(",", stats.ToArray()) +
                   "],\"skills\":[" + string.Join(",", skills.ToArray()) +
                   "],\"nodes\":[" + string.Join(",", nodes.ToArray()) +
                   "],\"nav\":" + navJson + ",\"polestar\":" + polestarJson + ",\"status\":" + statusJson + ",\"services\":" + (services == null ? "null" : "[" + string.Join(",", services.ToArray()) + "]") + ",\"session\":" + session +
                   ",\"nearby\":[" + string.Join(",", nearby.ToArray()) +
                   "],\"friends\":[" + string.Join(",", friends.ToArray()) +
                   "],\"reps\":[" + string.Join(",", reps.ToArray()) +
                   "],\"bank\":[" + string.Join(",", bank.ToArray()) +
                   "],\"recipes\":[" + string.Join(",", recipes.ToArray()) +
                   "],\"tree\":[" + string.Join(",", tree.ToArray()) +
                   "],\"addonVersion\":" + Json.Esc(addonVersion) +
                   ",\"addonError\":" + Json.Esc(error) + "}";
        }

        // The character part of the last parsed snapshot (bags + bank), for the all-characters inventory.
        public static string LastCharKey, LastCharJson;

        // uiMapID -> "name\tmapType", filled while parsing; used by the map image cache.
        public static Dictionary<int, string> MapNames = new Dictionary<int, string>();
    }

    // Item icons: itemID -> Wowhead tooltip JSON (icon name) -> zamimg JPEG, cached on disk.
    // Only item IDs leave the PC. A ".none" marker remembers misses for a day.
    public class IconCache
    {
        readonly string dir;
        readonly Dictionary<string, object> locks = new Dictionary<string, object>();
        static readonly System.Text.RegularExpressions.Regex IconRx =
            new System.Text.RegularExpressions.Regex("\"icon\"\\s*:\\s*\"([A-Za-z0-9_\\-]+)\"");
        public long Fetched, Failed;

        public IconCache(string cacheDir)
        {
            dir = cacheDir;
            Directory.CreateDirectory(dir);
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        object LockFor(string id)
        {
            lock (locks)
            {
                object o;
                if (!locks.TryGetValue(id, out o)) { o = new object(); locks[id] = o; }
                return o;
            }
        }

        static string Download(string url, bool binary, out byte[] data)
        {
            data = null;
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "EdgeLink/0.2 (personal Xeneon Edge widget)";
                if (binary) { data = wc.DownloadData(url); return null; }
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString(url);
            }
        }

        public byte[] Get(string id)
        {
            int n;
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n <= 0) return null;
            id = n.ToString(CultureInfo.InvariantCulture);
            string jpg = Path.Combine(dir, id + ".jpg"), none = Path.Combine(dir, id + ".none");
            lock (LockFor(id))
            {
                if (File.Exists(jpg)) return File.ReadAllBytes(jpg);
                if (File.Exists(none) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(none)).TotalHours < 24) return null;
                string[] envs = new string[] { "?dataEnv=4", "" }; // classic data first, then retail
                foreach (string env in envs)
                {
                    try
                    {
                        byte[] unused;
                        string json = Download("https://nether.wowhead.com/tooltip/item/" + id + env, false, out unused);
                        System.Text.RegularExpressions.Match m = IconRx.Match(json ?? "");
                        if (!m.Success) continue;
                        byte[] img;
                        Download("https://wow.zamimg.com/images/wow/icons/large/" + m.Groups[1].Value.ToLowerInvariant() + ".jpg", true, out img);
                        if (img == null || img.Length < 100) continue;
                        File.WriteAllBytes(jpg, img);
                        Interlocked.Increment(ref Fetched);
                        return img;
                    }
                    catch { }
                }
                Interlocked.Increment(ref Failed);
                try { File.WriteAllText(none, DateTime.UtcNow.ToString("o")); } catch { }
                return null;
            }
        }
    }

    // Map art: uiMapID -> map name (from the snapshot) -> Wowhead classic area ID -> parchment map JPEG.
    // The images keep the in-game map's aspect ratio, so map coordinates are plain percentages.
    // Continents/world aren't searchable on Wowhead, so they use fixed IDs.
    public class MapCache
    {
        readonly string dir;
        readonly object gate = new object();
        static readonly System.Text.RegularExpressions.Regex ObjRx =
            new System.Text.RegularExpressions.Regex("\\{[^{}]*\\}");
        static readonly System.Text.RegularExpressions.Regex TypeRx =
            new System.Text.RegularExpressions.Regex("\"type\"\\s*:\\s*7[,}]");
        static readonly System.Text.RegularExpressions.Regex IdRx =
            new System.Text.RegularExpressions.Regex("\"id\"\\s*:\\s*(-?\\d+)");
        static readonly System.Text.RegularExpressions.Regex NameRx =
            new System.Text.RegularExpressions.Regex("\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        static readonly Dictionary<string, int> Fixed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Azeroth", -1 }, { "Eastern Kingdoms", -3 }, { "Kalimdor", -6 }
        };

        public MapCache(string cacheDir)
        {
            dir = cacheDir;
            Directory.CreateDirectory(dir);
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        static WebClient Client()
        {
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "EdgeLink/0.3 (personal Xeneon Edge widget)";
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        static byte[] TryImage(int areaId)
        {
            foreach (string kind in new string[] { "original", "zoom" })
            {
                try
                {
                    using (WebClient wc = Client())
                    {
                        byte[] b = wc.DownloadData("https://wow.zamimg.com/images/wow/classic/maps/enus/" + kind + "/" +
                                                   areaId.ToString(CultureInfo.InvariantCulture) + ".jpg");
                        if (b != null && b.Length > 1000) return b;
                    }
                }
                catch { }
            }
            return null;
        }

        static List<int> SearchAreaIds(string name)
        {
            List<int> exact = new List<int>(), other = new List<int>();
            string json;
            using (WebClient wc = Client())
                json = wc.DownloadString("https://www.wowhead.com/classic/search/suggestions-template?q=" + Uri.EscapeDataString(name));
            foreach (System.Text.RegularExpressions.Match o in ObjRx.Matches(json))
            {
                if (!TypeRx.IsMatch(o.Value)) continue;
                System.Text.RegularExpressions.Match id = IdRx.Match(o.Value), nm = NameRx.Match(o.Value);
                if (!id.Success) continue;
                int v = int.Parse(id.Groups[1].Value, CultureInfo.InvariantCulture);
                if (nm.Success && string.Equals(System.Text.RegularExpressions.Regex.Unescape(nm.Groups[1].Value), name, StringComparison.OrdinalIgnoreCase)) exact.Add(v);
                else other.Add(v);
            }
            exact.AddRange(other);
            return exact;
        }

        public byte[] Get(string idText)
        {
            int uiMapId;
            if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out uiMapId) || uiMapId <= 0) return null;
            string jpg = Path.Combine(dir, uiMapId + ".jpg"), none = Path.Combine(dir, uiMapId + ".none");
            lock (gate)
            {
                if (File.Exists(jpg)) return File.ReadAllBytes(jpg);
                if (File.Exists(none) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(none)).TotalHours < 24) return null;
                string entry = null;
                lock (SnapshotParser.MapNames) { SnapshotParser.MapNames.TryGetValue(uiMapId, out entry); }
                if (entry == null) return null; // name not known yet; don't negative-cache
                string name = entry.Split('\t')[0];
                byte[] img = null;
                try
                {
                    int fixedId;
                    if (Fixed.TryGetValue(name, out fixedId)) img = TryImage(fixedId);
                    else
                    {
                        int tries = 0;
                        foreach (int area in SearchAreaIds(name))
                        {
                            if (area <= 0 || ++tries > 4) continue;
                            img = TryImage(area);
                            if (img != null) break;
                        }
                    }
                }
                catch { }
                if (img != null) { File.WriteAllBytes(jpg, img); return img; }
                try { File.WriteAllText(none, name); } catch { }
                return null;
            }
        }
    }

    // Remembers each character's bags and bank (one small JSON file per character) so the widget can show
    // what your other characters are carrying.
    public class CharStore
    {
        readonly string dir;
        readonly object gate = new object();
        readonly Dictionary<string, string> last = new Dictionary<string, string>();
        long lastWriteTicks;

        public CharStore(string directory) { dir = directory; try { Directory.CreateDirectory(dir); } catch { } }

        static string FileName(string key)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in key) sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return sb.ToString() + ".json";
        }

        public void Save(string key, string json)
        {
            lock (gate)
            {
                string prev;
                if (last.TryGetValue(key, out prev) && prev == json) return;
                if (DateTime.UtcNow.Ticks - lastWriteTicks < 5 * 10000000L) return;   // not more than once every 5 s
                long epoch = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                try
                {
                    File.WriteAllText(Path.Combine(dir, FileName(key)), "{\"seen\":" + epoch + ",\"char\":" + json + "}", new UTF8Encoding(false));
                    last[key] = json; lastWriteTicks = DateTime.UtcNow.Ticks;
                }
                catch { }
            }
        }

        public string ListJson()
        {
            lock (gate)
            {
                List<string> all = new List<string>();
                try { foreach (string f in Directory.GetFiles(dir, "*.json")) { try { all.Add(File.ReadAllText(f, Encoding.UTF8)); } catch { } } }
                catch { }
                return "[" + string.Join(",", all.ToArray()) + "]";
            }
        }
    }

    // Learned trails: a 256x256 grid per map of the cells the player has walked through. Used by the widget to
    // suggest a path along ground that is known to be walkable. Stored as one small bitmap file per map.
    public class TrailStore
    {
        public const int N = 256;
        readonly string dir;
        readonly object gate = new object();
        readonly Dictionary<int, byte[]> maps = new Dictionary<int, byte[]>();
        readonly HashSet<int> dirty = new HashSet<int>();
        int lastMap = -1, lastX, lastY;
        long lastSaveTicks = DateTime.UtcNow.Ticks;

        public TrailStore(string directory) { dir = directory; try { Directory.CreateDirectory(dir); } catch { } }

        byte[] Load(int map)
        {
            byte[] b;
            if (maps.TryGetValue(map, out b)) return b;
            b = new byte[N * N / 8];
            try
            {
                string f = Path.Combine(dir, map + ".bin");
                if (File.Exists(f)) { byte[] d = File.ReadAllBytes(f); if (d.Length == b.Length) b = d; }
            }
            catch { }
            maps[map] = b;
            return b;
        }

        void Mark(byte[] b, int map, int x, int y)
        {
            int i = y * N + x; byte bit = (byte)(1 << (i & 7));
            if ((b[i >> 3] & bit) == 0) { b[i >> 3] |= bit; dirty.Add(map); }
        }

        public void Add(int map, double x, double y)
        {
            if (map <= 0 || x <= 0 || y <= 0 || x >= 1 || y >= 1) return;
            int cx = (int)(x * N), cy = (int)(y * N);
            lock (gate)
            {
                if (map == lastMap && cx == lastX && cy == lastY) return;
                byte[] b = Load(map);
                // Connect to the previous sample when it is close (normal movement); a big jump is a teleport or hearth.
                if (map == lastMap && Math.Abs(cx - lastX) <= 4 && Math.Abs(cy - lastY) <= 4)
                {
                    int steps = Math.Max(Math.Abs(cx - lastX), Math.Abs(cy - lastY));
                    for (int i = 1; i <= steps; i++)
                        Mark(b, map, lastX + (cx - lastX) * i / steps, lastY + (cy - lastY) * i / steps);
                }
                else Mark(b, map, cx, cy);
                lastMap = map; lastX = cx; lastY = cy;
                if (dirty.Count > 0 && DateTime.UtcNow.Ticks - lastSaveTicks > 30 * 10000000L) SaveLocked();
            }
        }

        void SaveLocked()
        {
            foreach (int m in dirty) { try { File.WriteAllBytes(Path.Combine(dir, m + ".bin"), maps[m]); } catch { } }
            dirty.Clear();
            lastSaveTicks = DateTime.UtcNow.Ticks;
        }

        public void Save() { lock (gate) { SaveLocked(); } }

        public string Json(int map)
        {
            lock (gate)
            {
                byte[] b = Load(map);
                int count = 0;
                foreach (byte v in b) { int t = v; while (t != 0) { count += t & 1; t >>= 1; } }
                return "{\"n\":" + N + ",\"count\":" + count + ",\"cells\":\"" + Convert.ToBase64String(b) + "\"}";
            }
        }
    }

    public class Bridge
    {
        // Version of the program hosting the bridge (EdgeLink.exe sets it; empty for the PowerShell fallback)
        public string AppVersion = "";
        // Updates (EdgeLink.exe only): GET /update -> state JSON; /update/check and /update/install need the command header
        public Func<string> UpdateJson;
        public Func<string, string, string> UpdateAction;   // (action, version) -> JSON
        public string[] ProcessNames = new string[] { "Wow" };
        public int Port = 8787;
        public int IntervalMs = 33;
        public string WidgetDir;
        public bool Verbose;
        public IconCache Icons;
        public MapCache Maps;

        volatile string snapshotJson = "null";
        volatile bool wowFound;
        long lastSnapshotTicks, framesRead, pagesOk, pagesBad, snapshots;
        string lastError = "";

        // Status for the tray app
        public bool WowFound { get { return wowFound; } }
        public double SnapshotAgeSeconds { get { return lastSnapshotTicks == 0 ? -1 : (DateTime.UtcNow.Ticks - lastSnapshotTicks) / 10000000.0; } }
        public long PagesOk { get { return Interlocked.Read(ref pagesOk); } }
        public long PagesBad { get { return Interlocked.Read(ref pagesBad); } }
        public string LastError { get { return lastError; } }
        int snapshotId = -1;
        Assembler assembler = new Assembler();
        // Optional TLS certificate (with private key). When set, the same port also answers https.
        public X509Certificate2 Certificate;
        public bool HttpsEnabled { get { return Certificate != null; } }
        long requestsHttp, requestsHttps, tlsFailures;
        string lastTlsError = "";

        List<TcpListener> listeners = new List<TcpListener>();
        Thread captureThread;
        volatile bool running;

        // SslProtocols.None (= let Windows choose) needs .NET 4.7+ behavior; fall back to explicit TLS 1.2/1.3 if refused.
        void AuthenticateServer(SslStream ssl)
        {
            try { ssl.AuthenticateAsServer(Certificate, false, SslProtocols.None, false); }
            catch (ArgumentException) { ssl.AuthenticateAsServer(Certificate, false, (SslProtocols)(3072 | 12288), false); }
        }

        public void Start()
        {
            Native.MakeDpiAware();
            running = true;
            // Loopback only: never reachable from the network.
            listeners.Add(new TcpListener(IPAddress.Loopback, Port));
            if (Socket.OSSupportsIPv6) listeners.Add(new TcpListener(IPAddress.IPv6Loopback, Port));
            foreach (TcpListener l in listeners)
            {
                l.Start();
                TcpListener captured = l;
                Thread t = new Thread(delegate() { AcceptLoop(captured); });
                t.IsBackground = true; t.Start();
            }
            captureThread = new Thread(CaptureLoop); captureThread.IsBackground = true; captureThread.Start();
        }

        public void Stop()
        {
            running = false;
            foreach (TcpListener l in listeners) { try { l.Stop(); } catch { } }
            if (Trails != null) Trails.Save();
        }

        // Widget taps -> WoW (off unless the user enables it in the tray). See addon RunCommand for the digit format.
        public TrailStore Trails;
        public CharStore Chars;
        public volatile bool AllowInput;
        readonly object inputLock = new object();

        public string SendCommand(string digits)
        {
            if (!AllowInput) return "{\"ok\":false,\"error\":\"Control is off. Right-click the EdgeLink tray icon and enable 'Let widget taps control WoW'.\"}";
            if (string.IsNullOrEmpty(digits) || digits.Length > 16) return "{\"ok\":false,\"error\":\"bad command\"}";
            foreach (char c in digits) if (c < '0' || c > '9') return "{\"ok\":false,\"error\":\"bad command\"}";
            if (!Monitor.TryEnter(inputLock)) return "{\"ok\":false,\"error\":\"busy\"}";
            try
            {
                IntPtr wow = FindWow();
                if (wow == IntPtr.Zero) return "{\"ok\":false,\"error\":\"WoW is not running\"}";
                if (Native.GetForegroundWindow() != wow)
                {
                    // A tap on the Edge can take focus; hand it back to WoW (the Alt tap lets a background app do that).
                    Native.Key(Native.VK_MENU, true); Native.Key(Native.VK_MENU, false);
                    Native.SetForegroundWindow(wow);
                    for (int i = 0; i < 20 && Native.GetForegroundWindow() != wow; i++) Thread.Sleep(10);
                    if (Native.GetForegroundWindow() != wow) return "{\"ok\":false,\"error\":\"Could not bring WoW to the front\"}";
                    Thread.Sleep(60);
                }
                Native.Key(Native.VK_CONTROL, true); Native.Key(Native.VK_SHIFT, true);
                try
                {
                    Tap((byte)(Native.VK_F1 + 10));                                  // F11 = begin
                    foreach (char c in digits) Tap((byte)(Native.VK_F1 + (c - '0')));  // F1..F10 = 0..9
                    Tap((byte)(Native.VK_F1 + 11));                                  // F12 = end
                }
                finally { Native.Key(Native.VK_SHIFT, false); Native.Key(Native.VK_CONTROL, false); }
                return "{\"ok\":true}";
            }
            finally { Monitor.Exit(inputLock); }
        }

        static void Tap(byte vk)
        {
            Native.Key(vk, true); Thread.Sleep(12);
            Native.Key(vk, false); Thread.Sleep(12);
        }

        // Only the widget may send commands: the companion's own pages, or iCUE (which loads widgets from a non-web origin).
        static bool TrustedOrigin(string origin)
        {
            if (origin == null || origin == "null") return true;
            Uri u;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out u)) return false;
            if (u.Scheme != "http" && u.Scheme != "https") return true;
            return u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]";
        }

        IntPtr FindWow()
        {
            foreach (string name in ProcessNames)
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; } catch { }
                }
            }
            return IntPtr.Zero;
        }

        // Latest live position from a v2 page header.
        volatile string posJson = "null";
        long lastPosTicks;

        static string B(int v) { return v != 0 ? "true" : "false"; }

        public void Feed(Page p)
        {
            Interlocked.Increment(ref pagesOk);
            if (p.HasPos)
            {
                posJson = "{\"mapId\":" + p.MapId +
                          ",\"x\":" + p.X.ToString("0.#####", CultureInfo.InvariantCulture) +
                          ",\"y\":" + p.Y.ToString("0.#####", CultureInfo.InvariantCulture) +
                          ",\"facing\":" + (p.Facing == 255 ? "null" : (p.Facing / 255.0 * 2 * Math.PI).ToString("0.###", CultureInfo.InvariantCulture)) +
                          ",\"valid\":" + ((p.Flags & 1) != 0 ? "true" : "false") +
                          ",\"combat\":" + ((p.Flags & 2) != 0 ? "true" : "false") +
                          ",\"taxi\":" + ((p.Flags & 4) != 0 ? "true" : "false") +
                          ",\"windows\":{\"vendor\":" + B(p.Flags & 8) + ",\"bank\":" + B(p.Flags & 16) + ",\"profession\":" + B(p.Flags & 32) +
                          ",\"mail\":" + B(p.Flags & 64) + ",\"flightMap\":" + B(p.Flags & 128) + "}}";
                lastPosTicks = DateTime.UtcNow.Ticks;
                // Learn where the player walks (not in combat: position is frozen; not on a flight path)
                if (Trails != null && (p.Flags & 7) == 1) Trails.Add(p.MapId, p.X, p.Y);
            }
            string text = assembler.Add(p);
            if (text == null) return;
            snapshotJson = SnapshotParser.ToJson(text);
            if (Chars != null && SnapshotParser.LastCharKey != null) Chars.Save(SnapshotParser.LastCharKey, SnapshotParser.LastCharJson);
            snapshotId = p.SnapshotId;
            Interlocked.Increment(ref snapshots);
            lastSnapshotTicks = DateTime.UtcNow.Ticks;
            if (Verbose) Console.WriteLine("snapshot " + p.SnapshotId + " (" + p.Count + " pages, " + text.Length + " chars)");
        }

        public void FeedError(string err)
        {
            Interlocked.Increment(ref pagesBad);
            lastError = err;
        }

        // 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right
        volatile int corner;
        int misses;
        static readonly string[] CornerNames = { "top-left", "top-right", "bottom-left", "bottom-right" };

        void CaptureLoop()
        {
            int w = Protocol.Blocks * Protocol.BlockPx, h = Protocol.BlockPx;
            IntPtr hwnd = IntPtr.Zero;
            int searchCounter = 0;
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                while (running)
                {
                    try
                    {
                        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
                        {
                            hwnd = IntPtr.Zero;
                            if (searchCounter-- <= 0) { hwnd = FindWow(); searchCounter = 30; }
                        }
                        wowFound = hwnd != IntPtr.Zero;
                        if (!wowFound || Native.IsIconic(hwnd)) { Thread.Sleep(250); continue; }
                        // The strip can sit in any corner (/edgelink corner in game): look in the last good one,
                        // and after ~1 s without a strip there, try the next corner.
                        Native.RECT rc;
                        Native.GetClientRect(hwnd, out rc);
                        int cw = rc.Right - rc.Left, ch = rc.Bottom - rc.Top;
                        Native.POINT pt = new Native.POINT();
                        pt.X = (corner == 1 || corner == 3) ? Math.Max(0, cw - w) : 0;
                        pt.Y = (corner == 2 || corner == 3) ? Math.Max(0, ch - h) : 0;
                        Native.ClientToScreen(hwnd, ref pt);
                        g.CopyFromScreen(pt.X, pt.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                        Interlocked.Increment(ref framesRead);
                        string err;
                        Page p = Protocol.Decode(bmp, 0, 0, out err);
                        if (p != null) { Feed(p); misses = 0; }
                        else if (err != null) FeedError(err);
                        else if (++misses >= 30) { misses = 0; corner = (corner + 1) % 4; }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        Thread.Sleep(500);
                    }
                    Thread.Sleep(IntervalMs);
                }
            }
        }

        string StateJson()
        {
            double age = lastSnapshotTicks == 0 ? -1 : (DateTime.UtcNow.Ticks - lastSnapshotTicks) / 10000000.0;
            return "{\"ok\":true,\"companionVersion\":" + Json.Esc(AppVersion) + ",\"wowFound\":" + (wowFound ? "true" : "false") + ",\"inputAllowed\":" + (AllowInput ? "true" : "false") +
                   ",\"snapshotAgeSec\":" + age.ToString("0.0", CultureInfo.InvariantCulture) +
                   ",\"snapshotId\":" + snapshotId +
                   ",\"stats\":{\"frames\":" + framesRead + ",\"pagesOk\":" + pagesOk + ",\"pagesBad\":" + pagesBad +
                   ",\"snapshots\":" + snapshots + ",\"stripCorner\":" + Json.Esc(CornerNames[corner]) + ",\"lastError\":" + Json.Esc(lastError) +
                   ",\"https\":" + (HttpsEnabled ? "true" : "false") + ",\"requestsHttp\":" + requestsHttp +
                   ",\"requestsHttps\":" + requestsHttps + ",\"tlsFailures\":" + tlsFailures +
                   ",\"lastTlsError\":" + Json.Esc(lastTlsError) + "}" +
                   ",\"pos\":" + posJson +
                   ",\"posAgeSec\":" + (lastPosTicks == 0 ? "-1" : ((DateTime.UtcNow.Ticks - lastPosTicks) / 10000000.0).ToString("0.0", CultureInfo.InvariantCulture)) +
                   ",\"data\":" + snapshotJson + "}";
        }

        static string ContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": case ".mjs": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                default: return "application/octet-stream";
            }
        }

        void AcceptLoop(TcpListener l)
        {
            while (running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { break; }
                ThreadPool.QueueUserWorkItem(delegate(object o) { HandleClient((TcpClient)o); }, c);
            }
        }

        // One request per connection. A first byte of 0x16 is a TLS ClientHello -> https.
        void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                    Socket s = client.Client;
                    byte[] peek = new byte[1];
                    if (s.Receive(peek, 0, 1, SocketFlags.Peek) < 1) return;
                    Stream stream = client.GetStream();
                    bool tls = peek[0] == 0x16;
                    if (tls)
                    {
                        if (Certificate == null) { Interlocked.Increment(ref tlsFailures); lastTlsError = "https request but no certificate installed (run Install-EdgeLinkCert.ps1)"; return; }
                        SslStream ssl = new SslStream(stream, false);
                        try { AuthenticateServer(ssl); }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref tlsFailures);
                            lastTlsError = ex.InnerException != null ? ex.Message + " / " + ex.InnerException.Message : ex.Message;
                            return;
                        }
                        stream = ssl;
                        Interlocked.Increment(ref requestsHttps);
                    }
                    else Interlocked.Increment(ref requestsHttp);
                    using (stream) { Serve(stream); }
                }
                catch { }
            }
        }

        static string Header(string head, string name)
        {
            foreach (string line in head.Split(new string[] { "\r\n" }, StringSplitOptions.None))
            {
                int c = line.IndexOf(':');
                if (c > 0 && string.Equals(line.Substring(0, c).Trim(), name, StringComparison.OrdinalIgnoreCase)) return line.Substring(c + 1).Trim();
            }
            return null;
        }

        static string QueryValue(string query, string key)
        {
            foreach (string part in query.Split('&'))
            {
                int e = part.IndexOf('=');
                if (e > 0 && part.Substring(0, e) == key) { try { return Uri.UnescapeDataString(part.Substring(e + 1)); } catch { return null; } }
            }
            return null;
        }

        static string ReadHead(Stream s)
        {
            StringBuilder sb = new StringBuilder();
            int b;
            while (sb.Length < 16384 && (b = s.ReadByte()) >= 0)
            {
                sb.Append((char)b);
                int n = sb.Length;
                if (n >= 4 && sb[n - 4] == '\r' && sb[n - 3] == '\n' && sb[n - 2] == '\r' && sb[n - 1] == '\n') break;
            }
            return sb.ToString();
        }

        void Serve(Stream stream)
        {
            string head = ReadHead(stream);
            int sp1 = head.IndexOf(' '), sp2 = sp1 < 0 ? -1 : head.IndexOf(' ', sp1 + 1);
            if (sp2 < 0) return;
            string method = head.Substring(0, sp1);
            string target = head.Substring(sp1 + 1, sp2 - sp1 - 1);
            string query = "";
            int q = target.IndexOf('?'); if (q >= 0) { query = target.Substring(q + 1); target = target.Substring(0, q); }
            string origin = Header(head, "Origin");
            bool cmdHeader = Header(head, "X-EdgeLink-Cmd") != null;
            string path;
            try { path = Uri.UnescapeDataString(target); } catch { path = "/"; }

            byte[] body; int status = 200; string type = "application/json; charset=utf-8";
            string extraHeaders = "";
            if (method == "OPTIONS")
            {
                body = new byte[0]; status = 204;
                if ((path == "/cmd" || path.StartsWith("/update/")) && TrustedOrigin(origin)) extraHeaders = "Access-Control-Allow-Headers: X-EdgeLink-Cmd\r\n";
            }
            else if (path == "/update/check" || path == "/update/install")
            {
                if (!cmdHeader || !TrustedOrigin(origin)) { status = 403; body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"forbidden\"}"); }
                else if (UpdateAction == null) body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"Updates need the EdgeLink app (not the PowerShell companion).\"}");
                else body = Encoding.UTF8.GetBytes(UpdateAction(path.Substring(8), QueryValue(query, "v")));
            }
            else if (path == "/update")
            {
                body = Encoding.UTF8.GetBytes(UpdateJson != null ? UpdateJson() : "{\"status\":\"unsupported\"}");
            }
            else if (path == "/cmd")
            {
                // The custom header forces a CORS preflight, which only trusted origins pass; plain links/images can't add it.
                if (!cmdHeader || !TrustedOrigin(origin)) { status = 403; body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"forbidden\"}"); }
                else body = Encoding.UTF8.GetBytes(SendCommand(QueryValue(query, "d")));
            }
            else if (method != "GET" && method != "HEAD") { status = 405; body = Encoding.UTF8.GetBytes("{\"error\":\"method\"}"); }
            else if (path == "/state" || path == "/health")
            {
                body = Encoding.UTF8.GetBytes(StateJson());
            }
            else if (path == "/chars") body = Encoding.UTF8.GetBytes(Chars != null ? Chars.ListJson() : "[]");
            else if (path.StartsWith("/trail/"))
            {
                int tm;
                if (Trails != null && int.TryParse(path.Substring(7), out tm)) body = Encoding.UTF8.GetBytes(Trails.Json(tm));
                else body = Encoding.UTF8.GetBytes("{\"n\":0,\"count\":0,\"cells\":\"\"}");
            }
            else if (path.StartsWith("/mapimg/"))
            {
                byte[] img = Maps == null ? null : Maps.Get(path.Substring(8));
                if (img != null) { body = img; type = "image/jpeg"; }
                else { status = 404; body = Encoding.UTF8.GetBytes("{\"error\":\"no map art\"}"); }
            }
            else if (path.StartsWith("/icon/"))
            {
                byte[] img = Icons == null ? null : Icons.Get(path.Substring(6));
                if (img != null) { body = img; type = "image/jpeg"; }
                else { status = 404; body = Encoding.UTF8.GetBytes("{\"error\":\"no icon\"}"); }
            }
            else
            {
                string rel = path == "/" ? "index.html" : path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string full = null;
                try { full = WidgetDir == null ? null : Path.GetFullPath(Path.Combine(WidgetDir, rel)); } catch { }
                string rootDir = WidgetDir == null ? null : Path.GetFullPath(WidgetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full != null && full.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    body = File.ReadAllBytes(full); type = ContentType(full);
                }
                else
                {
                    status = 404; body = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
                }
            }
            string reason = status == 200 ? "OK" : status == 204 ? "No Content" : status == 404 ? "Not Found" : status == 403 ? "Forbidden" : "Method Not Allowed";
            string hdr = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                         "Content-Type: " + type + "\r\n" +
                         "Content-Length: " + body.Length + "\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
                         "Access-Control-Allow-Private-Network: true\r\n" + extraHeaders +
                         "Cache-Control: no-store\r\n" +
                         "Connection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(hdr);
            stream.Write(h, 0, h.Length);
            if (method != "HEAD") stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        // Offline test: decode strip PNGs (strip at 0,0) and return the state JSON.
        public string DecodeImages(string[] files)
        {
            foreach (string f in files)
            {
                using (Bitmap b = new Bitmap(f))
                {
                    string err;
                    Page p = Protocol.Decode(b, 0, 0, out err);
                    if (p != null) Feed(p); else FeedError(err ?? "no strip");
                }
            }
            return StateJson();
        }
    }
}
