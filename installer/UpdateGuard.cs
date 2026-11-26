using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace GammaCoop
{
    // what an "update apply" run of the CLI may write to, worked out before it starts (paths are relative to the game folder)
    public class UpdatePlan
    {
        public List<string> Dirs = new List<string>();      // folders that exist now: copied to the backup
        public List<string> NewDirs = new List<string>();   // folders that do not exist yet: removed on rollback
        public List<string> Files = new List<string>();     // single files that exist now: copied to the backup
        public List<string> NewFiles = new List<string>();  // single files that do not exist yet: removed on rollback
        public long Bytes;
        public List<string> Notes = new List<string>();
    }

    public class UpdateJournal
    {
        public string State = "running";        // running | done | rolledback
        public string Started = "", Finished = "", Backup = "", BuildId = "", BuildPath = "";
        public bool Copied;                     // the backup is complete (until then the game was not touched)
        public long Bytes;
        public List<string> Dirs = new List<string>(), NewDirs = new List<string>(), Files = new List<string>(), NewFiles = new List<string>();
    }

    // [U] safety: the CLI's update writes into mod folders and cannot be stopped halfway cleanly. Before it runs, the folders it can
    // write to are copied to GAMMA Coop\backup\update-<stamp> and listed in GAMMA Coop\update_journal.json; a failure / Esc / a crash
    // puts them back. The mapping of repo files to mod folders is that of stalker-gamma-cli 1.44.0 (SpecialRepos\*, DiffModPackMakerLists).
    public static class UpdateGuard
    {
        public const string Profile = "G.A.M.M.A";    // the CLI's MO2 profile
        const string TeivazMod = "312- Gunslinger Guns for Anomaly - Teivazcz & Gunslinger Team";
        public static readonly string[] Repos = { "Stalker_GAMMA", "gamma_large_files_v2", "teivaz_anomaly_gunslinger", "gamma_setup" };
        const long Margin = 1024L * 1024 * 1024;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string from, string to, int flags);

        public static string JournalPath(string root) { return System.IO.Path.Combine(root, @"GAMMA Coop\update_journal.json"); }

        static JavaScriptSerializer Ser()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }

        // ------------------------------------------------------------ the journal
        static List<string> StrList(Dictionary<string, object> d, string k)
        {
            List<string> r = new List<string>();
            object o;
            IEnumerable e = d.TryGetValue(k, out o) ? o as IEnumerable : null;
            if (e != null) foreach (object x in e) if (x != null) r.Add(x.ToString());
            return r;
        }

        static string S(Dictionary<string, object> d, string k)
        {
            object o;
            return d.TryGetValue(k, out o) && o != null ? o.ToString() : "";
        }

        // null when there is no journal (or it cannot be read)
        public static UpdateJournal Load(string root)
        {
            try
            {
                string f = JournalPath(root);
                if (!File.Exists(f)) return null;
                Dictionary<string, object> d = Ser().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) return null;
                UpdateJournal j = new UpdateJournal();
                j.State = S(d, "state"); j.Started = S(d, "started"); j.Finished = S(d, "finished"); j.Backup = S(d, "backup");
                j.BuildId = S(d, "buildId"); j.BuildPath = S(d, "buildPath");
                object o;
                j.Copied = d.TryGetValue("copied", out o) && o is bool && (bool)o;
                long.TryParse(S(d, "bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out j.Bytes);
                j.Dirs = StrList(d, "dirs"); j.NewDirs = StrList(d, "newDirs"); j.Files = StrList(d, "files"); j.NewFiles = StrList(d, "newFiles");
                return j;
            }
            catch (Exception) { return null; }
        }

        // an update that was started and neither finished nor rolled back (power cut, kill)
        public static bool Pending(string root)
        {
            UpdateJournal j = Load(root);
            return j != null && j.State == "running";
        }

        static List<object> Obj(List<string> l) { return new List<object>(l.ToArray()); }

        // atomic and durable like progress.json: tmp file with WriteThrough + Flush(true), then one MoveFileEx(replace)
        static void Save(string root, UpdateJournal j)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["version"] = 1; d["state"] = j.State; d["started"] = j.Started; d["finished"] = j.Finished; d["backup"] = j.Backup;
            d["buildId"] = j.BuildId; d["buildPath"] = j.BuildPath; d["copied"] = j.Copied; d["bytes"] = j.Bytes;
            d["dirs"] = Obj(j.Dirs); d["newDirs"] = Obj(j.NewDirs); d["files"] = Obj(j.Files); d["newFiles"] = Obj(j.NewFiles);
            byte[] bytes = new UTF8Encoding(false).GetBytes(Ser().Serialize(d));
            string file = JournalPath(root), tmp = file + ".tmp";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            using (FileStream f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                f.Write(bytes, 0, bytes.Length);
                f.Flush(true);
            }
            if (!MoveFileEx(tmp, file, 1 | 8)) throw new IOException("update_journal.json: MoveFileEx, ошибка " + Marshal.GetLastWin32Error());
        }

        static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------ what the update can write to
        class Rec
        {
            public int Counter;
            public string Link, Instr, Patch, Name, Zip, Md5;
        }

        static bool Ws(string s) { return string.IsNullOrWhiteSpace(s); }

        // the folder in GAMMA\mods a record is extracted to ("{line}- {addon} {patch}")
        static string DirName(Rec r) { return r.Counter.ToString(CultureInfo.InvariantCulture) + "- " + (r.Name ?? "") + " " + (r.Patch ?? ""); }

        // the CLI makes a downloadable record only out of these (DownloadableRecordFactory.TryCreate)
        static bool Creatable(Rec r)
        {
            string link = r.Link ?? "";
            if (link.Contains("moddb") && !Ws(r.Name) && !Ws(r.Patch) && !Ws(r.Zip)) return true;
            if (!Ws(r.Name) && Ws(r.Zip) && !Ws(link) && link.Contains("moddb")) return true;
            return link.Contains("github") && !Ws(r.Name) && !Ws(r.Patch);
        }

        // the sender's modpack_maker_list.txt, numbered like ModPackMakerRecordFactory
        static List<Rec> ParseTxt(string txt)
        {
            List<Rec> l = new List<Rec>();
            foreach (string raw in txt.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                Rec r = new Rec();
                r.Counter = l.Count + 1;
                r.Link = f[0].Trim();
                r.Instr = f.Length > 1 ? f[1].Trim() : null;
                r.Patch = f.Length > 2 ? f[2].Trim() : null;
                r.Name = f.Length > 3 ? f[3].Trim() : null;
                r.Zip = f.Length > 5 ? f[5].Trim() : null;
                r.Md5 = f.Length > 6 ? f[6].Trim() : null;
                l.Add(r);
            }
            return l;
        }

        // the CLI's own list of what is installed: GAMMA\profiles\G.A.M.M.A\modpack_maker_list.json
        static List<Rec> ParseJson(string json)
        {
            List<Rec> l = new List<Rec>();
            IEnumerable arr = Ser().DeserializeObject(json) as IEnumerable;
            if (arr == null) throw new Exception("modpack_maker_list.json не читается");
            foreach (object o in arr)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                Rec r = new Rec();
                r.Counter = l.Count + 1;
                r.Link = S(d, "dlLink");
                List<string> ins = StrList(d, "instructions");
                r.Instr = ins.Count > 0 ? string.Join(":", ins.ToArray()) : "0";
                object p;
                r.Patch = d.TryGetValue("patch", out p) && p != null ? "- " + p : null;
                r.Name = d.TryGetValue("addonName", out p) && p != null ? p.ToString() : null;
                r.Zip = d.TryGetValue("zipName", out p) && p != null ? p.ToString() : null;
                r.Md5 = d.TryGetValue("md5ModDb", out p) && p != null ? p.ToString() : null;
                l.Add(r);
            }
            return l;
        }

        static readonly Regex AuthorRx = new Regex(@"^-\s+(?<author>.+)");
        static string Author(string patch) { return AuthorRx.Match(patch ?? "").Groups["author"].Value; }

        // names of the mod folders the update re-extracts (DiffModPackMakerLists.Diff + DeleteRenumberedAddonFolders)
        static void AddonDirs(List<Rec> local, List<Rec> online, HashSet<string> write, HashSet<string> removed)
        {
            HashSet<string> onlineDirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (Rec n in online) onlineDirs.Add(DirName(n));
            foreach (Rec old in local)
            {
                Rec m = online.Find(delegate (Rec x) { return x.Name == old.Name && x.Link == old.Link; });
                if (m == null) continue;
                bool renum = old.Counter > 0 && m.Counter > 0 && old.Counter != m.Counter;
                if (string.Equals(m.Md5, old.Md5, StringComparison.OrdinalIgnoreCase) && m.Instr == old.Instr && Author(m.Patch) == Author(old.Patch) && !renum) continue;
                if (Creatable(m)) write.Add(DirName(m));
                if (renum && !onlineDirs.Contains(DirName(old))) removed.Add(DirName(old));
            }
            foreach (Rec n in online)
            {
                Rec m = local.Find(delegate (Rec x) { return x.Name == n.Name && x.Link == n.Link; });
                if (m == null && Creatable(n)) write.Add(DirName(n));
            }
        }

        // ------------------------------------------------------------ repo files -> destinations
        static string[] Segs(string p) { return p.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries); }

        // mod folder a repo file is extracted into (GetModsDirName), or null
        static string ModDirOf(string repo, string path)
        {
            string[] s = Segs(path);
            if (repo == "Stalker_GAMMA") return s.Length > 3 && s[0] == "G.A.M.M.A" && s[1] == "modpack_addons" ? s[2] : null;
            if (repo == "gamma_large_files_v2") return s.Length > 1 ? s[0] : null;
            if (repo == "teivaz_anomaly_gunslinger") return Array.IndexOf(s, "gamedata") >= 0 ? TeivazMod : null;
            if (repo == "gamma_setup") return s.Length > 2 && s[0] == "modpack_addons" ? s[1] : null;
            return null;
        }

        // Stalker_GAMMA files that land outside GAMMA\mods: modpack_patches -> ANOMALY\..., the definition version -> GAMMA\version.txt
        static string OutsideOf(string repo, string path)
        {
            if (repo != "Stalker_GAMMA") return null;
            string[] s = Segs(path);
            if (s.Length > 2 && s[0] == "G.A.M.M.A" && s[1] == "modpack_patches") return @"ANOMALY\" + string.Join(@"\", s, 2, s.Length - 2);
            if (s.Length == 1 && s[0] == "G.A.M.M.A_definition_version.txt") return @"GAMMA\version.txt";
            return null;
        }

        // blob path -> sha of a commit's tree from the GitHub API
        static Dictionary<string, string> Tree(Func<string, string> fetch, string repo, string sha)
        {
            string txt = fetch("https://api.github.com/repos/Grokitach/" + repo + "/git/trees/" + sha + "?recursive=1");
            Dictionary<string, object> d = Ser().DeserializeObject(txt) as Dictionary<string, object>;
            if (d == null || !d.ContainsKey("tree")) throw new Exception("GitHub не вернул список файлов " + repo);
            object tr;
            if (d.TryGetValue("truncated", out tr) && tr is bool && (bool)tr) throw new Exception("список файлов " + repo + " слишком большой для GitHub API");
            Dictionary<string, string> r = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (object o in (IEnumerable)d["tree"])
            {
                Dictionary<string, object> e = o as Dictionary<string, object>;
                if (e == null || S(e, "type") != "blob") continue;
                r[S(e, "path")] = S(e, "sha");
            }
            return r;
        }

        public static string HttpGet(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "GammaCoopInstaller/0.95";
            req.Accept = "application/vnd.github+json";
            req.Timeout = 60000; req.ReadWriteTimeout = 60000;
            using (WebResponse resp = req.GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }

        static void AddUnique(List<string> l, string s) { if (!l.Exists(delegate (string x) { return string.Equals(x, s, StringComparison.OrdinalIgnoreCase); })) l.Add(s); }

        static long DirSize(string dir)
        {
            long n = 0;
            foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) n += fi.Length;
            return n;
        }

        // localJson / senderTxt = text of the two lists; oldCommits / newCommits = repo -> commit sha (what is extracted now / what the
        // sender pinned); fetch = GitHub API reader (HttpGet, or a stub in tests). Throws a Russian message when it cannot say which
        // folders the update touches: then the update must not run.
        public static UpdatePlan Plan(string root, string localJson, string senderTxt, IDictionary<string, string> oldCommits, IDictionary<string, string> newCommits, Func<string, string> fetch, Action<string> log)
        {
            UpdatePlan pl = new UpdatePlan();
            string mods = System.IO.Path.Combine(root, @"GAMMA\mods");
            HashSet<string> write = new HashSet<string>(StringComparer.OrdinalIgnoreCase), removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddonDirs(ParseJson(localJson), ParseTxt(senderTxt), write, removed);
            pl.Notes.Add("модов с другим архивом: " + write.Count);
            HashSet<string> dirs = new HashSet<string>(write, StringComparer.OrdinalIgnoreCase);
            foreach (string r in removed) dirs.Add(r);
            List<string> outside = new List<string>();
            foreach (string repo in Repos)
            {
                string nw, od;
                if (!newCommits.TryGetValue(repo, out nw) || nw.Length != 40) throw new Exception("у отправителя не указана версия репозитория " + repo);
                if (!oldCommits.TryGetValue(repo, out od)) od = null;
                if (od != null && string.Equals(od, nw, StringComparison.OrdinalIgnoreCase)) continue;
                Dictionary<string, string> newTree, oldTree = null;
                try
                {
                    newTree = Tree(fetch, repo, nw);
                    if (od != null) oldTree = Tree(fetch, repo, od);
                }
                catch (Exception e) { throw new Exception("не удалось узнать, какие файлы меняются в " + repo + " (" + e.Message + ")"); }
                int n = 0;
                foreach (KeyValuePair<string, string> kv in newTree)
                {
                    string osha;
                    if (oldTree != null && oldTree.TryGetValue(kv.Key, out osha) && osha == kv.Value) continue;   // unchanged (od unknown: every file counts)
                    string md = ModDirOf(repo, kv.Key), os = OutsideOf(repo, kv.Key);
                    if (md != null) dirs.Add(md);
                    if (os != null) AddUnique(outside, os);
                    n++;
                }
                if (oldTree != null)
                    foreach (string p in oldTree.Keys)
                    {
                        if (newTree.ContainsKey(p)) continue;   // deleted upstream: the CLI deletes the file where it was extracted
                        string md = ModDirOf(repo, p), os = OutsideOf(repo, p);
                        if (md != null) dirs.Add(md);
                        if (os != null) AddUnique(outside, os);
                        n++;
                    }
                pl.Notes.Add(repo + ": файлов меняется " + n + (od == null ? " (старая версия неизвестна — беру все)" : ""));
            }
            foreach (string name in dirs)
            {
                if (name.Length == 0 || name.IndexOfAny(new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || name == "." || name == "..")
                    throw new Exception("недопустимое имя папки мода в списке: " + name);
                string rel = @"GAMMA\mods\" + name;
                if (Directory.Exists(System.IO.Path.Combine(root, rel))) pl.Dirs.Add(rel); else pl.NewDirs.Add(rel);
            }
            // the CLI also rewrites the MO2 profile, ModOrganizer.ini and the "applied commit" markers of the repos
            string prof = @"GAMMA\profiles\" + Profile;
            if (Directory.Exists(System.IO.Path.Combine(root, prof))) pl.Dirs.Add(prof);
            List<string> single = new List<string>(outside);
            single.Add(@"GAMMA\ModOrganizer.ini"); single.Add(@"GAMMA\version.txt");
            foreach (string repo in Repos) single.Add(@"cache\" + repo + ".applied-commit");
            foreach (string rel in single)
            {
                if (File.Exists(System.IO.Path.Combine(root, rel))) pl.Files.Add(rel); else pl.NewFiles.Add(rel);
            }
            long bytes = 0;
            foreach (string rel in pl.Dirs) bytes += DirSize(System.IO.Path.Combine(root, rel));
            foreach (string rel in pl.Files) bytes += new FileInfo(System.IO.Path.Combine(root, rel)).Length;
            pl.Bytes = bytes;
            if (log != null) log("Обновление: затронутся папки модов " + (pl.Dirs.Count + pl.NewDirs.Count) + ", файлов " + (pl.Files.Count + pl.NewFiles.Count) + ", резерв " + (bytes / 1048576) + " МБ; " + string.Join("; ", pl.Notes.ToArray()));
            return pl;
        }

        // ------------------------------------------------------------ backup / commit / rollback
        static string Abs(string root, string rel) { return System.IO.Path.Combine(root, rel); }

        static void CopyDir(string root, string src, string dst, ref long done, long total, Action<double, string> prog, Func<bool> aborted)
        {
            Directory.CreateDirectory(dst);
            string pre = new DirectoryInfo(src).FullName.TrimEnd('\\') + "\\";
            foreach (DirectoryInfo di in new DirectoryInfo(src).EnumerateDirectories("*", SearchOption.AllDirectories))
                Directory.CreateDirectory(System.IO.Path.Combine(dst, di.FullName.Substring(pre.Length)));
            foreach (FileInfo fi in new DirectoryInfo(src).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (aborted != null && aborted()) throw new OperationCanceledException();
                string to = System.IO.Path.Combine(dst, fi.FullName.Substring(pre.Length));
                File.Copy(fi.FullName, to, true);
                done += fi.Length;
                if (prog != null && total > 0) prog(Math.Min(1.0, (double)done / total), "копирую моды в резерв: " + (done / 1048576) + " из " + (total / 1048576) + " МБ");
            }
        }

        // checks free space, copies the folders and files of the plan into backup\update-<stamp>, writes the journal. Returns null when stopped
        // (nothing was changed and the half-made copy is removed). Throws when there is no room or a path is unsafe.
        public static UpdateJournal Begin(string root, UpdatePlan plan, string buildId, string buildPath, Action<string> log, Action<double, string> prog, Func<bool> aborted)
        {
            DriveInfo di = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(root)).Substring(0, 1));
            long need = plan.Bytes + Margin;
            if (di.AvailableFreeSpace < need)
                throw new Exception("Не хватает места для резервной копии модов: нужно " + (need / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture) + " ГБ, свободно " + (di.AvailableFreeSpace / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture) + " ГБ. Освободи место на диске " + di.Name.Substring(0, 2) + " и повтори. В игре ничего не изменено.");
            string stamp = "update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string relBackup = @"GAMMA Coop\backup\" + stamp;
            for (int n = 2; Directory.Exists(Abs(root, relBackup)); n++) relBackup = @"GAMMA Coop\backup\" + stamp + "-" + n;
            UpdateJournal j = new UpdateJournal();
            j.Started = Stamp(); j.Backup = relBackup; j.BuildId = buildId ?? ""; j.BuildPath = buildPath ?? ""; j.Bytes = plan.Bytes;
            j.Dirs = plan.Dirs; j.NewDirs = plan.NewDirs; j.Files = plan.Files; j.NewFiles = plan.NewFiles;
            string bk = Abs(root, relBackup);
            Safety.GuardWrite(root, bk, true);
            Directory.CreateDirectory(bk);
            Save(root, j);   // "running", not copied yet: a kill during the copy leaves the game untouched and this record says so
            long done = 0;
            try
            {
                foreach (string rel in plan.Dirs)
                {
                    Safety.GuardWrite(root, Abs(root, rel), false);   // no junction / symlink inside the folder we are about to replace
                    CopyDir(root, Abs(root, rel), Abs(bk, rel), ref done, plan.Bytes, prog, aborted);
                }
                foreach (string rel in plan.Files)
                {
                    if (aborted != null && aborted()) throw new OperationCanceledException();
                    string to = Abs(bk, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                    File.Copy(Abs(root, rel), to, true);
                    done += new FileInfo(to).Length;
                }
            }
            catch (Exception e)
            {
                try { Safety.DeleteDir(root, bk); } catch (Exception) { }
                j.State = "rolledback"; j.Finished = Stamp();
                try { Save(root, j); } catch (Exception) { }
                if (e is OperationCanceledException) { if (log != null) log("Резерв модов остановлен, копия удалена"); return null; }
                throw;
            }
            j.Copied = true;
            Save(root, j);
            if (log != null) log("Резерв модов готов: " + relBackup + " (" + (done / 1048576) + " МБ), папок " + plan.Dirs.Count + ", файлов " + plan.Files.Count);
            return j;
        }

        // the update finished: the copy is not needed any more (the journal stays as a record)
        public static void Commit(string root, UpdateJournal j, Action<string> log)
        {
            j.State = "done"; j.Finished = Stamp();
            Save(root, j);
            try { if (j.Backup.Length > 0) Safety.DeleteDir(root, Abs(root, j.Backup)); }
            catch (Exception e) { if (log != null) log("Резерв обновления не удалён: " + e.Message); }
            if (log != null) log("Обновление закончено, резерв удалён");
        }

        // puts every folder / file of the journal back as it was. Safe to run again after a crash: what is already back is skipped.
        // Returns the number of items restored. Throws (journal stays "running", backup kept) when something cannot be put back.
        public static int Rollback(string root, UpdateJournal j, Action<string> log, Action<double, string> prog)
        {
            string bk = Abs(root, j.Backup);
            int restored = 0, total = j.Dirs.Count + j.NewDirs.Count + j.Files.Count + j.NewFiles.Count, i = 0;
            if (j.Copied)
            {
                foreach (string rel in j.Dirs)
                {
                    i++;
                    if (prog != null) prog((double)i / Math.Max(1, total), "возвращаю моды: " + i + " / " + total);
                    string live = Abs(root, rel), src = Abs(bk, rel);
                    if (!Directory.Exists(src)) continue;   // already moved back
                    Safety.GuardWrite(root, live, false);
                    if (Directory.Exists(live)) Safety.DeleteDir(root, live, false);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(live));
                    Directory.Move(src, live);
                    restored++;
                    if (log != null) log("Возвращено: " + rel);
                }
                foreach (string rel in j.Files)
                {
                    i++;
                    string live = Abs(root, rel), src = Abs(bk, rel);
                    if (!File.Exists(src)) continue;
                    Safety.GuardWrite(root, live, false);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(live));
                    if (File.Exists(live)) File.SetAttributes(live, FileAttributes.Normal);
                    File.Copy(src, live, true);
                    File.Delete(src);
                    restored++;
                }
                foreach (string rel in j.NewDirs)
                {
                    i++;
                    string live = Abs(root, rel);
                    if (Directory.Exists(live)) { Safety.DeleteDir(root, live, false); restored++; if (log != null) log("Убрано новое: " + rel); }
                }
                foreach (string rel in j.NewFiles)
                {
                    i++;
                    string live = Abs(root, rel);
                    if (File.Exists(live)) { Safety.DeleteFile(root, live, false); restored++; }
                }
                // a half-done run must not be "resumed" onto folders that are old again
                string rs = Abs(root, @"GAMMA\resume.txt");
                if (File.Exists(rs)) Safety.DeleteFile(root, rs, false);
            }
            j.State = "rolledback"; j.Finished = Stamp();
            Save(root, j);
            try { if (Directory.Exists(bk)) Safety.DeleteDir(root, bk); }
            catch (Exception e) { if (log != null) log("Пустой резерв не удалён: " + e.Message); }
            if (log != null) log("Откат обновления: возвращено " + restored);
            return restored;
        }
    }
}
