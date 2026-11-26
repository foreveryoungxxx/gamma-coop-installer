using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace GammaCoop
{
    public enum Phase { Idle, Running, Paused, RateLimit, Failed, Done }

    public class Item
    {
        public int Id;
        public string Name;
        public string Op;
        public int Stage;
        public double Pct = -1;
        public long Bytes, Total;
        public double Speed;
        public long LastBytes, Counted;
        public double LastT, Seen;
        public bool Finished;
    }

    public class SimEv
    {
        public double T;
        public int Idx;
        public string Raw;          // ready JSON line, or null for a progress event
        public string Name, Op;
        public double Prog;
        public long Bytes, Total;
        public int Cmp, Tot;
        public bool Mods;
    }

    // pinned version: installer\passport\passport.json
    public class Passport
    {
        public string Dir, Name, Channel, Xrrazom, CliVersion, CliSha;
        public Dictionary<string, string> Repos = new Dictionary<string, string>();
        public string ModPackMaker { get { return Path.Combine(Dir, "modpack_maker_list.txt"); } }
        public string ModList { get { return Path.Combine(Dir, "modlist.txt"); } }
        public string CliUrl { get { return "https://github.com/FaithBeam/stalker-gamma-cli/releases/download/" + CliVersion + "/stalker-gamma+win.x64.zip"; } }

        public static Passport Load(string dir)
        {
            Passport p = new Passport();
            p.Dir = dir;
            string txt = File.ReadAllText(Path.Combine(dir, "passport.json"), Encoding.UTF8);
            Dictionary<string, object> d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(txt);
            p.Name = Engine.S(d, "name");
            p.Channel = Engine.S(d, "channel");
            p.Xrrazom = Engine.S(d, "xrrazom");
            object o;
            if (d.TryGetValue("repos", out o))
            {
                Dictionary<string, object> rd = o as Dictionary<string, object>;
                if (rd != null) foreach (KeyValuePair<string, object> kv in rd) if (kv.Value != null) p.Repos[kv.Key] = kv.Value.ToString();
            }
            if (d.TryGetValue("cli", out o))
            {
                Dictionary<string, object> cd = o as Dictionary<string, object>;
                if (cd != null) { p.CliVersion = Engine.S(cd, "version"); p.CliSha = Engine.S(cd, "sha256").ToLowerInvariant(); }
            }
            if (p.Name.Length == 0 || p.CliVersion == null || p.CliSha == null) throw new Exception("passport.json неполный");
            return p;
        }
    }

    // Installation engine: runs (or simulates) the CLI, parses its NDJSON into thread-safe state.
    public class Engine
    {
        public const long ExpectedBytes = 52L * 1073741824L;
        public static readonly string[] StageNames = {
            "Программа установки", "Настройка", "Anomaly 1.5.3", "Репозитории GAMMA", "Моды",
            "Проверка Anomaly", "Уборка архива Anomaly",
            "Кооп xrRazom и озвучка", "Библиотеки Visual C++", "Ярлык и инструкция" };
        const string RateText = "ModDb temporarily blocked you";
        const double RateWait = 65 * 60;
        const int SimMods = 437;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool DeleteFileW(string name);

        public readonly object L = new object();
        public Phase Phase = Phase.Idle;
        public readonly string[] St = { "pending", "pending", "pending", "pending", "pending", "pending", "pending", "pending", "pending", "pending" };
        public readonly double[] StSeen = new double[10];
        public readonly bool[] StAny = new bool[10];
        public readonly int[] StCmp = new int[10];
        public readonly int[] StTot = new int[10];
        public readonly Dictionary<string, Item> Items = new Dictionary<string, Item>();
        public long DlBytes;
        public double Speed;
        public double DlStart = -1;
        public string Error = "";
        public string Note = "";
        public double WaitUntil;
        public double FreedGb;
        public double StartedAt = -1, FinishedAt;
        public bool FullOk;
        public long CacheBase;
        bool cacheMeasured;
        string pending;
        public bool Sim;
        public bool Repair, RepairVc, NeedReboot;
        public bool Load;           // [2] Загрузить сборку / [4] repair of a chosen build: the screen says "build applied"
        public bool Update;         // [U]: first bring GAMMA itself to the sender's version (pinned commits), then apply the build
        public string PackPath;     // the build file to use (null: the newest one found)
        public bool TakeKeys;       // also put the sender's key bindings into user.ltx / axr_options.ltx
        public PackPlan Plan;       // already verified build (null: verify inside the coop stage)
        public double CoopPct;
        bool checkDone;
        PackInfo applied;
        public ProgressFile Prog;   // null in simulation and for a repair of a folder that has no progress.json
        double lastBeat;

        readonly Action<string> log;
        readonly string root, toolDir, exe, cliData, appDir, beside;
        readonly Passport pass;
        readonly JavaScriptSerializer ser = new JavaScriptSerializer();
        readonly Queue<double[]> win = new Queue<double[]>();
        readonly List<string> recent = new List<string>();
        double winSum;
        int nextId;
        volatile string abort;
        Process cur;
        bool workerRunning, restartPending;
        bool resSeen, resOk;
        string resCmd, resErr, lastErr;
        int chkTotal, chkBad;

        // virtual clock (simulation) or real clock
        static readonly Stopwatch clk = Stopwatch.StartNew();
        double vnow;

        // simulation
        List<SimEv> ev;
        int simIdx;
        double simT;
        public double SimDuration;

        public Engine(string rootDir, Passport p, Action<string> logger, bool simulate, string appDirectory)
        {
            appDir = appDirectory; beside = Path.GetDirectoryName(appDirectory.TrimEnd('\\'));
            root = rootDir; pass = p; log = logger; Sim = simulate;
            toolDir = Path.Combine(root, @"tools\stalker-gamma");
            exe = Path.Combine(toolDir, "stalker-gamma.exe");
            cliData = Path.Combine(root, "cli");
        }

        public double Now() { return Sim ? vnow : clk.Elapsed.TotalSeconds; }

        public static string S(Dictionary<string, object> d, string k)
        {
            object o;
            if (d.TryGetValue(k, out o) && o != null) return o.ToString();
            return "";
        }

        static double Nd(object o)
        {
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        public void AddRecent(string s)
        {
            lock (L)
            {
                recent.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
                if (recent.Count > 300) recent.RemoveRange(0, 100);
            }
        }

        public string[] Recent(int n)
        {
            lock (L)
            {
                int from = Math.Max(0, recent.Count - n);
                return recent.GetRange(from, recent.Count - from).ToArray();
            }
        }

        // ------------------------------------------------------------ parser
        static int StageOf(string name)
        {
            if (name.StartsWith("Кооп-пакет")) return 7;
            if (name.StartsWith("vc_redist")) return 8;
            string n = name.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
            if (n.Contains("stalkergamma") || n.Contains("gammalargefiles") || n.Contains("teivazanomalygunslinger") || n.Contains("gammasetup")) return 3;
            string l = name.ToLowerInvariant();
            if (l.Contains("anomaly") && (l.Contains("1.5.3") || l.Contains("-full") || l.Trim() == "anomaly")) return 2;
            return 4;
        }

        public void Feed(string line)
        {
            if (line == null) return;
            line = line.Trim();
            if (line.Length == 0) return;
            log("CLI> " + line);
            if (line.IndexOf(RateText, StringComparison.OrdinalIgnoreCase) >= 0) RateHit();
            Dictionary<string, object> d = null;
            lock (L)
            {
                if (pending != null) { line = pending + "\n" + line; pending = null; }
            }
            if (line[0] == '{')
            {
                try { lock (L) { d = ser.DeserializeObject(line) as Dictionary<string, object>; } }
                catch (Exception) { d = null; }
                // a JSON line cut by a line break: keep it until the closing brace arrives
                if (d == null && !line.EndsWith("}") && line.Length < 65536)
                {
                    lock (L) { pending = line; }
                    return;
                }
            }
            if (d == null) { AddRecent(line); return; }
            string type = S(d, "type");
            if (type == "progress")
            {
                object o;
                double prog = -1;
                if (d.TryGetValue("progress", out o) && o != null) prog = Nd(o);
                bool fin = false, hasCnt = false;
                int cmp = 0, tot = 0;
                if (d.TryGetValue("complete", out o) && o != null)
                {
                    if (o is bool) fin = (bool)o; else { cmp = (int)Nd(o); hasCnt = true; }
                }
                if (d.TryGetValue("total", out o) && o != null) tot = (int)Nd(o);
                long bytes = 0, total = 0;
                if (d.TryGetValue("bytesDownloaded", out o) && o != null) bytes = (long)Nd(o);
                if (d.TryGetValue("totalBytes", out o) && o != null) total = (long)Nd(o);
                Progress(S(d, "name"), S(d, "operation"), prog, bytes, total, hasCnt, cmp, tot, fin, -1);
            }
            else if (type == "log")
            {
                string lvl = S(d, "level");
                string msg = S(d, "message");
                int nl = msg.IndexOf('\n');
                if (nl >= 0) msg = msg.Substring(0, nl).TrimEnd();
                AddRecent("[" + lvl + "] " + msg);
                if (lvl.IndexOf("err", StringComparison.OrdinalIgnoreCase) >= 0 || lvl.IndexOf("crit", StringComparison.OrdinalIgnoreCase) >= 0)
                    lock (L) { lastErr = S(d, "message"); }
            }
            else if (type == "checksum")
            {
                string st = S(d, "status");
                lock (L)
                {
                    chkTotal++;
                    if (!string.Equals(st, "Ok", StringComparison.OrdinalIgnoreCase)) chkBad++;
                }
            }
            else if (type == "sim")
            {
                int si = (int)Nd(d.ContainsKey("stage") ? d["stage"] : -1);
                lock (L) { if (si >= 0 && si < St.Length) St[si] = S(d, "state"); if (d.ContainsKey("pct")) CoopPct = Nd(d["pct"]); }
            }
            else if (type == "result")
            {
                string cmd = S(d, "command"), err = S(d, "error");
                bool ok = d.ContainsKey("success") && d["success"] is bool && (bool)d["success"];
                lock (L) { resSeen = true; resOk = ok; resCmd = cmd; resErr = err.Length > 0 ? err : null; }
                AddRecent("результат " + cmd + ": " + (ok ? "успех" : "ошибка " + err));
                if (Sim)
                {
                    if (cmd == "full-install") { if (ok) FullDone(); else Fail(err); }
                    else if (cmd == "anomaly check") CheckDone(ok);
                    else if (cmd == "coop") Finish();
                }
            }
        }

        void SafeFeed(string line)
        {
            try { Feed(line); }
            catch (Exception ex) { try { log("Ошибка разбора строки CLI: " + ex.Message); } catch (Exception) { } }
        }

        void Progress(string name, string op, double prog, long bytes, long total, bool hasCnt, int cmp, int tot, bool fin, int forceStage)
        {
            if (name.Length == 0) return;
            double now = Now();
            lock (L)
            {
                int stage = forceStage >= 0 ? forceStage : StageOf(name);
                Item it;
                if (!Items.TryGetValue(name, out it))
                {
                    it = new Item();
                    it.Id = ++nextId; it.Name = name; it.Stage = stage;
                    long b0 = (op == "Download" && bytes < 8388608L) ? 0 : bytes;
                    it.LastBytes = b0; it.Counted = b0; it.LastT = now;
                    Items[name] = it;
                }
                if (it.Op != null && it.Op != op) { it.Speed = 0; it.LastBytes = bytes; it.LastT = now; }
                it.Op = op;
                it.Pct = prog >= 0 ? Math.Min(1.0, prog) : (total > 0 ? Math.Min(1.0, (double)bytes / total) : -1);
                it.Bytes = bytes; it.Total = total; it.Seen = now;
                it.Finished = fin || op == "Skipped" || prog >= 1.0;
                if (bytes > it.LastBytes)
                {
                    double dt = now - it.LastT;
                    if (dt >= 0.5)
                    {
                        double inst = (bytes - it.LastBytes) / dt;
                        if (dt > 5) it.Speed = 0;
                        else it.Speed = it.Speed <= 0 ? inst : it.Speed * 0.6 + inst * 0.4;
                        it.LastBytes = bytes; it.LastT = now;
                    }
                }
                else if (bytes < it.LastBytes) { it.LastBytes = bytes; it.LastT = now; }
                if (op == "Download" && stage >= 2)
                {
                    if (bytes > it.Counted)
                    {
                        long delta = bytes - it.Counted;
                        it.Counted = bytes;
                        DlBytes += delta;
                        if (DlStart < 0) DlStart = now;
                        win.Enqueue(new double[] { now, delta });
                        winSum += delta;
                    }
                    else if (bytes < it.Counted) it.Counted = bytes;
                }
                StAny[stage] = true; StSeen[stage] = now;
                if (stage >= 2 && hasCnt && tot > 0) { StCmp[stage] = cmp; StTot[stage] = tot; }
            }
        }

        // called by the UI each frame
        public void Tick()
        {
            double now = Now();
            bool resume = false, beat = false;
            lock (L)
            {
                if (Prog != null && Phase == Phase.Running && now - lastBeat >= 60) { lastBeat = now; beat = true; }
                List<string> rm = new List<string>();
                foreach (KeyValuePair<string, Item> kv in Items)
                    if (now - kv.Value.Seen > 6 || (kv.Value.Finished && now - kv.Value.Seen > 1.0)) rm.Add(kv.Key);
                foreach (string k in rm) Items.Remove(k);
                while (win.Count > 0 && now - win.Peek()[0] > 3.0) winSum -= win.Dequeue()[1];
                double span = 3.0;
                if (DlStart >= 0 && now - DlStart < 3.0) span = Math.Max(1.0, now - DlStart);
                Speed = Phase == Phase.Running && winSum > 0 ? winSum / span : 0;
                if (Phase == Phase.RateLimit && now >= WaitUntil) resume = true;
            }
            if (beat) Prog.Beat();
            if (resume) Start();
        }

        // ------------------------------------------------------------ view helpers (call under L)
        public string StageState(int i, double now)
        {
            if (i < 2 || i > 4 || Repair) return St[i];
            if (FullOk) return "done";
            if (!StAny[i]) return "pending";
            if (Phase == Phase.Running && now - StSeen[i] < 5) return "active";
            for (int j = i + 1; j <= 4; j++) if (StAny[j]) return "done";
            return "active";
        }

        public double Overall()
        {
            if (Repair) return CoopPct;
            if (FullOk) return 1;
            int cs = -1;
            for (int i = 2; i <= 4; i++) if (StAny[i]) cs = i;
            if (cs < 0) return 0;
            if (StTot[cs] > 0) return Math.Min(1.0, (double)StCmp[cs] / StTot[cs]);
            double sum = 0; int n = 0;
            foreach (Item it in Items.Values) if (it.Stage == cs && it.Pct >= 0) { sum += it.Pct; n++; }
            return n > 0 ? sum / n : 0;
        }

        // ------------------------------------------------------------ control
        public void Start()
        {
            bool launch = false;
            lock (L)
            {
                if (Phase == Phase.Running || Phase == Phase.Done) return;
                Error = ""; Note = "";
                Phase = Phase.Running;
                if (StartedAt < 0) StartedAt = Now();
                Items.Clear();
                if (Sim) { abort = null; }
                else if (workerRunning) restartPending = true;   // the old worker is still winding down: it starts the new run itself
                else { abort = null; workerRunning = true; launch = true; }
            }
            lastBeat = Now();
            PgRun(true, null);
            AddRecent("запуск установки");
            log("Установка: старт/продолжение");
            if (Sim) { SimBegin(); return; }
            if (launch) LaunchWorker();
        }

        void LaunchWorker()
        {
            Thread w = new Thread(Worker);
            w.IsBackground = true;
            w.Start();
        }

        // synchronous kill for the quit / window-close path
        public void Shutdown()
        {
            bool wasDone;
            lock (L)
            {
                wasDone = Phase == Phase.Done;
                if (!wasDone) { abort = "stop"; Phase = Phase.Idle; restartPending = false; }
            }
            if (!wasDone) PgRun(false, null);
            KillCli();
        }

        public void Pause()
        {
            lock (L) { if (Phase != Phase.Running) return; abort = "pause"; Phase = Phase.Paused; }
            PgRun(false, null);
            AddRecent("пауза");
            log("Пауза");
            KillAsync();
        }

        public void Stop()
        {
            lock (L)
            {
                if (Phase == Phase.Idle || Phase == Phase.Done) return;
                abort = "stop"; Phase = Phase.Idle;
            }
            PgRun(false, null);
            AddRecent("остановлено");
            log("Остановлено пользователем");
            KillAsync();
        }

        void RateHit()
        {
            lock (L)
            {
                if (Phase != Phase.Running || abort != null) return;
                abort = "rate"; Phase = Phase.RateLimit; WaitUntil = Now() + RateWait;
            }
            PgRun(false, "ModDB ограничил скорость, ждём 65 минут");
            AddRecent("ModDB попросил подождать");
            log("ModDB: лимит, ждём 65 минут");
            KillAsync();
        }

        public void ForceFail(string text)
        {
            lock (L) { Phase = Phase.Failed; Error = text; }
        }

        void Fail(string msg)
        {
            lock (L) { if (abort != null) return; Phase = Phase.Failed; Error = msg; }
            PgRun(false, msg);
            log("ОШИБКА: " + msg);
            AddRecent("ОШИБКА: " + msg);
        }

        void KillAsync() { ThreadPool.QueueUserWorkItem(delegate (object o) { KillCli(); }); }

        void KillCli()
        {
            Process p;
            lock (L) { p = cur; }
            if (p == null) return;
            try
            {
                ProcessStartInfo k = new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F");
                k.CreateNoWindow = true; k.UseShellExecute = false;
                using (Process t = Process.Start(k)) t.WaitForExit(5000);
            }
            catch (Exception e) { log("taskkill: " + e.Message); }
        }

        // ------------------------------------------------------------ real pipeline
        void Worker()
        {
            try
            {
                if (!Repair && !checkDone)
                {
                    if (PgIs("check"))
                    {
                        // the check passed in an earlier run and the Anomaly archive is already deleted: nothing to download again
                        log("Этапы от «" + ProgressFile.NameOf("tool") + "» до «" + ProgressFile.NameOf("check") + "» уже сделаны раньше — пропускаю");
                        lock (L) { for (int i = 0; i < 7; i++) St[i] = "done"; FullOk = true; checkDone = true; }
                    }
                    else
                    {
                        if (PgIs("tool") && File.Exists(exe)) SkipStage("tool", 0);
                        else if (!RunStage("tool", StageTool)) return;
                        if (PgIs("config")) SkipStage("config", 1);
                        else if (!RunStage("config", StageConfig)) return;
                        if (PgIs("install")) { SkipStage("install", -1); FullDone(); }
                        else if (!RunStage("install", StageInstall)) return;
                        if (Update)
                        {
                            // GAMMA brought to the sender's version: the Anomaly check and the archive cleanup are not needed here
                            lock (L) { St[5] = "skip"; St[6] = "skip"; checkDone = true; }
                        }
                        else
                        {
                            StageBegin("check");
                            StageCheck();
                            if (abort != null || !checkDone) return;
                            log("Этап «" + ProgressFile.NameOf("check") + "»: завершён");
                        }
                    }
                }
                if (!Repair && PgIs("coop")) SkipStage("coop", 7);
                else if (!RunStage("coop", StageCoop)) return;
                if (!Repair || RepairVc)
                {
                    if (!Repair && PgIs("vc")) SkipStage("vc", 8);
                    else if (!RunStage("vc", StageVc)) return;
                }
                if (!Repair && PgIs("shortcut")) SkipStage("shortcut", 9);
                else if (!RunStage("shortcut", StageShortcut)) return;
                Finish();
            }
            catch (Exception e) { log("Исключение: " + e); Fail(e.Message); }
            finally
            {
                bool again;
                lock (L)
                {
                    again = restartPending;
                    restartPending = false;
                    if (again) abort = null; else workerRunning = false;
                }
                if (again) LaunchWorker();
            }
        }

        void SetSt(int i, string s) { lock (L) { St[i] = s; } }

        // ------------------------------------------------------------ progress.json helpers (all no-ops without Prog)
        bool PgIs(string id) { ProgressFile p = Prog; return p != null && p.IsDone(id); }
        void PgRun(bool running, string err) { ProgressFile p = Prog; if (p != null) p.SetRunning(running, err); }

        void StageBegin(string id)
        {
            log("Этап «" + ProgressFile.NameOf(id) + "»: начало");
            ProgressFile p = Prog;
            if (p != null) p.SetLast(id);
        }

        void StageEnd(string id)
        {
            log("Этап «" + ProgressFile.NameOf(id) + "»: готово");
            ProgressFile p = Prog;
            if (p != null) p.MarkDone(id);
        }

        bool RunStage(string id, Func<bool> f)
        {
            StageBegin(id);
            bool ok = f();
            if (ok) StageEnd(id);
            else log("Этап «" + ProgressFile.NameOf(id) + "»: не завершён" + (abort != null ? " (" + abort + ")" : ""));
            return ok;
        }

        void SkipStage(string id, int ui)
        {
            log("Этап «" + ProgressFile.NameOf(id) + "»: уже сделан раньше, пропускаю");
            if (ui >= 0) SetSt(ui, "done");
        }

        static string Sha256(string file)
        {
            using (FileStream f = File.OpenRead(file))
            using (SHA256 h = SHA256.Create())
            {
                byte[] b = h.ComputeHash(f);
                StringBuilder sb = new StringBuilder();
                foreach (byte x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        bool StageTool()
        {
            SetSt(0, "active");
            string marker = Path.Combine(toolDir, ".sha256");
            bool have = File.Exists(exe) && File.Exists(marker) && File.ReadAllText(marker).Trim() == pass.CliSha;
            if (!have)
            {
                string tools = Path.Combine(root, "tools");
                Directory.CreateDirectory(tools);
                string zip = Path.Combine(tools, "stalker-gamma.zip");
                log("Скачиваю программу установки: " + pass.CliUrl);
                if (!Download(pass.CliUrl, zip, "stalker-gamma.zip", 0)) return false;
                string sha = Sha256(zip);
                if (sha != pass.CliSha)
                {
                    try { Safety.DeleteFile(root, zip); } catch (Exception) { }
                    Fail("Контрольная сумма программы установки не совпала (ждали " + pass.CliSha.Substring(0, 12) + "…, получили " + sha.Substring(0, 12) + "…). Скачай ещё раз.");
                    return false;
                }
                Safety.DeleteDir(root, toolDir);
                ZipFile.ExtractToDirectory(zip, toolDir);
                foreach (string f in Directory.GetFiles(toolDir, "*", SearchOption.AllDirectories)) DeleteFileW(f + ":Zone.Identifier");
                File.WriteAllText(marker, pass.CliSha);
                Safety.DeleteFile(root, zip);
                log("Программа установки распакована: " + toolDir);
            }
            lock (L) { Items.Remove("stalker-gamma.zip"); St[0] = "done"; }
            return abort == null;
        }

        bool Download(string url, string dest, string name, int stage)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "GammaCoopInstaller/0.95";
            req.Timeout = 30000; req.ReadWriteTimeout = 30000;
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream f = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                long total = resp.ContentLength, got = 0;
                byte[] buf = new byte[81920];
                double last = 0;
                while (true)
                {
                    if (abort != null) return false;
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    f.Write(buf, 0, n); got += n;
                    double now = Now();
                    if (now - last > 0.2)
                    {
                        last = now;
                        Progress(name, "Download", total > 0 ? (double)got / total : -1, got, total, false, 0, 0, false, stage);
                    }
                }
                Progress(name, "Download", 1.0, got, got, false, 0, 0, false, stage);
            }
            return true;
        }

        bool StageConfig()
        {
            SetSt(1, "active");
            Directory.CreateDirectory(cliData);
            StringBuilder a = new StringBuilder("config create --json");
            a.Append(" --anomaly ").Append(Q(Path.Combine(root, "Anomaly")));
            a.Append(" --gamma ").Append(Q(Path.Combine(root, "GAMMA")));
            a.Append(" --cache ").Append(Q(Path.Combine(root, "cache")));
            a.Append(" --download-threads 4");
            string sha;
            if (pass.Repos.TryGetValue("Stalker_GAMMA", out sha)) a.Append(" --stalker-gamma-repo-branch ").Append(sha);
            if (pass.Repos.TryGetValue("gamma_large_files_v2", out sha)) a.Append(" --gamma-large-files-repo-branch ").Append(sha);
            if (pass.Repos.TryGetValue("teivaz_anomaly_gunslinger", out sha)) a.Append(" --teivaz-anomaly-gunslinger-repo-branch ").Append(sha);
            if (pass.Repos.TryGetValue("gamma_setup", out sha)) a.Append(" --gamma-setup-repo-branch ").Append(sha);
            int code = RunCli(a.ToString());
            if (abort != null) return false;
            bool ok; string err;
            lock (L) { ok = resSeen && resOk; err = resErr ?? lastErr; }
            if (!ok) { Fail("Не удалась настройка: " + (err ?? "код " + code)); return false; }
            SetSt(1, "done");
            return true;
        }

        bool StageInstall()
        {
            if (!cacheMeasured)
            {
                long sz = 0;
                try { string cd = Path.Combine(root, "cache"); if (Directory.Exists(cd)) foreach (string f in Directory.GetFiles(cd, "*", SearchOption.AllDirectories)) sz += new FileInfo(f).Length; }
                catch (Exception) { }
                lock (L) { CacheBase = sz; cacheMeasured = true; }
                log("В cache уже лежит " + (sz / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture) + " ГБ");
            }
            StringBuilder a = new StringBuilder("full-install --json --use-anomaly-mirror --add-folders-to-win-defender-exclusion --enable-long-paths");
            a.Append(" --mod-pack-maker-path ").Append(Q(pass.ModPackMaker));
            string modList = pass.ModList;
            PackInfo pk = PickPack();
            if (pk != null)
            {
                try
                {
                    string pm = Path.Combine(root, @"GAMMA Coop\pack_modlist.txt");
                    Coop.ExtractEntry(pk.Path, "profile/modlist.txt", pm);
                    modList = pm;
                    log("Список модов взят из пакета кооп: " + pk.Path);
                }
                catch (Exception e) { log("Список модов из пакета не прочитан, беру из passport: " + e.Message); }
            }
            a.Append(" --mod-list-path ").Append(Q(modList));
            a.Append(" --resume");   // without resume.txt the CLI only warns and starts from scratch
            int code = RunCli(a.ToString());
            if (abort != null) return false;
            bool ok; string err;
            lock (L) { ok = resSeen && resOk; err = resErr ?? lastErr; }
            if (!ok) { Fail(err ?? ("full-install завершился с кодом " + code)); return false; }
            FullDone();
            return true;
        }

        void StageCheck()
        {
            int code = RunCli("anomaly check --json");
            if (abort != null) return;
            bool ok;
            lock (L) { ok = resSeen && resOk; }
            CheckDone(ok);
        }

        void FullDone()
        {
            lock (L) { FullOk = true; St[5] = "active"; chkTotal = 0; chkBad = 0; }
            log("Установка завершена, проверяю Anomaly");
        }

        void CheckDone(bool cliOk)
        {
            bool all;
            lock (L)
            {
                all = cliOk && chkTotal > 0 && chkBad == 0;
                St[5] = all ? "done" : "warn";
                if (!all) Note = "Anomaly не прошла проверку (" + chkBad + " из " + chkTotal + ") — архив оставлен в cache.";
                else St[6] = "active";
                checkDone = true;
            }
            log("Проверка Anomaly: файлов " + chkTotal + ", плохих " + chkBad + ", итог " + (all ? "ок" : "НЕ ок"));
            ProgressFile p = Prog;
            if (all)
            {
                Cleanup();
                SetSt(6, "done");
                if (p != null) p.MarkDone("check");
            }
            else
            {
                // broken Anomaly: do not go on to coop and never mark the install finished.
                // Next run repeats the CLI install (--resume, archive is kept) and the check.
                if (p != null) p.Unmark("install");
                lock (L) { checkDone = false; }
                Fail("Anomaly не прошла проверку (" + chkBad + " из " + chkTotal + " файлов). Запусти установщик ещё раз — он докачает и перепроверит.");
            }
        }

        void Finish()
        {
            lock (L) { Phase = Phase.Done; FinishedAt = Now(); }
            ProgressFile p = Prog;
            if (p != null) { if (Repair) p.SetRunning(false, null); else p.SetFinished(); }
            log("Всё готово");
        }

        // ------------------------------------------------------------ coop stages
        // the build file to use: the chosen one, otherwise the newest one found
        PackInfo PickPack()
        {
            if (PackPath != null) return Coop.Probe(PackPath);
            return Coop.Newest(beside, root);
        }

        bool StageCoop()
        {
            SetSt(7, "active");
            PackInfo pk = PickPack();
            if (pk == null)
            {
                Fail("Нет файла сборки. Положи файл GAMMA_Coop_build_….zip в «Загрузки» или рядом с установщиком (рядом с «GAMMA Coop.bat») и нажми Enter, чтобы повторить. Если закроешь окно: положи файл, запусти установщик и выбери «Починить кооп».");
                return false;
            }
            applied = pk;
            try
            {
                Action<double, string> pr = delegate (double f, string what)
                {
                    lock (L) { CoopPct = f; }
                    Progress("Кооп-пакет", "Extract", f, 0, 0, false, 0, 0, false, 7);
                };
                Func<bool> ab = delegate () { return abort != null; };
                // the game may have been started since the screen asked: check again right before anything is moved
                if (Finder.GameRunning())
                {
                    Fail("Игра или Mod Organizer 2 запущены. Закрой их и нажми Enter, чтобы повторить. В игре ничего не изменено.");
                    return false;
                }
                if (Plan != null)
                {
                    // the file may have changed since it was checked on the diff screen: verify it again, then apply exactly that
                    string anm = Path.Combine(root, "Anomaly"), gmm = Path.Combine(root, "GAMMA");
                    PackPlan fresh = Coop.Verify(pk.Path, anm, gmm, log, delegate (double f, string what) { pr(0.4 * f, what); }, ab);
                    if (fresh == null) return false;
                    Coop.ApplyAndStore(pk.Path, fresh, anm, gmm, root, log, delegate (double f, string what) { pr(0.4 + 0.58 * f, what); }, ab, TakeKeys);
                }
                else
                    Coop.Install(pk.Path, Path.Combine(root, "Anomaly"), Path.Combine(root, "GAMMA"), root, log, pr, ab);
            }
            catch (Exception e) { log("Кооп: " + e); Fail("Кооп не поставился: " + e.Message); return false; }
            if (abort != null) return false;
            SetSt(7, "done");
            return true;
        }

        bool StageVc()
        {
            SetSt(8, "active");
            bool have = false;
            try
            {
                using (Microsoft.Win32.RegistryKey b = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (Microsoft.Win32.RegistryKey k = b.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"))
                {
                    if (k != null)
                        have = Convert.ToInt32(k.GetValue("Installed", 0)) == 1 && Convert.ToInt32(k.GetValue("Major", 0)) == 14 && Convert.ToInt32(k.GetValue("Minor", 0)) >= 40;
                }
            }
            catch (Exception e) { log("Реестр VC++: " + e.Message); }
            if (have) { log("Библиотеки Visual C++ уже стоят"); SetSt(8, "done"); return true; }
            try
            {
                string cache = Path.Combine(root, "cache");
                Directory.CreateDirectory(cache);
                string f = Path.Combine(cache, "vc_redist.x64.exe");
                log("Скачиваю Visual C++: https://aka.ms/vs/17/release/vc_redist.x64.exe");
                if (!Download("https://aka.ms/vs/17/release/vc_redist.x64.exe", f, "vc_redist.x64.exe", 8)) return false;
                ProcessStartInfo psi = new ProcessStartInfo(f, "/install /quiet /norestart");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                int code;
                using (Process p = Process.Start(psi)) { p.WaitForExit(); code = p.ExitCode; }
                log("vc_redist: код " + code);
                if (code != 0 && code != 1638 && code != 3010) { Fail("Не поставились библиотеки Visual C++ (код " + code + "). Поставь вручную: https://aka.ms/vs/17/release/vc_redist.x64.exe и нажми Enter."); return false; }
                if (code == 3010) lock (L) { NeedReboot = true; }
            }
            catch (Exception e) { log("VC++: " + e); Fail("Не удалось поставить Visual C++: " + e.Message); return false; }
            SetSt(8, "done");
            return abort == null;
        }

        bool StageShortcut()
        {
            SetSt(9, "active");
            try
            {
                string src = Path.Combine(appDir, @"launcher\Launcher.cs");
                string guide = Path.Combine(appDir, @"guide\guide.html");
                if (!File.Exists(src)) { Fail(@"Нет файла installer\launcher\Launcher.cs — ярлык не собрать."); return false; }
                if (!File.Exists(guide)) { Fail(@"Нет файла installer\guide\guide.html — инструкцию не положить."); return false; }
                string csc = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
                string exeOut = Path.Combine(root, "GAMMA Coop.exe");
                string args = "/nologo /target:winexe /optimize /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /out:" + Q(exeOut);
                string ico = Path.Combine(appDir, @"launcher\icon.ico");
                if (File.Exists(ico)) args += " /win32icon:" + Q(ico);
                args += " " + Q(src);
                ProcessStartInfo psi = new ProcessStartInfo(csc, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                log("Сборка лаунчера: csc " + args);
                string outp; int code;
                using (Process p = Process.Start(psi))
                {
                    string er = null;
                    Thread t = new Thread(delegate () { er = p.StandardError.ReadToEnd(); });
                    t.Start();
                    outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(); t.Join();
                    outp += er;
                    code = p.ExitCode;
                }
                log("csc: код " + code + "\n" + outp);
                if (code != 0) { Fail("Не собрался лаунчер (csc, код " + code + "): " + FirstLine(outp)); return false; }

                string guideName = "GAMMA Coop — как играть.html";
                File.Copy(guide, Path.Combine(root, guideName), true);
                string desk = Finder.DesktopDir();
                Directory.CreateDirectory(desk);
                File.Copy(guide, Path.Combine(desk, guideName), true);
                Coop.MakeShortcut(Path.Combine(desk, "GAMMA Coop.lnk"), exeOut, root);
                // the installer itself goes into the game folder, with a second shortcut that opens the hub ([2] Загрузить сборку)
                Coop.CopyApp(appDir, root, log);
                string appBat = Path.Combine(root, @"GAMMA Coop\app\GAMMA Coop.bat");
                if (File.Exists(appBat)) Coop.MakeShortcut(Path.Combine(desk, "GAMMA Coop — сборка.lnk"), appBat, Path.GetDirectoryName(appBat), exeOut);
                else log("Нет app\\GAMMA Coop.bat — второй ярлык не создан");
                PackInfo wi = applied ?? PickPack();
                Coop.WriteInstalled(root, wi);
                log("Ярлыки на рабочем столе и инструкция готовы, installed.json записан");
            }
            catch (Exception e) { log("Ярлык: " + e); Fail("Не удалось сделать ярлык и инструкцию: " + e.Message); return false; }
            SetSt(9, "done");
            return abort == null;
        }

        static string FirstLine(string s)
        {
            foreach (string l in s.Replace("\r", "").Split('\n')) if (l.Trim().Length > 0) return l.Trim();
            return "";
        }

        void Cleanup()
        {
            double freed = 0;
            if (Sim) freed = 6.0e9;
            else
            {
                string cache = Path.Combine(root, "cache");
                if (Directory.Exists(cache))
                    foreach (string f in Directory.GetFiles(cache, "Anomaly-1.5.3-Full*.7z"))
                    {
                        try
                        {
                            long len = new FileInfo(f).Length;
                            Safety.DeleteFile(root, f);
                            freed += len;
                            log("Удалён архив Anomaly: " + f + " (" + (len / 1073741824.0).ToString("F2", CultureInfo.InvariantCulture) + " ГБ)");
                        }
                        catch (Exception e) { log("Не удалось удалить " + f + ": " + e.Message); }
                    }
            }
            lock (L) { FreedGb = freed / 1073741824.0; }
            log("Уборка: освобождено " + (freed / 1073741824.0).ToString("F2", CultureInfo.InvariantCulture) + " ГБ");
        }

        int RunCli(string args)
        {
            lock (L) { resSeen = false; resOk = false; resCmd = null; resErr = null; lastErr = null; }
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = toolDir;
            psi.EnvironmentVariables["STALKER_GAMMA_APPDATA"] = cliData;
            Process p = new Process();
            p.StartInfo = psi;
            p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) SafeFeed(e.Data); };
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) SafeFeed(e.Data); };
            log("Запуск: stalker-gamma.exe " + args);
            p.Start();
            lock (L) { cur = p; }
            if (abort != null) KillCli();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // not WaitForExit() without timeout: a lingering grandchild would keep the pipes open forever
            while (!p.WaitForExit(500)) { }
            Thread.Sleep(500);   // let the reader drain the last lines
            int code = p.ExitCode;
            lock (L) { cur = null; }
            p.Dispose();
            log("Процесс завершён, код " + code);
            return code;
        }

        // ------------------------------------------------------------ simulation
        void SimBegin()
        {
            if (ev == null) { BuildSim(); if (!Repair) lock (L) { St[0] = "pending"; } }
        }

        // advance simulated time by dt seconds (UI thread only)
        public void Pump(double dt)
        {
            double v0 = vnow, s0 = simT, s1 = s0 + dt;
            if (Phase == Phase.Running && ev != null)
            {
                bool brk = false;
                while (simIdx < ev.Count && ev[simIdx].T <= s1)
                {
                    SimEv e = ev[simIdx++];
                    vnow = v0 + Math.Max(0, e.T - s0);
                    simT = Math.Max(simT, e.T);
                    Feed(e.Raw);
                    if (Phase != Phase.Running) { brk = true; break; }
                }
                if (!brk) simT = s1;
            }
            vnow = v0 + dt;
            Tick();
        }

        static string J(double d) { return d.ToString("0.####", CultureInfo.InvariantCulture); }

        SimEv Raw(double t, string json) { SimEv e = new SimEv(); e.T = t; e.Raw = json; return e; }

        SimEv Prg(double t, string name, string op, double prog, long bytes, long total, int cmp, int tot, bool mods)
        {
            SimEv e = new SimEv();
            e.T = t; e.Name = name; e.Op = op; e.Prog = prog; e.Bytes = bytes; e.Total = total; e.Cmp = cmp; e.Tot = tot; e.Mods = mods;
            return e;
        }

        void BuildSim()
        {
            Random r = new Random(95);
            List<SimEv> e = new List<SimEv>();
            if (Repair)
            {
                AddCoopSim(e, 0.5, !RepairVc);
                FinishSim(e, new List<double>());
                return;
            }
            e.Add(Raw(0.0, "{\"type\":\"sim\",\"stage\":0,\"state\":\"active\"}"));
            e.Add(Raw(2.0, "{\"type\":\"sim\",\"stage\":0,\"state\":\"done\"}"));
            e.Add(Raw(2.0, "{\"type\":\"sim\",\"stage\":1,\"state\":\"active\"}"));
            e.Add(Raw(3.0, "{\"type\":\"sim\",\"stage\":1,\"state\":\"done\"}"));
            double t = 3.5;
            e.Add(Raw(3.4, "{\"type\":\"log\",\"level\":\"Information\",\"message\":\"Anomaly 1.5.3: скачиваю с зеркала faith-beam.com\"}"));
            string an = "Anomaly-1.5.3-Full.mt-mx5.7z";
            long asz = 6000000000L;
            for (int s = 1; ; s++)
            {
                long b = Math.Min(asz, (long)(s * 70e6));
                e.Add(Prg(t + s, an, "Download", (double)b / asz, b, asz, 0, 1, false));
                if (b >= asz) { t += s; break; }
            }
            for (int k = 1; k <= 25; k++) e.Add(Prg(t + k, an, "Extract", k / 25.0, 0, 0, k == 25 ? 1 : 0, 1, false));
            t += 25;
            e.Add(Raw(t + 0.1, "{\"type\":\"log\",\"level\":\"Information\",\"message\":\"Репозитории GAMMA: клонирую 4 репозитория\"}"));
            string[] repos = { "Stalker_GAMMA", "gamma_large_files_v2", "teivaz_anomaly_gunslinger", "gamma_setup" };
            for (int i = 0; i < repos.Length; i++)
            {
                for (int k = 1; k <= 3; k++) e.Add(Prg(t + k, repos[i], "Download", k / 3.0, 0, 0, i + (k == 3 ? 1 : 0), 4, false));
                t += 3;
            }
            e.Add(Raw(t + 0.1, "{\"type\":\"log\",\"level\":\"Information\",\"message\":\"Моды: 437 аддонов, 4 потока\"}"));
            // addons
            double[] sizes = new double[SimMods];
            double sum = 0;
            for (int i = 0; i < SimMods; i++) { double u = r.NextDouble(); sizes[i] = Math.Exp(5.5 * u * u); sum += sizes[i]; }
            double scale = 40e9 / sum;
            string[] pre = { "Anomaly_Reborn", "Lost_Alpha_Ambience", "Weapons_Pack_HD", "Pripyat_Textures", "Zone_Sounds", "HUD_Overhaul", "Mutants_Redux", "Artefact_Ext", "Faction_War_Fix", "Outfit_Models" };
            double[] free = { t, t, t, t };
            List<double> ends = new List<double>();
            for (int i = 0; i < SimMods; i++)
            {
                int slot = 0;
                for (int k = 1; k < 4; k++) if (free[k] < free[slot]) slot = k;
                double s0 = free[slot];
                string name = pre[(i * 7) % pre.Length] + "_" + (i + 1).ToString("000") + (i % 3 == 0 ? ".7z" : ".zip");
                long size = Math.Max(1500000L, (long)(sizes[i] * scale));
                double end;
                if (i % 11 == 5)
                {
                    e.Add(Prg(s0 + 0.5, name, "Skipped", 1.0, size, size, 0, SimMods, true));
                    end = s0 + 0.6;
                }
                else
                {
                    double sp = (12 + 20 * r.NextDouble()) * 1e6;
                    double dur = Math.Max(1.0, size / sp);
                    for (double s = 1; s < dur; s += 1) e.Add(Prg(s0 + s, name, "Download", s / dur, (long)(s * sp), size, 0, SimMods, true));
                    e.Add(Prg(s0 + dur, name, "Download", 1.0, size, size, 0, SimMods, true));
                    e.Add(Prg(s0 + dur + 0.5, name, "CheckMd5", 1.0, 0, 0, 0, SimMods, true));
                    double ex = Math.Max(1.0, size / 150e6);
                    double te = s0 + dur + 1.0;
                    for (double s = 1; s < ex; s += 1) e.Add(Prg(te + s, name, "Extract", s / ex, 0, 0, 0, SimMods, true));
                    e.Add(Prg(te + ex, name, "Extract", 1.0, 0, 0, 0, SimMods, true));
                    end = te + ex;
                }
                ends.Add(end);
                free[slot] = end + 0.1;
            }
            ends.Sort();
            double tEnd = ends[ends.Count - 1] + 1.0;
            e.Add(Raw(ends[(int)(0.4 * SimMods)] + 0.05, "{\"type\":\"log\",\"level\":\"Warning\",\"message\":\"ModDb temporarily blocked you. Try again in 1 hour.\"}"));
            e.Add(Raw(tEnd, "{\"type\":\"result\",\"command\":\"full-install\",\"success\":true,\"exitCode\":0,\"error\":null}"));
            string[] fl = { "bin/xrEngine.exe", "bin/xrGame.dll", "bin/xrRender_R4.dll", "gamedata/configs/system.ltx", "gamedata/scripts/_g.script", "gamedata/meshes/actors/stalker_hero.ogf" };
            for (int i = 0; i < fl.Length; i++) e.Add(Raw(tEnd + 0.5 + i * 0.4, "{\"type\":\"checksum\",\"file\":\"" + fl[i] + "\",\"status\":\"Ok\"}"));
            e.Add(Raw(tEnd + 4, "{\"type\":\"result\",\"command\":\"anomaly check\",\"success\":true,\"exitCode\":0,\"error\":null}"));
            AddCoopSim(e, tEnd + 5, false);
            FinishSim(e, ends);
        }

        static string SimSt(int stage, string state, double pct)
        {
            return "{\"type\":\"sim\",\"stage\":" + stage + ",\"state\":\"" + state + "\",\"pct\":" + J(pct) + "}";
        }

        // coop, Visual C++, shortcut stages (repair: no Visual C++)
        void AddCoopSim(List<SimEv> e, double t, bool repair)
        {
            e.Add(Raw(t, SimSt(7, "active", 0)));
            for (int k = 1; k <= 6; k++) e.Add(Prg(t + k * 0.6, "Кооп-пакет", "Extract", k / 6.0, 0, 0, 0, 1, false));
            for (int k = 1; k <= 6; k++) e.Add(Raw(t + k * 0.6, SimSt(7, "active", k / 6.0)));
            t += 4.5;
            e.Add(Raw(t, SimSt(7, "done", 1)));
            if (!repair)
            {
                e.Add(Raw(t + 0.1, SimSt(8, "active", 1)));
                for (int k = 1; k <= 4; k++) e.Add(Prg(t + 0.5 * k, "vc_redist.x64.exe", "Download", k / 4.0, k * 6000000L, 24000000L, 0, 1, false));
                t += 3;
                e.Add(Raw(t, SimSt(8, "done", 1)));
            }
            e.Add(Raw(t + 0.1, SimSt(9, "active", 1)));
            e.Add(Raw(t + 2.0, SimSt(9, "done", 1)));
            e.Add(Raw(t + 2.1, "{\"type\":\"result\",\"command\":\"coop\",\"success\":true,\"exitCode\":0,\"error\":null}"));
        }

        void FinishSim(List<SimEv> e, List<double> ends)
        {
            // second pass: json lines for progress events, with the "complete" counter for addons
            for (int i = 0; i < e.Count; i++) e[i].Idx = i;
            e.Sort(delegate (SimEv a, SimEv b) { int c = a.T.CompareTo(b.T); return c != 0 ? c : a.Idx.CompareTo(b.Idx); });
            int ei = 0;
            foreach (SimEv x in e)
            {
                if (x.Raw != null) continue;
                int cmp = x.Cmp;
                if (x.Mods) { while (ei < ends.Count && ends[ei] <= x.T) ei++; cmp = ei; }
                x.Raw = "{\"type\":\"progress\",\"name\":\"" + x.Name + "\",\"operation\":\"" + x.Op + "\",\"progress\":" + J(x.Prog)
                    + ",\"bytesDownloaded\":" + x.Bytes + ",\"totalBytes\":" + x.Total + ",\"complete\":" + cmp + ",\"total\":" + x.Tot + "}";
            }
            ev = e;
            SimDuration = e[e.Count - 1].T;
        }
    }
}
