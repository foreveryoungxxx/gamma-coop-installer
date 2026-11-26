// GAMMA Coop launcher: starts Mod Organizer 2 and bridges Discord Rich Presence.
// Build: csc.exe /target:winexe /optimize /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll Launcher.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Launcher
{
    const string ClientId = "1573818485543272672";
    const int UpdateEverySec = 15;
    const int StaleAfterSec = 20;
    const string DefaultImage = "gamma_logo";
    const string GameTitle = "S.T.A.L.K.E.R. G.A.M.M.A.";

    static string root;
    static string logPath;
    static readonly long sessionStart = UnixNow();
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();

    static readonly Dictionary<string, string> Locations = new Dictionary<string, string>
    {
        { "l01_escape", "Кордон" },
        { "l02_garbage", "Свалка" },
        { "l03_agroprom", "Агропром" },
        { "l03u_agr_underground", "Подземелья Агропрома" },
        { "l04_darkvalley", "Тёмная долина" },
        { "l04u_labx18", "Лаборатория X-18" },
        { "l05_bar", "Бар «100 рентген»" },
        { "l06_rostok", "Росток" },
        { "l07_military", "Армейские склады" },
        { "l08_yantar", "Янтарь" },
        { "l08u_brainlab", "Лаборатория X-16" },
        { "l09_deadcity", "Мёртвый город" },
        { "l10_limansk", "Лиманск" },
        { "l10_radar", "Радар" },
        { "l10_red_forest", "Рыжий лес" },
        { "l11_hospital", "Госпиталь" },
        { "l11_pripyat", "Припять" },
        { "l12_stancia", "ЧАЭС" },
        { "l12_stancia_2", "ЧАЭС-2" },
        { "l12u_control_monolith", "Пульт Монолита" },
        { "l12u_sarcofag", "Саркофаг" },
        { "k00_marsh", "Болота" },
        { "k01_darkscape", "Тёмный Скейп" },
        { "k02_trucks_cemetery", "Кладбище техники" },
        { "jupiter", "Юпитер" },
        { "jupiter_underground", "Подземка Юпитера" },
        { "pripyat", "Зона отчуждения (Припять)" },
        { "zaton", "Затон" },
        { "labx8", "Лаборатория X-8" },
        { "l13_generators", "Генераторы" }
    };

    static readonly Dictionary<string, string> Factions = new Dictionary<string, string>
    {
        { "stalker", "Одиночка" },
        { "bandit", "Бандит" },
        { "dolg", "Долг" },
        { "freedom", "Свобода" },
        { "csky", "Чистое небо" },
        { "ecolog", "Эколог" },
        { "killer", "Наёмник" },
        { "army", "Военный" },
        { "monolith", "Монолит" },
        { "zombied", "Зомбированный" },
        { "renegade", "Ренегат" },
        { "isg", "ISG" }
    };

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int pid);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr p);

    static long UnixNow()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    static void Log(string s)
    {
        try
        {
            string dir = Path.GetDirectoryName(logPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            FileInfo fi = new FileInfo(logPath);
            if (fi.Exists && fi.Length > 200 * 1024) File.Delete(logPath);
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n", Encoding.UTF8);
        }
        catch (Exception) { }
    }

    static bool IsElevated()
    {
        try
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    // ---------- processes ----------

    static bool MoRunning()
    {
        string mo = Path.GetFullPath(Path.Combine(Path.Combine(root, "GAMMA"), "ModOrganizer.exe"));
        Process[] ps = null;
        try
        {
            ps = Process.GetProcessesByName("ModOrganizer");
            foreach (Process p in ps)
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(p.MainModule.FileName), mo, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        finally
        {
            if (ps != null) foreach (Process p in ps) p.Dispose();
        }
        return false;
    }

    static bool GameRunning()
    {
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                try { if (p.ProcessName.StartsWith("AnomalyDX", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception) { }
        return false;
    }

    // ---------- payload ----------

    static string FindFile()
    {
        string[] c =
        {
            Path.Combine(root, @"ANOMALY\appdata\savedgames\discord_rpc.json"),
            Path.Combine(root, @"GAMMA\overwrite\appdata\savedgames\discord_rpc.json")
        };
        string best = null;
        DateTime bt = DateTime.MinValue;
        foreach (string p in c)
        {
            try
            {
                if (!File.Exists(p)) continue;
                DateTime t = File.GetLastWriteTimeUtc(p);
                if (best == null || t > bt) { best = p; bt = t; }
            }
            catch (Exception) { }
        }
        return best;
    }

    static Dictionary<string, object> ReadData(out bool fresh)
    {
        fresh = false;
        try
        {
            string path = FindFile();
            if (path == null) return null;
            fresh = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds < StaleAfterSec;
            byte[] raw = File.ReadAllBytes(path);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(raw); }
            catch (Exception) { text = Encoding.GetEncoding(1251).GetString(raw); }
            text = text.TrimStart('﻿');
            Dictionary<string, object> d = json.DeserializeObject(text) as Dictionary<string, object>;
            if (d == null) fresh = false;
            return d;
        }
        catch (Exception)
        {
            fresh = false;
            return null;
        }
    }

    static string Str(Dictionary<string, object> d, string k, string def)
    {
        object v;
        if (d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
        return def;
    }

    static long Num(Dictionary<string, object> d, string k, long def)
    {
        object v;
        try
        {
            if (d.TryGetValue(k, out v) && v != null) return Convert.ToInt64(Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)));
        }
        catch (Exception) { }
        return def;
    }

    static string Clip(string s)
    {
        if (s.Length >= 2) return s.Length > 128 ? s.Substring(0, 128) : s;
        return s + " ";
    }

    static string Money(long m)
    {
        NumberFormatInfo nf = new NumberFormatInfo();
        nf.NumberGroupSeparator = " ";
        nf.NumberDecimalDigits = 0;
        return m.ToString("N", nf);
    }

    static Dictionary<string, object> BuildActivity(Dictionary<string, object> data, bool fresh)
    {
        Dictionary<string, object> a = new Dictionary<string, object>();
        Dictionary<string, object> assets = new Dictionary<string, object>();
        if (data == null || !fresh)
        {
            a["details"] = GameTitle;
            a["state"] = "В меню / на паузе";
            assets["large_image"] = DefaultImage;
            assets["large_text"] = GameTitle;
        }
        else
        {
            string levelId = Str(data, "level", "");
            string facId = Str(data, "faction", "stalker");
            string loc, fac;
            if (!Locations.TryGetValue(levelId, out loc)) loc = levelId;
            if (!Factions.TryGetValue(facId, out fac)) fac = facId;

            string task = Str(data, "task", "");
            string details = task.Length > 0 ? "Задание: " + task : "Исследует Зону";

            long armor = Num(data, "armor", -1);
            string armorTxt = armor >= 0 ? "\U0001F6E1 " + armor + "%" : "\U0001F6E1 нет брони";
            string state = "❤ " + Num(data, "hp", 0) + "% · " + armorTxt + " · ☢ " + Num(data, "rad", 0) + "%";

            string largeText = "\U0001F4CD " + loc;
            string armorName = Str(data, "armor_name", "");
            if (armorName.Length > 0) largeText += " | " + armorName;

            a["details"] = Clip(details);
            a["state"] = Clip(state);
            assets["large_image"] = levelId.Length > 0 ? levelId : DefaultImage;
            assets["large_text"] = Clip(largeText);
            assets["small_image"] = facId;
            assets["small_text"] = Clip(fac + " · " + Money(Num(data, "money", 0)) + " RU");
        }
        a["assets"] = assets;
        Dictionary<string, object> ts = new Dictionary<string, object>();
        ts["start"] = sessionStart;
        a["timestamps"] = ts;
        return a;
    }

    // ---------- Discord IPC ----------

    class Discord
    {
        NamedPipeClientStream pipe;

        public bool Connected { get { return pipe != null; } }

        public static int FindPipe()
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using (NamedPipeClientStream p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.None))
                    {
                        p.Connect(250);
                        return i;
                    }
                }
                catch (Exception) { }
            }
            return -1;
        }

        public bool Connect()
        {
            Close();
            for (int i = 0; i < 10; i++)
            {
                NamedPipeClientStream p = null;
                try
                {
                    p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.None);
                    p.Connect(250);
                    pipe = p;
                    Dictionary<string, object> hs = new Dictionary<string, object>();
                    hs["v"] = 1;
                    hs["client_id"] = ClientId;
                    WriteFrame(0, json.Serialize(hs));
                    ReadFrame();
                    Log("discord connected, pipe " + i);
                    return true;
                }
                catch (Exception e)
                {
                    if (pipe != null) Log("discord handshake failed: " + e.Message);
                    Close();
                    if (p != null) { try { p.Dispose(); } catch (Exception) { } }
                }
            }
            return false;
        }

        public void Close()
        {
            if (pipe != null)
            {
                try { pipe.Dispose(); } catch (Exception) { }
                pipe = null;
            }
        }

        void WriteFrame(int op, string payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(payload);
            byte[] buf = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(buf, 0);
            BitConverter.GetBytes(body.Length).CopyTo(buf, 4);
            body.CopyTo(buf, 8);
            pipe.Write(buf, 0, buf.Length);
            pipe.Flush();
        }

        void ReadExact(byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                Task<int> t = pipe.ReadAsync(buf, got, n - got);
                if (!t.Wait(5000)) throw new TimeoutException("discord read timeout");
                if (t.Result <= 0) throw new IOException("pipe closed");
                got += t.Result;
            }
        }

        void ReadFrame()
        {
            byte[] h = new byte[8];
            ReadExact(h, 8);
            int len = BitConverter.ToInt32(h, 4);
            if (len < 0 || len > (1 << 20)) throw new IOException("bad frame");
            byte[] b = new byte[len];
            if (len > 0) ReadExact(b, len);
        }

        // activity == null clears the presence
        public void SetActivity(Dictionary<string, object> activity)
        {
            Dictionary<string, object> args = new Dictionary<string, object>();
            args["pid"] = Process.GetCurrentProcess().Id;
            args["activity"] = activity;
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["cmd"] = "SET_ACTIVITY";
            msg["args"] = args;
            msg["nonce"] = Guid.NewGuid().ToString();
            WriteFrame(1, json.Serialize(msg));
            ReadFrame();
        }
    }

    // ---------- new build detection ----------

    static string DownloadsDir()
    {
        string h = Environment.GetEnvironmentVariable("GAMMA_COOP_DOWNLOADS");
        if (!string.IsNullOrEmpty(h)) return h;
        try
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
            {
                string s = Marshal.PtrToStringUni(p);
                CoTaskMemFree(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch (Exception) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    static string LocalSendDir()
    {
        try
        {
            string ad = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(ad)) return null;
            string f = Path.Combine(ad, @"org.localsend\localsend_app\shared_preferences.json");
            if (!File.Exists(f)) return null;
            Dictionary<string, object> d = json.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
            object o;
            if (d != null && d.TryGetValue("flutter.ls_destination", out o) && o != null && o.ToString().Trim().Length > 0) return o.ToString().Trim();
        }
        catch (Exception) { }
        return null;
    }

    // folders where a new build file may arrive
    static List<string> SearchDirs()
    {
        List<string> l = new List<string>();
        string[] c = {
            DownloadsDir(), LocalSendDir(),
            Environment.GetEnvironmentVariable("GAMMA_COOP_DESKTOP") ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(root, @"GAMMA Coop\inbox") };
        foreach (string d in c) if (!string.IsNullOrEmpty(d) && !l.Contains(d)) l.Add(d);
        return l;
    }

    // buildId, created (UTC text), sourceComputer from manifest.json of a build zip; only that one entry is read
    static string[] ReadManifestHead(string zip)
    {
        try
        {
            using (FileStream fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (ZipArchive z = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                ZipArchiveEntry e = z.GetEntry("manifest.json");
                if (e == null) return null;
                string txt;
                using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8)) txt = r.ReadToEnd();
                return new string[] { Grab(txt, "buildId"), Grab(txt, "created"), Grab(txt, "sourceComputer") };
            }
        }
        catch (Exception) { return null; }
    }

    static string Grab(string txt, string key)
    {
        Match m = Regex.Match(txt, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    static string LauncherJson() { return Path.Combine(root, @"GAMMA Coop\launcher.json"); }

    static List<string> SkippedBuilds(string gameRoot)
    {
        List<string> r = new List<string>();
        try
        {
            string f = Path.Combine(gameRoot, @"GAMMA Coop\launcher.json");
            if (!File.Exists(f)) return r;
            Dictionary<string, object> d = json.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
            object o;
            if (d != null && d.TryGetValue("skipped", out o) && o is System.Collections.IEnumerable)
                foreach (object x in (System.Collections.IEnumerable)o) if (x != null) r.Add(x.ToString());
        }
        catch (Exception) { }
        return r;
    }

    static void RememberSkip(string buildId)
    {
        try
        {
            List<string> l = SkippedBuilds(root);
            if (!l.Contains(buildId)) l.Add(buildId);
            while (l.Count > 20) l.RemoveAt(0);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["skipped"] = l;
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherJson()));
            File.WriteAllText(LauncherJson(), json.Serialize(d), new UTF8Encoding(false));
        }
        catch (Exception e) { Log("cannot remember skipped build: " + e.Message); }
    }

    // The newest build file (in dirs) that arrived after the last applied build, has a different buildId and was not skipped.
    // Returns {path, buildId, created (local text), source} or null. Static and free of UI so that a test can call it.
    internal static string[] FindNewBuild(string gameRoot, List<string> dirs)
    {
        string inst = Path.Combine(gameRoot, @"GAMMA Coop\installed.json");
        if (!File.Exists(inst)) return null;
        Dictionary<string, object> d = json.DeserializeObject(File.ReadAllText(inst, Encoding.UTF8)) as Dictionary<string, object>;
        if (d == null) return null;
        object o;
        string curId = d.TryGetValue("buildId", out o) && o != null ? o.ToString() : "";
        string at = d.TryGetValue("appliedAt", out o) && o != null ? o.ToString() : (d.TryGetValue("date", out o) && o != null ? o.ToString() : "");
        DateTime applied;
        if (!DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out applied)) return null;
        List<string> skipped = SkippedBuilds(gameRoot);
        string[] best = null;
        DateTime bestT = DateTime.MinValue;
        string[] patterns = { "GAMMA_Coop_build_*.zip", "GAMMA_Coop_pack.zip" };
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string pat in patterns)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, pat); } catch (Exception) { continue; }
                foreach (string f in files)
                {
                    try
                    {
                        if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.GetLastWriteTime(f) <= applied) continue;
                        string[] m = ReadManifestHead(f);
                        if (m == null || m[0].Length == 0 || m[0] == curId || skipped.Contains(m[0])) continue;
                        DateTime cu;
                        if (!DateTime.TryParse(m[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out cu)) cu = File.GetLastWriteTimeUtc(f);
                        if (best == null || cu > bestT)
                        {
                            best = new string[] { f, m[0], cu.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), m[2].Length > 0 ? m[2] : "?" };
                            bestT = cu;
                        }
                    }
                    catch (Exception) { }
                }
            }
        }
        return best;
    }

    // true: the update was started (the app takes over, this launcher must exit)
    static bool OfferUpdate()
    {
        try
        {
            string appBat = Path.Combine(root, @"GAMMA Coop\app\GAMMA Coop.bat");
            if (!File.Exists(appBat)) return false;
            string[] nb = FindNewBuild(root, SearchDirs());
            if (nb == null) return false;
            Log("new build found: " + nb[0] + " id " + nb[1]);
            DialogResult r = MessageBox.Show("Найдена новая сборка (" + nb[2] + ", с компьютера " + nb[3] + "). Обновить перед игрой?",
                "GAMMA Coop", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { RememberSkip(nb[1]); return false; }
            string req = Path.Combine(root, @"GAMMA Coop\app\installer\load_request.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(req));
            File.WriteAllText(req, nb[0] + "\r\n" + root, new UTF8Encoding(false));
            ProcessStartInfo psi = new ProcessStartInfo(appBat);
            psi.WorkingDirectory = Path.GetDirectoryName(appBat);
            psi.UseShellExecute = true;
            Process.Start(psi);
            Log("started the app to load the new build");
            return true;
        }
        catch (Exception e) { Log("update check failed: " + e.Message); return false; }
    }

    // ---------- main ----------

    static int SelfTest()
    {
        AttachConsole(-1);
        bool fresh;
        Dictionary<string, object> d = ReadData(out fresh);
        string o1 = "data file: " + (FindFile() ?? "(none)") + ", fresh=" + fresh;
        string o2 = "activity: " + json.Serialize(BuildActivity(d, fresh));
        int pipeNo = Discord.FindPipe();
        string o3 = pipeNo >= 0 ? "discord pipe found: discord-ipc-" + pipeNo : "discord pipe not found";
        foreach (string s in new string[] { o1, o2, o3 })
        {
            try { Console.Out.WriteLine(s); Console.Out.Flush(); } catch (Exception) { }
            Log("selftest: " + s);
        }
        return 0;
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            root = Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath));
            logPath = Path.Combine(Path.Combine(root, "GAMMA Coop"), "launcher.log");
        }
        catch (Exception) { }
        if (root == null) return 1;
        if (args.Length > 0 && args[0] == "--selftest")
        {
            try { return SelfTest(); } catch (Exception) { return 1; }
        }
        try { return Run(); }
        catch (Exception e) { Log("fatal: " + e); return 1; }
    }

    static int Run()
    {
        // Discord RPC does not work when the game is elevated and Discord is not.
        if (IsElevated())
        {
            string stamp = Path.Combine(Path.Combine(root, "GAMMA Coop"), "relaunch.stamp");
            bool recent = false;
            try { recent = File.Exists(stamp) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp)).TotalSeconds < 20; }
            catch (Exception) { }
            if (!recent)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stamp));
                    File.WriteAllText(stamp, "x");
                    Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\"");
                    Log("elevated, relaunched via explorer");
                    return 0;
                }
                catch (Exception e) { Log("relaunch failed: " + e.Message); }
            }
        }

        string gammaDir = Path.Combine(root, "GAMMA");
        string mo = Path.Combine(gammaDir, "ModOrganizer.exe");
        if (!File.Exists(mo))
        {
            MessageBox.Show("Не найден файл GAMMA\\ModOrganizer.exe рядом с лаунчером.\nУстановите GAMMA Coop заново или положите «GAMMA Coop.exe» в папку с GAMMA и ANOMALY.",
                "GAMMA Coop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        bool createdNew;
        using (Mutex mtx = new Mutex(true, "Local\\GAMMACoopLauncher_" + root.ToLowerInvariant().GetHashCode().ToString("x"), out createdNew))
        {
            if (!createdNew)
            {
                // another launcher already bridges RPC; just make sure MO2 is up
                if (!MoRunning()) StartMo(mo, gammaDir);
                return 0;
            }
            if (!MoRunning() && !GameRunning() && OfferUpdate()) return 0;
            if (!MoRunning()) StartMo(mo, gammaDir);
            Loop();
        }
        return 0;
    }

    static void StartMo(string mo, string dir)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(mo);
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = true;
            Process.Start(psi);
            Log("started ModOrganizer");
        }
        catch (Exception e) { Log("cannot start MO2: " + e.Message); }
    }

    static void Loop()
    {
        Stopwatch sw = Stopwatch.StartNew();
        long lastAlive = 5000; // 5 s + 15 s = ~20 s grace for MO2 to appear
        long nextRpc = 0;
        Discord dc = new Discord();
        string lastSent = null;

        while (true)
        {
            try
            {
                bool game = GameRunning();
                if (game || MoRunning()) lastAlive = sw.ElapsedMilliseconds;
                else if (sw.ElapsedMilliseconds - lastAlive > 15000) break;

                if (sw.ElapsedMilliseconds >= nextRpc)
                {
                    nextRpc = sw.ElapsedMilliseconds + UpdateEverySec * 1000;
                    string key;
                    Dictionary<string, object> act = null;
                    if (game)
                    {
                        bool fresh;
                        act = BuildActivity(ReadData(out fresh), fresh);
                        key = json.Serialize(act);
                    }
                    else key = "";

                    if (!dc.Connected) { lastSent = null; dc.Connect(); }
                    if (dc.Connected && key != lastSent)
                    {
                        try { dc.SetActivity(act); lastSent = key; }
                        catch (Exception e) { Log("discord update failed: " + e.Message); dc.Close(); lastSent = null; }
                    }
                }
            }
            catch (Exception e) { Log("loop error: " + e.Message); }
            Thread.Sleep(1000);
        }

        try { if (dc.Connected) dc.SetActivity(null); } catch (Exception) { }
        dc.Close();
        Log("nothing running, exit");
    }
}
