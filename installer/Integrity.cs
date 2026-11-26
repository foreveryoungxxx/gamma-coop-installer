using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace GammaCoop
{
    // one line of the host's file list (files.manifest inside a build file)
    public class ManFile
    {
        public string Path;      // inside the mod folder, with /
        public long Size;
        public string Sha1 = ""; // "" or "-" = not hashed
    }

    public class FileManifest
    {
        public List<string> Mods = new List<string>();
        public Dictionary<string, List<ManFile>> Files = new Dictionary<string, List<ManFile>>(StringComparer.OrdinalIgnoreCase);
        public int Count;
        public bool Complete;    // the closing line was there
    }

    public class ModProblem
    {
        public string Name = "";
        public bool FolderMissing, Empty;
        public int Total, Missing, Size, Hash, Includes, Extra;
        public List<string> Examples = new List<string>();

        public string What()
        {
            if (FolderMissing) return "папки мода нет";
            if (Empty) return "папка пустая";
            List<string> l = new List<string>();
            if (Missing > 0) l.Add("нет файлов: " + Missing);
            if (Size > 0) l.Add("другой размер: " + Size);
            if (Hash > 0) l.Add("другое содержимое: " + Hash);
            if (Includes > 0) l.Add("не найден #include: " + Includes);
            return string.Join(", ", l.ToArray());
        }
    }

    public class IntegrityResult
    {
        public string Mode = "manifest";   // manifest = compared with the host's file list; local = only a sanity check of this PC
        public string Source = "", BuildId = "", BuildWhen = "";
        public int ModsChecked, FilesChecked, Extra, NotEnabled, NotChecked;
        public List<ModProblem> Bad = new List<ModProblem>();
        public double Seconds;
        public bool Aborted;
        public string ReportPath = "", CopyPath = "";
        public bool Ok { get { return Bad.Count == 0 && !Aborted; } }

        public List<string> BadNames()
        {
            List<string> l = new List<string>();
            foreach (ModProblem p in Bad) l.Add(p.Name);
            return l;
        }

        // one line for the done screen / the top of the report
        public string Line()
        {
            string how = Mode == "manifest" ? "по списку файлов хоста" : "только проверка этого компьютера";
            if (Aborted) return "Проверка целостности прервана";
            if (Bad.Count == 0)
                return "Всё целое ✓ (" + how + "): модов " + ModsChecked + ", файлов " + FilesChecked;
            return "Проверка целостности: проблемы в " + Bad.Count + " из " + ModsChecked + " модов (" + how + ")";
        }
    }

    public class RepairInfo
    {
        public string Path = "", Created = "", Source = "", Where = "";
        public DateTime CreatedUtc, FileTimeUtc;
        public long Size;
        public List<string> Mods = new List<string>();
        public string When { get { return CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture); } }
    }

    // "Проверка целостности": the host's file list in a build file (names + sizes of every file of every enabled mod, SHA-1 of the
    // small text files) and a read-only comparison of a game folder with it. No UI; used by Coop.Build and by the engine / hub.
    public static class Integrity
    {
        public const string ManifestEntry = "files.manifest";
        public const string ReportBegin = "=== МОДЫ ДЛЯ ПОЧИНКИ ===";
        public const string ReportEnd = "=== КОНЕЦ СПИСКА ===";
        public const string ReportPrefix = "GAMMA_Coop_report_";
        static readonly string[] HashExt = { ".ltx", ".script", ".xml", ".ini" };
        const long MaxHash = 16L * 1024 * 1024;
        static readonly Encoding Latin = Encoding.GetEncoding(28591);
        static readonly Regex IncludeRx = new Regex("^[ \\t]*#include[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        public static string ReportFile(string root) { return System.IO.Path.Combine(root, @"GAMMA Coop\integrity_report.txt"); }

        static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // SHA-1 of a text file with CRLF read as LF: the same mod unpacked by git with or without autocrlf is the same mod
        static string Sha1Text(string file)
        {
            byte[] b;
            using (FileStream f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536))
            {
                b = new byte[f.Length];
                int got = 0, n;
                while (got < b.Length && (n = f.Read(b, got, b.Length - got)) > 0) got += n;
                if (got < b.Length) Array.Resize(ref b, got);
            }
            int k = 0;
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] == 13 && i + 1 < b.Length && b[i + 1] == 10) continue;
                b[k++] = b[i];
            }
            using (SHA1 h = SHA1.Create()) return Hex(h.ComputeHash(b, 0, k));
        }

        // enabled (+) mods of an MO2 modlist.txt, separators left out
        public static List<string> EnabledMods(string modlistFile)
        {
            List<string> r = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(modlistFile, Encoding.UTF8))
            {
                if (line.Length < 2 || line[0] != '+') continue;
                string n = line.Substring(1).TrimEnd();
                if (n.EndsWith("_separator", StringComparison.OrdinalIgnoreCase) || !seen.Add(n)) continue;
                r.Add(n);
            }
            return r;
        }

        static string ModlistOf(string gamma) { return System.IO.Path.Combine(gamma, @"profiles\" + Coop.ActiveProfile(gamma) + @"\modlist.txt"); }

        // files MO2 or the mod itself rewrites: they legitimately differ between two PCs
        static bool Noise(string mod, string rel)
        {
            string low = rel.ToLowerInvariant();
            if (low == "meta.ini" || low.EndsWith(".bak") || low.EndsWith(".mohidden")) return true;
            return mod == "Discord RPC" && System.IO.Path.GetFileName(low) == "rpc.py";
        }

        // relative path (with /) -> size, of every file in a mod folder except the noise
        static Dictionary<string, long> LocalFiles(string dir, string mod)
        {
            Dictionary<string, long> r = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            string pre = new DirectoryInfo(dir).FullName.TrimEnd('\\') + "\\";
            foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string rel = fi.FullName.Substring(pre.Length);
                if (Noise(mod, rel)) continue;
                r[rel.Replace('\\', '/')] = fi.Length;
            }
            return r;
        }

        // ------------------------------------------------------------ the host's list
        // mods = the enabled mods of the profile; the per-user MCM mod is left out. Text, one line per file:
        // "F<TAB>size<TAB>sha1 of the text with CRLF as LF, or -<TAB>path". Never writes anything; the caller puts the text into the build zip.
        public static string BuildManifestText(string gamma, List<string> mods, Action<string> log, Action<double, string> prog, Func<bool> aborted)
        {
            StringBuilder sb = new StringBuilder(8 * 1024 * 1024);
            sb.Append("GCFM 1\n");
            string modsDir = System.IO.Path.Combine(gamma, "mods");
            int total = 0, done = 0, hashed = 0;
            foreach (string mod in mods)
            {
                done++;
                if (aborted != null && aborted()) throw new OperationCanceledException();
                if (prog != null) prog(mods.Count > 0 ? (double)done / mods.Count : 1.0, "список файлов модов: " + done + " / " + mods.Count);
                if (mod == Coop.McmModName) continue;
                string dir = System.IO.Path.Combine(modsDir, mod);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    string pre = new DirectoryInfo(dir).FullName.TrimEnd('\\') + "\\";
                    StringBuilder part = new StringBuilder();
                    int n = 0;
                    foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        string rel = fi.FullName.Substring(pre.Length);
                        if (Noise(mod, rel)) continue;
                        string sha = "-";
                        long len = fi.Length;
                        if (len <= MaxHash && Array.IndexOf(HashExt, fi.Extension.ToLowerInvariant()) >= 0)
                        {
                            try { sha = Sha1Text(fi.FullName); hashed++; }
                            catch (IOException) { sha = "-"; }
                        }
                        part.Append("F\t").Append(len.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(sha).Append('\t').Append(rel.Replace('\\', '/')).Append('\n');
                        n++;
                    }
                    if (n == 0) continue;
                    sb.Append("M\t").Append(mod).Append('\n').Append(part);
                    total += n;
                }
                catch (Exception e) { if (log != null) log("Список файлов: мод «" + mod + "» не прочитан: " + e.Message); }
            }
            sb.Append("E\t").Append(total.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (log != null) log("Список файлов модов: " + total + " файлов, с хэшем " + hashed);
            return sb.ToString();
        }

        public static FileManifest ParseManifest(string text)
        {
            FileManifest m = new FileManifest();
            List<ManFile> cur = null;
            foreach (string raw in text.Split('\n'))
            {
                if (raw.Length < 2) continue;
                string[] p = raw.TrimEnd('\r').Split('\t');
                if (p[0] == "M" && p.Length >= 2) { cur = new List<ManFile>(); m.Mods.Add(p[1]); m.Files[p[1]] = cur; }
                else if (p[0] == "F" && p.Length >= 4 && cur != null)
                {
                    ManFile f = new ManFile();
                    long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out f.Size);
                    f.Sha1 = p[2]; f.Path = p[3];
                    cur.Add(f); m.Count++;
                }
                else if (p[0] == "E" && p.Length >= 2) m.Complete = true;
            }
            return m;
        }

        // null when the build has no file list (format 1 / 2)
        public static FileManifest ReadManifestFile(string zipPath)
        {
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry e = z.GetEntry(ManifestEntry);
                if (e == null) return null;
                string txt;
                using (StreamReader r = new StreamReader(e.Open(), new UTF8Encoding(false), true)) txt = r.ReadToEnd();
                FileManifest m = ParseManifest(txt);
                return m.Complete ? m : null;
            }
        }

        // ------------------------------------------------------------ the check
        static HashSet<string> LocalEnabled(string gamma)
        {
            string ml = ModlistOf(gamma);
            if (!File.Exists(ml)) return null;
            return new HashSet<string>(EnabledMods(ml), StringComparer.OrdinalIgnoreCase);
        }

        static void Example(ModProblem p, string path, string why)
        {
            if (p.Examples.Count < 3) p.Examples.Add(path + " (" + why + ")");
        }

        // read-only: compares the enabled mods of the game folder with the host's list
        public static IntegrityResult CheckManifest(string root, FileManifest m, Action<double, string> prog, Func<bool> aborted)
        {
            Stopwatch sw = Stopwatch.StartNew();
            IntegrityResult r = new IntegrityResult();
            r.Mode = "manifest";
            string gamma = System.IO.Path.Combine(root, "GAMMA"), modsDir = System.IO.Path.Combine(gamma, "mods");
            HashSet<string> enabled = LocalEnabled(gamma);
            int i = 0;
            foreach (string mod in m.Mods)
            {
                i++;
                if (aborted != null && aborted()) { r.Aborted = true; break; }
                if (prog != null) prog((double)i / m.Mods.Count, "проверка модов: " + i + " / " + m.Mods.Count);
                if (enabled != null && !enabled.Contains(mod)) { r.NotEnabled++; continue; }
                r.ModsChecked++;
                List<ManFile> want = m.Files[mod];
                ModProblem p = new ModProblem();
                p.Name = mod; p.Total = want.Count;
                r.FilesChecked += want.Count;
                string dir = System.IO.Path.Combine(modsDir, mod);
                if (!Directory.Exists(dir))
                {
                    p.FolderMissing = true; p.Missing = want.Count;
                    r.Bad.Add(p);
                    continue;
                }
                Dictionary<string, long> have;
                try { have = LocalFiles(dir, mod); }
                catch (Exception e) { p.Missing = want.Count; p.Examples.Add("папка не читается: " + e.Message); r.Bad.Add(p); continue; }
                foreach (ManFile f in want)
                {
                    long sz;
                    if (!have.TryGetValue(f.Path, out sz)) { p.Missing++; Example(p, f.Path, "нет файла"); continue; }
                    if (f.Sha1.Length > 0 && f.Sha1 != "-")
                    {
                        // a text file: by content (a different size can be just CRLF against LF)
                        string h;
                        try { h = Sha1Text(System.IO.Path.Combine(dir, f.Path.Replace('/', '\\'))); }
                        catch (IOException) { continue; }   // busy right now: not a proof of a broken file
                        if (h != f.Sha1) { p.Hash++; Example(p, f.Path, "содержимое другое"); }
                    }
                    else if (sz != f.Size) { p.Size++; Example(p, f.Path, "размер " + sz + " вместо " + f.Size); }
                }
                p.Extra = have.Count - (want.Count - p.Missing);
                if (p.Extra > 0) r.Extra += p.Extra;
                if (p.Missing + p.Size + p.Hash > 0) r.Bad.Add(p);
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        // gamedata-relative path of "inc" written in a file that lives in gamedata-relative folder dirRel; null = cannot say
        static string ResolveInclude(string dirRel, string inc)
        {
            if (inc.IndexOf(':') >= 0 || inc.IndexOf('$') >= 0) return null;
            string p = (dirRel.Length > 0 ? dirRel + "/" : "") + inc.Replace('\\', '/');
            List<string> parts = new List<string>();
            foreach (string s in p.Split('/'))
            {
                if (s.Length == 0 || s == ".") continue;
                if (s == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
                else parts.Add(s);
            }
            return string.Join("/", parts.ToArray()).ToLowerInvariant();
        }

        // no host list available: only what this PC can say about itself. Every enabled mod folder must exist and hold a file;
        // every .ltx "#include" must resolve inside gamedata of an enabled mod or of ANOMALY. The base game keeps part of its files in
        // archives that cannot be read here (and a disabled mod may only override one of them), so an include nobody provides is an error
        // only when a file of that name lies elsewhere inside the same mod; the rest is counted as "не проверено". Prefers under-reporting.
        public static IntegrityResult CheckLocal(string root, Action<double, string> prog, Func<bool> aborted)
        {
            Stopwatch sw = Stopwatch.StartNew();
            IntegrityResult r = new IntegrityResult();
            r.Mode = "local";
            string gamma = System.IO.Path.Combine(root, "GAMMA"), modsDir = System.IO.Path.Combine(gamma, "mods");
            string ml = ModlistOf(gamma);
            if (!File.Exists(ml)) throw new Exception("Не найден список модов профиля: " + ml);
            List<string> enabledList = EnabledMods(ml);
            HashSet<string> enabled = new HashSet<string>(enabledList, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ModProblem> problems = new Dictionary<string, ModProblem>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> gd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);          // gamedata-relative files of enabled mods + ANOMALY
            List<string[]> ltx = new List<string[]>();                                            // {full path, mod, gamedata-relative folder, name}
            HashSet<string> modLtx = new HashSet<string>();                                       // "mod|name" of those
            List<string> dirs = new List<string>();
            foreach (string d in Directory.GetDirectories(modsDir)) dirs.Add(System.IO.Path.GetFileName(d));
            int i = 0;
            foreach (string mod in dirs)
            {
                i++;
                if (aborted != null && aborted()) { r.Aborted = true; return r; }
                if (prog != null) prog(0.5 * i / Math.Max(1, dirs.Count), "читаю моды: " + i + " / " + dirs.Count);
                bool on = enabled.Contains(mod);
                string dir = System.IO.Path.Combine(modsDir, mod);
                string pre = new DirectoryInfo(dir).FullName.TrimEnd('\\') + "\\";
                int files = 0;
                try
                {
                    foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        string rel = fi.FullName.Substring(pre.Length).Replace('\\', '/');
                        if (Noise(mod, rel)) continue;
                        files++;
                        bool isLtx = fi.Extension.Equals(".ltx", StringComparison.OrdinalIgnoreCase);
                        if (!rel.StartsWith("gamedata/", StringComparison.OrdinalIgnoreCase)) continue;
                        string g = rel.Substring(9);
                        if (on)
                        {
                            gd.Add(g);
                            if (isLtx && fi.Length <= 2 * 1024 * 1024)
                            {
                                int sl = g.LastIndexOf('/');
                                ltx.Add(new string[] { fi.FullName, mod, sl < 0 ? "" : g.Substring(0, sl), sl < 0 ? g : g.Substring(sl + 1) });
                                modLtx.Add(mod + "|" + (sl < 0 ? g : g.Substring(sl + 1)).ToLowerInvariant());
                            }
                        }
                    }
                }
                catch (Exception) { if (on) { r.NotChecked++; continue; } }
                if (!on) continue;
                r.ModsChecked++; r.FilesChecked += files;
                // an empty "gamedata" is a legit (empty) mod; a folder with nothing at all is a broken one
                if (files == 0 && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    ModProblem p = new ModProblem();
                    p.Name = mod; p.Empty = true;
                    problems[mod] = p;
                }
            }
            foreach (string mod in enabledList)
                if (!Directory.Exists(System.IO.Path.Combine(modsDir, mod)))
                {
                    ModProblem p = new ModProblem();
                    p.Name = mod; p.FolderMissing = true;
                    problems[mod] = p;
                    r.ModsChecked++;
                }
            string agd = System.IO.Path.Combine(root, @"ANOMALY\gamedata");
            if (Directory.Exists(agd))
            {
                try
                {
                    string pre = new DirectoryInfo(agd).FullName.TrimEnd('\\') + "\\";
                    foreach (FileInfo fi in new DirectoryInfo(agd).EnumerateFiles("*", SearchOption.AllDirectories)) gd.Add(fi.FullName.Substring(pre.Length).Replace('\\', '/'));
                }
                catch (Exception) { }
            }
            i = 0;
            foreach (string[] f in ltx)
            {
                i++;
                if (aborted != null && aborted()) { r.Aborted = true; return r; }
                if (prog != null && i % 200 == 0) prog(0.5 + 0.5 * i / ltx.Count, "ищу #include: " + i + " / " + ltx.Count);
                string text;
                try { text = Latin.GetString(File.ReadAllBytes(f[0])); }
                catch (Exception) { continue; }
                if (text.IndexOf("#include", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Match mt in IncludeRx.Matches(text))
                {
                    string inc = mt.Groups[1].Value.Trim();
                    string want = ResolveInclude(f[2], inc);
                    if (want == null) { r.NotChecked++; continue; }
                    if (gd.Contains(want)) continue;
                    // a file of that name inside the same mod, just not where the include looks: a half-replaced or misplaced mod
                    if (!modLtx.Contains(f[1] + "|" + want.Substring(want.LastIndexOf('/') + 1))) { r.NotChecked++; continue; }
                    string why = "лежит в другой папке этого же мода";
                    ModProblem p;
                    if (!problems.TryGetValue(f[1], out p)) { p = new ModProblem(); p.Name = f[1]; problems[f[1]] = p; }
                    p.Includes++;
                    Example(p, f[3] + ": #include \"" + inc + "\"", why);
                }
            }
            foreach (string mod in enabledList) { ModProblem p; if (problems.TryGetValue(mod, out p)) r.Bad.Add(p); }
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        // the host's list from the given build (when it has one) or from the newest saved build that has one; otherwise the local check.
        // zipPath may be null.
        public static IntegrityResult Run(string root, string zipPath, Action<double, string> prog, Func<bool> aborted, Action<string> log)
        {
            List<string> cands = new List<string>();
            if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath)) cands.Add(zipPath);
            string bd = Coop.BuildsDir(root);
            if (Directory.Exists(bd))
            {
                List<string> l = new List<string>(Directory.GetFiles(bd, "*.zip"));
                l.Sort(delegate (string a, string b) { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); });
                foreach (string s in l) if (!cands.Exists(delegate (string c) { return string.Equals(c, s, StringComparison.OrdinalIgnoreCase); })) cands.Add(s);
            }
            FileManifest m = null;
            string src = null;
            foreach (string c in cands)
            {
                try { m = ReadManifestFile(c); }
                catch (Exception e) { if (log != null) log("Список файлов из " + c + " не прочитан: " + e.Message); m = null; }
                if (m != null) { src = c; break; }
            }
            IntegrityResult r;
            if (m != null)
            {
                r = CheckManifest(root, m, prog, aborted);
                r.Source = src;
                PackInfo pk = Coop.Probe(src);
                if (pk != null) { r.BuildId = pk.BuildId; r.BuildWhen = pk.When; }
            }
            else r = CheckLocal(root, prog, aborted);
            if (log != null) log("Проверка целостности: " + r.Line() + ", " + r.Seconds.ToString("F1", CultureInfo.InvariantCulture) + " с" + (r.Source.Length > 0 ? ", сборка " + r.Source : ""));
            return r;
        }

        // ------------------------------------------------------------ the report
        public static string ReportText(string root, IntegrityResult r)
        {
            StringBuilder b = new StringBuilder();
            b.Append("GAMMA Coop — проверка целостности\r\n");
            b.Append("Дата: ").Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)).Append("\r\n");
            b.Append("Компьютер: ").Append(Environment.MachineName).Append("\r\n");
            b.Append("Папка игры: ").Append(root).Append("\r\n");
            b.Append("Сверка: ").Append(r.Mode == "manifest" ? "со списком файлов хоста (сборка " + (r.BuildId.Length > 0 ? r.BuildId : "?") + (r.BuildWhen.Length > 0 ? " от " + r.BuildWhen : "") + ")" : "только проверка этого компьютера (в сборке нет списка файлов)").Append("\r\n");
            b.Append("Итог: ").Append(r.Line()).Append("\r\n");
            b.Append("Проверено модов: ").Append(r.ModsChecked).Append(", файлов: ").Append(r.FilesChecked).Append(", лишних файлов: ").Append(r.Extra)
                .Append(", не проверено: ").Append(r.NotChecked).Append(", время: ").Append(r.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append(" с\r\n\r\n");
            b.Append(ReportBegin).Append("\r\n");
            foreach (ModProblem p in r.Bad) b.Append(p.Name).Append("\r\n");
            b.Append(ReportEnd).Append("\r\n\r\n");
            if (r.Bad.Count > 0)
            {
                b.Append("Подробности:\r\n");
                foreach (ModProblem p in r.Bad)
                {
                    b.Append("  ").Append(p.Name).Append(" — ").Append(p.What()).Append("\r\n");
                    foreach (string e in p.Examples) b.Append("      ").Append(e).Append("\r\n");
                }
                b.Append("\r\nПришли этот файл хосту: у него [H] «Моды для починки» соберёт файл починки.\r\n");
            }
            return b.ToString();
        }

        // writes root\GAMMA Coop\integrity_report.txt (reportFile) and, when something is broken, a copy GAMMA_Coop_report_<date>.txt in copyDir
        // (Downloads: the friend sends it to the host). Either path may be null. Failures are logged, never thrown.
        public static void SaveReport(string root, IntegrityResult r, string reportFile, string copyDir, Action<string> log)
        {
            string text = ReportText(root, r);
            try
            {
                if (reportFile != null)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportFile));
                    File.WriteAllText(reportFile, text, new UTF8Encoding(true));
                    r.ReportPath = reportFile;
                }
                if (copyDir != null && r.Bad.Count > 0)
                {
                    Directory.CreateDirectory(copyDir);
                    string cp = System.IO.Path.Combine(copyDir, ReportPrefix + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt");
                    File.WriteAllText(cp, text, new UTF8Encoding(true));
                    r.CopyPath = cp;
                }
            }
            catch (Exception e) { if (log != null) log("Отчёт о целостности не записан: " + e.Message); }
        }

        // mod names from a report file (what the friend sent); empty when the file is not a report
        public static List<string> ReadReportMods(string file)
        {
            List<string> r = new List<string>();
            bool inside = false;
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                string l = raw.Trim();
                if (l == ReportBegin) { inside = true; continue; }
                if (l == ReportEnd) break;
                if (inside && l.Length > 0 && !r.Contains(l)) r.Add(l);
            }
            return r;
        }
    }

    // "Собрать моды для починки" (host) and "применить файл починки" (friend): whole mod folders in a small zip
    public static partial class Coop
    {
        public const string RepairPrefix = "GAMMA_Coop_repair_";
        public const string RepairEntry = "repair.json";

        public static string NewRepairName() { return RepairPrefix + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip"; }

        // null when the file is not a repair file
        public static RepairInfo ProbeRepair(string zipPath)
        {
            try
            {
                if (!File.Exists(zipPath)) return null;
                using (ZipArchive z = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry e = z.GetEntry(RepairEntry);
                    if (e == null) return null;
                    string txt;
                    using (StreamReader r = new StreamReader(e.Open(), new UTF8Encoding(false), true)) txt = r.ReadToEnd();
                    Dictionary<string, object> d = Ser().DeserializeObject(txt) as Dictionary<string, object>;
                    if (d == null || Str(d, "kind") != "repair") return null;
                    RepairInfo ri = new RepairInfo();
                    ri.Path = zipPath; ri.Created = Str(d, "created"); ri.Source = Str(d, "sourceComputer");
                    try { ri.Size = new FileInfo(zipPath).Length; } catch (Exception) { }
                    if (!DateTime.TryParse(ri.Created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out ri.CreatedUtc))
                        ri.CreatedUtc = File.GetLastWriteTimeUtc(zipPath);
                    IEnumerable ml = d.ContainsKey("mods") ? d["mods"] as IEnumerable : null;
                    if (ml != null) foreach (object o in ml) if (o != null) ri.Mods.Add(o.ToString());
                    return ri;
                }
            }
            catch (Exception) { return null; }
        }

        // host: zips the given mod folders of hostRoot (read-only). Returns the number of mods that went in.
        public static int BuildRepair(string hostRoot, List<string> mods, string outZip, Action<string> log, Action<double, string> prog, Func<bool> aborted)
        {
            string modsDir = System.IO.Path.Combine(hostRoot, @"GAMMA\mods");
            List<string> names = new List<string>(), srcs = new List<string>(), put = new List<string>();
            foreach (string m in mods)
            {
                if (!ValidName(m)) { log("Имя мода не подходит, пропускаю: " + m); continue; }
                if (m == McmModName) { log("Личные настройки не отправляю: " + m); continue; }
                string dir = System.IO.Path.Combine(modsDir, m);
                if (!Directory.Exists(dir)) { log("У хоста нет мода «" + m + "» — пропускаю"); continue; }
                int before = names.Count;
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    names.Add("mods/" + m + "/" + f.Substring(dir.Length + 1).Replace('\\', '/')); srcs.Add(f);
                }
                if (names.Count == before) { log("Мод «" + m + "» пустой — пропускаю"); continue; }
                put.Add(m);
            }
            if (put.Count == 0) throw new Exception("Нечего собирать: ни одного из этих модов нет у тебя в GAMMA\\mods.");
            long grand = 0;
            foreach (string s in srcs) { try { grand += new FileInfo(s).Length; } catch (Exception) { } }
            string outDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outZip));
            Directory.CreateDirectory(outDir);
            string tmp = outZip + ".tmp";
            if (File.Exists(tmp)) Safety.DeleteFile(outDir, tmp, false);
            List<object> files = new List<object>();
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
                            Dictionary<string, object> fd = new Dictionary<string, object>();
                            fd["path"] = names[i]; fd["size"] = size; fd["sha256"] = Hex(h.Hash);
                            files.Add(fd);
                        }
                        bytes += size;
                        if (prog != null) prog(grand > 0 ? Math.Min(1.0, (double)bytes / grand) : 1.0, "сжимаю файлы: " + (i + 1) + " / " + names.Count);
                    }
                    Dictionary<string, object> man = new Dictionary<string, object>();
                    man["format"] = 1; man["kind"] = "repair"; man["build"] = BuildName;
                    man["created"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                    man["sourceComputer"] = Environment.MachineName;
                    man["mods"] = new List<object>(put.ToArray());
                    man["files"] = files;
                    ZipArchiveEntry me = z.CreateEntry(RepairEntry, CompressionLevel.Optimal);
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
                throw;
            }
            return put.Count;
        }

        // friend: puts the mod folders of a repair file into GAMMA\mods. Everything is checked first; the old folders go to
        // GAMMA Coop\backup (same machinery as Apply); a failure or a stop midway removes the half-written folders and moves the old ones back.
        // Returns false when stopped.
        public static bool ApplyRepair(string zipPath, string root, Action<string> log, Action<double, string> prog, Func<bool> aborted)
        {
            string modsDir = System.IO.Path.Combine(root, @"GAMMA\mods");
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry me = z.GetEntry(RepairEntry);
                if (me == null) throw new Exception("в файле нет " + RepairEntry);
                string txt;
                using (StreamReader r = new StreamReader(me.Open(), new UTF8Encoding(false), true)) txt = r.ReadToEnd();
                Dictionary<string, object> d = Ser().DeserializeObject(txt) as Dictionary<string, object>;
                if (d == null || Str(d, "kind") != "repair") throw new Exception("это не файл починки");
                List<string> mods = new List<string>();
                IEnumerable ml = d.ContainsKey("mods") ? d["mods"] as IEnumerable : null;
                if (ml != null) foreach (object o in ml) if (o != null) mods.Add(o.ToString());
                IEnumerable fl = d.ContainsKey("files") ? d["files"] as IEnumerable : null;
                if (mods.Count == 0 || fl == null) throw new Exception("файл починки пуст");
                foreach (string m in mods)
                {
                    if (!ValidName(m) || m == McmModName) throw new Exception("в файле починки недопустимое имя мода: " + m);
                    Dest(modsDir, m);
                }
                List<string> paths = new List<string>(), shas = new List<string>();
                List<long> sizes = new List<long>();
                long total = 0;
                foreach (object o in fl)
                {
                    Dictionary<string, object> fd = o as Dictionary<string, object>;
                    if (fd == null) continue;
                    string p = Str(fd, "path");
                    if (!p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) throw new Exception("в файле починки лишний путь: " + p);
                    string rest = p.Substring(5);
                    int sl = rest.IndexOf('/');
                    string first = sl < 0 ? rest : rest.Substring(0, sl);
                    if (sl < 0 || !mods.Exists(delegate (string m) { return string.Equals(m, first, StringComparison.OrdinalIgnoreCase); })) throw new Exception("в файле починки лишний файл: " + p);
                    Dest(modsDir, rest);
                    long sz = 0; long.TryParse(Str(fd, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out sz);
                    paths.Add(p); shas.Add(Str(fd, "sha256").ToLowerInvariant()); sizes.Add(sz); total += sz;
                }
                if (paths.Count == 0) throw new Exception("файл починки пуст");
                long done = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    if (aborted()) return false;
                    ZipArchiveEntry e = z.GetEntry(paths[i]);
                    if (e == null) throw new Exception("в файле починки нет " + paths[i]);
                    string sha;
                    using (Stream s = e.Open()) sha = Sha(s);
                    if (sha != shas[i]) throw new Exception("Файл починки повреждён: не совпала контрольная сумма " + paths[i] + ". Скопируй файл заново.");
                    done += sizes[i];
                    if (total > 0) prog(0.4 * done / total, "проверка файла починки");
                }
                log("Починка: контрольные суммы " + paths.Count + " файлов совпали, модов " + mods.Count);

                Directory.CreateDirectory(modsDir);
                Safety.GuardWrite(root, modsDir);
                string stamp = System.IO.Path.Combine(root, @"GAMMA Coop\backup\" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                string backupDir = stamp;
                for (int n = 2; Directory.Exists(backupDir); n++) backupDir = stamp + "-" + n;
                int bDirs = 0, bFiles = 0;
                MoveLog = new List<string[]>();
                bool ok = false;
                try
                {
                    foreach (string m in mods)
                    {
                        string md = System.IO.Path.Combine(modsDir, m);
                        if (Directory.Exists(md)) BackupMove(root, backupDir, md, true, ref bDirs, ref bFiles);
                    }
                    done = 0;
                    for (int i = 0; i < paths.Count; i++)
                    {
                        if (aborted()) return false;
                        string dest = Dest(modsDir, paths[i].Substring(5));
                        Safety.GuardWrite(root, dest, false);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                        WriteEntry(z.GetEntry(paths[i]), dest, shas[i]);
                        done += sizes[i];
                        if (total > 0) prog(0.4 + 0.58 * done / total, "распаковка");
                    }
                    ok = true;
                }
                finally
                {
                    if (!ok)
                    {
                        // half-written folders out, the old ones back
                        foreach (string m in mods)
                        {
                            try { Safety.DeleteDir(root, System.IO.Path.Combine(modsDir, m)); } catch (Exception e) { log("Не удалось убрать недописанный мод " + m + ": " + e.Message); }
                        }
                        UndoMoves(log);
                        log("Починка остановлена: старые моды возвращены");
                    }
                    MoveLog = null;
                }
                log("Починка: заменено модов " + mods.Count + (bDirs > 0 ? ", старые в " + backupDir : ""));
                if (bDirs > 0) TrimBackups(root, log);
            }
            try
            {
                string ap = System.IO.Path.Combine(root, @"GAMMA Coop\applied_repairs.txt");
                File.AppendAllText(ap, System.IO.Path.GetFileName(zipPath) + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
            return true;
        }

        // repair files found by Finder that were not applied yet
        public static bool RepairApplied(string root, string zipPath)
        {
            try
            {
                string ap = System.IO.Path.Combine(root, @"GAMMA Coop\applied_repairs.txt");
                if (!File.Exists(ap)) return false;
                string n = System.IO.Path.GetFileName(zipPath);
                foreach (string l in File.ReadAllLines(ap)) if (string.Equals(l.Trim(), n, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception) { }
            return false;
        }
    }
}
