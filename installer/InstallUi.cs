using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GammaCoop
{
    // step 2 screens: consent, progress, overlays, and the simulated headless run
    public partial class App
    {
        string appDir;
        string installLog;
        ProgressFile prog;
        bool installSep;
        bool simulate;
        int screen = 3;            // 0 start (install), 1 consent, 2 install/progress, 3 hub (menu, save / load build)
        bool consent = false;
        bool showLog = false, confirmStop = false;
        Engine eng;
        Passport pass;
        int savedSh, savedOy;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------ helpers
        void OvBegin() { savedSh = sh; savedOy = oy; oy += sh / 2; sh = 0; }
        void OvEnd() { sh = savedSh; oy = savedOy; }

        static List<string> Wrap(string s, int w)
        {
            List<string> r = new List<string>();
            string cur = "";
            foreach (string word in s.Split(' '))
            {
                if (cur.Length > 0 && cur.Length + 1 + word.Length > w) { r.Add(cur); cur = word; }
                else cur = cur.Length == 0 ? word : cur + " " + word;
            }
            if (cur.Length > 0) r.Add(cur);
            return r;
        }

        static string Mb(double bytes) { return (bytes / 1048576.0).ToString("F1", Inv); }
        static string Gb(double bytes) { return (bytes / 1073741824.0).ToString("F1", Inv); }

        static string EtaText(double sec)
        {
            if (sec < 60) return "меньше минуты";
            int m = (int)Math.Ceiling(sec / 60);
            if (m < 60) return m + " мин";
            return (m / 60) + " ч " + (m % 60) + " мин";
        }

        static string OpRu(string op)
        {
            switch (op)
            {
                case "Download": return "Загрузка";
                case "Extract": return "Распаковка";
                case "Expand": return "Распаковка";
                case "CheckMd5": return "Проверка";
                case "Skipped": return "Пропуск";
            }
            return op;
        }

        void Bar(int x, int y, int w, double f, int c)
        {
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            int n = (int)Math.Round(f * w);
            Put(x, y, new string('█', n), c);
            Put(x + n, y, new string('░', w - n), CDim);
        }

        // centred dialog on top of everything (ignores the banner shift)
        void Dialog(string title, List<string> lines, List<int> cols, string keys, int w, bool center)
        {
            OvBegin();
            int h = lines.Count + 4 + (keys != null ? 2 : 0);
            int y1 = Math.Max(0, 22 - h), y2 = y1 + h - 1;   // sits over the banner, bottom edge just above the panels
            int x1 = (W - w) / 2, x2 = x1 + w - 1;
            Fill(x1, y1, x2, y2);
            Box(x1, y1, x2, y2, title);
            int y = y1 + 2;
            for (int i = 0; i < lines.Count; i++)
            {
                string s = Trunc(lines[i], w - 6);
                Put(center ? x1 + (w - s.Length) / 2 : x1 + 3, y, s, cols[i]);
                y++;
            }
            if (keys != null) Markup(x1 + (w - keys.Length) / 2, y + 1, keys, CMuted);
            OvEnd();
        }

        static void Ln(List<string> l, List<int> c, string s, int col) { l.Add(s); c.Add(col); }

        // ------------------------------------------------------------ consent
        void DrawConsent()
        {
            List<string> l = new List<string>(); List<int> c = new List<int>();
            foreach (string s in Wrap("Установщик добавит папки Anomaly, GAMMA и cache в исключения Windows Defender — так требует гайд GAMMA, иначе антивирус ломает файлы. Защиту он не выключает.", 80)) Ln(l, c, s, CText);
            Ln(l, c, "", CText);
            Ln(l, c, "Включит поддержку длинных путей Windows.", CText);
            Ln(l, c, "", CText);
            foreach (string s in Wrap("Убедись, что установщик скачан с официального источника: " + Official.Where + ". " + Official.NotLiable + " Файлы сборки бери только от тех, кому доверяешь.", 80)) Ln(l, c, s, CWarn);
            Dialog("СОГЛАСИЕ", l, c, "[Enter] Согласен и начать   [Esc] Назад", 88, false);
        }

        // ------------------------------------------------------------ progress screen
        void DrawInstall(double t)
        {
            eng.Tick();
            double now = eng.Now();
            Phase ph;
            char spin = Spin[(int)(t * 12) % Spin.Length];
            lock (eng.L)
            {
                ph = eng.Phase;
                string head; int hc;
                if (ph == Phase.Paused) { head = "ПАУЗА — [P] продолжить"; hc = CWarn; }
                else if (ph == Phase.RateLimit) { head = "ждём ModDB… продолжу сам"; hc = CWarn; }
                else if (ph == Phase.Failed) { head = "остановлено из-за ошибки"; hc = CBad; }
                else if (ph == Phase.Done) { head = "готово"; hc = COk; }
                else { head = "идёт установка " + spin; hc = CMuted; }
                Markup((W - head.Length) / 2, 21, head, hc);

                Box(3, 22, 96, 33, "УСТАНОВКА · " + pass.Name + " · " + pass.Channel);

                // stages
                for (int i = 0; i < Engine.StageNames.Length; i++)
                {
                    string st = eng.StageState(i, now);
                    string icon; int ic, tc;
                    if (st == "done") { icon = "✓"; ic = COk; tc = CText; }
                    else if (st == "warn") { icon = "!"; ic = CWarn; tc = CWarn; }
                    else if (st == "skip") { icon = "–"; ic = CDim; tc = CDim; }
                    else if (st == "active")
                    {
                        if (ph == Phase.Running) { icon = spin.ToString(); ic = Tone[5]; }
                        else { icon = "▸"; ic = CWarn; }
                        tc = Tone[7];
                    }
                    else { icon = "·"; ic = CDim; tc = CMuted; }
                    string label = Engine.StageNames[i];
                    if (i == 4 && eng.StTot[4] > 0) label = "Моды (" + eng.StCmp[4] + " / " + eng.StTot[4] + ")";
                    Put(5, 23 + i, icon, ic);
                    Put(7, 23 + i, Trunc(label, 23), tc);
                }

                // active items
                List<Item> list = new List<Item>(eng.Items.Values);
                list.Sort(delegate (Item a, Item b) { return a.Id.CompareTo(b.Id); });
                for (int k = 0; k < list.Count && k < 4; k++)
                {
                    Item it = list[k];
                    int y = 23 + k;
                    Put(32, y, Trunc(it.Name, 20), CText);
                    Put(53, y, OpRu(it.Op), CMuted);
                    if (it.Pct >= 0) Bar(64, y, 12, it.Pct, Tone[4]);
                    else
                    {
                        Put(64, y, new string('░', 12), CDim);
                        Put(64 + (int)(t * 10) % 10, y, "███", Tone[4]);
                    }
                    string pc = it.Pct >= 0 ? ((int)(it.Pct * 100)).ToString(Inv) + "%" : Mb(it.Bytes) + " МБ";
                    Put(77, y, pc, CText);
                    if (it.Speed > 0 && it.Op == "Download") Put(84, y, Mb(it.Speed) + " МБ/с", CMuted);
                }
                if (list.Count > 4) Put(32, 27, "+ ещё " + (list.Count - 4) + " в работе", CDim);

                // totals
                double ov = eng.Overall();
                Put(32, 29, "Всего", CMuted);
                Bar(39, 29, 40, ov, Tone[5]);
                string ovt = ((int)(ov * 100)).ToString(Inv) + "%";
                int sc = eng.StTot[4] > 0 ? 4 : (eng.StTot[3] > 0 ? 3 : 2);
                for (int j = 4; j >= 2; j--) if (eng.StAny[j]) { sc = j; break; }
                if (eng.StTot[sc] > 0) ovt += "  ·  " + eng.StCmp[sc] + " / " + eng.StTot[sc];
                Put(80, 29, Trunc(ovt, 16), CText);

                string tl = "скачано " + Gb(eng.DlBytes) + " ГБ";
                if (ph == Phase.Running)
                {
                    tl += "    скорость " + Mb(eng.Speed) + " МБ/с    ";
                    if (eng.DlStart < 0 || now - eng.DlStart < 20 || eng.Speed < 1048576.0) tl += "осталось: считаю…";
                    else
                    {
                        double rem = Math.Max(0, Engine.ExpectedBytes - eng.CacheBase - eng.DlBytes);
                        tl += "~осталось " + EtaText(rem / eng.Speed);
                    }
                }
                Put(32, 30, Trunc(tl, 64), CText);
                Put(32, 31, "сеть: напрямую · сон ПК запрещён", CDim);
                Put(32, 32, Trunc("лог: " + Path.GetFileName(logPath), 64), CDim);
            }

            // hotkeys
            string hk;
            if (ph == Phase.Paused) hk = "[P] Продолжить   [L] Последние строки лога   [Esc] Остановить";
            else if (ph == Phase.RateLimit) hk = "[Enter] Попробовать сейчас   [L] Лог   [Esc] Остановить";
            else if (ph == Phase.Failed) hk = "[Enter] Повторить с места   [L] Лог   [Esc] Выйти";
            else if (ph == Phase.Done) hk = "[Enter] Запустить игру   [I] Инструкция   [Esc] Выход";
            else hk = "[P] Пауза   [L] Последние строки лога   [Esc] Остановить (продолжит с места)";
            Markup((W - hk.Length) / 2, 34, hk, CMuted);

            if (ph == Phase.RateLimit) DrawHourglass(t, now);
            else if (ph == Phase.Failed) DrawFailed();
            else if (ph == Phase.Done) DrawDone();
            if (confirmStop)
            {
                List<string> l = new List<string>(); List<int> c = new List<int>();
                Ln(l, c, "Остановить? Потом можно продолжить с места.", CText);
                Dialog("ОСТАНОВИТЬ", l, c, "[Enter] Да   [Esc] Нет", 56, true);
            }
            if (showLog) DrawLog();
        }

        void DrawHourglass(double t, double now)
        {
            double rem, frac;
            lock (eng.L) { rem = Math.Max(0, eng.WaitUntil - now); }
            frac = 1 - rem / (65 * 60);
            int s = (int)Math.Ceiling(rem);
            string cd = (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
            bool drip = ((int)(t * 4)) % 2 == 0;
            List<string> l = new List<string>(); List<int> c = new List<int>();
            Ln(l, c, "╒═══════╕", CMuted);
            Ln(l, c, frac < 0.7 ? " ╲ ▓▓▓ ╱ " : " ╲     ╱ ", Tone[5]);
            Ln(l, c, frac < 0.9 ? "  ╲ ▓ ╱  " : "  ╲   ╱  ", Tone[5]);
            Ln(l, c, drip ? "   ╲·╱   " : "   ╲ ╱   ", CMuted);
            Ln(l, c, "   ╱ ╲   ", CMuted);
            Ln(l, c, frac > 0.1 ? "  ╱ ░ ╲  " : "  ╱   ╲  ", Tone[3]);
            Ln(l, c, frac > 0.3 ? " ╱ ░░░ ╲ " : " ╱     ╲ ", Tone[3]);
            Ln(l, c, "╘═══════╛", CMuted);
            Ln(l, c, "", CText);
            Ln(l, c, cd, Tone[7]);
            Ln(l, c, "", CText);
            Ln(l, c, "ModDB ограничил скорость скачивания. Это нормально —", CText);
            Ln(l, c, "потом продолжу сам.", CText);
            Dialog("ModDB попросил подождать", l, c, "[Enter] Попробовать сейчас   [Esc] Остановить", 62, true);
        }

        void DrawFailed()
        {
            string err;
            lock (eng.L) { err = eng.Error; }
            List<string> l = new List<string>(); List<int> c = new List<int>();
            Ln(l, c, "Установка остановилась с ошибкой:", CBad);
            Ln(l, c, "", CText);
            int n = 0;
            foreach (string raw in err.Replace("\r", "").Split('\n'))
            {
                if (raw.Trim().Length == 0) continue;
                foreach (string w in Wrap(raw.Trim(), 80)) { if (n >= 3) break; Ln(l, c, w, CText); n++; }
                if (n >= 3) break;
            }
            Dialog("ОШИБКА", l, c, "[Enter] Повторить с места   [L] Лог   [Esc] Выйти", 88, false);
        }

        void DrawDone()
        {
            double gbDl, freed, mins;
            string note;
            bool reboot, rep;
            lock (eng.L) { gbDl = eng.DlBytes / 1073741824.0; freed = eng.FreedGb; mins = (eng.FinishedAt - eng.StartedAt) / 60.0; note = eng.Note; reboot = eng.NeedReboot; rep = eng.Repair; }
            List<string> l = new List<string>(); List<int> c = new List<int>();
            Ln(l, c, "✓", COk);
            Ln(l, c, eng.Load ? "Готово. Сборка применена." : (rep ? "Готово. Кооп починен." : "Готово. " + pass.Name + " стоит в " + root + "."), Tone[7]);
            Ln(l, c, "", CText);
            Ln(l, c, "На рабочем столе есть ярлыки «GAMMA Coop», «GAMMA Coop — сборка» и инструкция.", CText);
            Ln(l, c, "", CText);
            if (!rep)
            {
                string st = "скачано " + gbDl.ToString("F1", Inv) + " ГБ за " + ((int)Math.Round(mins)) + " мин";
                if (freed > 0) st += " · архив Anomaly удалён, свободно +" + freed.ToString("F1", Inv) + " ГБ";
                Ln(l, c, st, CMuted);
            }
            if (note.Length > 0) Ln(l, c, note, CWarn);
            if (reboot) Ln(l, c, "Нужна перезагрузка Windows — сделай её перед первым запуском игры.", CWarn);
            Dialog("ГОТОВО", l, c, "[Enter] Запустить игру   [I] Инструкция   [любая другая клавиша] Выход", 90, true);
        }

        void DrawLog()
        {
            List<string> l = new List<string>(); List<int> c = new List<int>();
            string[] r = eng.Recent(15);
            if (r.Length == 0) Ln(l, c, "(пока пусто)", CMuted);
            foreach (string s in r) Ln(l, c, s, CText);
            Dialog("ЛОГ · последние строки", l, c, "[L / Esc] Закрыть", 96, false);
        }

        // ------------------------------------------------------------ flow
        void BeginInstall()
        {
            if (eng == null)
            {
                string blocked = null;
                prog = null;
                try
                {
                    pass = Passport.Load(Path.Combine(appDir, "passport"));
                    if (!simulate)
                    {
                        if (!File.Exists(pass.ModPackMaker) || !File.Exists(pass.ModList)) throw new Exception("нет списков модов в passport");
                        // the folder may have changed while the screen was open: check again right before touching the disk
                        RefreshFolder();
                        blocked = FolderBlocker(false);
                        if (blocked == null)
                        {
                            Directory.CreateDirectory(Path.Combine(root, "GAMMA Coop"));
                            prog = fstate == FolderState.Ours ? fprog : null;
                            if (prog == null) prog = ProgressFile.Create(root, Coop.BuildName, pass.Name);   // first thing, before any download
                            UseInstallLog();
                            WriteLastRoot();
                        }
                    }
                }
                catch (Exception e)
                {
                    screen = 0;
                    SetMsg("Не удалось начать: " + e.Message, CBad, 6);
                    Log("Не удалось начать установку: " + e);
                    return;
                }
                if (blocked != null)
                {
                    screen = 0;
                    SetMsg(blocked, CBad, 6);
                    Log("Установка не начата: " + blocked);
                    return;
                }
                eng = new Engine(root, pass, Log, simulate, appDir);
                eng.Prog = prog;
                Log("Установка: папка " + root + (simulate ? " (симуляция)" : ""));
            }
            screen = 2;
            eng.Start();
        }

        void UseInstallLog()
        {
            installLog = Path.Combine(root, @"GAMMA Coop\install.log");
            if (!installSep)
            {
                installSep = true;
                try { File.AppendAllText(installLog, "=== запуск " + startStamp + " ===" + Environment.NewLine, new UTF8Encoding(true)); }
                catch (Exception) { }
            }
        }

        void WriteLastRoot() { WriteLastRoot(root); }

        void WriteLastRoot(string r)
        {
            try { File.WriteAllText(Path.Combine(appDir, "last_root.txt"), r, new UTF8Encoding(false)); }
            catch (Exception e) { Log("last_root.txt не записан: " + e.Message); }
        }

        // pk: the build file to use (null = the newest one found)
        void BeginRepair(PackInfo pk)
        {
            string blocked = null;
            prog = null;
            try
            {
                pass = Passport.Load(Path.Combine(appDir, "passport"));
                if (!simulate)
                {
                    RefreshFolder();
                    blocked = FolderBlocker(true);
                    if (blocked == null)
                    {
                        Directory.CreateDirectory(Path.Combine(root, "GAMMA Coop"));
                        prog = fstate == FolderState.Ours ? fprog : null;   // an old install without progress.json gets none
                        UseInstallLog();
                    }
                }
            }
            catch (Exception e)
            {
                SetMsg("Не удалось начать: " + e.Message, CBad, 6);
                Log("Не удалось начать починку: " + e);
                return;
            }
            if (blocked != null)
            {
                SetMsg(blocked, CBad, 6);
                Log("Починка не начата: " + blocked);
                return;
            }
            eng = new Engine(root, pass, Log, simulate, appDir);
            eng.Prog = prog;
            eng.Repair = true;
            if (pk != null) eng.PackPath = pk.Path;
            for (int i = 0; i < 7; i++) eng.St[i] = "skip";
            eng.RepairVc = !File.Exists(Coop.InstalledJson(root));
            if (!eng.RepairVc) eng.St[8] = "skip";
            Log("Починка кооп: папка " + root + (simulate ? " (симуляция)" : ""));
            screen = 2;
            eng.Start();
        }

        // started from an admin process, so go through explorer to get an unelevated child
        void LaunchUnelevated(string path)
        {
            Log("Запуск без повышения прав: " + path);
            if (simulate) return;
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception e) { Log("Не удалось запустить " + path + ": " + e.Message); }
        }

        void DoStop()
        {
            eng.Stop();
            eng = null;
            pathRelaxed = false;
            screen = 0;
            RefreshFolder();
            SetMsg("Остановлено. Нажми Enter, чтобы продолжить с места", CWarn, 6);
        }

        void HandleInstKey(ConsoleKeyInfo k)
        {
            if (screen == 1)
            {
                if (k.Key == ConsoleKey.Enter) { consent = true; Log("Согласие на исключения Defender и длинные пути получено"); BeginInstall(); }
                else if (k.Key == ConsoleKey.Escape) screen = 0;
                return;
            }
            if (eng == null) { screen = 0; return; }
            if (showLog) { showLog = false; return; }
            if (confirmStop)
            {
                if (k.Key == ConsoleKey.Enter) { confirmStop = false; DoStop(); }
                else if (k.Key == ConsoleKey.Escape || k.Key == ConsoleKey.N) confirmStop = false;
                return;
            }
            Phase ph = eng.Phase;
            if (ph == Phase.Done)
            {
                if (k.Key == ConsoleKey.Enter) { LaunchUnelevated(Path.Combine(root, "GAMMA Coop.exe")); quit = true; }
                else if (k.Key == ConsoleKey.I) LaunchUnelevated(Path.Combine(root, "GAMMA Coop — как играть.html"));
                else quit = true;
                return;
            }
            if (k.Key == ConsoleKey.L) { showLog = true; return; }
            if (ph == Phase.Running)
            {
                if (k.Key == ConsoleKey.P) eng.Pause();
                else if (k.Key == ConsoleKey.Escape) confirmStop = true;
            }
            else if (ph == Phase.Paused)
            {
                if (k.Key == ConsoleKey.P) eng.Start();
                else if (k.Key == ConsoleKey.Escape) confirmStop = true;
            }
            else if (ph == Phase.RateLimit)
            {
                if (k.Key == ConsoleKey.Enter) eng.Start();
                else if (k.Key == ConsoleKey.Escape) confirmStop = true;
            }
            else if (ph == Phase.Failed)
            {
                if (k.Key == ConsoleKey.Enter) eng.Start();
                else if (k.Key == ConsoleKey.Escape) { Log("Выход после ошибки"); quit = true; }
            }
        }

        string StageSummary()
        {
            string[] s = new string[Engine.StageNames.Length];
            double now = eng.Now();
            for (int i = 0; i < s.Length; i++) s[i] = eng.StageState(i, now);
            return string.Join(",", s);
        }

        // ------------------------------------------------------------ headless simulation
        int RunHeadlessSim(int frames)
        {
            StreamWriter o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            foreach (Check c in checks)
            {
                if (c.Id == "moddb") continue;
                c.Status = "ok";
                if (c.Id == "admin") c.Text = "права администратора";
                else if (c.Id == "dpi") c.Text = "zapret / GoodbyeDPI / ByeDPI не запущены";
                else if (c.Id == "space") c.Text = "свободно 812 ГБ (нужно 150)";
                else if (c.Id == "disktype") c.Text = "диск SSD";
                else if (c.Id == "coop") c.Text = "пакет кооп от хоста · 26.11.2026";
                else if (c.Id == "github") c.Text = "GitHub: 21.4 МБ/с";
                else c.Text = "зеркало Anomaly: 18.9 МБ/с";
            }
            freeGb = 812; diskType = "SSD"; ghSpeed = 21.4; mirSpeed = 18.9;

            StringBuilder all = new StringBuilder();
            int n = 0;
            double totalMs = 0;
            int hourglass = 0, doneFrames = 0, maxActive = 0, consentFrames = 0, withSpeed = 0;
            Action<string> emit = delegate (string label)
            {
                double t = 2.5 + n / 24.0;
                Stopwatch sw = Stopwatch.StartNew();
                DrawScreen(t, W, H);
                ToAnsi(true);
                sw.Stop();
                totalMs += sw.Elapsed.TotalMilliseconds;
                all.Append("--- frame ").Append(n).Append(' ').Append(label).Append('\n');
                all.Append(ToPlain());
                n++;
            };
            ConsoleKeyInfo enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);

            screen = 0;                             // the main frames below are those of the install screen, not of the hub
            emit("start");
            HandleKey(enter);                       // all green -> consent
            emit("consent"); consentFrames++;
            emit("consent"); consentFrames++;
            HandleKey(enter);                       // consent -> install
            if (eng == null) { o.WriteLine("{\"sim\":false,\"error\":\"engine not started\"}"); o.Flush(); return 1; }
            double dtN = eng.SimDuration / Math.Max(10, frames - 30);
            while (n < frames)
            {
                Phase ph = eng.Phase;
                double dt = ph == Phase.RateLimit ? 500 : (ph == Phase.Done ? 1 : (n == 3 ? 0.5 : dtN));
                eng.Pump(dt);
                ph = eng.Phase;
                if (ph == Phase.RateLimit) hourglass++;
                if (ph == Phase.Done) doneFrames++;
                lock (eng.L)
                {
                    maxActive = Math.Max(maxActive, eng.Items.Count);
                    if (eng.Speed > 0) withSpeed++;
                }
                emit(ph.ToString());
            }
            string summary;
            lock (eng.L)
            {
                summary = "\"phase\":\"" + eng.Phase + "\",\"stages\":\"" + StageSummary() + "\",\"mods\":\"" + eng.StCmp[4] + "/" + eng.StTot[4]
                    + "\",\"downloadedGb\":" + (eng.DlBytes / 1073741824.0).ToString("F2", Inv) + ",\"freedGb\":" + eng.FreedGb.ToString("F2", Inv)
                    + ",\"fullOk\":" + (eng.FullOk ? "true" : "false");
            }
            int mainFrames = n;

            // extra states on a fresh simulated run: paused, confirm stop, log, failure
            eng.Stop();
            eng = null; screen = 0;
            BeginInstall();
            for (int i = 0; i < 40; i++) eng.Pump(5);
            emit("extra: running");
            eng.Pause(); emit("extra: PAUSE");
            eng.Start(); eng.Pump(1);
            confirmStop = true; emit("extra: confirm stop"); confirmStop = false;
            showLog = true; emit("extra: log overlay"); showLog = false;
            eng.ForceFail("Не удалось скачать файл: сервер ответил 503 (Service Unavailable).\nМожно повторить — установка продолжится с того же места.");
            emit("extra: FAILED");

            // repair run
            eng.Stop();
            eng = null; screen = 0;
            BeginRepair(null);
            for (int i = 0; i < 60 && eng.Phase != Phase.Done; i++) { eng.Pump(1); if (i == 3) emit("extra: repair running"); }
            emit("extra: repair " + eng.Phase);
            summary += ",\"repair\":\"" + eng.Phase + "\",\"repairStages\":\"" + StageSummary() + "\"";

            // the hub and the simulated [3] save / [2] load flows (no disk writes)
            double simSecs = eng.SimDuration;
            eng = null; screen = 3;
            string hubRes = RunHubSim(emit);
            summary += "," + hubRes;

            File.WriteAllText(Path.Combine(Path.GetDirectoryName(logPath), "frames_install.txt"), all.ToString(), new UTF8Encoding(true));
            o.WriteLine("{\"sim\":true,\"frames\":" + n + ",\"mainFrames\":" + mainFrames + ",\"simSeconds\":" + simSecs.ToString("F0", Inv) + "," + summary
                + ",\"hourglassFrames\":" + hourglass + ",\"doneFrames\":" + doneFrames + ",\"consentFrames\":" + consentFrames
                + ",\"maxActiveItems\":" + maxActive + ",\"framesWithSpeed\":" + withSpeed + ",\"avg_ms\":" + (totalMs / n).ToString("F3", Inv) + "}");
            o.Flush();
            eng = null;
            return 0;
        }
    }
}
