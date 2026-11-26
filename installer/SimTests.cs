using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace GammaCoop
{
    // -Simulate only: checks of the integrity check, repair files and the [U] backup / rollback on a synthetic game folder
    // (real files in a temp folder, removed at the end; nothing outside it is touched)
    public partial class App
    {
        static void WF(string root, string rel, string text)
        {
            string f = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, text, new UTF8Encoding(false));
        }

        // a small game folder: 4 enabled mods, one disabled, one separator, the per-user MCM mod
        static void MakeTree(string d)
        {
            WF(d, @"GAMMA\ModOrganizer.exe", "x");
            WF(d, @"ANOMALY\bin\AnomalyDX9.exe", "x");
            WF(d, @"GAMMA\ModOrganizer.ini", "[General]\r\nselected_profile=@ByteArray(G.A.M.M.A)\r\n");
            WF(d, @"GAMMA\profiles\G.A.M.M.A\modlist.txt", "# list\r\n+ModA\r\n+ModB\r\n+ModC\r\n+ModD\r\n-Off\r\n+Sec_separator\r\n");
            WF(d, @"GAMMA\mods\ModA\gamedata\configs\a.ltx", "[a]\r\n#include \"inc_a.ltx\"\r\n");
            WF(d, @"GAMMA\mods\ModA\gamedata\configs\inc_a.ltx", "[inc]\r\nx=1\r\n");
            WF(d, @"GAMMA\mods\ModA\gamedata\scripts\s.script", "print('a')\r\n");
            WF(d, @"GAMMA\mods\ModA\meta.ini", "[General]\r\nmodid=1\r\n");
            WF(d, @"GAMMA\mods\ModB\gamedata\b.xml", "<b/>\r\n");
            byte[] big = new byte[100000];
            new Random(7).NextBytes(big);
            Directory.CreateDirectory(Path.Combine(d, @"GAMMA\mods\ModB\gamedata\textures"));
            File.WriteAllBytes(Path.Combine(d, @"GAMMA\mods\ModB\gamedata\textures\big.dds"), big);
            WF(d, @"GAMMA\mods\ModC\gamedata\c.ltx", "[c]\r\nv=1\r\n");
            WF(d, @"GAMMA\mods\ModD\gamedata\d.ltx", "value=AAAA\r\n");
            WF(d, @"GAMMA\mods\Off\gamedata\o.ltx", "[o]\r\n");
            WF(d, @"GAMMA\mods\Sec_separator\meta.ini", "[General]\r\n");
            WF(d, @"GAMMA\mods\" + Coop.McmModName + @"\gamedata\configs\axr_options.ltx", "personal=1\r\n");
        }

        // relative path -> "size:sha1" of every file, except GAMMA Coop (the installer's own folder)
        static Dictionary<string, string> TreeSig(string dir)
        {
            Dictionary<string, string> r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string pre = new DirectoryInfo(dir).FullName.TrimEnd('\\') + "\\";
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(pre.Length);
                if (rel.StartsWith(@"GAMMA Coop\", StringComparison.OrdinalIgnoreCase)) continue;
                using (SHA1 h = SHA1.Create())
                using (FileStream fs = File.OpenRead(f))
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (byte x in h.ComputeHash(fs)) sb.Append(x.ToString("x2"));
                    r[rel] = new FileInfo(f).Length + ":" + sb;
                }
            }
            return r;
        }

        static string SameSig(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            foreach (KeyValuePair<string, string> kv in a)
            {
                string v;
                if (!b.TryGetValue(kv.Key, out v)) return "после: нет файла " + kv.Key;
                if (v != kv.Value) return "после: другой файл " + kv.Key;
            }
            foreach (string k in b.Keys) if (!a.ContainsKey(k)) return "после: лишний файл " + k;
            return null;
        }

        static string TFail(string why) { return "FAIL: " + why; }

        static ModProblem Find(IntegrityResult r, string name)
        {
            foreach (ModProblem p in r.Bad) if (p.Name == name) return p;
            return null;
        }

        string TestIntegrity(string tmp)
        {
            string h = Path.Combine(tmp, "int");
            MakeTree(h);
            string gamma = Path.Combine(h, "GAMMA");
            List<string> mods = Integrity.EnabledMods(Path.Combine(gamma, @"profiles\G.A.M.M.A\modlist.txt"));
            if (mods.Count != 4) return TFail("включённых модов " + mods.Count + " вместо 4");
            string text = Integrity.BuildManifestText(gamma, mods, null, null, null);
            FileManifest m = Integrity.ParseManifest(text);
            if (!m.Complete || m.Mods.Count != 4 || m.Count != 7) return TFail("список файлов: модов " + m.Mods.Count + ", файлов " + m.Count);
            if (text.Contains("meta.ini") || text.Contains("axr_options")) return TFail("в списке есть meta.ini или личный мод");
            // the list travels inside a zip
            string zp = Path.Combine(tmp, "b.zip");
            using (ZipArchive z = ZipFile.Open(zp, ZipArchiveMode.Create))
            using (StreamWriter w = new StreamWriter(z.CreateEntry(Integrity.ManifestEntry).Open(), new UTF8Encoding(false))) w.Write(text);
            FileManifest m2 = Integrity.ReadManifestFile(zp);
            if (m2 == null || m2.Count != 7) return TFail("список из zip не прочитан");
            IntegrityResult ok = Integrity.CheckManifest(h, m, null, null);
            if (!ok.Ok || ok.ModsChecked != 4 || ok.FilesChecked != 7) return TFail("целая папка не прошла проверку: " + ok.Line());
            WF(h, @"GAMMA\mods\ModA\meta.ini", "[General]\r\nmodid=2\r\n");
            WF(h, @"GAMMA\mods\ModB\gamedata\extra.txt", "extra");
            IntegrityResult ok2 = Integrity.CheckManifest(h, m, null, null);
            if (!ok2.Ok || ok2.Extra != 1) return TFail("meta.ini / лишний файл не должны ломать проверку: " + ok2.Line() + " extra " + ok2.Extra);
            // break four mods in four different ways
            File.Delete(Path.Combine(h, @"GAMMA\mods\ModA\gamedata\configs\inc_a.ltx"));
            using (FileStream fs = new FileStream(Path.Combine(h, @"GAMMA\mods\ModB\gamedata\textures\big.dds"), FileMode.Append)) fs.WriteByte(1);
            Directory.Delete(Path.Combine(h, @"GAMMA\mods\ModC"), true);
            WF(h, @"GAMMA\mods\ModD\gamedata\d.ltx", "value=BBBB\r\n");
            IntegrityResult bad = Integrity.CheckManifest(h, m, null, null);
            ModProblem a = Find(bad, "ModA"), b = Find(bad, "ModB"), c = Find(bad, "ModC"), d = Find(bad, "ModD");
            if (bad.Ok || bad.Bad.Count != 4 || a == null || b == null || c == null || d == null) return TFail("не нашёл все 4 поломки: " + bad.Bad.Count);
            if (a.Missing != 1 || b.Size != 1 || !c.FolderMissing || d.Hash != 1) return TFail("не тот тип поломки: A.missing " + a.Missing + " B.size " + b.Size + " C.folder " + c.FolderMissing + " D.hash " + d.Hash);
            // the report: file in the game folder (BOM, names in the marked section) + a copy for the friend
            string rep = Path.Combine(tmp, @"rep\integrity_report.txt"), dl = Path.Combine(tmp, "dl");
            Integrity.SaveReport(h, bad, rep, dl, null);
            string[] cp = Directory.Exists(dl) ? Directory.GetFiles(dl, "GAMMA_Coop_report_*.txt") : new string[0];
            if (!File.Exists(rep) || cp.Length != 1) return TFail("отчёт или его копия не записаны");
            byte[] head = new byte[3];
            using (FileStream fs = File.OpenRead(rep)) fs.Read(head, 0, 3);
            if (head[0] != 0xEF || head[1] != 0xBB || head[2] != 0xBF) return TFail("отчёт без BOM");
            List<string> names = Integrity.ReadReportMods(cp[0]);
            if (names.Count != 4 || !names.Contains("ModC")) return TFail("имена модов из отчёта: " + string.Join("|", names.ToArray()));
            // no host list: only the local sanity check; an intact folder passes, a misplaced include / an empty folder do not
            string h2 = Path.Combine(tmp, "int2");
            MakeTree(h2);
            IntegrityResult l1 = Integrity.CheckLocal(h2, null, null);
            if (!l1.Ok || l1.Mode != "local") return TFail("локальная проверка целой папки: " + l1.Line());
            Directory.CreateDirectory(Path.Combine(h2, @"GAMMA\mods\ModA\gamedata\configs\sub"));
            File.Move(Path.Combine(h2, @"GAMMA\mods\ModA\gamedata\configs\inc_a.ltx"), Path.Combine(h2, @"GAMMA\mods\ModA\gamedata\configs\sub\inc_a.ltx"));
            Directory.Delete(Path.Combine(h2, @"GAMMA\mods\ModD"), true);
            Directory.CreateDirectory(Path.Combine(h2, @"GAMMA\mods\ModD"));
            Directory.Delete(Path.Combine(h2, @"GAMMA\mods\ModC"), true);
            IntegrityResult l2 = Integrity.CheckLocal(h2, null, null);
            ModProblem la = Find(l2, "ModA"), ld = Find(l2, "ModD"), lc = Find(l2, "ModC");
            if (la == null || la.Includes != 1 || ld == null || !ld.Empty || lc == null || !lc.FolderMissing) return TFail("локальная проверка не нашла поломки: " + l2.Bad.Count);
            return "OK";
        }

        string TestRepair(string tmp)
        {
            string host = Path.Combine(tmp, "host"), friend = Path.Combine(tmp, "friend");
            MakeTree(host); MakeTree(friend);
            FileManifest m = Integrity.ParseManifest(Integrity.BuildManifestText(Path.Combine(host, "GAMMA"), Integrity.EnabledMods(Path.Combine(host, @"GAMMA\profiles\G.A.M.M.A\modlist.txt")), null, null, null));
            File.Delete(Path.Combine(friend, @"GAMMA\mods\ModA\gamedata\configs\inc_a.ltx"));
            WF(friend, @"GAMMA\mods\ModD\gamedata\d.ltx", "value=BBBB\r\n");
            Directory.Delete(Path.Combine(friend, @"GAMMA\mods\ModC"), true);
            IntegrityResult before = Integrity.CheckManifest(friend, m, null, null);
            if (before.Bad.Count != 3) return TFail("у друга поломок " + before.Bad.Count + " вместо 3");
            string zip = Path.Combine(tmp, @"out\" + Coop.NewRepairName());
            int n = Coop.BuildRepair(host, before.BadNames(), zip, delegate (string s) { }, null, null);
            RepairInfo ri = Coop.ProbeRepair(zip);
            if (n != 3 || ri == null || ri.Mods.Count != 3) return TFail("файл починки: модов " + n);
            if (Coop.ProbeRepair(Path.Combine(tmp, "b.zip")) != null) return TFail("обычный zip принят за файл починки");
            if (Coop.RepairApplied(friend, zip)) return TFail("файл починки уже числится применённым");
            // a stop in the middle of unpacking: the old folders come back untouched
            Dictionary<string, string> sigBefore = TreeSig(friend);
            int calls = 0, verifyCalls = 0;
            using (ZipArchive z = ZipFile.OpenRead(zip)) verifyCalls = z.Entries.Count - 1;
            bool done = Coop.ApplyRepair(zip, friend, delegate (string s) { }, delegate (double f, string w) { }, delegate () { return ++calls > verifyCalls + 2; });
            string diff = SameSig(sigBefore, TreeSig(friend));
            if (done || diff != null) return TFail("остановка посреди починки: " + (done ? "вернул true" : diff));
            // the real thing
            if (!Coop.ApplyRepair(zip, friend, delegate (string s) { }, delegate (double f, string w) { }, delegate () { return false; })) return TFail("починка вернула false");
            Dictionary<string, string> hs = TreeSig(Path.Combine(host, @"GAMMA\mods")), fs2 = TreeSig(Path.Combine(friend, @"GAMMA\mods"));
            string d3 = SameSig(hs, fs2);
            if (d3 != null) return TFail("после починки моды не совпали с хостом: " + d3);
            IntegrityResult after = Integrity.CheckManifest(friend, m, null, null);
            if (!after.Ok) return TFail("после починки проверка не прошла: " + after.Line());
            bool inBackup = false;
            foreach (string bd in Directory.GetDirectories(Path.Combine(friend, @"GAMMA Coop\backup"))) if (File.Exists(Path.Combine(bd, @"GAMMA\mods\ModD\gamedata\d.ltx"))) inBackup = true;
            if (!inBackup) return TFail("старый мод не лежит в резерве");
            if (!Coop.RepairApplied(friend, zip)) return TFail("применённый файл не записан в applied_repairs.txt");
            return "OK";
        }

        static string TreeJson(string[][] items)
        {
            StringBuilder b = new StringBuilder("{\"sha\":\"x\",\"truncated\":false,\"tree\":[");
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) b.Append(',');
                b.Append("{\"path\":\"").Append(items[i][0]).Append("\",\"type\":\"").Append(items[i].Length > 2 ? items[i][2] : "blob").Append("\",\"sha\":\"").Append(items[i][1]).Append("\"}");
            }
            return b.Append("]}").ToString();
        }

        string[] TestGuard(string tmp)
        {
            string[] res = { "?", "?", "?", "?", "?" };   // plan, restore, journal offer, commit, space refusal
            string g = Path.Combine(tmp, "guard");
            MakeTree(g);
            string A = new string('a', 40), B = new string('b', 40), C = new string('c', 40);
            foreach (string mod in new string[] { "1- Alpha - Auth", "2- Beta - Auth2", "3- Gamma - Auth3", "Gamma_Mod", "Other", "312- Gunslinger Guns for Anomaly - Teivazcz & Gunslinger Team" })
            {
                WF(g, @"GAMMA\mods\" + mod + @"\gamedata\f.ltx", "file of " + mod + "\r\n");
                WF(g, @"GAMMA\mods\" + mod + @"\gamedata\g.ltx", "second file of " + mod + "\r\n");
            }
            WF(g, @"GAMMA\profiles\G.A.M.M.A\settings.ini", "[a]\r\n");
            WF(g, @"GAMMA\version.txt", "920");
            WF(g, @"ANOMALY\gamedata\p.ltx", "old patch\r\n");
            WF(g, @"cache\Stalker_GAMMA.applied-commit", A);
            string json = "[{\"dlLink\":\"https://www.moddb.com/addons/start/1\",\"instructions\":[],\"patch\":\"Auth\",\"addonName\":\"Alpha\",\"modDbUrl\":\"u1\",\"zipName\":\"z1.7z\",\"md5ModDb\":\"m1\"},"
                + "{\"dlLink\":\"https://www.moddb.com/addons/start/2\",\"instructions\":[],\"patch\":\"Auth2\",\"addonName\":\"Beta\",\"modDbUrl\":\"u2\",\"zipName\":\"z2.7z\",\"md5ModDb\":\"m2\"},"
                + "{\"dlLink\":\"https://www.moddb.com/addons/start/3\",\"instructions\":[\"x\\\\y\"],\"patch\":\"Auth3\",\"addonName\":\"Gamma\",\"modDbUrl\":\"u3\",\"zipName\":\"z3.7z\",\"md5ModDb\":\"m3\"}]";
            string txt = "https://www.moddb.com/addons/start/1\t0\t - Auth\tAlpha\tu1\tz1.7z\tm1\r\n"
                + "https://www.moddb.com/addons/start/2\t0\t - Auth2\tBeta\tu2\tz2.7z\tm2NEW\r\n"
                + "https://www.moddb.com/addons/start/3\tx\\y\t - Auth3\tGamma\tu3\tz3.7z\tm3\r\n";
            Dictionary<string, string> oldC = new Dictionary<string, string>(), newC = new Dictionary<string, string>();
            oldC["Stalker_GAMMA"] = A; newC["Stalker_GAMMA"] = B;
            foreach (string r in new string[] { "gamma_large_files_v2", "teivaz_anomaly_gunslinger", "gamma_setup" }) { oldC[r] = C; newC[r] = C; }
            string oldT = TreeJson(new string[][] {
                new string[] { "G.A.M.M.A/modpack_addons/Gamma_Mod/gamedata/g.ltx", "1" }, new string[] { "G.A.M.M.A/modpack_addons/Gamma_Mod/gamedata/del.ltx", "5" },
                new string[] { "G.A.M.M.A/modpack_addons/Other/gamedata/o.ltx", "9" }, new string[] { "G.A.M.M.A/modpack_patches/gamedata/p.ltx", "2" },
                new string[] { "G.A.M.M.A_definition_version.txt", "6" }, new string[] { "G.A.M.M.A/modpack_addons", "t", "tree" } });
            string newT = TreeJson(new string[][] {
                new string[] { "G.A.M.M.A/modpack_addons/Gamma_Mod/gamedata/g.ltx", "3" },
                new string[] { "G.A.M.M.A/modpack_addons/Other/gamedata/o.ltx", "9" }, new string[] { "G.A.M.M.A/modpack_patches/gamedata/p.ltx", "4" },
                new string[] { "G.A.M.M.A/modpack_patches/gamedata/q.ltx", "8" }, new string[] { "G.A.M.M.A_definition_version.txt", "7" }, new string[] { "G.A.M.M.A/modpack_addons", "t", "tree" } });
            Func<string, string> fetch = delegate (string url)
            {
                if (url.Contains("/Stalker_GAMMA/git/trees/" + A)) return oldT;
                if (url.Contains("/Stalker_GAMMA/git/trees/" + B)) return newT;
                throw new Exception("stub: не ожидали " + url);
            };
            UpdatePlan plan;
            try { plan = UpdateGuard.Plan(g, json, txt, oldC, newC, fetch, null); }
            catch (Exception e) { res[0] = TFail("план: " + e.Message); return res; }
            string[] wantDirs = { @"GAMMA\mods\2- Beta - Auth2", @"GAMMA\mods\Gamma_Mod", @"GAMMA\profiles\G.A.M.M.A" };
            string[] wantFiles = { @"ANOMALY\gamedata\p.ltx", @"GAMMA\version.txt", @"GAMMA\ModOrganizer.ini", @"cache\Stalker_GAMMA.applied-commit" };
            foreach (string w in wantDirs) if (!plan.Dirs.Contains(w)) { res[0] = TFail("в плане нет папки " + w); return res; }
            foreach (string w in wantFiles) if (!plan.Files.Contains(w)) { res[0] = TFail("в плане нет файла " + w); return res; }
            if (plan.Dirs.Count != 3 || plan.NewDirs.Count != 0) { res[0] = TFail("в плане лишние папки: " + string.Join("|", plan.Dirs.ToArray()) + " / " + string.Join("|", plan.NewDirs.ToArray())); return res; }
            if (!plan.NewFiles.Contains(@"ANOMALY\gamedata\q.ltx")) { res[0] = TFail("новый патч q.ltx не в плане"); return res; }
            // no way to ask GitHub: the update must not start
            try { UpdateGuard.Plan(g, json, txt, oldC, newC, delegate (string url) { throw new Exception("нет сети"); }, null); res[0] = TFail("план без сети не отказал"); return res; }
            catch (Exception e) { if (!e.Message.Contains("не удалось узнать")) { res[0] = TFail("не то сообщение: " + e.Message); return res; } }
            res[0] = "OK";

            Dictionary<string, string> sig = TreeSig(g);
            Action<string> mutate = delegate (string d)
            {
                WF(d, @"GAMMA\mods\2- Beta - Auth2\gamedata\f.ltx", "NEW beta\r\n");
                File.Delete(Path.Combine(d, @"GAMMA\mods\Gamma_Mod\gamedata\g.ltx"));
                WF(d, @"GAMMA\mods\Gamma_Mod\gamedata\fresh.ltx", "fresh\r\n");
                WF(d, @"ANOMALY\gamedata\p.ltx", "new patch\r\n");
                WF(d, @"ANOMALY\gamedata\q.ltx", "new q\r\n");
                WF(d, @"cache\Stalker_GAMMA.applied-commit", B);
                File.Delete(Path.Combine(d, @"GAMMA\profiles\G.A.M.M.A\settings.ini"));
                WF(d, @"GAMMA\resume.txt", "{}");
                WF(d, @"GAMMA\version.txt", "921");
            };
            // 1) the run is killed halfway (no commit, no rollback) -> the next start offers to restore
            UpdateJournal j = UpdateGuard.Begin(g, plan, "bid1", @"C:\x\GAMMA_Coop_build_x.zip", null, null, null);
            if (j == null || !j.Copied || !UpdateGuard.Pending(g)) { res[1] = TFail("резерв не создан"); return res; }
            mutate(g);
            simPendRoot = g; simPendReal = true; syncJobs = true; screen = 3; pg = null; job = null;
            bool offered = CheckPendingUpdate();
            string title = pg != null ? pg.Title : "";
            if (selfEmit != null) selfEmit("selftest: unfinished update offered at start");
            bool hasR = pg != null && pg.Hot.ContainsKey(ConsoleKey.R) && pg.Hot.ContainsKey(ConsoleKey.U) == false;   // the build file is not there: no resume
            if (!offered || title != "ОБНОВЛЕНИЕ НЕ ЗАКОНЧЕНО" || !hasR) { res[2] = TFail("предложение вернуть моды: " + offered + " / " + title); simPendRoot = null; simPendReal = false; syncJobs = false; return res; }
            HubKey(Key(ConsoleKey.R, 'r'));
            if (selfEmit != null) selfEmit("selftest: mods put back");
            simPendRoot = null; simPendReal = false; syncJobs = false; pendRoot = null;
            res[2] = UpdateGuard.Pending(g) ? TFail("после [R] журнал всё ещё running") : (pg != null && pg.Title == "ГОТОВО" ? "OK" : TFail("нет страницы ГОТОВО: " + (pg != null ? pg.Title : "null")));
            string d1 = SameSig(sig, TreeSig(g));
            res[1] = d1 == null && UpdateGuard.Load(g).State == "rolledback" ? "OK" : TFail(d1 ?? "состояние " + UpdateGuard.Load(g).State);
            // 2) a failure / Esc while the app is alive: Rollback directly
            j = UpdateGuard.Begin(g, plan, "bid2", "", null, null, null);
            mutate(g);
            int back = UpdateGuard.Rollback(g, j, null, null);
            string d2 = SameSig(sig, TreeSig(g));
            if (d2 != null || back < 3 || Directory.Exists(Path.Combine(g, j.Backup))) res[1] = TFail("откат: " + (d2 ?? "возвращено " + back));
            // 3) success: the copy goes away, the journal stays as "done"
            j = UpdateGuard.Begin(g, plan, "bid3", "", null, null, null);
            UpdateGuard.Commit(g, j, null);
            res[3] = !UpdateGuard.Pending(g) && UpdateGuard.Load(g).State == "done" && !Directory.Exists(Path.Combine(g, j.Backup)) ? "OK" : TFail("после Commit");
            // 4) not enough room for the copy: refuse, change nothing
            UpdatePlan huge = new UpdatePlan();
            huge.Bytes = long.MaxValue / 4;
            try { UpdateGuard.Begin(g, huge, "bid4", "", null, null, null); res[4] = TFail("нет отказа при нехватке места"); }
            catch (Exception e) { res[4] = e.Message.Contains("Не хватает места") && !UpdateGuard.Pending(g) ? "OK" : TFail(e.Message); }
            return res;
        }

        Action<string> selfEmit;   // draws a frame of the current screen into frames_install.txt

        string RunSelfTests(Action<string> emit)
        {
            selfEmit = emit;
            string tmp = Path.Combine(Path.GetTempPath(), "gc_selftest_" + Guid.NewGuid().ToString("N"));
            string integ, repair;
            string[] guard;
            try
            {
                Directory.CreateDirectory(tmp);
                try { integ = TestIntegrity(tmp); } catch (Exception e) { integ = TFail("исключение " + e); }
                try { repair = TestRepair(tmp); } catch (Exception e) { repair = TFail("исключение " + e); }
                try { guard = TestGuard(tmp); } catch (Exception e) { guard = new string[] { TFail("исключение " + e), "", "", "", "" }; }
            }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
            return "\"selfTests\":{\"integrity\":\"" + Esc(integ) + "\",\"repairZip\":\"" + Esc(repair) + "\",\"updatePlan\":\"" + Esc(guard[0]) + "\",\"updateRestore\":\"" + Esc(guard[1])
                + "\",\"journalOffer\":\"" + Esc(guard[2]) + "\",\"updateCommit\":\"" + Esc(guard[3]) + "\",\"spaceRefusal\":\"" + Esc(guard[4]) + "\"}";
        }
    }
}
