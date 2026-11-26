using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace GammaCoop
{
    public class PackInfo
    {
        public string Path, Build, Xrrazom, Created, Where = "";
        public string BuildId = "", Source = "", Profile = "";
        public int Format = 1;
        public bool HasKeybinds, HasFiles;   // HasFiles: format 3, the build carries the host's file list (files.manifest)
        public long Size;
        public DateTime CreatedUtc, FileTimeUtc;
        public List<string> Mods = new List<string>();
        public Dictionary<string, string> Gamma = new Dictionary<string, string>();
        public string Date { get { return CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture); } }
        public string When { get { return CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture); } }
    }

    // a build file whose every checksum and path has been verified; nothing on disk has been touched yet
    public class PackPlan
    {
        public PackInfo Info;
        public List<string> Paths = new List<string>(), Shas = new List<string>();
        public List<long> Sizes = new List<long>();
        public Dictionary<string, string> ShaByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long Total;
        public List<string> Mods = new List<string>();
    }

    // what applying a plan would change (shown before anything is applied)
    public class PackDiff
    {
        public bool Same;
        public string LocalBuildId = "";
        public List<string> Add = new List<string>(), Update = new List<string>(), Keep = new List<string>(), Remove = new List<string>();
        public bool BinChanged, GdChanged;
        public List<string> GammaDiff = new List<string>();   // repos whose commit differs from the sender's
    }

    public class InstalledInfo
    {
        public string BuildId = "", Source = "", Created = "";
        public List<string> Mods = new List<string>();
        public Dictionary<string, string> Gamma = new Dictionary<string, string>();
        public bool HasMods;
        public DateTime AppliedAt = DateTime.MinValue;
    }

    // "build file from the host": building the zip (make_pack.ps1 / [3] Сохранить сборку) and putting it into a game (stage / [2] Загрузить сборку)
    public static partial class Coop
    {
        public const string PackName = "GAMMA_Coop_pack.zip";          // old name, still accepted
        public const string BuildPrefix = "GAMMA_Coop_build_";
        public const string BuildName = "GAMMA Coop 0.95";
        public const string XrrazomVer = "1.5";
        public static readonly string[] Repos = { "Stalker_GAMMA", "gamma_large_files_v2", "teivaz_anomaly_gunslinger" };
        // only for old format=1 packs: they have no mod list inside
        public static readonly string[] ModNames = { "xrRazom Co-op", "Faction-Based Voiced Actor", "Discord RPC", "GAMMA Coop - Silent Inspect Voice" };
        public static readonly string[] BinFiles = {
            "AnomalyDX8.exe", "AnomalyDX8AVX.exe", "AnomalyDX9.exe", "AnomalyDX9AVX.exe",
            "AnomalyDX10.exe", "AnomalyDX10AVX.exe", "AnomalyDX11.exe", "AnomalyDX11AVX.exe",
            "GameNetworkingSockets.dll", "steam_api64.dll", "steam_appid.txt" };

        public static string NewBuildName() { return BuildPrefix + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip"; }
        public static string BuildsDir(string root) { return System.IO.Path.Combine(root, @"GAMMA Coop\builds"); }
        public static string InstalledJson(string root) { return System.IO.Path.Combine(root, @"GAMMA Coop\installed.json"); }

        static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static string Sha(Stream s)
        {
            using (SHA256 h = SHA256.Create()) return Hex(h.ComputeHash(s));
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            if (d.TryGetValue(k, out o) && o != null) return o.ToString();
            return "";
        }

        static JavaScriptSerializer Ser()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }

        static Dictionary<string, object> ReadJson(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                return Ser().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (Exception) { return null; }
        }

        static bool ValidName(string n)
        {
            return n != null && n.Length > 0 && n != "." && n != ".." && n.IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0;
        }

        // ------------------------------------------------------------ reading a build file
        static Dictionary<string, object> ReadManifest(ZipArchive z)
        {
            ZipArchiveEntry e = z.GetEntry("manifest.json");
            if (e == null) throw new Exception("в файле нет manifest.json");
            string txt;
            using (StreamReader r = new StreamReader(e.Open(), new UTF8Encoding(false), true)) txt = r.ReadToEnd();
            Dictionary<string, object> d = Ser().DeserializeObject(txt) as Dictionary<string, object>;
            if (d == null) throw new Exception("manifest.json повреждён");
            return d;
        }

        static PackInfo InfoFrom(string zipPath, Dictionary<string, object> d)
        {
            PackInfo p = new PackInfo();
            p.Path = zipPath; p.Build = Str(d, "build"); p.Xrrazom = Str(d, "xrrazom"); p.Created = Str(d, "created");
            p.HasKeybinds = Str(d, "hasKeybinds").ToLowerInvariant() == "true";
            p.BuildId = Str(d, "buildId"); p.Source = Str(d, "sourceComputer"); p.Profile = Str(d, "profile");
            int.TryParse(Str(d, "format"), NumberStyles.Integer, CultureInfo.InvariantCulture, out p.Format);
            if (p.Format < 1) p.Format = 1;
            try { p.Size = new FileInfo(zipPath).Length; } catch (Exception) { }
            if (!DateTime.TryParse(p.Created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out p.CreatedUtc))
                p.CreatedUtc = File.GetLastWriteTimeUtc(zipPath);
            IEnumerable ml = d.ContainsKey("mods") ? d["mods"] as IEnumerable : null;
            if (p.Format >= 2 && ml != null) { foreach (object o in ml) if (o != null) p.Mods.Add(o.ToString()); }
            else p.Mods.AddRange(ModNames);
            Dictionary<string, object> gd = d.ContainsKey("gamma") ? d["gamma"] as Dictionary<string, object> : null;
            if (gd != null) foreach (KeyValuePair<string, object> kv in gd) if (kv.Value != null) p.Gamma[kv.Key] = kv.Value.ToString();
            return p;
        }

        // null if the file is missing or is not a valid build
        public static PackInfo Probe(string zipPath)
        {
            try
            {
                if (!File.Exists(zipPath)) return null;
                using (ZipArchive z = ZipFile.OpenRead(zipPath))
                {
                    PackInfo pi = InfoFrom(zipPath, ReadManifest(z));
                    pi.HasFiles = z.GetEntry(Integrity.ManifestEntry) != null;
                    return pi;
                }
            }
            catch (Exception) { return null; }
        }

        // the newest build file found anywhere (Downloads, LocalSend, Desktop, beside the installer, inside the game folder)
        public static PackInfo Newest(string beside, string root)
        {
            List<PackInfo> l = Finder.FindBuildFiles(root, beside);
            return l.Count > 0 ? l[0] : null;
        }

        public static void ExtractEntry(string zipPath, string entry, string dest)
        {
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry e = z.GetEntry(entry);
                if (e == null) throw new Exception("в сборке нет " + entry);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                WriteEntry(e, dest, null);
            }
        }

        // the passport of the sender (pinned commits, cli) + his mod lists, as a passport folder for the engine
        public static void ExtractPassport(string zipPath, string destDir, string fallbackDir)
        {
            Directory.CreateDirectory(destDir);
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                Dictionary<string, object> man = ReadManifest(z);
                Dictionary<string, object> pd = man.ContainsKey("passport") ? man["passport"] as Dictionary<string, object> : null;
                if (pd == null) pd = ReadJson(System.IO.Path.Combine(fallbackDir, "passport.json"));
                if (pd == null) throw new Exception("нет passport.json");
                File.WriteAllText(System.IO.Path.Combine(destDir, "passport.json"), Ser().Serialize(pd), new UTF8Encoding(false));
                ZipArchiveEntry ml = z.GetEntry("profile/modlist.txt");
                if (ml == null) throw new Exception("в сборке нет profile/modlist.txt");
                WriteEntry(ml, System.IO.Path.Combine(destDir, "modlist.txt"), null);
                ZipArchiveEntry mm = z.GetEntry("passport/modpack_maker_list.txt");
                if (mm != null) WriteEntry(mm, System.IO.Path.Combine(destDir, "modpack_maker_list.txt"), null);
                else File.Copy(System.IO.Path.Combine(fallbackDir, "modpack_maker_list.txt"), System.IO.Path.Combine(destDir, "modpack_maker_list.txt"), true);
            }
        }

        // expectSha != null: the content is hashed while it is written and must match
        static void WriteEntry(ZipArchiveEntry e, string dest, string expectSha)
        {
            using (Stream s = e.Open())
            using (FileStream f = new FileStream(dest, FileMode.Create, FileAccess.Write))
            using (SHA256 h = SHA256.Create())
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { f.Write(buf, 0, n); h.TransformBlock(buf, 0, n, null, 0); }
                h.TransformFinalBlock(buf, 0, 0);
                if (expectSha != null && Hex(h.Hash) != expectSha) throw new Exception("Файл сборки изменился во время установки (не совпала контрольная сумма): " + e.FullName);
            }
            try { File.SetLastWriteTime(dest, e.LastWriteTime.DateTime); } catch (Exception) { }
        }

        // ------------------------------------------------------------ the sender's GAMMA version
        // MO2 profile in use: ModOrganizer.ini selected_profile=@ByteArray(name); G.A.M.M.A by default
        public static string ActiveProfile(string gamma)
        {
            try
            {
                string ini = System.IO.Path.Combine(gamma, "ModOrganizer.ini");
                if (File.Exists(ini))
                    foreach (string line in File.ReadAllLines(ini, Encoding.UTF8))
                    {
                        if (!line.StartsWith("selected_profile=", StringComparison.OrdinalIgnoreCase)) continue;
                        string v = line.Substring(17).Trim();
                        if (v.StartsWith("@ByteArray(") && v.EndsWith(")")) v = v.Substring(11, v.Length - 12);
                        if (ValidName(v)) return v;
                    }
            }
            catch (Exception) { }
            return "G.A.M.M.A";
        }

        // HEAD commit of a git repo folder (loose ref file, or packed-refs); null when unreadable
        static string ReadHead(string repoDir)
        {
            try
            {
                string git = System.IO.Path.Combine(repoDir, ".git");
                string head = System.IO.Path.Combine(git, "HEAD");
                if (!File.Exists(head)) return null;
                string h = File.ReadAllText(head).Trim();
                string sha = null;
                if (h.StartsWith("ref:"))
                {
                    string rf = h.Substring(4).Trim();
                    string rfile = System.IO.Path.Combine(git, rf.Replace('/', '\\'));
                    if (File.Exists(rfile)) sha = File.ReadAllText(rfile).Trim();
                    else
                    {
                        string packed = System.IO.Path.Combine(git, "packed-refs");
                        if (File.Exists(packed))
                            foreach (string line in File.ReadAllLines(packed))
                            {
                                if (line.StartsWith("#") || line.StartsWith("^")) continue;
                                int sp = line.IndexOf(' ');
                                if (sp > 0 && line.Substring(sp + 1).Trim() == rf) { sha = line.Substring(0, sp); break; }
                            }
                    }
                }
                else sha = h;
                if (sha != null && System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-fA-F]{40}$")) return sha.ToLowerInvariant();
            }
            catch (Exception) { }
            return null;
        }

        // GAMMA version of a game folder: HEAD of the three repos in GAMMA\.Grok's Modpack Installer\resources;
        // a repo that cannot be read falls back to fallback[repo] (the installer's passport) when given
        public static Dictionary<string, string> GammaCommits(string gammaDir, IDictionary<string, string> fallback)
        {
            Dictionary<string, string> r = new Dictionary<string, string>();
            string res = System.IO.Path.Combine(gammaDir, @".Grok's Modpack Installer\resources");
            foreach (string repo in Repos)
            {
                string sha = ReadHead(System.IO.Path.Combine(res, repo));
                string fb;
                if (sha == null && fallback != null && fallback.TryGetValue(repo, out fb) && fb != null && fb.Length > 0) sha = fb;
                if (sha != null) r[repo] = sha;
            }
            return r;
        }

        public static Dictionary<string, string> PassportRepos(string passportDir)
        {
            Dictionary<string, string> r = new Dictionary<string, string>();
            Dictionary<string, object> d = ReadJson(System.IO.Path.Combine(passportDir, "passport.json"));
            Dictionary<string, object> rd = d != null && d.ContainsKey("repos") ? d["repos"] as Dictionary<string, object> : null;
            if (rd != null) foreach (KeyValuePair<string, object> kv in rd) if (kv.Value != null) r[kv.Key] = kv.Value.ToString();
            return r;
        }

        // names of the stock GAMMA mods (enabled or not) from passport\modlist.stock.txt
        static HashSet<string> StockNames(string passportDir)
        {
            string f = System.IO.Path.Combine(passportDir, "modlist.stock.txt");
            if (!File.Exists(f)) throw new Exception("Нет списка стандартных модов GAMMA: " + f);
            HashSet<string> h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                if (line.Length > 1 && (line[0] == '+' || line[0] == '-')) h.Add(line.Substring(1).TrimEnd());
            return h;
        }

        // enabled mods of the profile that are not stock GAMMA: this is what makes the build
        public static List<string> CustomMods(string modlistFile, string passportDir)
        {
            HashSet<string> stock = StockNames(passportDir);
            List<string> r = new List<string>();
            foreach (string line in File.ReadAllLines(modlistFile, Encoding.UTF8))
            {
                if (line.Length < 2 || line[0] != '+') continue;
                string n = line.Substring(1).TrimEnd();
                if (n.EndsWith("_separator", StringComparison.OrdinalIgnoreCase) || stock.Contains(n) || r.Contains(n)) continue;
                r.Add(n);
            }
            return r;
        }

        // ------------------------------------------------------------ backups
        // existing mod folders / gamedata / bin files / profile list are never deleted: they are moved (same volume, instant) into
        // root\GAMMA Coop\backup\<stamp>\ keeping their path relative to root
        static void BackupMove(string root, string backupDir, string src, bool isDir, ref int dirs, ref int files)
        {
            string rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string srcFull = System.IO.Path.GetFullPath(src);
            if (!srcFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new Exception("Отказ переносить вне папки игры: " + src);
            Safety.GuardWrite(root, srcFull, false);   // no junction / symlink on the item or any parent up to the game folder
            string dest = System.IO.Path.Combine(backupDir, srcFull.Substring(rootFull.Length));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
            if (isDir) Directory.Move(srcFull, dest); else File.Move(srcFull, dest);
            if (MoveLog != null) MoveLog.Add(new string[] { srcFull, dest, isDir ? "d" : "f" });
            if (isDir) dirs++; else files++;
        }

        // moves done so far by the current Apply (to be undone if a later move fails)
        [ThreadStatic] static List<string[]> MoveLog;

        static void UndoMoves(Action<string> log)
        {
            List<string[]> l = MoveLog;
            if (l == null) return;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (l[i][2] == "d") Directory.Move(l[i][1], l[i][0]); else File.Move(l[i][1], l[i][0]);
                }
                catch (Exception e) { log("Не удалось вернуть из резерва " + l[i][0] + ": " + e.Message); }
            }
            l.Clear();
        }

        // keep only the newest 3 backups (names sort by date)
        static void TrimBackups(string root, Action<string> log)
        {
            string bdir = System.IO.Path.Combine(root, @"GAMMA Coop\backup");
            if (!Directory.Exists(bdir)) return;
            List<string> all = new List<string>();
            foreach (string d in Directory.GetDirectories(bdir)) if (!System.IO.Path.GetFileName(d).StartsWith("update-", StringComparison.OrdinalIgnoreCase)) all.Add(d);   // update-* belong to UpdateGuard
            all.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count - 3; i++)
            {
                try { Safety.DeleteDir(root, all[i]); log("Удалён старый резерв: " + all[i]); }
                catch (Exception e) { log("Старый резерв не удалён: " + all[i] + " (" + e.Message + ")"); }
            }
        }

        // destination inside baseDir for a relative path from the zip; refuses to leave baseDir
        static string Dest(string baseDir, string rel)
        {
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, rel.Replace('/', '\\')));
            string b = System.IO.Path.GetFullPath(baseDir).TrimEnd('\\') + "\\";
            if (!full.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new Exception("в сборке недопустимый путь: " + rel);
            return full;
        }

        // ------------------------------------------------------------ step 1: verify (touches nothing)
        // prog(fraction 0..1, label); aborted() is polled between files (then null is returned). Throws on any problem.
        public static PackPlan Verify(string zipPath, string anomaly, string gamma, Action<string> log, Action<double, string> prog, Func<bool> aborted)
        {
            if (!File.Exists(zipPath)) throw new Exception("не найден файл сборки: " + zipPath);
            PackPlan plan = new PackPlan();
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                Dictionary<string, object> man = ReadManifest(z);
                plan.Info = InfoFrom(zipPath, man);
                plan.Mods = plan.Info.Mods;
                IEnumerable files = man.ContainsKey("files") ? man["files"] as IEnumerable : null;
                if (files == null) throw new Exception("в manifest.json нет списка файлов");
                foreach (object o in files)
                {
                    Dictionary<string, object> fd = o as Dictionary<string, object>;
                    if (fd == null) continue;
                    string p = Str(fd, "path"), sh = Str(fd, "sha256").ToLowerInvariant();
                    plan.Paths.Add(p); plan.Shas.Add(sh); plan.ShaByPath[p] = sh;
                    long sz = 0; long.TryParse(Str(fd, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out sz);
                    plan.Sizes.Add(sz); plan.Total += sz;
                }
                List<string> paths = plan.Paths;
                if (paths.Count == 0) throw new Exception("сборка пуста");
                log("Сборка: " + zipPath + ", файлов " + paths.Count + ", " + plan.Info.Build + ", создана " + plan.Info.Created + (plan.Info.BuildId.Length > 0 ? ", id " + plan.Info.BuildId : ""));

                string modsDir = System.IO.Path.Combine(gamma, "mods");
                string binDir = System.IO.Path.Combine(anomaly, "bin");
                string gdDir = System.IO.Path.Combine(anomaly, "gamedata");

                // required parts must be present; nothing unexpected may be inside
                foreach (string m in plan.Mods)
                {
                    if (!ValidName(m)) throw new Exception("в сборке недопустимое имя мода: " + m);
                    Dest(modsDir, m);
                    string pre = "mods/" + m + "/";
                    if (!paths.Exists(delegate (string p) { return p.StartsWith(pre, StringComparison.OrdinalIgnoreCase); })) throw new Exception("в сборке нет мода " + m);
                }
                foreach (string b in BinFiles) if (!paths.Contains("anomaly/bin/" + b)) throw new Exception("в сборке нет anomaly/bin/" + b);
                if (!paths.Exists(delegate (string p) { return p.StartsWith("anomaly/gamedata/", StringComparison.OrdinalIgnoreCase); })) throw new Exception("в сборке нет anomaly/gamedata");
                if (!paths.Contains("profile/modlist.txt")) throw new Exception("в сборке нет profile/modlist.txt");
                foreach (string p in paths)
                {
                    if (p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = p.Substring(5);
                        int sl = rest.IndexOf('/');
                        string first = sl < 0 ? rest : rest.Substring(0, sl);
                        if (sl < 0 || !plan.Mods.Exists(delegate (string m) { return string.Equals(m, first, StringComparison.OrdinalIgnoreCase); }))
                            throw new Exception("в сборке лишний файл вне списка модов: " + p);
                    }
                    else if (p.StartsWith("anomaly/bin/", StringComparison.OrdinalIgnoreCase))
                    {
                        string bn = p.Substring(12);
                        if (Array.FindIndex(BinFiles, delegate (string b) { return string.Equals(b, bn, StringComparison.OrdinalIgnoreCase); }) < 0) throw new Exception("в сборке лишний файл в bin: " + p);
                    }
                    else if (!p.StartsWith("anomaly/gamedata/", StringComparison.OrdinalIgnoreCase) && p != "profile/modlist.txt" && p != "passport/modpack_maker_list.txt" && p != "keybinds/user_binds.txt" && p != "keybinds/mcm_keys.txt")
                        throw new Exception("в сборке неожиданный файл: " + p);
                }

                // verify everything before touching the game
                long done = 0;
                double lastP = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    if (aborted()) return null;
                    ZipArchiveEntry e = z.GetEntry(paths[i]);
                    if (e == null) throw new Exception("в сборке нет файла " + paths[i]);
                    string sha;
                    using (Stream s = e.Open()) sha = Sha(s);
                    if (sha != plan.Shas[i]) throw new Exception("Файл сборки повреждён: не совпала контрольная сумма файла " + paths[i] + ". Скопируй файл заново.");
                    // reject bad paths (zip-slip) before anything is deleted
                    string pp = paths[i];
                    if (pp.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) Dest(modsDir, pp.Substring(5));
                    else if (pp.StartsWith("anomaly/bin/", StringComparison.OrdinalIgnoreCase)) Dest(binDir, pp.Substring(12));
                    else if (pp.StartsWith("anomaly/gamedata/", StringComparison.OrdinalIgnoreCase)) Dest(gdDir, pp.Substring(17));
                    done += plan.Sizes[i];
                    double f = plan.Total > 0 ? (double)done / plan.Total : 1;
                    if (f - lastP > 0.005) { lastP = f; prog(f, "проверка файла сборки"); }
                }
                log("Сборка: контрольные суммы всех " + paths.Count + " файлов совпали");
            }
            return plan;
        }

        // mods of the previously applied build that the new one no longer has (and that are still on disk)
        public static List<string> RemovedMods(string root, PackPlan plan)
        {
            List<string> r = new List<string>();
            InstalledInfo inst = ReadInstalled(root);
            if (inst == null) return r;
            List<string> prev = inst.HasMods ? inst.Mods : new List<string>(ModNames);
            string modsDir = System.IO.Path.Combine(root, @"GAMMA\mods");
            foreach (string m in prev)
            {
                if (!ValidName(m) || r.Contains(m)) continue;
                if (plan.Mods.Exists(delegate (string x) { return string.Equals(x, m, StringComparison.OrdinalIgnoreCase); })) continue;
                if (Directory.Exists(System.IO.Path.Combine(modsDir, m))) r.Add(m);
            }
            return r;
        }

        // ------------------------------------------------------------ step 2: apply (the first thing it does is the backup move)
        public static void Apply(string zipPath, PackPlan plan, string anomaly, string gamma, string root, Action<string> log, Action<double, string> prog, Func<bool> aborted, bool takeKeys = false)
        {
            List<string> remove = RemovedMods(root, plan);
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                string modsDir = System.IO.Path.Combine(gamma, "mods");
                string binDir = System.IO.Path.Combine(anomaly, "bin");
                string gdDir = System.IO.Path.Combine(anomaly, "gamedata");
                string profile = ActiveProfile(gamma);
                string ml = System.IO.Path.Combine(gamma, @"profiles\" + profile + @"\modlist.txt");
                Directory.CreateDirectory(modsDir);
                Directory.CreateDirectory(binDir);
                // the receiver's MCM file may live inside ANOMALY\gamedata, which is replaced as a whole: read it before that
                string preAf = takeKeys ? FindMcmFile(gamma, anomaly, ml) : null;
                string preText = preAf != null ? Latin.GetString(File.ReadAllBytes(preAf)) : null;
                Safety.GuardWrite(root, modsDir);   // the game folder itself is allowed and has no junction on the way
                string stamp = System.IO.Path.Combine(root, @"GAMMA Coop\backup\" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                string backupDir = stamp;
                for (int n = 2; Directory.Exists(backupDir); n++) backupDir = stamp + "-" + n;
                int bDirs = 0, bFiles = 0;
                MoveLog = new List<string[]>();
                try
                {
                foreach (string m in plan.Mods)
                {
                    string md = System.IO.Path.Combine(modsDir, m);
                    if (Directory.Exists(md)) BackupMove(root, backupDir, md, true, ref bDirs, ref bFiles);
                }
                foreach (string m in remove)
                {
                    string md = System.IO.Path.Combine(modsDir, m);
                    if (Directory.Exists(md)) { BackupMove(root, backupDir, md, true, ref bDirs, ref bFiles); log("Мод убран (в резерве): " + m); }
                }
                if (Directory.Exists(gdDir)) BackupMove(root, backupDir, gdDir, true, ref bDirs, ref bFiles);
                foreach (string b in BinFiles)
                {
                    string bf = System.IO.Path.Combine(binDir, b);
                    if (File.Exists(bf)) BackupMove(root, backupDir, bf, false, ref bDirs, ref bFiles);
                }
                if (File.Exists(ml)) BackupMove(root, backupDir, ml, false, ref bDirs, ref bFiles);
                }
                catch (Exception)
                {
                    UndoMoves(log);   // put everything already moved back, then report the failure
                    throw;
                }
                finally { MoveLog = null; }
                log("Резерв старых файлов: перенесено папок " + bDirs + ", файлов " + bFiles + (bDirs + bFiles > 0 ? " в " + backupDir : " (ничего не было)"));
                if (bDirs + bFiles > 0) TrimBackups(root, log);

                long done = 0;
                double lastP = 0;
                int copied = 0;
                for (int i = 0; i < plan.Paths.Count; i++)
                {
                    if (aborted()) return;
                    string p = plan.Paths[i];
                    string dest = null;
                    if (p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) dest = Dest(modsDir, p.Substring(5));
                    else if (p.StartsWith("anomaly/bin/", StringComparison.OrdinalIgnoreCase)) dest = Dest(binDir, p.Substring(12));
                    else if (p.StartsWith("anomaly/gamedata/", StringComparison.OrdinalIgnoreCase)) dest = Dest(gdDir, p.Substring(17));
                    if (dest == null) continue;
                    Safety.GuardWrite(root, dest, false);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                    WriteEntry(z.GetEntry(p), dest, plan.Shas[i]);
                    copied++;
                    done += plan.Sizes[i];
                    double f = plan.Total > 0 ? (double)done / plan.Total : 0.98;
                    if (f - lastP > 0.005) { lastP = f; prog(f, "распаковка"); }
                }
                log("Распаковано файлов: " + copied);

                // MO2 profile list (the old one is in the backup folder)
                Safety.GuardWrite(root, ml, false);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ml));
                WriteEntry(z.GetEntry("profile/modlist.txt"), ml, plan.ShaByPath["profile/modlist.txt"]);
                log("Записан список модов сборки: " + ml);
                if (takeKeys) ApplyKeys(z, plan, anomaly, gamma, root, backupDir, ml, preAf, preText, log);
            }
        }

        // ------------------------------------------------------------ key bindings (optional part of a build)
        // .ltx files are read and written as Latin-1 so that every byte survives unchanged
        static readonly Encoding Latin = Encoding.GetEncoding(28591);
        public const string McmModName = "G.A.M.M.A. MCM values - Rename to keep your personal changes";

        static string[] SplitLines(string s) { return s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }

        // the bind / bind_sec lines of user.ltx
        public static string ExtractUserBinds(string text)
        {
            List<string> r = new List<string>();
            foreach (string l in SplitLines(text))
            {
                string t = l.Trim();
                if (t.StartsWith("bind ", StringComparison.Ordinal) || t.StartsWith("bind_sec ", StringComparison.Ordinal)) r.Add(t);
            }
            return string.Join("\n", r.ToArray());
        }

        static string KeyOf(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || t[0] == ';' || t.StartsWith("//") || t[0] == '[') return null;
            int eq = t.IndexOf('=');
            return eq <= 0 ? null : t.Substring(0, eq).Trim();
        }

        static string SectionOf(string line)
        {
            string t = line.Trim();
            return t.Length > 2 && t[0] == '[' && t.EndsWith("]") ? t.Substring(1, t.Length - 2).Trim() : null;
        }

        static bool IsKeyEntry(string section, string key)
        {
            if (key == null) return false;
            return string.Equals(section, "global_keybinds", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_key", StringComparison.OrdinalIgnoreCase);
        }

        // only the key entries of axr_options.ltx, with their [section] headers
        public static string ExtractMcmKeys(string text)
        {
            List<string> r = new List<string>();
            string sec = "", emitted = null;
            foreach (string l in SplitLines(text))
            {
                string s = SectionOf(l);
                if (s != null) { sec = s; continue; }
                string k = KeyOf(l);
                if (!IsKeyEntry(sec, k)) continue;
                if (emitted != sec) { r.Add("[" + sec + "]"); emitted = sec; }
                r.Add(l.Trim());
            }
            return string.Join("\n", r.ToArray());
        }

        // text split into lines that keep their own line endings (eols[i] is "" for a last line without one)
        static void SplitKeep(string s, List<string> lines, List<string> eols)
        {
            int i = 0;
            while (i < s.Length)
            {
                int j = i;
                while (j < s.Length && s[j] != '\r' && s[j] != '\n') j++;
                int k = j;
                if (k < s.Length) { if (s[k] == '\r' && k + 1 < s.Length && s[k + 1] == '\n') k += 2; else k++; }
                lines.Add(s.Substring(i, j - i)); eols.Add(s.Substring(j, k - j));
                i = k;
            }
        }

        static string DomEol(string s) { return s.Contains("\r\n") ? "\r\n" : (s.Contains("\n") ? "\n" : "\r\n"); }

        static string Compose(List<string> lines, List<string> eols)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append(eols[i]);
            return sb.ToString();
        }

        static void InsertLine(List<string> lines, List<string> eols, int idx, string text, string eol)
        {
            if (idx >= lines.Count && idx > 0 && eols[idx - 1].Length == 0) { eols[idx - 1] = eol; lines.Add(text); eols.Add(""); }
            else { lines.Insert(idx, text); eols.Insert(idx, eol); }
        }

        static bool IsBind(string line)
        {
            string t = line.Trim();
            return t.StartsWith("bind ", StringComparison.Ordinal) || t.StartsWith("bind_sec ", StringComparison.Ordinal);
        }

        static string BindId(string line)
        {
            string[] p = line.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return (p.Length > 1 ? p[0] + " " + p[1] : p[0]).ToLowerInvariant();
        }

        // edited in place: a bind the sender also has is replaced where it stands, the receiver's other binds go,
        // the sender's new ones are added after the last bind; line endings and everything else stay as they were
        public static string MergeUserBinds(string target, string binds)
        {
            string eol = DomEol(target);
            List<string> lines = new List<string>(), eols = new List<string>();
            SplitKeep(target, lines, eols);
            Dictionary<string, string> sm = new Dictionary<string, string>();
            List<string> order = new List<string>();
            foreach (string b in SplitLines(binds))
            {
                if (b.Trim().Length == 0) continue;
                string id = BindId(b);
                if (!sm.ContainsKey(id)) order.Add(id);
                sm[id] = b.Trim();
            }
            List<string> used = new List<string>();
            int last = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!IsBind(lines[i])) continue;
                string id = BindId(lines[i]);
                if (sm.ContainsKey(id) && !used.Contains(id)) { lines[i] = sm[id]; used.Add(id); last = i; }
                else { lines.RemoveAt(i); eols.RemoveAt(i); i--; }
            }
            int at = last + 1;
            if (last < 0) { at = lines.Count; while (at > 0 && lines[at - 1].Trim().Length == 0) at--; }
            foreach (string id in order)
            {
                if (used.Contains(id)) continue;
                InsertLine(lines, eols, at, sm[id], eol);
                at++;
            }
            return Compose(lines, eols);
        }

        // values of the sender's key entries replace the same entries in place; missing ones are added inside their section
        public static string MergeMcm(string target, string fragment)
        {
            string eol = DomEol(target);
            List<string> lines = new List<string>(), eols = new List<string>();
            SplitKeep(target, lines, eols);
            List<string> secs = new List<string>();
            Dictionary<string, List<string>> ent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string cur = null;
            foreach (string l in SplitLines(fragment))
            {
                string s = SectionOf(l);
                if (s != null) { cur = s; if (!ent.ContainsKey(s)) { ent[s] = new List<string>(); secs.Add(s); } continue; }
                if (cur != null && KeyOf(l) != null) ent[cur].Add(l.Trim());
            }
            foreach (string sec in secs)
            {
                int start = lines.FindIndex(delegate (string l) { string x = SectionOf(l); return x != null && string.Equals(x, sec, StringComparison.OrdinalIgnoreCase); });
                if (start < 0)
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) InsertLine(lines, eols, lines.Count, "", eol);
                    InsertLine(lines, eols, lines.Count, "[" + sec + "]", eol);
                    foreach (string sl in ent[sec]) InsertLine(lines, eols, lines.Count, sl, eol);
                    continue;
                }
                foreach (string sl in ent[sec])
                {
                    string key = KeyOf(sl);
                    int end = start + 1;
                    while (end < lines.Count && SectionOf(lines[end]) == null) end++;
                    int hit = -1, lastNonBlank = start;
                    for (int i = start + 1; i < end; i++)
                    {
                        if (lines[i].Trim().Length > 0) lastNonBlank = i;
                        string k = KeyOf(lines[i]);
                        if (k != null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { hit = i; break; }
                    }
                    if (hit >= 0) lines[hit] = lines[hit].Substring(0, lines[hit].IndexOf('=') + 1) + sl.Substring(sl.IndexOf('=') + 1);
                    else InsertLine(lines, eols, lastNonBlank + 1, sl, eol);
                }
            }
            return Compose(lines, eols);
        }

        // axr_options.ltx in use: the enabled MCM values mod, else overwrite, else the game's own; null when there is none
        public static string FindMcmFile(string gamma, string anomaly, string modlistFile)
        {
            string rel = @"gamedata\configs\axr_options.ltx";
            try
            {
                if (File.Exists(modlistFile))
                {
                    bool on = false;
                    foreach (string l in File.ReadAllLines(modlistFile, Encoding.UTF8)) if (l.TrimEnd() == "+" + McmModName) on = true;
                    string f = System.IO.Path.Combine(gamma, @"mods\" + McmModName + @"\" + rel);
                    if (on && File.Exists(f)) return f;
                }
            }
            catch (Exception) { }
            string o = System.IO.Path.Combine(gamma, @"overwrite\" + rel);
            if (File.Exists(o)) return o;
            string a = System.IO.Path.Combine(anomaly, rel);
            return File.Exists(a) ? a : null;
        }

        static string EntryText(ZipArchive z, PackPlan plan, string name)
        {
            ZipArchiveEntry e = z.GetEntry(name);
            if (e == null) return null;
            byte[] b;
            using (Stream s = e.Open()) using (MemoryStream ms = new MemoryStream()) { s.CopyTo(ms); b = ms.ToArray(); }
            string sha;
            using (SHA256 h = SHA256.Create()) sha = Hex(h.ComputeHash(b));
            if (sha != plan.ShaByPath[name]) throw new Exception("Файл сборки изменился во время установки (не совпала контрольная сумма): " + name);
            return Latin.GetString(b);
        }

        static void ApplyKeys(ZipArchive z, PackPlan plan, string anomaly, string gamma, string root, string backupDir, string modlistFile, string preAf, string preText, Action<string> log)
        {
            int d0 = 0, f0 = 0;
            string binds = EntryText(z, plan, "keybinds/user_binds.txt");
            if (binds != null)
            {
                string ul = System.IO.Path.Combine(anomaly, @"appdata\user.ltx");
                string cur = "";
                if (File.Exists(ul)) { cur = Latin.GetString(File.ReadAllBytes(ul)); BackupMove(root, backupDir, ul, false, ref d0, ref f0); }
                Safety.GuardWrite(root, ul, false);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ul));
                File.WriteAllBytes(ul, Latin.GetBytes(MergeUserBinds(cur, binds)));
                log("Клавиши отправителя записаны в " + ul + " (старый файл в резерве)");
            }
            string mcm = EntryText(z, plan, "keybinds/mcm_keys.txt");
            if (mcm != null)
            {
                string af = FindMcmFile(gamma, anomaly, modlistFile);
                string cur = "";
                if (af != null)
                {
                    bool same = preAf != null && string.Equals(System.IO.Path.GetFullPath(af), System.IO.Path.GetFullPath(preAf), StringComparison.OrdinalIgnoreCase);
                    bool inGd = System.IO.Path.GetFullPath(af).StartsWith(System.IO.Path.GetFullPath(System.IO.Path.Combine(anomaly, "gamedata")) + "\\", StringComparison.OrdinalIgnoreCase);
                    cur = same ? preText : Latin.GetString(File.ReadAllBytes(af));
                    if (!(same && inGd)) BackupMove(root, backupDir, af, false, ref d0, ref f0);   // inside gamedata the original is already in the backup
                }
                else if (preAf != null) { af = preAf; cur = preText; }   // it was inside the replaced gamedata and the build has none
                else af = System.IO.Path.Combine(gamma, @"overwrite\gamedata\configs\axr_options.ltx");
                Safety.GuardWrite(root, af, false);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(af));
                File.WriteAllBytes(af, Latin.GetBytes(MergeMcm(cur, mcm)));
                log("Клавиши MCM отправителя записаны в " + af + " (старый файл в резерве)");
            }
        }

        // keep the build for later repairs (newest 3 in root\GAMMA Coop\builds)
        public static void StoreBuild(string root, string zipPath, Action<string> log)
        {
            try
            {
                string dir = BuildsDir(root);
                Directory.CreateDirectory(dir);
                string dest = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(zipPath));
                if (!string.Equals(System.IO.Path.GetFullPath(zipPath), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(zipPath, dest, true);
                    File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
                    log("Сборка сохранена для починки: " + dest);
                }
                List<string> all = new List<string>(Directory.GetFiles(dir, "*.zip"));
                all.Sort(delegate (string a, string b) { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                for (int i = 3; i < all.Count; i++)
                {
                    try { Safety.DeleteFile(root, all[i]); log("Удалена старая сохранённая сборка: " + all[i]); }
                    catch (Exception e) { log("Старая сборка не удалена: " + all[i] + " (" + e.Message + ")"); }
                }
            }
            catch (Exception ex) { log("Не удалось сохранить копию сборки (она уже применена): " + ex.Message); }
        }

        public static void ApplyAndStore(string zipPath, PackPlan plan, string anomaly, string gamma, string root, Action<string> log, Action<double, string> prog, Func<bool> aborted, bool takeKeys = false)
        {
            Apply(zipPath, plan, anomaly, gamma, root, log, prog, aborted, takeKeys);
            if (aborted()) return;
            StoreBuild(root, zipPath, log);
        }

        // stage "Кооп xrRazom и озвучка" / repair: verify (0..40%) + apply (40..100%). Returns the build, null when stopped.
        public static PackInfo Install(string zipPath, string anomaly, string gamma, string root, Action<string> log, Action<double, string> prog, Func<bool> aborted)
        {
            PackPlan plan = Verify(zipPath, anomaly, gamma, log, delegate (double f, string w) { prog(0.4 * f, w); }, aborted);
            if (plan == null || aborted()) return null;
            ApplyAndStore(zipPath, plan, anomaly, gamma, root, log, delegate (double f, string w) { prog(0.4 + 0.58 * f, w); }, aborted);
            if (aborted()) return null;
            prog(1.0, "готово");
            return plan.Info;
        }

        // ------------------------------------------------------------ the diff shown before applying
        static Dictionary<string, long> LocalFiles(string dir, string skipName)
        {
            Dictionary<string, long> r = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (skipName != null && string.Equals(System.IO.Path.GetFileName(f), skipName, StringComparison.OrdinalIgnoreCase)) continue;
                r[f.Substring(dir.Length + 1).Replace('\\', '/')] = new FileInfo(f).Length;
            }
            return r;
        }

        static bool SameFiles(Dictionary<string, long> want, Dictionary<string, long> have)
        {
            if (want.Count != have.Count) return false;
            foreach (KeyValuePair<string, long> kv in want)
            {
                long l;
                if (!have.TryGetValue(kv.Key, out l) || l != kv.Value) return false;
            }
            return true;
        }

        public static PackDiff Diff(string root, PackPlan plan, IDictionary<string, string> passportRepos)
        {
            PackDiff d = new PackDiff();
            InstalledInfo inst = ReadInstalled(root);
            d.LocalBuildId = inst != null ? inst.BuildId : "";
            d.Same = d.LocalBuildId.Length > 0 && string.Equals(d.LocalBuildId, plan.Info.BuildId, StringComparison.OrdinalIgnoreCase);
            string gamma = System.IO.Path.Combine(root, "GAMMA"), anomaly = System.IO.Path.Combine(root, "ANOMALY");
            string modsDir = System.IO.Path.Combine(gamma, "mods");
            Dictionary<string, Dictionary<string, long>> want = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> wantGd = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.Paths.Count; i++)
            {
                string p = plan.Paths[i];
                if (p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = p.Substring(5);
                    int sl = rest.IndexOf('/');
                    if (sl < 0) continue;
                    string m = rest.Substring(0, sl);
                    Dictionary<string, long> w;
                    if (!want.TryGetValue(m, out w)) { w = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); want[m] = w; }
                    w[rest.Substring(sl + 1)] = plan.Sizes[i];
                }
                else if (p.StartsWith("anomaly/gamedata/", StringComparison.OrdinalIgnoreCase)) wantGd[p.Substring(17)] = plan.Sizes[i];
            }
            foreach (string m in plan.Mods)
            {
                string md = System.IO.Path.Combine(modsDir, m);
                Dictionary<string, long> w;
                if (!Directory.Exists(md)) d.Add.Add(m);
                else if (want.TryGetValue(m, out w) && SameFiles(w, LocalFiles(md, string.Equals(m, "Discord RPC", StringComparison.OrdinalIgnoreCase) ? "rpc.py" : null))) d.Keep.Add(m);
                else d.Update.Add(m);
            }
            d.Remove = RemovedMods(root, plan);
            try
            {
                foreach (string b in BinFiles)
                {
                    string f = System.IO.Path.Combine(anomaly, @"bin\" + b);
                    int i = plan.Paths.IndexOf("anomaly/bin/" + b);
                    if (i < 0 || !File.Exists(f) || new FileInfo(f).Length != plan.Sizes[i]) { d.BinChanged = true; break; }
                }
                string gd = System.IO.Path.Combine(anomaly, "gamedata");
                d.GdChanged = !Directory.Exists(gd) || !SameFiles(wantGd, LocalFiles(gd, null));
            }
            catch (Exception) { d.BinChanged = true; d.GdChanged = true; }
            if (plan.Info.Gamma.Count > 0)
            {
                Dictionary<string, string> local = GammaCommits(gamma, passportRepos);
                foreach (KeyValuePair<string, string> kv in plan.Info.Gamma)
                {
                    string lv;
                    if (!local.TryGetValue(kv.Key, out lv) || !string.Equals(lv, kv.Value, StringComparison.OrdinalIgnoreCase)) d.GammaDiff.Add(kv.Key);
                }
            }
            return d;
        }

        // ------------------------------------------------------------ installed.json, shortcuts, app copy
        // null when there is no installed.json
        public static InstalledInfo ReadInstalled(string root)
        {
            Dictionary<string, object> d = ReadJson(InstalledJson(root));
            if (d == null) return null;
            InstalledInfo r = new InstalledInfo();
            r.BuildId = Str(d, "buildId"); r.Source = Str(d, "sourceComputer"); r.Created = Str(d, "created");
            IEnumerable ml = d.ContainsKey("mods") ? d["mods"] as IEnumerable : null;
            if (ml != null) { r.HasMods = true; foreach (object o in ml) if (o != null) r.Mods.Add(o.ToString()); }
            Dictionary<string, object> gd = d.ContainsKey("gamma") ? d["gamma"] as Dictionary<string, object> : null;
            if (gd != null) foreach (KeyValuePair<string, object> kv in gd) if (kv.Value != null) r.Gamma[kv.Key] = kv.Value.ToString();
            string at = Str(d, "appliedAt");
            if (at.Length == 0) at = Str(d, "date");
            DateTime t;
            if (DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) r.AppliedAt = t;
            return r;
        }

        // pk = the build that was applied (null: only the version fields are written)
        public static void WriteInstalled(string root, PackInfo pk)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            string now = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            d["build"] = BuildName; d["xrrazom"] = XrrazomVer;
            d["date"] = now; d["appliedAt"] = now; d["root"] = root;
            d["packCreated"] = pk != null ? (pk.Created ?? "") : "";
            if (pk != null)
            {
                d["buildId"] = pk.BuildId; d["created"] = pk.Created ?? ""; d["sourceComputer"] = pk.Source;
                d["mods"] = new List<object>(pk.Mods.ToArray());
                Dictionary<string, object> g = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> kv in pk.Gamma) g[kv.Key] = kv.Value;
                d["gamma"] = g;
            }
            string f = InstalledJson(root);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
            File.WriteAllText(f, Ser().Serialize(d), new UTF8Encoding(false));
        }

        public static void MakeShortcut(string lnkPath, string target, string workDir) { MakeShortcut(lnkPath, target, workDir, target); }

        public static void MakeShortcut(string lnkPath, string target, string workDir, string iconFile)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new Exception("WScript.Shell недоступен");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { iconFile + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "GAMMA Coop" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }

        static void CopyOne(string src, string dest, string root)
        {
            Safety.GuardWrite(root, dest);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
            File.Copy(src, dest, true);
            File.SetAttributes(dest, FileAttributes.Normal);
        }

        // the installer itself (installer\*, the .bat files, passport) goes to root\GAMMA Coop\app so the hub is always at hand.
        // Logs, last_root.txt and zip files are left out. Returns the number of files copied (0 when already running from there).
        public static int CopyApp(string appDir, string root, Action<string> log)
        {
            string appFull = System.IO.Path.GetFullPath(appDir).TrimEnd('\\');
            string beside = System.IO.Path.GetDirectoryName(appFull);
            string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, @"GAMMA Coop\app")).TrimEnd('\\');
            if (appFull.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) { log("Установщик уже запущен из папки app — копировать не нужно"); return 0; }
            int n = 0;
            foreach (string f in Directory.GetFiles(beside))
            {
                string name = System.IO.Path.GetFileName(f);
                if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "README.txt", StringComparison.OrdinalIgnoreCase))
                { CopyOne(f, System.IO.Path.Combine(target, name), root); n++; }
            }
            foreach (string f in Directory.GetFiles(appFull, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(appFull.Length + 1);
                string name = System.IO.Path.GetFileName(f);
                if (rel.StartsWith(@"logs\", StringComparison.OrdinalIgnoreCase)) continue;
                if (name == "last_root.txt" || name == "load_request.txt") continue;
                string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
                if (ext == ".zip" || ext == ".tmp" || ext == ".log") continue;
                CopyOne(f, System.IO.Path.Combine(target, @"installer\" + rel), root);
                n++;
            }
            log("Установщик скопирован в " + target + ": файлов " + n);
            return n;
        }

        // ------------------------------------------------------------ building a build file (sender side)
        // hostRoot = folder with GAMMA\ModOrganizer.exe and ANOMALY\bin. Returns "files|bytes".
        public static string Build(string hostRoot, string outZip, Action<string> log, string passportDir)
        {
            return Build(hostRoot, outZip, log, passportDir, null, null);
        }

        public static string Build(string hostRoot, string outZip, Action<string> log, string passportDir, Action<double, string> prog, Func<bool> aborted)
        {
            string gamma = System.IO.Path.Combine(hostRoot, "GAMMA");
            string anomaly = System.IO.Path.Combine(hostRoot, "ANOMALY");
            string profile = ActiveProfile(gamma);
            string ml = System.IO.Path.Combine(gamma, @"profiles\" + profile + @"\modlist.txt");
            List<string> names = new List<string>();     // entry names
            List<string> srcs = new List<string>();      // source files
            List<string> missing = new List<string>();
            List<string> mods = new List<string>();      // mods that really go into the build

            if (!File.Exists(ml)) throw new Exception("Не найден список модов профиля " + profile + ": " + ml);
            foreach (string m in CustomMods(ml, passportDir))
            {
                string dir = System.IO.Path.Combine(gamma, @"mods\" + m);
                if (!Directory.Exists(dir)) { log("Мод «" + m + "» включён в списке, но папки у него нет — пропускаю"); continue; }
                int before = names.Count;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(dir.Length + 1);
                    if (m == "Discord RPC" && string.Equals(System.IO.Path.GetFileName(rel), "rpc.py", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add("mods/" + m + "/" + rel.Replace('\\', '/')); srcs.Add(f);
                }
                if (names.Count == before) { log("Мод «" + m + "» пустой — пропускаю"); continue; }
                mods.Add(m);
            }
            if (mods.Count == 0) throw new Exception("В профиле " + profile + " нет включённых модов, которых нет в стандартной GAMMA — собирать нечего.");
            foreach (string b in BinFiles)
            {
                string f = System.IO.Path.Combine(anomaly, @"bin\" + b);
                if (!File.Exists(f)) { missing.Add(f); continue; }
                names.Add("anomaly/bin/" + b); srcs.Add(f);
            }
            string gd = System.IO.Path.Combine(anomaly, "gamedata");
            if (!Directory.Exists(gd)) missing.Add(gd);
            else foreach (string f in Directory.GetFiles(gd, "*", SearchOption.AllDirectories))
                { names.Add("anomaly/gamedata/" + f.Substring(gd.Length + 1).Replace('\\', '/')); srcs.Add(f); }
            names.Add("profile/modlist.txt"); srcs.Add(ml);
            string mm = System.IO.Path.Combine(passportDir, "modpack_maker_list.txt");
            if (File.Exists(mm)) { names.Add("passport/modpack_maker_list.txt"); srcs.Add(mm); }
            if (missing.Count > 0) throw new Exception("Не найдено у хоста:\n  " + string.Join("\n  ", missing.ToArray()));

            // optional: key bindings of the sender (generated into a temp folder, packed like any other file)
            string kdir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gc_keys_" + Guid.NewGuid().ToString("N"));
            bool hasKeys = false;
            try
            {
                string ul = System.IO.Path.Combine(anomaly, @"appdata\user.ltx");
                string binds = File.Exists(ul) ? ExtractUserBinds(Latin.GetString(File.ReadAllBytes(ul))) : "";
                string af = FindMcmFile(gamma, anomaly, ml);
                string mk = af != null ? ExtractMcmKeys(Latin.GetString(File.ReadAllBytes(af))) : "";
                Directory.CreateDirectory(kdir);
                if (binds.Length > 0)
                {
                    string kf = System.IO.Path.Combine(kdir, "user_binds.txt"); File.WriteAllBytes(kf, Latin.GetBytes(binds + "\n"));
                    names.Add("keybinds/user_binds.txt"); srcs.Add(kf); hasKeys = true;
                }
                if (mk.Length > 0)
                {
                    string kf = System.IO.Path.Combine(kdir, "mcm_keys.txt"); File.WriteAllBytes(kf, Latin.GetBytes(mk + "\n"));
                    names.Add("keybinds/mcm_keys.txt"); srcs.Add(kf); hasKeys = true;
                }
                log("Клавиши: user.ltx " + (binds.Length > 0 ? "есть" : "нет") + ", MCM " + (mk.Length > 0 ? af : "нет"));
            }
            catch (Exception ex) { log("Клавиши в сборку не попали: " + ex.Message); }

            long grand = 0;
            foreach (string s in srcs) { try { grand += new FileInfo(s).Length; } catch (Exception) { } }
            Dictionary<string, string> commits = GammaCommits(gamma, PassportRepos(passportDir));

            string tmp = outZip + ".tmp";
            string outDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outZip));
            Directory.CreateDirectory(outDir);
            if (File.Exists(tmp)) Safety.DeleteFile(outDir, tmp, false);
            List<object> manifestFiles = new List<object>();
            List<string> idLines = new List<string>();
            long bytes = 0;
            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
                using (ZipArchive z = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    byte[] buf = new byte[81920];
                    for (int i = 0; i < names.Count; i++)
                    {
                        ZipArchiveEntry e = z.CreateEntry(names[i], CompressionLevel.Optimal);
                        DateTime lw = File.GetLastWriteTime(srcs[i]);
                        if (lw.Year < 1980 || lw.Year > 2099) lw = DateTime.Now;
                        e.LastWriteTime = lw;
                        long size = 0;
                        using (SHA256 h = SHA256.Create())
                        using (Stream o = e.Open())
                        using (FileStream src = new FileStream(srcs[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            int n;
                            while ((n = src.Read(buf, 0, buf.Length)) > 0)
                            {
                                if (aborted != null && aborted()) throw new OperationCanceledException();
                                h.TransformBlock(buf, 0, n, null, 0); o.Write(buf, 0, n); size += n;
                            }
                            h.TransformFinalBlock(buf, 0, 0);
                            string sha = Hex(h.Hash);
                            Dictionary<string, object> fd = new Dictionary<string, object>();
                            fd["path"] = names[i]; fd["size"] = size; fd["sha256"] = sha;
                            manifestFiles.Add(fd);
                            idLines.Add(names[i] + ":" + sha);
                        }
                        bytes += size;
                        if (prog != null) prog(grand > 0 ? Math.Min(1.0, (double)bytes / grand) : 1.0, "сжимаю файлы: " + (i + 1) + " / " + names.Count);
                        if (i % 200 == 0 || i == names.Count - 1) log("  " + (i + 1) + " / " + names.Count + " файлов");
                    }
                    idLines.Sort(StringComparer.Ordinal);
                    string buildId;
                    using (SHA256 h = SHA256.Create()) buildId = Hex(h.ComputeHash(new UTF8Encoding(false).GetBytes(string.Join("\n", idLines.ToArray())))).Substring(0, 12);

                    Dictionary<string, object> man = new Dictionary<string, object>();
                    man["format"] = 3;
                    man["build"] = BuildName; man["xrrazom"] = XrrazomVer;
                    man["buildId"] = buildId;
                    man["created"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                    man["sourceComputer"] = Environment.MachineName;
                    man["profile"] = profile;
                    if (hasKeys) man["hasKeybinds"] = true;
                    man["mods"] = new List<object>(mods.ToArray());
                    Dictionary<string, object> g = new Dictionary<string, object>();
                    foreach (KeyValuePair<string, string> kv in commits) g[kv.Key] = kv.Value;
                    man["gamma"] = g;
                    Dictionary<string, object> pd = ReadJson(System.IO.Path.Combine(passportDir, "passport.json")) ?? new Dictionary<string, object>();
                    Dictionary<string, object> pr = new Dictionary<string, object>();
                    Dictionary<string, object> old = pd.ContainsKey("repos") ? pd["repos"] as Dictionary<string, object> : null;
                    if (old != null) foreach (KeyValuePair<string, object> kv in old) pr[kv.Key] = kv.Value;
                    foreach (KeyValuePair<string, string> kv in commits) pr[kv.Key] = kv.Value;   // the real versions beat the installer's pins
                    pd["repos"] = pr;
                    man["passport"] = pd;
                    man["files"] = manifestFiles;
                    // the host's file list of ALL enabled mods (not only the custom ones): the friend's integrity check compares against it
                    log("Список файлов всех включённых модов (для проверки целостности)…");
                    string fmText = Integrity.BuildManifestText(gamma, Integrity.EnabledMods(ml), log, prog, aborted);
                    ZipArchiveEntry fe = z.CreateEntry(Integrity.ManifestEntry, CompressionLevel.Optimal);
                    using (Stream o = fe.Open())
                    {
                        byte[] fb = new UTF8Encoding(false).GetBytes(fmText);
                        o.Write(fb, 0, fb.Length);
                    }
                    ZipArchiveEntry me = z.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using (Stream o = me.Open())
                    {
                        byte[] mb = new UTF8Encoding(false).GetBytes(Ser().Serialize(man));
                        o.Write(mb, 0, mb.Length);
                    }
                }
                if (File.Exists(outZip)) Safety.DeleteFile(outDir, outZip, false);
                File.Move(tmp, outZip);
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) Safety.DeleteFile(outDir, tmp, false); } catch (Exception) { }
                try { Safety.DeleteDir(System.IO.Path.GetTempPath(), kdir, false); } catch (Exception) { }
                throw;
            }
            try { Safety.DeleteDir(System.IO.Path.GetTempPath(), kdir, false); } catch (Exception) { }
            return names.Count + "|" + bytes;
        }
    }
}
