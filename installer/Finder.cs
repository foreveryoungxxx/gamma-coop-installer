using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace GammaCoop
{
    // finding things on the PC: the game folder, build files, known folders. Read-only; no UI.
    // Test hooks (environment variables): GAMMA_COOP_DOWNLOADS, GAMMA_COOP_DESKTOP; LocalSend is read from %APPDATA%.
    public static class Finder
    {
        // installer folder (set by the app): last_root.txt lives there, and builds are searched beside it
        public static string AppDir;

        static readonly string[] SkipDirs = { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "AppData" };

        public static string DownloadsDir()
        {
            string h = Environment.GetEnvironmentVariable("GAMMA_COOP_DOWNLOADS");
            if (!string.IsNullOrEmpty(h)) return h;
            return Safety.Downloads();
        }

        public static string DesktopDir()
        {
            string h = Environment.GetEnvironmentVariable("GAMMA_COOP_DESKTOP");
            if (!string.IsNullOrEmpty(h)) return h;
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        // where LocalSend saves received files; null when LocalSend is not set up (its default on Windows is Downloads)
        public static string LocalSendDir()
        {
            try
            {
                string ad = Environment.GetEnvironmentVariable("APPDATA");
                if (string.IsNullOrEmpty(ad)) return null;
                string f = Path.Combine(ad, @"org.localsend\localsend_app\shared_preferences.json");
                if (!File.Exists(f)) return null;
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                object o;
                if (d != null && d.TryGetValue("flutter.ls_destination", out o) && o != null)
                {
                    string s = o.ToString().Trim();
                    if (s.Length > 0) return s;
                }
            }
            catch (Exception) { }
            return null;
        }

        public static bool IsGammaRoot(string p)
        {
            try
            {
                return !string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, @"GAMMA\ModOrganizer.exe")) && Directory.Exists(Path.Combine(p, @"ANOMALY\bin"));
            }
            catch (Exception) { return false; }
        }

        static string Clean(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            try
            {
                string f = Path.GetFullPath(p.Trim().Trim('"')).TrimEnd('\\');
                return f.Length > 2 ? f : null;
            }
            catch (Exception) { return null; }
        }

        static void AddRoot(List<string> l, string p)
        {
            string c = Clean(p);
            if (c == null || !IsGammaRoot(c)) return;
            foreach (string x in l) if (string.Equals(x, c, StringComparison.OrdinalIgnoreCase)) return;
            l.Add(c);
        }

        static void AddCand(List<string> l, string p)
        {
            string c = Clean(p);
            if (c == null) return;
            foreach (string x in l) if (string.Equals(x, c, StringComparison.OrdinalIgnoreCase)) return;
            l.Add(c);
        }

        // root fields recorded in <root>\GAMMA Coop\progress.json and installed.json
        static void AddRecorded(List<string> cands, string root)
        {
            foreach (string f in new string[] { Safety.ProgressPath(root), Coop.InstalledJson(root) })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                    object o;
                    if (d != null && d.TryGetValue("root", out o) && o != null) AddCand(cands, o.ToString());
                }
                catch (Exception) { }
            }
        }

        // quick sources (last_root.txt, the installer's own folder and parents, roots recorded in progress.json / installed.json);
        // only when they give nothing, every fixed NTFS drive is scanned to depth 4 (about 10 s at most)
        public static List<string> FindGammaRoots() { return FindGammaRoots(false); }

        public static List<string> FindGammaRoots(bool forceScan)
        {
            List<string> cands = new List<string>();
            try
            {
                if (AppDir != null)
                {
                    string lr = Path.Combine(AppDir, "last_root.txt");
                    if (File.Exists(lr)) AddCand(cands, File.ReadAllText(lr, Encoding.UTF8).Trim());
                    string cur = Clean(AppDir);
                    for (int i = 0; i < 6 && cur != null; i++)
                    {
                        AddCand(cands, cur);
                        try { cur = Clean(Path.GetDirectoryName(cur)); } catch (Exception) { cur = null; }
                    }
                }
            }
            catch (Exception) { }
            for (int i = cands.Count - 1; i >= 0; i--) AddRecorded(cands, cands[i]);
            List<string> res = new List<string>();
            foreach (string c in cands) AddRoot(res, c);
            if (res.Count == 0 || forceScan) Scan(res);
            return res;
        }

        static void Scan(List<string> res)
        {
            Stopwatch sw = Stopwatch.StartNew();
            List<Thread> ths = new List<Thread>();
            List<string> found = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        string fmt = d.DriveFormat;
                        if (!string.Equals(fmt, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (Exception) { continue; }
                    string dn = d.Name;
                    Thread t = new Thread(delegate ()
                    {
                        List<string> mine = new List<string>();
                        Walk(new DirectoryInfo(dn), 0, sw, mine);
                        lock (found) { found.AddRange(mine); }
                    });
                    t.IsBackground = true;
                    t.Start();
                    ths.Add(t);
                }
            }
            catch (Exception) { }
            foreach (Thread t in ths)
            {
                int left = (int)Math.Max(100, 10500 - sw.ElapsedMilliseconds);
                t.Join(left);
            }
            lock (found) { foreach (string f in found) AddRoot(res, f); }
        }

        static void Walk(DirectoryInfo di, int depth, Stopwatch sw, List<string> found)
        {
            if (sw.ElapsedMilliseconds > 10000) return;
            if (IsGammaRoot(di.FullName)) { found.Add(di.FullName); return; }
            if (depth >= 4) return;
            DirectoryInfo[] subs;
            try { subs = di.GetDirectories(); }
            catch (Exception) { return; }
            foreach (DirectoryInfo s in subs)
            {
                if (sw.ElapsedMilliseconds > 10000) return;
                try
                {
                    if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    bool skip = false;
                    foreach (string n in SkipDirs) if (string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                    if (skip) continue;
                }
                catch (Exception) { continue; }
                Walk(s, depth + 1, sw, found);
            }
        }

        // ------------------------------------------------------------ build files
        class Place { public string Dir, Label; public string[] Patterns; }

        static void AddPlace(List<Place> l, string dir, string label, params string[] patterns)
        {
            string c = Clean(dir);
            if (c == null) return;
            foreach (Place p in l) if (string.Equals(p.Dir, c, StringComparison.OrdinalIgnoreCase)) return;
            Place n = new Place();
            n.Dir = c; n.Label = label; n.Patterns = patterns;
            l.Add(n);
        }

        static readonly string[] BuildPatterns = { "GAMMA_Coop_build_*.zip", "GAMMA_Coop_pack.zip" };

        // newest first (by the date inside the file, then by file time); the same build found twice is listed once.
        // root may be null: then the folders inside the game folder are not searched
        public static List<PackInfo> FindBuildFiles(string root, string appDir)
        {
            List<Place> places = new List<Place>();
            AddPlace(places, DownloadsDir(), "Загрузки", BuildPatterns);
            AddPlace(places, LocalSendDir(), "LocalSend", BuildPatterns);
            AddPlace(places, DesktopDir(), "Рабочий стол", BuildPatterns);
            if (appDir != null)
            {
                AddPlace(places, appDir, "папка установщика", BuildPatterns);
                try { AddPlace(places, Path.GetDirectoryName(appDir.TrimEnd('\\')), "папка установщика", BuildPatterns); } catch (Exception) { }
            }
            if (root != null)
            {
                AddPlace(places, Path.Combine(root, @"GAMMA Coop\inbox"), "inbox", BuildPatterns);
                AddPlace(places, Path.Combine(root, @"GAMMA Coop\builds"), "сохранённая", BuildPatterns);
                AddPlace(places, Path.Combine(root, @"GAMMA Coop"), "сохранённая", "pack.zip");
            }
            List<PackInfo> all = new List<PackInfo>();
            List<string> seen = new List<string>();
            foreach (Place pl in places)
            {
                foreach (string pat in pl.Patterns)
                {
                    string[] files;
                    try { files = Directory.Exists(pl.Dir) ? Directory.GetFiles(pl.Dir, pat) : new string[0]; }
                    catch (Exception) { continue; }
                    foreach (string f in files)
                    {
                        if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                        string full = Clean(f);
                        if (full == null || seen.Contains(full.ToLowerInvariant())) continue;
                        seen.Add(full.ToLowerInvariant());
                        PackInfo p = Coop.Probe(full);
                        if (p == null) continue;
                        p.Where = pl.Label;
                        p.FileTimeUtc = File.GetLastWriteTimeUtc(full);
                        all.Add(p);
                    }
                }
            }
            all.Sort(delegate (PackInfo a, PackInfo b)
            {
                int c = b.CreatedUtc.CompareTo(a.CreatedUtc);
                return c != 0 ? c : b.FileTimeUtc.CompareTo(a.FileTimeUtc);
            });
            List<PackInfo> res = new List<PackInfo>();
            List<string> ids = new List<string>();
            foreach (PackInfo p in all)
            {
                if (p.BuildId.Length > 0)
                {
                    if (ids.Contains(p.BuildId)) continue;
                    ids.Add(p.BuildId);
                }
                res.Add(p);
            }
            return res;
        }

        // Mod Organizer or the game is running (any copy)
        public static bool GameRunning()
        {
            string fake = Environment.GetEnvironmentVariable("GAMMA_COOP_FAKE_GAME");   // test hook: "1" = running, "0" = not running
            if (fake == "1") return true;
            if (fake == "0") return false;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string n = p.ProcessName;
                        if (n.Equals("ModOrganizer", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AnomalyDX", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (Exception) { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
