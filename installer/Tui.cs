using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GammaCoop
{
    public class Check
    {
        public string Id;
        public string Label;
        public string Status;   // pending | ok | warn | fail
        public string Text;
        public int Gen;
    }

    // text-field state: caret Pos, selection anchor Sel (-1 = none); pure logic, no drawing
    public class EditState
    {
        public const int Max = 80;
        public string Text = "";
        public int Pos = 0, Sel = -1;

        public bool HasSel { get { return Sel >= 0 && Sel != Pos; } }
        public int SelStart { get { return HasSel ? Math.Min(Sel, Pos) : Pos; } }
        public int SelEnd { get { return HasSel ? Math.Max(Sel, Pos) : Pos; } }

        public void Open(string s) { Text = s.Length > Max ? s.Substring(0, Max) : s; Pos = Text.Length; Sel = 0; }
        public void Replace(string s) { Text = s.Length > Max ? s.Substring(0, Max) : s; Pos = Text.Length; Sel = -1; }
        public void SelectAll() { Sel = 0; Pos = Text.Length; }

        public bool DeleteSel()
        {
            if (!HasSel) { Sel = -1; return false; }
            int a = SelStart, b = SelEnd;
            Text = Text.Substring(0, a) + Text.Substring(b);
            Pos = a; Sel = -1;
            return true;
        }

        public void Insert(char c)
        {
            DeleteSel();
            if (Text.Length >= Max) return;
            Text = Text.Substring(0, Pos) + c + Text.Substring(Pos);
            Pos++;
        }

        public void Backspace()
        {
            if (DeleteSel() || Pos == 0) return;
            Text = Text.Substring(0, Pos - 1) + Text.Substring(Pos);
            Pos--;
        }

        public void Delete()
        {
            if (DeleteSel() || Pos >= Text.Length) return;
            Text = Text.Substring(0, Pos) + Text.Substring(Pos + 1);
        }

        public void WordBackspace()
        {
            if (DeleteSel() || Pos == 0) return;
            int w = WordLeft(Text, Pos);
            Text = Text.Substring(0, w) + Text.Substring(Pos);
            Pos = w;
        }

        public void MoveTo(int p, bool extend)
        {
            if (extend) { if (Sel < 0) Sel = Pos; }
            else Sel = -1;
            Pos = Math.Max(0, Math.Min(Text.Length, p));
            if (Sel == Pos) Sel = -1;
        }

        public void Left(bool extend, bool word)
        {
            if (!extend && HasSel) { MoveTo(SelStart, false); return; }
            MoveTo(word ? WordLeft(Text, Pos) : Pos - 1, extend);
        }

        public void Right(bool extend, bool word)
        {
            if (!extend && HasSel) { MoveTo(SelEnd, false); return; }
            MoveTo(word ? WordRight(Text, Pos) : Pos + 1, extend);
        }

        static bool IsSep(char c) { return c == '\\' || c == '/' || c == ' ' || c == '.' || c == '_' || c == '-'; }

        public static int WordLeft(string s, int p)
        {
            while (p > 0 && IsSep(s[p - 1])) p--;
            while (p > 0 && !IsSep(s[p - 1])) p--;
            return p;
        }

        public static int WordRight(string s, int p)
        {
            while (p < s.Length && !IsSep(s[p])) p++;
            while (p < s.Length && IsSep(s[p])) p++;
            return p;
        }
    }

    public class SpeedResult
    {
        public long Bytes;
        public double Sec;
        public string Err;
    }

    public partial class App
    {
        const int W = 100;
        const int H = 35;
        const double NeedGb = 150.0;
        const string GhUrl = "https://github.com/FaithBeam/stalker-gamma-cli/releases/download/1.44.0/stalker-gamma+win.x64.zip";
        const string MirrorUrl = "https://files.faith-beam.com/anomaly/1.5.3/Anomaly-1.5.3-Full.mt-mx5.7z";

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int h);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint f);
        [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(CtrlDelegate h, bool add);
        delegate bool CtrlDelegate(uint type);
        static CtrlDelegate ctrlHandler;
        static App current;

        // window close / logoff / shutdown: kill the CLI tree before Windows ends the process
        static bool OnCtrl(uint type)
        {
            if (type == 2 || type == 5 || type == 6)
            {
                App a = current;
                try { if (a != null && a.eng != null) a.eng.Shutdown(); } catch (Exception) { }
            }
            return false;
        }
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // palette
        static readonly int[] Tone = { 0x0f2a12, 0x1c4a1f, 0x2c6e2c, 0x3f9a39, 0x5cc24c, 0x86e06c, 0xb8f59e, 0xe7ffd9 };
        const int CText = 0xe2f3da, CMuted = 0x86a37d, CDim = 0x3e5638, COk = 0x5fd068, CWarn = 0xf2c94c, CBad = 0xff5a4a;
        static readonly char[] Ramp = { ' ', '·', '•', '●' };
        static readonly string Spin = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        const string TitleText = "S . T . A . L . K . E . R .    G . A . M . M . A .    C O O P";
        const string ScrambleChars = "#%&*+=?@0123456789ABCDEF";

        static volatile bool quit = false;
        static readonly Stopwatch clock = Stopwatch.StartNew();

        string root;
        string logPath;
        readonly string startStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        FolderState fstate = FolderState.NotExists;
        int fcount;
        string pathErr, abruptFor;
        ProgressFile fprog;
        readonly object lk = new object();
        readonly object logLk = new object();
        readonly List<Check> checks = new List<Check>();
        readonly bool[] mods = { true, true, true, true };
        double freeGb = -1;
        string diskType = "";
        double ghSpeed = -1, mirSpeed = -1;
        bool repairAvail = false;

        bool editing = false;
        EditState ed = new EditState();
        int editScroll = 0;
        double lastCharT = -1;
        string msg = null;
        int msgColor = CWarn;
        double msgUntil = 0;

        char[] ch = new char[W * H];
        int[] col = new int[W * H];
        int[] bgc = new int[W * H];
        int gw = W, gh = H, ox = 0, oy = 0, sh = 0, br = 18;
        readonly StringBuilder sb = new StringBuilder(32768);

        public static int Run(string rootDir, bool headless, int frames, bool simulate)
        {
            App app = new App(rootDir, simulate);
            if (headless) return app.RunHeadless(frames);
            return app.RunTui();
        }

        App(string rootDir, bool simulateMode)
        {
            appDir = rootDir;
            Finder.AppDir = rootDir;
            simulate = simulateMode;
            string logs = Path.Combine(rootDir, "logs");
            Directory.CreateDirectory(logs);
            logPath = Path.Combine(logs, "gamma_coop_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            Safety.Log = Log;
            ProgressFile.Log = Log;
            Log("=== запуск " + startStamp + " ===");
            root = Directory.Exists(@"D:\") ? @"D:\GAMES\STALKER_GAMMA" : @"C:\GAMES\STALKER_GAMMA";
            // an unfinished install of an earlier run: offer to continue it
            try
            {
                string lrf = Path.Combine(appDir, "last_root.txt");
                if (!simulate && File.Exists(lrf))
                {
                    string lr = File.ReadAllText(lrf, Encoding.UTF8).Trim().TrimEnd('\\');
                    if (lr.Length > 0 && Safety.PathProblem(lr, appDir) == null && Safety.Inspect(lr) == FolderState.Ours)
                    {
                        ProgressFile lp = ProgressFile.Load(lr);
                        if (lp != null && !lp.Finished) root = lr;
                    }
                }
            }
            catch (Exception) { }
            // the "Загрузить сборку" request left by the launcher (new build found before the game)
            try
            {
                string req = Path.Combine(appDir, "load_request.txt");
                if (!simulate && File.Exists(req))
                {
                    string[] rl = File.ReadAllText(req, Encoding.UTF8).Replace("\r", "").Split('\n');
                    Safety.DeleteFile(appDir, req, false);
                    if (rl.Length > 0 && File.Exists(rl[0].Trim())) { loadFile = rl[0].Trim(); loadRoot = rl.Length > 1 && rl[1].Trim().Length > 0 ? rl[1].Trim() : null; }
                }
            }
            catch (Exception) { }
            AddCheck("admin", "права администратора");
            AddCheck("dpi", "обход DPI");
            AddCheck("space", "место на диске");
            AddCheck("disktype", "тип диска");
            AddCheck("github", "скорость GitHub");
            AddCheck("mirror", "скорость зеркала Anomaly");
            AddCheck("coop", "пакет кооп");
            Check mdb = new Check();
            mdb.Id = "moddb"; mdb.Label = "ModDB"; mdb.Status = "warn"; mdb.Text = "ModDB: проверится во время загрузки";
            checks.Add(mdb);
            RefreshFolder();
            Log("GAMMA Coop 0.95, шаг 1. ОС: " + Environment.OSVersion + ". Папка по умолчанию: " + root);
        }

        void AddCheck(string id, string label)
        {
            Check c = new Check();
            c.Id = id; c.Label = label; c.Status = "pending"; c.Text = "";
            checks.Add(c);
        }

        Check Get(string id)
        {
            foreach (Check c in checks) if (c.Id == id) return c;
            return null;
        }

        void Log(string s)
        {
            lock (logLk)
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine;
                try { File.AppendAllText(logPath, line, new UTF8Encoding(true)); }
                catch (Exception) { }
                if (installLog != null)
                {
                    try { File.AppendAllText(installLog, line, new UTF8Encoding(true)); }
                    catch (Exception) { }
                }
            }
        }

        // ------------------------------------------------------------ checks
        Thread StartCheck(string id)
        {
            Check c = Get(id);
            int gen;
            string r;
            lock (lk) { c.Gen++; gen = c.Gen; c.Status = "pending"; c.Text = ""; r = root; }
            Thread th = new Thread(delegate ()
            {
                string[] res;
                try { res = RunOne(id, r, gen); }
                catch (Exception e) { res = new string[] { "fail", "ошибка проверки: " + e.Message }; }
                lock (lk)
                {
                    if (c.Gen != gen) return;
                    c.Status = res[0]; c.Text = res[1];
                }
                Log("Проверка " + id + ": " + res[0] + " - " + res[1]);
            });
            th.IsBackground = true;
            th.Start();
            return th;
        }

        List<Thread> StartAll()
        {
            List<Thread> l = new List<Thread>();
            foreach (Check c in checks) if (c.Id != "moddb") l.Add(StartCheck(c.Id));
            return l;
        }

        string[] RunOne(string id, string r, int gen)
        {
            switch (id)
            {
                case "admin": return CheckAdmin();
                case "dpi": return CheckDpi();
                case "space": return CheckSpace(r, gen);
                case "disktype": return CheckDiskType(r, gen);
                case "github": return CheckGithub();
                case "mirror": return CheckMirror();
                case "coop": return CheckPack(r);
            }
            return new string[] { "fail", "неизвестная проверка" };
        }

        static string[] R(string st, string tx) { return new string[] { st, tx }; }

        static string[] CheckAdmin()
        {
            WindowsPrincipal p = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (p.IsInRole(WindowsBuiltInRole.Administrator)) return R("ok", "права администратора");
            return R("fail", "нет прав администратора — запусти .bat");
        }

        static string[] CheckDpi()
        {
            string[] names = { "winws", "goodbyedpi", "ciadpi", "zapret" };
            foreach (string n in names)
            {
                Process[] ps = Process.GetProcessesByName(n);
                bool found = ps.Length > 0;
                foreach (Process p in ps) p.Dispose();
                if (found) return R("fail", "запущен " + n + " — выключи на время установки");
            }
            return R("ok", "zapret / GoodbyeDPI / ByeDPI не запущены");
        }

        string[] CheckSpace(string r, int gen)
        {
            DriveInfo di = new DriveInfo(r.Substring(0, 1));
            if (!di.IsReady) { SetFree(-1, gen); return R("fail", "диск " + r.Substring(0, 2) + " недоступен"); }
            double gb = di.AvailableFreeSpace / 1073741824.0;
            SetFree(gb, gen);
            string n = gb.ToString("F0", CultureInfo.InvariantCulture);
            if (gb >= NeedGb) return R("ok", "свободно " + n + " ГБ (нужно 150)");
            return R("fail", "мало места: " + n + " ГБ из 150 нужно");
        }

        string[] CheckDiskType(string r, int gen)
        {
            char letter = char.ToUpper(r[0]);
            DriveInfo di = new DriveInfo(letter.ToString());
            if (!di.IsReady) { SetType("?", gen); return R("fail", "диск " + letter + ": недоступен"); }
            if (di.DriveType == DriveType.Removable) { SetType("EXT", gen); return R("fail", "внешний диск — гайд запрещает"); }
            if (di.DriveType == DriveType.Network) { SetType("EXT", gen); return R("fail", "сетевой диск не подходит"); }
            try
            {
                ManagementScope sc = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                sc.Connect();
                int diskNum = -1;
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(sc, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        object dl = o["DriveLetter"];
                        if (dl == null) continue;
                        if (Convert.ToInt32(dl) == (int)letter) { diskNum = Convert.ToInt32(o["DiskNumber"]); break; }
                    }
                }
                if (diskNum < 0) { SetType("?", gen); return R("warn", "тип диска не определён"); }
                int media = -1, bus = -1;
                bool found = false;
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(sc, new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk")))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        if (Convert.ToString(o["DeviceId"]) == diskNum.ToString())
                        {
                            media = Convert.ToInt32(o["MediaType"]);
                            bus = Convert.ToInt32(o["BusType"]);
                            found = true;
                            break;
                        }
                    }
                }
                if (!found) { SetType("?", gen); return R("warn", "тип диска не определён"); }
                if (bus == 7) { SetType("EXT", gen); return R("fail", "внешний диск — гайд запрещает"); }
                if (media == 4 || (media != 3 && bus == 17)) { SetType("SSD", gen); return R("ok", "диск SSD"); }
                if (media == 3) { SetType("HDD", gen); return R("warn", "HDD — будет медленно"); }
            }
            catch (Exception e)
            {
                Log("WMI ошибка: " + e.Message);
            }
            SetType("?", gen);
            return R("warn", "тип диска не определён");
        }

        void SetType(string t, int gen) { lock (lk) { if (Get("disktype").Gen == gen) diskType = t; } }

        void SetFree(double v, int gen) { lock (lk) { if (Get("space").Gen == gen) freeGb = v; } }

        static SpeedResult Measure(string url, bool range)
        {
            SpeedResult res = new SpeedResult();
            Stopwatch total = Stopwatch.StartNew();
            Stopwatch sw = new Stopwatch();
            HttpWebRequest req = null;
            Timer killer = null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "GammaCoopInstaller/0.95";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                if (range) req.AddRange(0, 104857599);
                HttpWebRequest rq = req;
                killer = new Timer(delegate (object o) { try { rq.Abort(); } catch (Exception) { } }, null, 8000, Timeout.Infinite);
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                {
                    int left = (int)Math.Max(500, 8000 - total.ElapsedMilliseconds);
                    s.ReadTimeout = left;
                    byte[] buf = new byte[65536];
                    sw.Start();
                    while (total.ElapsedMilliseconds < 8000)
                    {
                        int n = s.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        res.Bytes += n;
                    }
                    sw.Stop();
                    req.Abort();
                }
            }
            catch (Exception e)
            {
                res.Err = e.Message;
            }
            finally
            {
                if (killer != null) killer.Dispose();
            }
            res.Sec = sw.IsRunning ? sw.Elapsed.TotalSeconds : sw.Elapsed.TotalSeconds;
            if (res.Sec <= 0) res.Sec = Math.Max(total.Elapsed.TotalSeconds, 0.001);
            return res;
        }

        string[] CheckGithub()
        {
            SpeedResult s = Measure(GhUrl, false);
            double mbs = s.Bytes / 1048576.0 / s.Sec;
            lock (lk) { ghSpeed = (s.Bytes < 1048576) ? 0 : mbs; }
            if (s.Bytes < 1048576) return R("fail", "GitHub не открывается");
            string f = mbs.ToString("F1", CultureInfo.InvariantCulture);
            if (mbs >= 1.0) return R("ok", "GitHub: " + f + " МБ/с");
            return R("warn", "GitHub медленный: " + f + " МБ/с");
        }

        string BesideDir() { return Path.GetDirectoryName(appDir.TrimEnd('\\')); }

        string[] CheckPack(string r)
        {
            bool inst = File.Exists(Coop.InstalledJson(r)) || (File.Exists(Path.Combine(r, @"GAMMA\ModOrganizer.exe")) && Directory.Exists(Path.Combine(r, @"ANOMALY\bin")));
            lock (lk) { repairAvail = inst; }
            PackInfo p = Coop.Newest(BesideDir(), r);
            if (p != null) return R("ok", "сборка кооп от хоста · " + p.Date);
            return R("warn", "нет файла сборки — положи GAMMA_Coop_build_….zip в «Загрузки»");
        }

        string[] CheckMirror()
        {
            SpeedResult s = Measure(MirrorUrl, true);
            double mbs = s.Bytes / 1048576.0 / s.Sec;
            lock (lk) { mirSpeed = (s.Bytes < 1048576) ? 0 : mbs; }
            if (s.Bytes < 1048576) return R("fail", "зеркало Anomaly не открывается");
            string f = mbs.ToString("F1", CultureInfo.InvariantCulture);
            if (mbs >= 1.0) return R("ok", "зеркало Anomaly: " + f + " МБ/с");
            return R("warn", "зеркало Anomaly медленное: " + f + " МБ/с");
        }

        // ------------------------------------------------------------ rendering
        // size of the real window; computes centring offsets and banner size
        void Setup(int w, int h)
        {
            if (w != gw || h != gh || ch.Length != w * h) { gw = w; gh = h; ch = new char[w * h]; col = new int[w * h]; bgc = new int[w * h]; }
            int brMax = Math.Max(18, Math.Min(40, gw / 4));
            br = Math.Max(18, Math.Min(brMax, gh - 17));
            sh = br - 18;
            ox = (gw - W) / 2;
            oy = Math.Max(0, (gh - (H + sh)) / 2);
        }

        void Put(int x, int y, string s, int c) { PutBg(x, y, s, c, -1); }

        void PutBg(int x, int y, string s, int c, int bg)
        {
            int yy = y + oy + (y >= 18 ? sh : 0);
            if (yy < 0 || yy >= gh) return;
            for (int i = 0; i < s.Length; i++)
            {
                int xx = x + ox + i;
                if (xx < 0 || xx >= gw) continue;
                ch[yy * gw + xx] = s[i];
                col[yy * gw + xx] = c;
                bgc[yy * gw + xx] = bg;
            }
        }

        void Fill(int x1, int y1, int x2, int y2)
        {
            for (int y = y1; y <= y2; y++)
                for (int x = x1; x <= x2; x++) Put(x, y, " ", -1);
        }

        void PutC(int y, string s, int c) { Put((W - s.Length) / 2, y, s, c); }

        // [bracketed] parts in tone 7, rest in baseColor
        void Markup(int x, int y, string s, int baseColor)
        {
            bool inBr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[') inBr = true;
                Put(x + i, y, c.ToString(), inBr ? Tone[7] : baseColor);
                if (c == ']') inBr = false;
            }
        }

        static string Trunc(string s, int n)
        {
            if (s.Length <= n) return s;
            return s.Substring(0, n - 1) + "…";
        }

        void Box(int x1, int y1, int x2, int y2, string title)
        {
            for (int x = x1 + 1; x < x2; x++) { Put(x, y1, "─", CDim); Put(x, y2, "─", CDim); }
            for (int y = y1 + 1; y < y2; y++) { Put(x1, y, "│", CDim); Put(x2, y, "│", CDim); }
            Put(x1, y1, "╭", CDim); Put(x2, y1, "╮", CDim); Put(x1, y2, "╰", CDim); Put(x2, y2, "╯", CDim);
            if (title != null) Put(x1 + 2, y1, " " + title + " ", Tone[7]);
        }

        static double Smooth(double e)
        {
            if (e < 0) e = 0; if (e > 1) e = 1;
            return e * e * (3 - 2 * e);
        }

        static double WrapPi(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        void DrawBanner(double t)
        {
            int rows = br, cols = br * 4;
            double cx = (cols - 1) / 2.0, cy = (rows - 1) / 2.0, R = (rows - 2) / 2.0;
            int x0 = (gw - cols) / 2;
            double rot = t * 0.25;
            double pp = (t % 1.6) / 1.6;
            double rp = pp * 1.6 * R;
            double fade = 1 - pp;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    double dx = (x - cx) * 0.5, dy = y - cy;
                    double r = Math.Sqrt(dx * dx + dy * dy);
                    double a0 = Math.Atan2(dy, dx);
                    double d = 0; bool blade = false;
                    double nx = 0, ny = 0;
                    if (r > 1e-6) { nx = dx / r; ny = dy / r; }
                    double lit = -(nx * 0.6 + ny * 0.8);
                    if (r < 0.2 * R) { blade = true; d = 0.9; }
                    else if (r >= 0.3 * R && r <= R)
                    {
                        double a = a0 - rot;
                        for (int k = 0; k < 3; k++)
                        {
                            double ang = WrapPi(a + Math.PI / 2 - k * 2 * Math.PI / 3);
                            if (Math.Abs(ang) < Math.PI / 6)
                            {
                                double edge = Math.Min(Math.Min(r - 0.3 * R, R - r), r * (Math.PI / 6 - Math.Abs(ang)));
                                d = Math.Max(0.28, (0.70 + 0.22 * lit) * (0.55 + 0.45 * Smooth(edge)));
                                blade = true;
                                break;
                            }
                        }
                    }
                    double rw = Math.Abs(r - rp);
                    if (rw < 1.1 && pp < 0.98)
                    {
                        double w = (1 - rw / 1.1) * (0.4 + 0.6 * fade);
                        if (blade) d += 0.35 * w; else d = Math.Max(d, 0.32 * w);
                    }
                    if (Math.Abs(r - 1.12 * R) < 0.55 && x % 2 == 0 && Math.Sin((a0 + t * 0.25) * 18) > -0.5)
                        d = Math.Max(d, 0.2);
                    if (d > 0.02)
                    {
                        if (d > 1) d = 1;
                        int tone = (int)Math.Floor(d * 7 + 0.5);
                        if (tone < 0) tone = 0; if (tone > 7) tone = 7;
                        int level = 1 + Math.Min(2, (int)Math.Floor(d * 3));
                        int ax = x0 + x, ay = oy + y;
                        if (ax < 0 || ax >= gw || ay >= gh) continue;
                        int i = ay * gw + ax;
                        ch[i] = Ramp[level];
                        col[i] = Tone[tone];
                    }
                }
            }
        }

        void DrawTitle(double t)
        {
            string s = TitleText;
            double phase = t % 12.0;
            if (phase < 1.2)
            {
                int revealed = (int)Math.Floor(phase / 1.2 * s.Length);
                Random rnd = new Random((int)(t * 24));
                StringBuilder b = new StringBuilder(s);
                for (int i = revealed; i < b.Length; i++)
                {
                    char c = b[i];
                    if (c == ' ' || c == '.' || c == '·') continue;
                    b[i] = ScrambleChars[rnd.Next(ScrambleChars.Length)];
                }
                s = b.ToString();
            }
            PutC(19, s, Tone[7]);
            PutC(20, "GAMMA Coop 0.95  ·  xrRazom 1.5  ·  стабильная", CMuted);
        }

        static int StCol(string st)
        {
            if (st == "ok") return COk;
            if (st == "warn") return CWarn;
            if (st == "fail") return CBad;
            return CMuted;
        }

        void DrawCheck(int x, int y, int width, Check c, double t)
        {
            string icon; int ic; string text; int tc;
            if (c.Status == "pending")
            {
                icon = Spin[(int)(t * 12) % Spin.Length].ToString(); ic = Tone[5];
                text = c.Label + "…"; tc = CMuted;
            }
            else
            {
                ic = StCol(c.Status);
                icon = c.Status == "ok" ? "✓" : (c.Status == "fail" ? "✗" : (c.Id == "moddb" ? "~" : "!"));
                text = c.Text; tc = c.Status == "ok" ? CText : ic;
            }
            Put(x, y, icon, ic);
            Put(x + 2, y, Trunc(text, width - 2), tc);
        }

        void DrawScreen(double t, int w, int h)
        {
            Setup(w, h);
            for (int i = 0; i < ch.Length; i++) { ch[i] = ' '; col[i] = -1; bgc[i] = -1; }
            DrawBanner(t);
            DrawTitle(t);
            if (simulate) PutC(18, "РЕЖИМ ИМИТАЦИИ — ничего не качается и не ставится", CWarn);
            if (screen == 2 && eng != null) { DrawInstall(t); return; }
            if (screen == 3) { DrawHub(t); return; }

            double now = clock.Elapsed.TotalSeconds;
            lock (lk)
            {
                // VPN summary (row 21)
                Check g = Get("github"), m = Get("mirror");
                string rl = ResumeLine();
                if (rl != null) Markup((W - Math.Min(rl.Length, W - 2)) / 2, 21, Trunc(rl, W - 2), fprog.Finished ? COk : CWarn);
                else if (g.Status == "pending" || m.Status == "pending")
                    PutC(21, "VPN: проверяю скорость " + Spin[(int)(t * 12) % Spin.Length], CMuted);
                else if (ghSpeed >= 1.0 && mirSpeed >= 1.0)
                    PutC(21, "VPN: не нужен, всё открывается напрямую", COk);
                else
                    PutC(21, "VPN: GitHub/зеркало медленные — включи свой VPN и нажми [R]", CWarn);

                // PATH panel
                Box(3, 22, 48, 27, "ПУТЬ");
                Put(5, 23, Trunc(root, 42), CText);
                string l24 = "  Anomaly  +  GAMMA"; int c24 = CMuted;
                if (pathErr != null) { l24 = pathErr; c24 = CBad; }
                else if (fstate == FolderState.Foreign) { l24 = "В папке уже есть файлы (" + fcount + " шт.) ✗"; c24 = CBad; }
                else if (fstate == FolderState.ExistingGamma) { l24 = "уже стоит GAMMA — можно [K] починить"; c24 = CWarn; }
                else if (Resumable() && fprog.Running) { l24 = "прошлый запуск оборвался — продолжу"; c24 = CWarn; }
                Put(5, 24, Trunc(l24, 42), c24);
                Check cs = Get("space"), cd = Get("disktype");
                string line3; int c3;
                if (cs.Status == "pending" || cd.Status == "pending")
                {
                    line3 = "проверяю диск " + Spin[(int)(t * 12) % Spin.Length]; c3 = CMuted;
                }
                else
                {
                    string freePart = freeGb >= 0 ? freeGb.ToString("F0", CultureInfo.InvariantCulture) + " ГБ свободно · нужно 150" : "диск недоступен";
                    string typePart;
                    if (diskType == "SSD") typePart = "SSD ✓";
                    else if (diskType == "HDD") typePart = "HDD !";
                    else if (diskType == "EXT") typePart = "внешний диск ✗";
                    else typePart = "тип ?";
                    line3 = freePart + " · " + typePart;
                    if (cs.Status == "fail") line3 += " · мало места ✗";
                    c3 = (cs.Status == "fail" || cd.Status == "fail") ? CBad : ((cs.Status == "warn" || cd.Status == "warn") ? CWarn : COk);
                }
                Put(5, 25, Trunc(line3, 42), c3);
                Markup(5, 26, "[P] сменить папку", CMuted);

                // MODS panel
                Box(51, 22, 96, 27, "МОДЫ");
                string[] modNames = { "кооп xrRazom 1.5", "озвучка по группировкам", "выключить 6 «тарковских» озвучек", "Discord RPC" };
                for (int i = 0; i < 4; i++)
                {
                    Put(53, 23 + i, mods[i] ? "[x]" : "[ ]", mods[i] ? COk : CDim);
                    Put(57, 23 + i, modNames[i], mods[i] ? CText : CMuted);
                }

                // CHECKS panel
                Box(3, 28, 96, 33, "ПРОВЕРКИ");
                DrawCheck(5, 29, 46, Get("admin"), t);
                DrawCheck(5, 30, 46, Get("dpi"), t);
                DrawCheck(5, 31, 46, Get("space"), t);
                DrawCheck(5, 32, 46, Get("disktype"), t);
                DrawCheck(52, 29, 43, g, t);
                DrawCheck(52, 30, 43, m, t);
                DrawCheck(52, 31, 43, Get("moddb"), t);
                DrawCheck(52, 32, 43, Get("coop"), t);
            }

            // row 34: context-aware hotkeys / message
            string hk;
            if (screen == 1) hk = "[Enter] Согласен и начать   [Esc] Назад";
            else if (editing) hk = "[Enter] Сохранить   [Ctrl+V] Заменить путь   [Esc] Отмена";
            else hk = (Resumable() ? "[Enter] Продолжить   " : "[Enter] Начать   ") + (repairAvail ? "[K] Починить кооп   " : "") + "[P] Папка   [1-4] Моды   [R] Проверить снова   [Esc] В меню   [Q] Выход";
            if (screen == 0 && !editing && msg != null && now < msgUntil) PutC(34, msg, msgColor);
            else Markup((W - hk.Length) / 2, 34, hk, CMuted);
            PutC(35, "Ctrl + плюс / Ctrl + минус — крупнее/мельче", CDim);

            if (editing)
            {
                string h1 = @"Введи полный путь к папке, например D:\GAMES\STALKER_GAMMA (без пробелов и русских букв)";
                int bw = h1.Length + 6, x1 = (W - bw) / 2;
                Fill(x1, 14, x1 + bw, 20);
                Box(x1, 14, x1 + bw, 20, "ПАПКА");
                Put(x1 + 3, 15, h1, CMuted);
                Put(x1 + 3, 16, "Ctrl+V — заменить путь · Ctrl+A — выделить всё · Esc — отмена", CMuted);
                if (msg != null && now < msgUntil) Put(x1 + 3, 19, msg, msgColor);
                string pre = "Папка: ";
                Put(x1 + 3, 18, pre, CMuted);
                int fx = x1 + 3 + pre.Length, fw = bw - 6 - pre.Length;
                if (ed.Pos < editScroll) editScroll = ed.Pos;
                if (ed.Pos > editScroll + fw - 1) editScroll = ed.Pos - fw + 1;
                if (editScroll < 0) editScroll = 0;
                string vis = ed.Text.Substring(editScroll, Math.Min(fw, ed.Text.Length - editScroll));
                Put(fx, 18, vis, CText);
                if (ed.HasSel)
                {
                    int a = Math.Max(ed.SelStart, editScroll), b = Math.Min(ed.SelEnd, editScroll + vis.Length);
                    if (b > a) PutBg(fx + a - editScroll, 18, ed.Text.Substring(a, b - a), Tone[0], Tone[5]);
                }
                else if (((int)(t * 2)) % 2 == 0)
                {
                    int cx = ed.Pos - editScroll;
                    if (cx < vis.Length) PutBg(fx + cx, 18, vis.Substring(cx, 1), Tone[0], Tone[7]);
                    else Put(fx + cx, 18, "█", Tone[7]);
                }
            }

            if (screen == 1) DrawConsent();
        }

        string ToAnsi(bool sync)
        {
            sb.Length = 0;
            if (sync) sb.Append("\u001b[?2026h");
            sb.Append("\u001b[H");
            int cur = -2, curBg = -1;
            for (int y = 0; y < gh; y++)
            {
                sb.Append("\u001b[").Append(y + 1).Append(";1H");
                for (int x = 0; x < gw; x++)
                {
                    int i = y * gw + x;
                    char c = ch[i];
                    if (bgc[i] != curBg)
                    {
                        curBg = bgc[i];
                        if (curBg < 0) sb.Append("\u001b[49m");
                        else sb.Append("\u001b[48;2;").Append((curBg >> 16) & 255).Append(';').Append((curBg >> 8) & 255).Append(';').Append(curBg & 255).Append('m');
                    }
                    if (c != ' ' && col[i] != cur)
                    {
                        cur = col[i];
                        if (cur < 0) sb.Append("\u001b[39m");
                        else sb.Append("\u001b[38;2;").Append((cur >> 16) & 255).Append(';').Append((cur >> 8) & 255).Append(';').Append(cur & 255).Append('m');
                    }
                    sb.Append(c);
                }
            }
            sb.Append("\u001b[0m");
            if (sync) sb.Append("\u001b[?2026l");
            return sb.ToString();
        }

        string ToPlain()
        {
            StringBuilder b = new StringBuilder(gw * gh + gh);
            for (int y = 0; y < gh; y++)
            {
                b.Append(new string(ch, y * gw, gw).TrimEnd());
                b.Append('\n');
            }
            return b.ToString();
        }

        // ------------------------------------------------------------ input
        // ------------------------------------------------------------ install folder state (UI thread only)
        // re-reads the chosen folder: path rules, what is inside, progress.json of an unfinished install
        void RefreshFolder()
        {
            pathErr = null; fstate = FolderState.NotExists; fcount = 0; fprog = null;
            if (simulate) return;
            // the strict "only latin, no spaces" rule is for new installs; a game that is already there stays where it is
            pathErr = pathRelaxed ? null : Safety.PathProblem(root, appDir);
            if (pathErr != null) return;
            fstate = Safety.Inspect(root, out fcount);
            if (fstate == FolderState.Ours)
            {
                fprog = ProgressFile.Load(root);
                if (fprog != null && fprog.Running && !fprog.Finished && abruptFor != root)
                {
                    abruptFor = root;
                    Log("Прошлый запуск оборвался внезапно (выключение ПК или свет?) — продолжаю с места");
                }
            }
        }

        // null = the folder is fine for this action, otherwise the reason (Russian)
        string FolderBlocker(bool repair)
        {
            if (simulate) return null;
            if (pathErr != null) return pathErr;
            if (repair)
            {
                if (fstate == FolderState.Ours || fstate == FolderState.ExistingGamma) return null;
                return "Тут нечего чинить — нужна папка, где уже стоит игра";
            }
            if (fstate == FolderState.Foreign) return Safety.ForeignText(fcount);
            if (fstate == FolderState.ExistingGamma) return "Тут уже стоит GAMMA. Починить кооп — [K]; для новой установки выбери пустую папку";
            if (fstate == FolderState.Ours && fprog != null && fprog.Finished) return "Уже установлено. Починить кооп — [K]";
            return null;
        }

        bool Resumable() { return fstate == FolderState.Ours && fprog != null && !fprog.Finished; }

        string ResumeLine()
        {
            if (fstate != FolderState.Ours || fprog == null) return null;
            if (fprog.Finished) return "Уже установлено ✓   [K] починить кооп";
            DateTime u = fprog.UpdatedTime;
            string when = u == DateTime.MinValue ? "" : ", " + u.ToString("dd.MM HH:mm", Inv);
            return "Незаконченная установка: остановилась на «" + ProgressFile.NameOf(fprog.StopStage()) + "»" + when + ". [Enter] Продолжить";
        }

        void SetMsg(string text, int c, double secs)
        {
            msg = text; msgColor = c; msgUntil = clock.Elapsed.TotalSeconds + secs;
        }

        void LogState()
        {
            StringBuilder b = new StringBuilder();
            b.Append("Состояние: папка=").Append(root);
            b.Append("; моды=").Append(mods[0] ? "1" : "0").Append(mods[1] ? "1" : "0").Append(mods[2] ? "1" : "0").Append(mods[3] ? "1" : "0");
            lock (lk) { foreach (Check c in checks) b.Append("; ").Append(c.Id).Append('=').Append(c.Status); }
            Log(b.ToString());
        }

        void HandleKey(ConsoleKeyInfo k)
        {
            if (screen == 3) { HubKey(k); return; }
            if (screen != 0) { HandleInstKey(k); return; }
            if (IsPaste(k))
            {
                string pt = PasteText();
                if (pt.Length == 0) SetMsg("Буфер обмена пуст или недоступен", CWarn, 3);
                else { editing = true; ed.Replace(pt); lastCharT = -1; }
                return;
            }
            if (editing)
            {
                if (k.Key == ConsoleKey.Escape) { editing = false; Log("Смена папки отменена"); }
                else if (k.Key == ConsoleKey.Enter)
                {
                    // Enter right after a typed char is a line break inside a pasted burst, not a confirmation
                    if (clock.Elapsed.TotalSeconds - lastCharT < 0.05) return;
                    editing = false;
                    string p = ed.Text.Trim().Trim('"', '\'').Trim();
                    string err = Safety.PathProblem(p, appDir);
                    if (err != null) { SetMsg(err, CWarn, 5); Log("Путь отклонён: " + p + " (" + err + ")"); }
                    else
                    {
                        root = p.TrimEnd('\\');
                        pathRelaxed = false;
                        Log("Папка изменена: " + root);
                        RefreshFolder();
                        string why = FolderBlocker(false);
                        if (why != null) SetMsg(why, CWarn, 6);
                        StartCheck("space"); StartCheck("disktype"); StartCheck("coop");
                    }
                }
                else
                {
                    bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
                    bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
                    if (ctrl && k.Key == ConsoleKey.A) ed.SelectAll();
                    else if (k.KeyChar == (char)127 || k.KeyChar == (char)23) ed.WordBackspace(); // Ctrl+Backspace as DEL / Ctrl+W in some terminals
                    else if (k.Key == ConsoleKey.Backspace) { if (ctrl) ed.WordBackspace(); else ed.Backspace(); }
                    else if (k.Key == ConsoleKey.Delete) ed.Delete();
                    else if (k.Key == ConsoleKey.LeftArrow) ed.Left(shift, ctrl);
                    else if (k.Key == ConsoleKey.RightArrow) ed.Right(shift, ctrl);
                    else if (k.Key == ConsoleKey.Home) ed.MoveTo(0, shift);
                    else if (k.Key == ConsoleKey.End) ed.MoveTo(ed.Text.Length, shift);
                    else if (k.KeyChar == '"') { lastCharT = clock.Elapsed.TotalSeconds; } // quotes from "Copy as path" (Windows Terminal pastes as typed keys)
                    else if (k.KeyChar >= ' ' && k.KeyChar != (char)127) { ed.Insert(k.KeyChar); lastCharT = clock.Elapsed.TotalSeconds; }
                }
                return;
            }
            switch (k.Key)
            {
                case ConsoleKey.Q:
                    Log("Выход (клавиша)"); quit = true; break;
                case ConsoleKey.Escape:
                    HubHome(); break;
                case ConsoleKey.P:
                    editing = true; ed.Open(root); break;
                case ConsoleKey.R:
                    Log("Повторная проверка"); RefreshFolder(); StartAll(); break;
                case ConsoleKey.K:
                    {
                        if (!repairAvail) break;
                        bool adminBad, pending;
                        lock (lk) { adminBad = Get("admin").Status == "fail"; pending = Get("coop").Status == "pending"; }
                        if (adminBad) SetMsg("Нужны права администратора — запусти .bat", CBad, 4);
                        else if (pending) SetMsg("Подожди, проверки ещё идут", CWarn, 3);
                        else if (Coop.Newest(BesideDir(), root) == null) SetMsg("Нет файла сборки — положи GAMMA_Coop_build_….zip в «Загрузки» или рядом с установщиком", CBad, 5);
                        else
                        {
                            Log("Починка кооп");
                            screen = 3;   // the "close the game" page lives on the hub screen
                            WhenClosed(delegate () { BeginRepair(null); if (screen != 2) screen = 0; });
                        }
                        break;
                    }
                case ConsoleKey.D1: case ConsoleKey.NumPad1: Toggle(0); break;
                case ConsoleKey.D2: case ConsoleKey.NumPad2: Toggle(1); break;
                case ConsoleKey.D3: case ConsoleKey.NumPad3: Toggle(2); break;
                case ConsoleKey.D4: case ConsoleKey.NumPad4: Toggle(3); break;
                case ConsoleKey.Enter:
                    {
                        bool anyFail = false, anyPending = false;
                        lock (lk) { foreach (Check c in checks) { if (c.Status == "fail") anyFail = true; if (c.Status == "pending") anyPending = true; } }
                        LogState();
                        RefreshFolder();
                        string blocked = FolderBlocker(false);
                        if (blocked != null) { SetMsg(blocked, CBad, 6); Log("Начать нельзя: " + blocked); }
                        else if (anyFail) SetMsg("Сначала исправь пункты с ✗", CBad, 4);
                        else if (anyPending) SetMsg("Подожди, проверки ещё идут", CWarn, 3);
                        else
                        {
                            Log("Проверки зелёные, переход к установке");
                            if (consent) BeginInstall(); else screen = 1;
                        }
                        break;
                    }
            }
        }

        static bool IsPaste(ConsoleKeyInfo k)
        {
            if (k.KeyChar == '\u0016') return true;
            if (k.Key == ConsoleKey.V && (k.Modifiers & ConsoleModifiers.Control) != 0) return true;
            return k.Key == ConsoleKey.Insert && (k.Modifiers & ConsoleModifiers.Shift) != 0;
        }

        // clipboard text cleaned up for a path: first line, no spaces/quotes, no trailing backslash
        static string PasteText()
        {
            string raw = null;
            Thread th = new Thread(delegate ()
            {
                try { raw = System.Windows.Forms.Clipboard.GetText(); } catch (Exception) { raw = null; }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            if (!th.Join(2000) || raw == null) return "";
            foreach (string line in raw.Replace("\r", "").Split('\n'))
            {
                string s = line.Trim().Trim('"', '\'').Trim().TrimEnd('\\');
                if (s.Length > 0) return s;
            }
            return "";
        }

        void Toggle(int i)
        {
            mods[i] = !mods[i];
            Log("Мод " + (i + 1) + (mods[i] ? " включён" : " выключен"));
        }

        // ------------------------------------------------------------ modes
        int RunHeadless(int frames)
        {
            screen = 0;
            if (simulate) return RunHeadlessSim(frames);
            foreach (Thread th in StartAll()) th.Join();
            StreamWriter o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            foreach (Check c in checks)
            {
                o.WriteLine("{\"check\":\"" + c.Id + "\",\"status\":\"" + c.Status + "\",\"text\":\"" + Esc(c.Text) + "\"}");
            }
            o.WriteLine("{\"done\":true}");
            o.Flush();
            LogState();
            if (frames > 0)
            {
                StringBuilder all = new StringBuilder();
                double totalMs = 0;
                for (int i = 0; i < frames; i++)
                {
                    double t = 2.5 + i / 24.0;
                    Stopwatch sw = Stopwatch.StartNew();
                    DrawScreen(t, W, H);
                    ToAnsi(true);
                    sw.Stop();
                    totalMs += sw.Elapsed.TotalMilliseconds;
                    if (i > 0) all.Append("---\n");
                    all.Append(ToPlain());
                }
                DrawScreen(2.5, 160, 50);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(logPath), "frames_big.txt"), ToPlain(), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(logPath), "frames.txt"), all.ToString(), new UTF8Encoding(true));
                o.WriteLine("{\"frames\":" + frames + ",\"avg_ms\":" + (totalMs / frames).ToString("F3", CultureInfo.InvariantCulture) + "}");
                o.Flush();
            }
            return 0;
        }

        static string Esc(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.ToString();
        }

        static Mutex singleInstance;

        int RunTui()
        {
            bool first = true;
            try { singleInstance = new Mutex(true, @"Global\GammaCoopInstaller", out first); }
            catch (Exception) { first = true; }   // cannot create the mutex: do not block the install over it
            if (!first)
            {
                try { Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }
                Console.WriteLine("Установщик уже запущен в другом окне");
                Log("Второй запуск: установщик уже запущен в другом окне");
                Thread.Sleep(3000);
                return 0;
            }
            IntPtr hOut = GetStdHandle(-11);
            uint oldMode = 0;
            bool haveMode = false;
            TextWriter o = null;
            try
            {
                haveMode = GetConsoleMode(hOut, out oldMode);
                if (haveMode) SetConsoleMode(hOut, oldMode | 0x0004 | 0x0001);
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                current = this;
                ctrlHandler = new CtrlDelegate(OnCtrl);
                SetConsoleCtrlHandler(ctrlHandler, true);
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { e.Cancel = true; quit = true; };
            try
            {
                if (Console.WindowWidth < W || Console.WindowHeight < H)
                {
                    if (Console.BufferWidth < W) Console.BufferWidth = W;
                    if (Console.BufferHeight < H) Console.BufferHeight = H;
                    Console.SetWindowSize(Math.Max(Console.WindowWidth, W), Math.Max(Console.WindowHeight, H));
                }
            }
            catch (Exception) { }

                o = Console.Out;
                o.Write("\x1b[?1049h\x1b[?25l\x1b[2J");
                Log("Сон ПК запрещён, TUI запущен");
                StartAll();
                HubInit();
                if (CheckPendingUpdate()) { if (loadFile != null) Log("Сборка из лаунчера отложена: сначала незаконченное обновление"); }
                else if (loadFile != null) { Log("Сборка из лаунчера: " + loadFile); StartLoad(loadFile, loadRoot); }
                Stopwatch sw = Stopwatch.StartNew();
                long frameNo = 0;
                double lastPump = 0;
                int lastW = -1, lastH = -1;
                string lastSmall = null;
                bool clear = false;
                Stream so = Console.OpenStandardOutput();
                UTF8Encoding enc = new UTF8Encoding(false);
                while (!quit)
                {
                    while (Console.KeyAvailable) HandleKey(Console.ReadKey(true));
                    if (quit) break;
                    PumpHub();
                    double nowT = sw.Elapsed.TotalSeconds;
                    if (eng != null && simulate) eng.Pump((nowT - lastPump) * 30);
                    lastPump = nowT;
                    int cw = W, chh = H;
                    try { cw = Console.WindowWidth; chh = Console.WindowHeight; } catch (Exception) { }
                    string outp = null;
                    if (cw != lastW || chh != lastH) { clear = true; lastW = cw; lastH = chh; lastSmall = null; }
                    if (cw < W || chh < H)
                    {
                        string m = "Окно маленькое: растяни до 100×35 или разверни на весь экран (сейчас " + cw + "×" + chh + ")";
                        if (m != lastSmall)
                        {
                            lastSmall = m;
                            int x = Math.Max(1, (cw - m.Length) / 2 + 1), y = Math.Max(1, chh / 2);
                            outp = "\u001b[?2026h\u001b[2J\u001b[" + y + ";" + x + "H\u001b[38;2;242;201;76m" + m + "\u001b[0m\u001b[?2026l";
                            clear = false;
                        }
                    }
                    else
                    {
                        DrawScreen(2.5 + sw.Elapsed.TotalSeconds, cw, chh);
                        outp = ToAnsi(true);
                        if (clear) { outp = outp.Replace("\u001b[?2026h", "\u001b[?2026h\u001b[2J"); clear = false; }
                    }
                    if (outp != null)
                    {
                        byte[] bytes = enc.GetBytes(outp);
                        so.Write(bytes, 0, bytes.Length);
                        so.Flush();
                    }
                    frameNo++;
                    double wait = frameNo * 1000.0 / 24 - sw.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep((int)wait);
                    else if (wait < -200) frameNo = (long)(sw.ElapsedMilliseconds * 24 / 1000);
                }
            }
            finally
            {
                try { if (eng != null) eng.Shutdown(); } catch (Exception) { }
                try { if (o == null) o = Console.Out; o.Write("\x1b[0m\x1b[?25h\x1b[?1049l"); o.Flush(); } catch (Exception) { }
                SetThreadExecutionState(ES_CONTINUOUS);
                if (haveMode) SetConsoleMode(hOut, oldMode);
                Log("Выход, сон ПК снова разрешён");
                try { singleInstance.ReleaseMutex(); } catch (Exception) { }
            }
            return 0;
        }
    }
}
