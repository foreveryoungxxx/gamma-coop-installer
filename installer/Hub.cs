using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GammaCoop
{
    // background work with a progress line (searching, verifying, building); Esc cancels
    class Job
    {
        public volatile string Label = "";
        public double Pct = -1;
        public volatile bool Done, Cancel;
        public string Err;
        public object Res;
    }

    // one screen of the hub: text lines, an optional list, hotkeys
    class Page
    {
        public string Title = "";
        public List<string> Lines = new List<string>();
        public List<int> Cols = new List<int>();
        public List<string> Items;                 // optional list under the lines
        public int Sel, Scroll;
        public Func<int, List<string>> Detail;     // lines under the list for the selected item
        public string Keys = "";
        public Action<int> OnEnter;                // Enter: index of the selected item (-1 without a list)
        public Action OnEsc;                       // Esc: default is back to the menu
        public Func<string, bool> OnPath;          // [P] or Ctrl+V: the user typed / pasted a path; true = accepted
        public string PathTitle = "ПУТЬ", PathHint = "";
        public readonly Dictionary<ConsoleKey, Action> Hot = new Dictionary<ConsoleKey, Action>();
        public Action Poll;                        // called every 2 seconds while the page is open
        public double NextPoll;
    }

    // the start menu: install / load a build / save a build / repair, and the flows behind them
    public partial class App
    {
        static readonly string[] HubItems = {
            "Установить GAMMA с нуля",
            "Загрузить сборку — обновить игру как у друга/хоста",
            "Сохранить сборку — сделать файл для друзей",
            "Починить кооп",
            "Проверить целостность" };
        static readonly string[] HubDesc = {
            "новая установка Anomaly, GAMMA и кооп в пустую папку",
            "найду файл сборки сам (Загрузки, LocalSend, рабочий стол) и покажу, что изменится",
            "файл с твоими модами для друзей · [H] — моды для починки по отчёту друга",
            "поставить кооп заново из последней сборки (или применить файл починки от хоста)",
            "сверю моды со списком файлов хоста: что пропало или изменилось, и что делать" };

        int hubSel;
        Page pg;                       // current page; null = the menu
        Job job;
        Action<Job> jobDone;
        string jobTitle = "";
        bool syncJobs;                 // headless run: jobs execute inline
        bool pathRelaxed;              // the folder is a game that is already installed: the strict "latin, no spaces" rule does not apply
        bool hEdit;
        string hEditTitle = "", hEditHint = "", hEditLabel = "Путь: ";
        Func<string, bool> hEditOk;
        string hRoot, loadFile, loadRoot;
        PackInfo hPack;
        PackPlan hPlan;
        bool hPreset;                  // the build file came from the launcher: "back" goes to the menu
        bool hTakeKeys;                // [B] on the diff screen: also take the sender's key bindings
        bool simSame, simGammaDiff, simNoList;    // simulation switches for the diff screen (simNoList: no CLI list of installed mods -> no [U])

        const string SimRoot = @"D:\GAMES\STALKER_GAMMA";

        // ------------------------------------------------------------ jobs
        void RunJob(string title, Func<Job, object> work, Action<Job> done)
        {
            Job j = new Job();
            job = j; jobTitle = title; jobDone = done; pg = null;
            ThreadStart body = delegate ()
            {
                try { j.Res = work(j); }
                catch (Exception e) { j.Err = e.Message; Log("Задача «" + title + "»: " + e); }
                j.Done = true;
            };
            if (syncJobs) { body(); PumpJob(); return; }
            Thread th = new Thread(body);
            th.IsBackground = true;
            th.Start();
        }

        void PumpJob()
        {
            if (job == null || !job.Done) return;
            Job j = job;
            Action<Job> d = jobDone;
            job = null; jobDone = null;
            if (d != null) d(j);
        }

        void PumpHub()
        {
            PumpJob();
            Page p = pg;
            if (p != null && p.Poll != null)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now >= p.NextPoll) { p.NextPoll = now + 2; p.Poll(); }
            }
        }

        void HubInit()
        {
            hubSel = 0;
            if (!simulate && !Resumable() && ((fstate == FolderState.Ours && fprog != null && fprog.Finished) || fstate == FolderState.ExistingGamma)) hubSel = 1;
        }

        void HubHome()
        {
            if (job != null) job.Cancel = true;
            job = null; jobDone = null; pg = null; hEdit = false; pathRelaxed = false;
            screen = 3;
            if (pendRoot != null) { try { if (!UpdateGuard.Pending(pendRoot)) pendRoot = null; } catch (Exception) { pendRoot = null; } }
        }

        // ------------------------------------------------------------ page helpers
        static Page NewPage(string title) { Page p = new Page(); p.Title = title; return p; }
        static void L(Page p, string text, int c) { p.Lines.Add(text); p.Cols.Add(c); }
        static void LW(Page p, string text, int c) { foreach (string s in Wrap(text, 88)) L(p, s, c); }

        void InfoPage(string title, int c, params string[] lines)
        {
            Page p = NewPage(title);
            foreach (string s in lines) { if (s.Length == 0) L(p, "", CText); else LW(p, s, c); }
            p.Keys = "[Enter] В меню";
            p.OnEnter = delegate (int i) { HubHome(); };
            pg = p;
        }

        void ErrorPage(string title, string text)
        {
            Page p = NewPage(title);
            foreach (string raw in text.Replace("\r", "").Split('\n')) { if (raw.Trim().Length > 0) LW(p, raw.Trim(), CBad); }
            L(p, "", CText);
            L(p, "Ничего не изменено.", CText);
            p.Keys = "[Enter] В меню";
            p.OnEnter = delegate (int i) { HubHome(); };
            pg = p;
        }

        // ------------------------------------------------------------ keys
        void HubKey(ConsoleKeyInfo k)
        {
            if (job != null)
            {
                if (k.Key == ConsoleKey.Escape) { Log("Задача отменена"); HubHome(); }
                return;
            }
            if (hEdit) { HubEditKey(k); return; }
            if (pg != null) { PageKey(k); return; }
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: hubSel = (hubSel + HubItems.Length - 1) % HubItems.Length; break;
                case ConsoleKey.DownArrow: hubSel = (hubSel + 1) % HubItems.Length; break;
                case ConsoleKey.D1: case ConsoleKey.NumPad1: HubEnter(0); break;
                case ConsoleKey.D2: case ConsoleKey.NumPad2: HubEnter(1); break;
                case ConsoleKey.D3: case ConsoleKey.NumPad3: HubEnter(2); break;
                case ConsoleKey.D4: case ConsoleKey.NumPad4: HubEnter(3); break;
                case ConsoleKey.D5: case ConsoleKey.NumPad5: HubEnter(4); break;
                case ConsoleKey.H: StartRepairHelper(); break;
                case ConsoleKey.R: if (pendRoot != null) ShowPending(pendRoot); break;
                case ConsoleKey.Enter: HubEnter(hubSel); break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape: Log("Выход (клавиша)"); quit = true; break;
            }
        }

        void PageKey(ConsoleKeyInfo k)
        {
            Page p = pg;
            if (IsPaste(k) && p.OnPath != null)
            {
                string pt = PasteText();
                if (pt.Length == 0) SetMsg("Буфер обмена пуст или недоступен", CWarn, 3);
                else OpenEdit(p.PathTitle, p.PathHint, pt, p.OnPath);
                return;
            }
            if (p.Items != null && p.Items.Count > 0)
            {
                if (k.Key == ConsoleKey.UpArrow && p.Sel > 0) { p.Sel--; return; }
                if (k.Key == ConsoleKey.DownArrow && p.Sel < p.Items.Count - 1) { p.Sel++; return; }
            }
            if (k.Key == ConsoleKey.Enter) { if (p.OnEnter != null) p.OnEnter(p.Items != null ? p.Sel : -1); return; }
            if (k.Key == ConsoleKey.Escape) { if (p.OnEsc != null) p.OnEsc(); else HubHome(); return; }
            if (k.Key == ConsoleKey.P && p.OnPath != null) { OpenEdit(p.PathTitle, p.PathHint, "", p.OnPath); return; }
            Action a;
            if (p.Hot.TryGetValue(k.Key, out a)) a();
        }

        void OpenEdit(string title, string hint, string text, Func<string, bool> ok) { OpenEdit(title, hint, text, ok, "Путь: "); }

        void OpenEdit(string title, string hint, string text, Func<string, bool> ok, string label)
        {
            hEditLabel = label;
            hEdit = true; hEditTitle = title; hEditHint = hint; hEditOk = ok;
            ed.Open(text); lastCharT = -1; editScroll = 0;
        }

        void HubEditKey(ConsoleKeyInfo k)
        {
            if (IsPaste(k))
            {
                string pt = PasteText();
                if (pt.Length == 0) SetMsg("Буфер обмена пуст или недоступен", CWarn, 3);
                else { ed.Replace(pt); lastCharT = -1; }
                return;
            }
            if (k.Key == ConsoleKey.Escape) { hEdit = false; return; }
            if (k.Key == ConsoleKey.Enter)
            {
                if (clock.Elapsed.TotalSeconds - lastCharT < 0.05) return;   // a line break inside a pasted burst
                string p = ed.Text.Trim().Trim('"', '\'').Trim().TrimEnd('\\');
                Func<string, bool> ok = hEditOk;
                hEdit = false;
                if (ok != null && !ok(p)) hEdit = true;
                return;
            }
            bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
            bool shift = (k.Modifiers & ConsoleModifiers.Shift) != 0;
            if (ctrl && k.Key == ConsoleKey.A) ed.SelectAll();
            else if (k.KeyChar == (char)127 || k.KeyChar == (char)23) ed.WordBackspace();
            else if (k.Key == ConsoleKey.Backspace) { if (ctrl) ed.WordBackspace(); else ed.Backspace(); }
            else if (k.Key == ConsoleKey.Delete) ed.Delete();
            else if (k.Key == ConsoleKey.LeftArrow) ed.Left(shift, ctrl);
            else if (k.Key == ConsoleKey.RightArrow) ed.Right(shift, ctrl);
            else if (k.Key == ConsoleKey.Home) ed.MoveTo(0, shift);
            else if (k.Key == ConsoleKey.End) ed.MoveTo(ed.Text.Length, shift);
            else if (k.KeyChar == '"') { lastCharT = clock.Elapsed.TotalSeconds; }
            else if (k.KeyChar >= ' ' && k.KeyChar != (char)127) { ed.Insert(k.KeyChar); lastCharT = clock.Elapsed.TotalSeconds; }
        }

        // ------------------------------------------------------------ drawing
        void DrawHub(double t)
        {
            double now = clock.Elapsed.TotalSeconds;
            string rl = ResumeLine();
            if (pendRoot != null && pg == null && job == null) { string pw = "Обновление не закончено — [R] вернуть моды как были"; Markup((W - pw.Length) / 2, 21, pw, CBad); }
            else if (rl != null && pg == null && job == null) Markup((W - Math.Min(rl.Length, W - 2)) / 2, 21, Trunc(rl, W - 2), fprog.Finished ? COk : CWarn);
            string keys;
            if (job != null) { DrawJob(t); keys = "[Esc] Отмена"; }
            else if (pg != null) { DrawPage(pg); keys = pg.Keys; }
            else { DrawMenu(); keys = "[↑↓] Выбрать   [Enter] Открыть   [1-5] Сразу   [H] Сохранить моды для починки   [Esc/Q] Выход"; }
            if (hEdit) keys = "[Enter] Дальше   [Ctrl+V] Заменить путь   [Esc] Отмена";
            if (msg != null && now < msgUntil && !hEdit) PutC(34, msg, msgColor);
            else Markup((W - keys.Length) / 2, 34, keys, CMuted);
            PutC(35, "Ctrl + плюс / Ctrl + минус — крупнее/мельче", CDim);
            if (hEdit) DrawHubEdit(t, now);
        }

        void DrawMenu()
        {
            Box(3, 22, 96, 33, "ЧТО ДЕЛАЕМ");
            for (int i = 0; i < HubItems.Length; i++)
            {
                int y = 23 + i * 2;
                bool sel = i == hubSel;
                Put(5, y, sel ? "▸" : " ", Tone[5]);
                Markup(7, y, "[" + (i + 1) + "] " + HubItems[i], sel ? Tone[7] : CText);
                Put(11, y + 1, HubDesc[i], sel ? CMuted : CDim);
            }
            Put(5, 33, " " + Trunc("Оригинал: " + Official.Where + " · чужие копии — на свой риск", 86) + " ", CDim);   // in the bottom border: no room for a line
        }

        void DrawJob(double t)
        {
            Box(3, 22, 96, 28, "ЖДИ");
            Put(5, 24, Spin[(int)(t * 12) % Spin.Length].ToString(), Tone[5]);
            Put(7, 24, Trunc(jobTitle, 86), CText);
            Job j = job;
            double f = j.Pct;
            if (f >= 0) { Bar(7, 26, 70, f, Tone[5]); Put(79, 26, ((int)(f * 100)).ToString(Inv) + "%", CText); }
            else
            {
                Put(7, 26, new string('░', 70), CDim);
                Put(7 + (int)(t * 14) % 66, 26, "████", Tone[4]);
            }
            string lb = j.Label;
            if (lb != null && lb.Length > 0) Put(7, 27, Trunc(lb, 86), CMuted);
        }

        void DrawPage(Page p)
        {
            Box(3, 22, 96, 33, p.Title);
            int y = 23;
            for (int i = 0; i < p.Lines.Count && y <= 32; i++) Put(5, y++, Trunc(p.Lines[i], 90), p.Cols[i]);
            if (p.Items != null && p.Items.Count > 0)
            {
                int win = Math.Min(p.Items.Count, 5);
                if (p.Sel < p.Scroll) p.Scroll = p.Sel;
                if (p.Sel >= p.Scroll + win) p.Scroll = p.Sel - win + 1;
                for (int i = p.Scroll; i < p.Scroll + win && y <= 32; i++)
                {
                    bool sel = i == p.Sel;
                    Put(5, y, sel ? "▸" : " ", Tone[5]);
                    Put(7, y, Trunc(p.Items[i], 88), sel ? Tone[7] : CText);
                    y++;
                }
                if (p.Items.Count > win && y <= 32) { Put(7, y, "(" + (p.Sel + 1) + " из " + p.Items.Count + ")", CDim); y++; }
                if (p.Detail != null)
                {
                    y++;
                    foreach (string s in p.Detail(p.Sel)) { if (y > 32) break; Put(7, y++, Trunc(s, 88), CMuted); }
                }
            }
        }

        void DrawHubEdit(double t, double now)
        {
            string h1 = hEditHint;
            int bw = Math.Max(h1.Length, 66) + 6, x1 = (W - bw) / 2;
            Fill(x1, 14, x1 + bw, 20);
            Box(x1, 14, x1 + bw, 20, hEditTitle);
            Put(x1 + 3, 15, h1, CMuted);
            Put(x1 + 3, 16, "Ctrl+V — заменить путь · Ctrl+A — выделить всё · Esc — отмена", CMuted);
            if (msg != null && now < msgUntil) Put(x1 + 3, 19, Trunc(msg, bw - 6), msgColor);
            string pre = hEditLabel;
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

        // ------------------------------------------------------------ menu actions
        void HubEnter(int i)
        {
            hubSel = i;
            Log("Меню: пункт " + (i + 1));
            if (i == 0) { pathRelaxed = false; RefreshFolder(); screen = 0; }
            else if (i == 1) StartLoad(null, null);
            else if (i == 2) StartSave();
            else if (i == 3) StartRepairHub();
            else StartIntegrity();
        }

        // ------------------------------------------------------------ finding the game folder
        void PickRoot(string preset, Action<string> next)
        {
            if (preset != null && (simulate || Finder.IsGammaRoot(preset))) { next(preset.TrimEnd('\\')); return; }
            RunJob("Ищу папку с игрой (там, где лежат GAMMA и ANOMALY)…", delegate (Job j)
            {
                if (simulate) { SimWork(j, "проверяю диски", 600); return new List<string>(new string[] { SimRoot }); }
                return Finder.FindGammaRoots();
            }, delegate (Job j)
            {
                List<string> r = j.Res as List<string>;
                if (r == null || r.Count == 0) AskRoot(next, "Не нашёл папку с игрой — укажи её сам.");
                else if (r.Count == 1) { Log("Папка с игрой: " + r[0]); next(r[0]); }
                else ShowRootList(r, next);
            });
        }

        Func<string, bool> RootCheck(Action<string> next)
        {
            return delegate (string p)
            {
                if (!Finder.IsGammaRoot(p)) { SetMsg("Здесь нет GAMMA\\ModOrganizer.exe и ANOMALY\\bin", CWarn, 5); return false; }
                next(p);
                return true;
            };
        }

        const string RootHint = @"Введи путь к папке, где лежат GAMMA и ANOMALY, например D:\GAMES\STALKER_GAMMA";

        void AskRoot(Action<string> next, string why)
        {
            Page p = NewPage("ПАПКА С ИГРОЙ");
            LW(p, why, CWarn);
            L(p, "", CText);
            LW(p, @"Нужна папка, внутри которой лежат GAMMA и ANOMALY (например D:\GAMES\STALKER_GAMMA).", CText);
            p.OnPath = RootCheck(next); p.PathTitle = "ПАПКА С ИГРОЙ"; p.PathHint = RootHint;
            p.Keys = "[P] Ввести путь   [Ctrl+V] Вставить путь   [Esc] Назад";
            pg = p;
            OpenEdit(p.PathTitle, p.PathHint, "", p.OnPath);
        }

        void ShowRootList(List<string> roots, Action<string> next)
        {
            Page p = NewPage("ПАПКА С ИГРОЙ");
            L(p, "Нашёл несколько папок с игрой — выбери нужную:", CText);
            L(p, "", CText);
            p.Items = roots;
            p.OnEnter = delegate (int i) { Log("Папка с игрой: " + roots[i]); next(roots[i]); };
            p.OnPath = RootCheck(next); p.PathTitle = "ПАПКА С ИГРОЙ"; p.PathHint = RootHint;
            p.Keys = "[↑↓] Выбрать   [Enter] Эта папка   [P] Другой путь   [Esc] Назад";
            pg = p;
        }

        // simulation: fake progress so that the screens can be looked at
        void SimWork(Job j, string label, int ms)
        {
            j.Label = label;
            if (syncJobs) { j.Pct = 1; return; }
            for (int i = 1; i <= 10 && !j.Cancel; i++) { j.Pct = i / 10.0; Thread.Sleep(ms / 10); }
        }

        // ------------------------------------------------------------ [3] Сохранить сборку
        string SaveDir()
        {
            string d = Finder.DesktopDir();
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        PackInfo SimPack()
        {
            PackInfo p = new PackInfo();
            p.Path = @"C:\Users\Player\Downloads\GAMMA_Coop_build_20261126-1405.zip";
            p.Build = Coop.BuildName; p.Created = "2026-11-26T09:05:00Z"; p.CreatedUtc = new DateTime(2026, 11, 26, 9, 5, 0, DateTimeKind.Utc);
            p.BuildId = "3fa91c07be42"; p.Source = "HOST-PC"; p.Format = 2; p.HasKeybinds = true; p.Size = 214748364; p.Where = "Загрузки";
            p.Mods.AddRange(new string[] { "xrRazom Co-op", "Faction-Based Voiced Actor", "Discord RPC", "GAMMA Coop - Silent Inspect Voice", "xrRazom - Saiga M2 world position fix" });
            p.Gamma["Stalker_GAMMA"] = "ab0f743af42a3b3b0b0ee853b3676f5189f7f0d0";
            return p;
        }

        void StartSave()
        {
            PickRoot(null, delegate (string r)
            {
                hRoot = r;
                if (!simulate) WriteLastRoot(r);
                string outDir = SaveDir();
                string outZip = Path.Combine(outDir, Coop.NewBuildName());
                RunJob("Собираю сборку из твоей игры (файлы игры только читаются)…", delegate (Job j)
                {
                    if (simulate) { SimWork(j, "сжимаю файлы: 1200 / 1743", 1500); return new object[] { SimPack(), 1743 }; }
                    string res = Coop.Build(r, outZip, Log, Path.Combine(appDir, "passport"),
                        delegate (double f, string w) { j.Pct = f; j.Label = w; }, delegate () { return j.Cancel; });
                    PackInfo pk = Coop.Probe(outZip);
                    if (pk == null) throw new Exception("собранный файл не открывается: " + outZip);
                    return new object[] { pk, int.Parse(res.Split('|')[0], Inv) };
                }, delegate (Job j)
                {
                    if (j.Err != null) { ErrorPage("Не получилось собрать сборку", j.Err); return; }
                    object[] o = (object[])j.Res;
                    ShowSaved((PackInfo)o[0], (int)o[1]);
                });
            });
        }

        void ShowSaved(PackInfo pk, int files)
        {
            Log("Сборка сохранена: " + pk.Path + ", " + pk.Mods.Count + " модов, id " + pk.BuildId);
            Page p = NewPage("СБОРКА ГОТОВА");
            L(p, "✓ Файл сборки сохранён", COk);
            LW(p, pk.Path, Tone[7]);
            L(p, "Размер " + Mb(pk.Size) + " МБ · файлов " + files + " · id " + pk.BuildId + (pk.HasFiles ? " · список файлов модов есть ✓" : ""), CMuted);
            LW(p, "Моды (" + pk.Mods.Count + "): " + string.Join(", ", pk.Mods.ToArray()), CText);
            L(p, "", CText);
            LW(p, "Отправь этот файл друзьям (например через LocalSend). У них: ярлык «GAMMA Coop — сборка» → [2] Загрузить сборку.", CWarn);
            p.Keys = "[Enter] В меню   [O] Показать файл в папке";
            p.OnEnter = delegate (int i) { HubHome(); };
            string path = pk.Path;
            p.Hot[ConsoleKey.O] = delegate () { OpenInExplorer(path); };
            pg = p;
            OpenInExplorer(path);
        }

        void OpenInExplorer(string path)
        {
            Log("Показываю файл в проводнике: " + path);
            if (simulate) return;
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception e) { Log("Проводник не открылся: " + e.Message); }
        }

        // ------------------------------------------------------------ [2] Загрузить сборку
        PackPlan SimPlan(PackInfo pi)
        {
            PackPlan pl = new PackPlan();
            pl.Info = pi; pl.Mods = pi.Mods;
            return pl;
        }

        PackDiff SimDiff()
        {
            PackDiff d = new PackDiff();
            d.Same = simSame;
            d.Add.Add("Faction-Based Voiced Actor"); d.Add.Add("xrRazom - Saiga M2 world position fix");
            d.Update.Add("xrRazom Co-op"); d.Update.Add("GAMMA Coop - Silent Inspect Voice");
            d.Keep.Add("Discord RPC");
            d.Remove.Add("Old Custom Mod");
            d.BinChanged = true; d.GdChanged = true;
            if (simGammaDiff) { d.GammaDiff.Add("Stalker_GAMMA"); d.GammaDiff.Add("gamma_large_files_v2"); }
            return d;
        }

        void StartLoad(string file, string rootPreset)
        {
            hPreset = file != null;
            PickRoot(rootPreset, delegate (string r)
            {
                hRoot = r;
                if (!simulate)
                {
                    ProgressFile pf = Safety.Inspect(r) == FolderState.Ours ? ProgressFile.Load(r) : null;
                    if (pf != null && !pf.Finished)
                    {
                        InfoPage("НЕЛЬЗЯ ПРИМЕНИТЬ", CWarn, "В этой папке установка GAMMA не закончена.", "", "Сначала закончи её: в меню выбери [1] — установщик продолжит с места.");
                        return;
                    }
                }
                if (file != null) ChooseBuild(r, file);
                else OfferRepairFile(r, delegate () { ChooseBuild(r, null); });
            });
        }

        string BuildLine(PackInfo p, string curId)
        {
            string s = p.When + " · с " + (p.Source.Length > 0 ? p.Source : "?") + " · модов " + p.Mods.Count + " · " + p.Where;
            if (curId.Length > 0 && string.Equals(curId, p.BuildId, StringComparison.OrdinalIgnoreCase)) s += " · уже стоит ✓";
            return s;
        }

        void ChooseBuild(string r, string file)
        {
            if (file != null)
            {
                PackInfo pi = simulate ? SimPack() : Coop.Probe(file);
                if (pi == null) { ErrorPage("Файл сборки не открывается", file + " — это не файл сборки или он повреждён."); return; }
                VerifyBuild(r, pi);
                return;
            }
            RunJob("Ищу файл сборки (Загрузки, LocalSend, рабочий стол, папка установщика)…", delegate (Job j)
            {
                if (simulate) { SimWork(j, "смотрю папки", 600); return new List<PackInfo>(new PackInfo[] { SimPack() }); }
                return Finder.FindBuildFiles(r, appDir);
            }, delegate (Job j)
            {
                List<PackInfo> list = j.Res as List<PackInfo> ?? new List<PackInfo>();
                Func<string, bool> byPath = delegate (string path)
                {
                    PackInfo pi = Coop.Probe(path);
                    if (pi == null) { SetMsg("Это не файл сборки (или он повреждён)", CWarn, 5); return false; }
                    VerifyBuild(r, pi);
                    return true;
                };
                Page p = NewPage("ФАЙЛ СБОРКИ");
                p.OnPath = byPath; p.PathTitle = "ФАЙЛ СБОРКИ"; p.PathHint = @"Введи путь к файлу сборки, например C:\Users\Имя\Downloads\GAMMA_Coop_build_20261126-1405.zip";
                if (list.Count == 0)
                {
                    L(p, "Не нашёл файл сборки (GAMMA_Coop_build_….zip).", CWarn);
                    L(p, "", CText);
                    LW(p, "Положи его в «Загрузки» (или туда, куда LocalSend сохраняет файлы) и нажми [R] — поищу снова.", CText);
                    LW(p, "Или укажи путь к файлу: [P] / Ctrl+V.", CText);
                    p.Keys = "[R] Искать снова   [P] Свой путь   [Esc] Назад";
                    p.Hot[ConsoleKey.R] = delegate () { ChooseBuild(r, null); };
                    pg = p;
                    return;
                }
                string cur = "";
                if (!simulate) { InstalledInfo ii = Coop.ReadInstalled(r); if (ii != null) cur = ii.BuildId; }
                L(p, list.Count == 1 ? "Нашёл сборку:" : "Нашёл сборки, новые сверху:", CText);
                p.Items = new List<string>();
                foreach (PackInfo x in list) p.Items.Add(BuildLine(x, cur));
                p.Detail = delegate (int i)
                {
                    PackInfo x = list[i];
                    List<string> d = new List<string>();
                    d.Add(x.Path);
                    d.Add("Моды: " + string.Join(", ", x.Mods.ToArray()));
                    d.Add("id " + (x.BuildId.Length > 0 ? x.BuildId : "(старый формат)") + " · " + Mb(x.Size) + " МБ");
                    return d;
                };
                p.OnEnter = delegate (int i) { VerifyBuild(r, list[i]); };
                p.Keys = "[↑↓] Другая   [Enter] Загрузить эту   [P] Свой путь   [Esc] Назад";
                pg = p;
            });
        }

        void VerifyBuild(string r, PackInfo pi)
        {
            RunJob("Проверяю файл сборки: контрольные суммы и пути (в игре пока ничего не меняется)…", delegate (Job j)
            {
                PackPlan plan;
                PackDiff d;
                if (simulate)
                {
                    SimWork(j, "проверка файла сборки", 1200);
                    plan = SimPlan(pi); d = SimDiff();
                    return new object[] { plan, d };
                }
                plan = Coop.Verify(pi.Path, Path.Combine(r, "ANOMALY"), Path.Combine(r, "GAMMA"), Log,
                    delegate (double f, string w) { j.Pct = f; j.Label = w; }, delegate () { return j.Cancel; });
                if (plan == null) return null;
                j.Pct = -1; j.Label = "сравниваю с тем, что стоит у тебя…";
                d = Coop.Diff(r, plan, Coop.PassportRepos(Path.Combine(appDir, "passport")));
                return new object[] { plan, d };
            }, delegate (Job j)
            {
                if (j.Err != null) { ErrorPage("Файл сборки не подходит", j.Err); return; }
                if (j.Res == null) { HubHome(); return; }
                object[] o = (object[])j.Res;
                hTakeKeys = false;
                ShowDiff(r, pi, (PackPlan)o[0], (PackDiff)o[1]);
            });
        }

        static string ModsLine(string label, List<string> l)
        {
            return l.Count == 0 ? label + ": —" : label + " (" + l.Count + "): " + string.Join(", ", l.ToArray());
        }

        void ShowDiff(string r, PackInfo pi, PackPlan plan, PackDiff d)
        {
            hPack = pi; hPlan = plan;
            Page p = NewPage("ЧТО ИЗМЕНИТСЯ");
            L(p, "Сборка от " + pi.When + ", с компьютера " + (pi.Source.Length > 0 ? pi.Source : "?") + (pi.BuildId.Length > 0 ? ", id " + pi.BuildId : ""), CText);
            p.OnEsc = delegate () { if (hPreset) HubHome(); else ChooseBuild(r, null); };
            if (d.Same)
            {
                L(p, "", CText);
                L(p, "✓ Уже актуально — эта сборка уже применена.", COk);
                L(p, "Ничего менять не нужно. Если игра сломалась — в меню [4] Починить кооп.", CText);
                p.Keys = "[Esc] Назад";
                if (d.GammaDiff.Count > 0)
                {
                    L(p, "Но база GAMMA другая, чем у отправителя (" + string.Join(", ", d.GammaDiff.ToArray()) + ")", CWarn);
                    bool canUpd0 = simulate ? !simNoList : File.Exists(Engine.InstalledListJson(r));
                    if (canUpd0)
                    {
                        LW(p, "[U] обновит только отличающееся: репозитории с другой версией и моды, чей архив не совпадает со списком отправителя. Anomaly не качается.", CWarn);
                        p.Keys = "[U] Обновить GAMMA до его версии   [Esc] Назад";
                        p.Hot[ConsoleKey.U] = delegate () { ApplyGate(true); };
                    }
                    else
                    {
                        LW(p, "У тебя нет списка установленных модов (GAMMA ставилась лаунчером), поэтому по частям не обновить. [U] — полная синхронизация базы GAMMA (~32 ГБ, с подтверждением).", CWarn);
                        p.Keys = "[U] Полная синхронизация базы GAMMA   [Esc] Назад";
                        p.Hot[ConsoleKey.U] = delegate () { ConfirmFullSync(r, pi, plan, d); };
                    }
                }
                pg = p;
                return;
            }
            L(p, ModsLine("Добавить", d.Add), d.Add.Count > 0 ? COk : CMuted);
            L(p, ModsLine("Обновить", d.Update), d.Update.Count > 0 ? CText : CMuted);
            L(p, ModsLine("Убрать (уйдут в резерв)", d.Remove), d.Remove.Count > 0 ? CWarn : CMuted);
            L(p, ModsLine("Без изменений", d.Keep), CMuted);
            L(p, "Файлы игры: bin — " + (d.BinChanged ? "изменятся" : "как были") + ", gamedata — " + (d.GdChanged ? "изменятся" : "как были") + ", список модов профиля — заменится", CText);
            string keys = "[Enter] Применить   [Esc] Назад";
            if (d.GammaDiff.Count == 0) L(p, "База GAMMA: такая же, как у отправителя ✓", COk);
            else
            {
                L(p, "У отправителя другая версия GAMMA (" + string.Join(", ", d.GammaDiff.ToArray()) + ")", CWarn);
                bool canUpd = simulate ? !simNoList : File.Exists(Engine.InstalledListJson(r));
                if (canUpd)
                {
                    LW(p, "[U] обновит только отличающееся: репозитории с другой версией и моды, чей архив не совпадает со списком отправителя. Anomaly и такие же моды не качаются. Потом применится сборка. Без [U] применю как есть — часть модов может не заработать.", CWarn);
                    keys = "[Enter] Применить как есть   [U] Обновить GAMMA до его версии   [Esc] Назад";
                    p.Hot[ConsoleKey.U] = delegate () { ApplyGate(true); };
                }
                else
                {
                    LW(p, "У тебя нет списка установленных модов (GAMMA ставилась лаунчером), поэтому по частям не обновить. [U] — полная синхронизация базы GAMMA (~32 ГБ, с подтверждением). Без [U] применю только сборку — часть модов может не заработать.", CWarn);
                    keys = "[Enter] Применить как есть   [U] Полная синхронизация базы   [Esc] Назад";
                    p.Hot[ConsoleKey.U] = delegate () { ConfirmFullSync(r, pi, plan, d); };
                }
            }
            if (pi.HasKeybinds)
            {
                L(p, "Клавиши отправителя: [B] взять / не трогать — сейчас: " + (hTakeKeys ? "ВЗЯТЬ (твои старые клавиши уйдут в резерв)" : "не трогать"), hTakeKeys ? CWarn : CText);
                keys = keys.Replace("[Esc]", "[B] Клавиши   [Esc]");
                p.Hot[ConsoleKey.B] = delegate () { hTakeKeys = !hTakeKeys; ShowDiff(r, pi, plan, d); };
            }
            p.Keys = keys;
            p.OnEnter = delegate (int i) { ApplyGate(false); };
            pg = p;
        }

        // Mod Organizer / the game must be closed: checked every 2 seconds, then it goes on by itself
        bool hFull;                    // [U] as a full sync (no CLI mod list)
        void ApplyGate(bool update) { ApplyGate(update, false); }
        void ApplyGate(bool update, bool full) { hFull = full; WhenClosed(delegate () { BeginLoad(update); }); }

        // GAMMA from the official launcher has no modpack_maker_list.json: [U] is the old full install over it (pinned commits).
        // UpdateGuard cannot back that up (tens of GB), so say so; an interrupted run is resumed by the CLI (--resume).
        void ConfirmFullSync(string r, PackInfo pi, PackPlan plan, PackDiff d)
        {
            Page p = NewPage("ПОЛНАЯ СИНХРОНИЗАЦИЯ БАЗЫ GAMMA");
            LW(p, "Полная синхронизация базы GAMMA: ~32 ГБ загрузки, нужно 40+ ГБ свободного места, займёт ~1 час; не прерывай.", CWarn);
            L(p, "", CText);
            LW(p, "Это полная установка поверх твоей GAMMA до версии отправителя. Откатить её нельзя (слишком много файлов) — но если прервётся, установщик продолжит с места. После неё [U] станет быстрым и с защитой от обрыва.", CText);
            p.Keys = "[Enter] Начать   [Esc] Назад";
            p.OnEnter = delegate (int i)
            {
                if (!simulate)
                {
                    try
                    {
                        DriveInfo di = new DriveInfo(r.Substring(0, 1));
                        if (di.AvailableFreeSpace < 40L * 1073741824L)
                        {
                            ErrorPage("Не хватает места", "Для полной синхронизации нужно 40+ ГБ свободного места на диске " + r.Substring(0, 2) + ", свободно " + Gb(di.AvailableFreeSpace) + " ГБ. Освободи место и повтори.");
                            return;
                        }
                    }
                    catch (Exception e) { Log("Свободное место не определено: " + e.Message); }
                }
                ApplyGate(true, true);
            };
            p.OnEsc = delegate () { ShowDiff(r, pi, plan, d); };
            pg = p;
        }

        void WhenClosed(Action go)
        {
            if (simulate || !Finder.GameRunning()) { go(); return; }
            Log("Жду, пока закроют игру и MO2");
            Page p = NewPage("ЗАКРОЙ ИГРУ");
            L(p, "Сейчас запущены игра или Mod Organizer 2.", CWarn);
            L(p, "", CText);
            L(p, "Закрой игру и MO2 — я проверяю каждые 2 секунды и продолжу сам.", CText);
            p.Keys = "[Esc] Отмена";
            p.Poll = delegate () { if (!Finder.GameRunning()) { Log("Игра закрыта, продолжаю"); go(); } };
            pg = p;
        }

        void BeginLoad(bool update)
        {
            string blocked = null;
            prog = null;
            try
            {
                root = hRoot; pathRelaxed = true;
                string passDir = Path.Combine(appDir, "passport");
                if (update && !simulate)
                {
                    string ud = Path.Combine(root, @"GAMMA Coop\update");
                    Coop.ExtractPassport(hPack.Path, ud, passDir);
                    pass = Passport.Load(ud);
                }
                else pass = Passport.Load(passDir);
                if (!simulate)
                {
                    RefreshFolder();
                    blocked = FolderBlocker(true);
                    if (blocked == null)
                    {
                        Directory.CreateDirectory(Path.Combine(root, "GAMMA Coop"));
                        prog = (!update && fstate == FolderState.Ours) ? fprog : null;
                        UseInstallLog();
                        WriteLastRoot(root);
                    }
                }
            }
            catch (Exception e)
            {
                Log("Не удалось начать загрузку сборки: " + e);
                ErrorPage("Не удалось начать", e.Message);
                return;
            }
            if (blocked != null) { Log("Загрузка сборки не начата: " + blocked); ErrorPage("Не удалось начать", blocked); return; }
            eng = new Engine(root, pass, Log, simulate, appDir);
            eng.Prog = prog;
            eng.Load = true;
            eng.PackPath = hPack.Path;
            eng.Plan = hPlan;
            eng.TakeKeys = hTakeKeys && hPack.HasKeybinds;
            if (update) { eng.Update = true; eng.FullSync = hFull; }
            else
            {
                eng.Repair = true;
                for (int i = 0; i < 7; i++) eng.St[i] = "skip";
                eng.RepairVc = !File.Exists(Coop.InstalledJson(root));
                if (!eng.RepairVc) eng.St[8] = "skip";
            }
            Log("Загрузка сборки " + hPack.BuildId + ": папка " + root + (update ? ", с обновлением GAMMA" : "") + (simulate ? " (симуляция)" : ""));
            pg = null;
            screen = 2;
            eng.Start();
        }

        // ------------------------------------------------------------ [4] Починить кооп
        void StartRepairHub()
        {
            PickRoot(null, delegate (string r)
            {
                hRoot = r;
                OfferRepairFile(r, delegate () { StartRepairBuild(r); });
            });
        }

        void StartRepairBuild(string r)
        {
            RunJob("Ищу файл сборки для починки…", delegate (Job j)
            {
                if (simulate) { SimWork(j, "смотрю папки", 500); return new List<PackInfo>(new PackInfo[] { SimPack() }); }
                return Finder.FindBuildFiles(r, appDir);
            }, delegate (Job j)
            {
                List<PackInfo> list = j.Res as List<PackInfo>;
                if (list == null || list.Count == 0)
                {
                    InfoPage("НЕТ ФАЙЛА СБОРКИ", CWarn, "Не нашёл файл сборки (GAMMA_Coop_build_….zip) — чинить не из чего.",
                        "", "Положи его в «Загрузки» и выбери этот пункт снова, или возьми файл у друга/хоста.");
                    return;
                }
                PackInfo pk = list[0];
                Page p = NewPage("ПОЧИНИТЬ КООП");
                L(p, "Переставлю моды кооп из самой новой сборки:", CText);
                L(p, BuildLine(pk, ""), Tone[7]);
                LW(p, pk.Path, CMuted);
                L(p, "", CText);
                LW(p, "Старые файлы не удаляются — они переедут в резерв (GAMMA Coop\\backup).", CMuted);
                p.Keys = "[Enter] Починить   [Esc] Назад";
                p.OnEnter = delegate (int i) { WhenClosed(delegate () { RepairNow(pk); }); };
                pg = p;
            });
        }

        void RepairNow(PackInfo pk)
        {
            root = hRoot; pathRelaxed = true;
            pg = null;
            if (!simulate) WriteLastRoot(root);
            BeginRepair(pk);
            if (screen != 2) { pathRelaxed = false; screen = 3; }
        }

        // ------------------------------------------------------------ [5] Проверить целостность
        bool simIntegrityBad, simRepairFile;
        string simPendRoot;            // simulation: a game folder with an unfinished update (real temp files)
        bool simPendReal;

        static IntegrityResult SimIntegrity(bool bad)
        {
            IntegrityResult r = new IntegrityResult();
            r.Mode = "manifest"; r.BuildId = "3fa91c07be42"; r.BuildWhen = "26.11.2026 14:05";
            r.ModsChecked = 578; r.FilesChecked = 71696; r.Seconds = 1.7;
            if (bad)
            {
                ModProblem a = new ModProblem(), b = new ModProblem();
                a.Name = "312- Gunslinger Guns for Anomaly - Teivazcz & Gunslinger Team"; a.Missing = 3; a.Size = 1; a.Total = 940;
                a.Examples.Add(@"gamedata/configs/items/weapons/groza_new_sounds.ltx (нет файла)");
                b.Name = "G.A.M.M.A. UI"; b.Hash = 2; b.Total = 310;
                b.Examples.Add("gamedata/configs/ui/ui_custom_msgs.xml (содержимое другое)");
                r.Bad.Add(a); r.Bad.Add(b);
                r.CopyPath = @"C:\Users\Player\Downloads\GAMMA_Coop_report_20261126-1500.txt";
            }
            return r;
        }

        void StartIntegrity()
        {
            PickRoot(null, delegate (string r)
            {
                hRoot = r;
                RunJob("Проверяю целостность модов (файлы игры только читаются)…", delegate (Job j)
                {
                    if (simulate) { SimWork(j, "проверка модов: 300 / 578", 800); return SimIntegrity(simIntegrityBad); }
                    IntegrityResult res = Integrity.Run(r, null, delegate (double f, string w) { j.Pct = f; j.Label = w; }, delegate () { return j.Cancel; }, Log);
                    if (res.Aborted) return null;
                    Integrity.SaveReport(r, res, Integrity.ReportFile(r), Finder.DownloadsDir(), Log);
                    return res;
                }, delegate (Job j)
                {
                    if (j.Err != null) { ErrorPage("Проверка не получилась", j.Err); return; }
                    IntegrityResult res = j.Res as IntegrityResult;
                    if (res == null) { HubHome(); return; }
                    ShowIntegrity(res);
                });
            });
        }

        void ShowIntegrity(IntegrityResult res)
        {
            Log("Проверка целостности: " + res.Line() + "; плохих модов " + res.Bad.Count);
            Page p = NewPage("ЦЕЛОСТНОСТЬ МОДОВ");
            string how = res.Mode == "manifest"
                ? "сверка со списком файлов хоста (сборка " + (res.BuildId.Length > 0 ? res.BuildId : "?") + ")"
                : "только проверка этого компьютера — в сохранённой сборке нет списка файлов (пусть хост сохранит сборку заново: [3])";
            string stats = "модов " + res.ModsChecked + ", файлов " + res.FilesChecked + ", за " + res.Seconds.ToString("F1", Inv) + " с" + (res.NotChecked > 0 ? ", не проверено " + res.NotChecked : "");
            if (res.Ok)
            {
                L(p, "✓ Всё целое — не нашёл ни пропавших, ни изменённых файлов", COk);
                LW(p, stats + " · " + how, CMuted);
                L(p, "", CText);
                LW(p, "Это не значит, что игра точно не вылетит, но моды на месте и такие же, как у хоста.", CText);
            }
            else
            {
                L(p, "✗ Найдены проблемы в модах: " + res.Bad.Count, CBad);
                LW(p, stats + " · " + how, CMuted);
                List<string> names = res.BadNames();
                LW(p, "Попроси хоста: Сохранить моды для починки → " + string.Join(", ", names.ToArray()), CWarn);
                if (res.CopyPath.Length > 0) L(p, "Файл для хоста: " + res.CopyPath, Tone[7]);
                else if (res.ReportPath.Length > 0) L(p, "Отчёт: " + res.ReportPath, CMuted);
                for (int i = 0; i < res.Bad.Count && i < 3; i++)
                {
                    ModProblem m = res.Bad[i];
                    L(p, "• " + m.Name + " — " + m.What() + (m.Examples.Count > 0 ? " · напр. " + m.Examples[0] : ""), CText);
                }
                if (res.Bad.Count > 3) L(p, "…и ещё " + (res.Bad.Count - 3) + " (полный список в отчёте)", CDim);
            }
            p.Keys = res.ReportPath.Length > 0 ? "[Enter] В меню   [O] Показать отчёт" : "[Enter] В меню";
            p.OnEnter = delegate (int i) { HubHome(); };
            string rp = res.CopyPath.Length > 0 ? res.CopyPath : res.ReportPath;
            if (rp.Length > 0) p.Hot[ConsoleKey.O] = delegate () { OpenInExplorer(rp); };
            pg = p;
        }

        // ------------------------------------------------------------ [H] Сохранить моды для починки (host)
        string hRepairOut = null;      // test hook: where the repair file goes (default: Downloads)
        void StartRepairHelper()
        {
            PickRoot(null, delegate (string r)
            {
                hRoot = r;
                RunJob("Ищу отчёты друзей (GAMMA_Coop_report_….txt)…", delegate (Job j)
                {
                    if (simulate) { SimWork(j, "смотрю папки", 400); return new List<string[]>(new string[][] { new string[] { @"C:\Users\Player\Downloads\GAMMA_Coop_report_20261126-1500.txt", "Загрузки" } }); }
                    return Finder.FindReportFiles(r, appDir);
                }, delegate (Job j)
                {
                    List<string[]> list = j.Res as List<string[]> ?? new List<string[]>();
                    Page p = NewPage("СОХРАНИТЬ МОДЫ ДЛЯ ПОЧИНКИ");
                    p.OnPath = delegate (string path)
                    {
                        List<string> m = null;
                        try { m = File.Exists(path) ? Integrity.ReadReportMods(path) : null; } catch (Exception) { }
                        if (m == null || m.Count == 0) { SetMsg("Это не отчёт друга (нет списка модов)", CWarn, 5); return false; }
                        ConfirmRepairBuild(r, m);
                        return true;
                    };
                    p.PathTitle = "ОТЧЁТ ДРУГА"; p.PathHint = @"Введи путь к файлу отчёта, например C:\Users\Имя\Downloads\GAMMA_Coop_report_20261126-1500.txt";
                    p.Hot[ConsoleKey.M] = delegate ()
                    {
                        Func<string, bool> okMods = delegate (string txt)
                        {
                            List<string> m = new List<string>();
                            foreach (string s in txt.Split(';'))
                            {
                                string n = s.Trim();
                                if (n.Length == 0) continue;
                                if (!simulate && !Directory.Exists(Path.Combine(r, @"GAMMA\mods\" + n))) { SetMsg("Нет такого мода: " + n, CWarn, 6); return false; }
                                m.Add(n);
                            }
                            if (m.Count == 0) { SetMsg("Впиши хотя бы один мод", CWarn, 4); return false; }
                            ConfirmRepairBuild(r, m);
                            return true;
                        };
                        OpenEdit("МОДЫ ДЛЯ ПОЧИНКИ", "Названия модов точно как в GAMMA\\mods, через точку с запятой ;", "", okMods, "Моды: ");
                    };
                    if (list.Count == 0)
                    {
                        L(p, "Не нашёл отчётов друзей (GAMMA_Coop_report_….txt).", CWarn);
                        L(p, "", CText);
                        LW(p, "Друг делает [5] Проверить целостность — отчёт появится у него в «Загрузки». Пусть пришлёт его тебе (LocalSend) и положи в свои «Загрузки», потом [R].", CText);
                        LW(p, "Или укажи путь к отчёту ([P]), или выбери моды сам ([M]).", CText);
                        p.Keys = "[R] Искать снова   [P] Путь к отчёту   [M] Выбрать моды сам   [Esc] Назад";
                        p.Hot[ConsoleKey.R] = delegate () { StartRepairHelper(); };
                        pg = p;
                        return;
                    }
                    L(p, "Нашёл отчёты друзей, новые сверху — по какому собрать моды?", CText);
                    p.Items = new List<string>();
                    List<List<string>> mods = new List<List<string>>();
                    foreach (string[] x in list)
                    {
                        List<string> m = new List<string>();
                        try { m = Integrity.ReadReportMods(x[0]); } catch (Exception) { }
                        mods.Add(m);
                        p.Items.Add(File.Exists(x[0]) || !simulate ? Path.GetFileName(x[0]) + " · " + x[1] + " · модов " + m.Count : Path.GetFileName(x[0]) + " · " + x[1]);
                    }
                    p.Detail = delegate (int i) { return Wrap(mods[i].Count > 0 ? "Моды: " + string.Join(", ", mods[i].ToArray()) : "Моды: (в этом отчёте список читается при выборе)", 86); };
                    p.OnEnter = delegate (int i)
                    {
                        List<string> m = simulate ? new List<string>(new string[] { "312- Gunslinger Guns for Anomaly - Teivazcz & Gunslinger Team", "G.A.M.M.A. UI" }) : mods[i];
                        ConfirmRepairBuild(r, m);
                    };
                    p.Keys = "[↑↓] Другой   [Enter] Собрать по этому отчёту   [P] Путь к отчёту   [M] Моды сам   [Esc] Назад";
                    pg = p;
                });
            });
        }

        void ConfirmRepairBuild(string r, List<string> mods)
        {
            Page p = NewPage("СОХРАНИТЬ МОДЫ ДЛЯ ПОЧИНКИ");
            L(p, "Соберу файл починки из твоей игры (только чтение) — моды (" + mods.Count + "):", CText);
            LW(p, string.Join(", ", mods.ToArray()), Tone[7]);
            L(p, "", CText);
            LW(p, "У друга эти папки модов целиком заменятся твоими (его старые уйдут в резерв). Личные настройки MCM в файл не попадают.", CMuted);
            p.Keys = "[Enter] Собрать   [Esc] Назад";
            p.OnEnter = delegate (int i)
            {
                string outZip = Path.Combine(hRepairOut ?? Finder.DownloadsDir(), Coop.NewRepairName());
                RunJob("Собираю файл починки (файлы игры только читаются)…", delegate (Job j)
                {
                    if (simulate) { SimWork(j, "сжимаю файлы: 400 / 940", 800); return new object[] { outZip, mods.Count }; }
                    int n = Coop.BuildRepair(r, mods, outZip, Log, delegate (double f, string w) { j.Pct = f; j.Label = w; }, delegate () { return j.Cancel; });
                    return new object[] { outZip, n };
                }, delegate (Job j)
                {
                    if (j.Err != null) { ErrorPage("Не получилось собрать файл починки", j.Err); return; }
                    object[] o = (object[])j.Res;
                    Log("Файл починки: " + o[0] + ", модов " + o[1]);
                    Page d = NewPage("ФАЙЛ ПОЧИНКИ ГОТОВ");
                    L(d, "✓ Файл починки сохранён", COk);
                    LW(d, (string)o[0], Tone[7]);
                    L(d, "Модов в нём: " + o[1], CMuted);
                    L(d, "", CText);
                    LW(d, "Отправь его другу (например через LocalSend). У него: ярлык «GAMMA Coop — сборка» → [4] Починить кооп (или [2]) — установщик сам найдёт файл и заменит эти моды.", CWarn);
                    string path = (string)o[0];
                    d.Keys = "[Enter] В меню   [O] Показать файл в папке";
                    d.OnEnter = delegate (int k) { HubHome(); };
                    d.Hot[ConsoleKey.O] = delegate () { OpenInExplorer(path); };
                    pg = d;
                });
            };
            pg = p;
        }

        // ------------------------------------------------------------ repair file from the host ([2] / [4])
        // before the usual flow: is there a GAMMA_Coop_repair_*.zip that was not applied yet? cont() goes on with the usual flow
        void OfferRepairFile(string r, Action cont)
        {
            RunJob("Ищу файл починки от хоста…", delegate (Job j)
            {
                if (simulate)
                {
                    SimWork(j, "смотрю папки", 300);
                    List<RepairInfo> s = new List<RepairInfo>();
                    if (simRepairFile)
                    {
                        RepairInfo ri = new RepairInfo();
                        ri.Path = @"C:\Users\Player\Downloads\GAMMA_Coop_repair_20261126-1510.zip"; ri.Source = "HOST-PC"; ri.Where = "Загрузки"; ri.Size = 52428800;
                        ri.CreatedUtc = new DateTime(2026, 11, 26, 10, 10, 0, DateTimeKind.Utc);
                        ri.Mods.Add("312- Gunslinger Guns for Anomaly - Teivazcz & Gunslinger Team"); ri.Mods.Add("G.A.M.M.A. UI");
                        s.Add(ri);
                    }
                    return s;
                }
                return Finder.FindRepairFiles(r, appDir);
            }, delegate (Job j)
            {
                List<RepairInfo> l = j.Res as List<RepairInfo>;
                if (l == null || l.Count == 0) { cont(); return; }
                RepairInfo ri = l[0];
                Page p = NewPage("ФАЙЛ ПОЧИНКИ ОТ ХОСТА");
                L(p, "Нашёл файл починки: " + ri.When + " · с " + (ri.Source.Length > 0 ? ri.Source : "?") + " · " + ri.Where + " · " + Mb(ri.Size) + " МБ", CText);
                LW(p, ri.Path, CMuted);
                LW(p, "Моды (" + ri.Mods.Count + "): " + string.Join(", ", ri.Mods.ToArray()), Tone[7]);
                L(p, "", CText);
                LW(p, "Эти папки модов заменятся целиком, твои старые уйдут в резерв (GAMMA Coop\\backup).", CMuted);
                p.Keys = "[Enter] Применить файл починки   [S] Пропустить   [Esc] Назад";
                p.OnEnter = delegate (int i) { WhenClosed(delegate () { RepairZipNow(ri); }); };
                p.Hot[ConsoleKey.S] = delegate () { cont(); };
                pg = p;
            });
        }

        void RepairZipNow(RepairInfo ri)
        {
            root = hRoot; pathRelaxed = true;
            pg = null;
            if (!simulate) WriteLastRoot(root);
            BeginRepair(null, ri.Path);
            if (screen != 2) { pathRelaxed = false; screen = 3; }
        }

        // ------------------------------------------------------------ an update that never finished
        string pendRoot;               // game folder whose update_journal.json says "running"

        List<string> PendingRoots()
        {
            List<string> l = new List<string>();
            if (simulate) { if (simPendRoot != null) l.Add(simPendRoot); return l; }
            l.Add(root);
            try { string lr = Path.Combine(appDir, "last_root.txt"); if (File.Exists(lr)) l.Add(File.ReadAllText(lr, Encoding.UTF8).Trim().TrimEnd('\\')); } catch (Exception) { }
            if (loadRoot != null) l.Add(loadRoot);
            return l;
        }

        // power cut / kill during [U]: before anything else offer to put the mods back. true = the offer is on the screen
        bool CheckPendingUpdate()
        {
            foreach (string r in PendingRoots())
            {
                if (string.IsNullOrEmpty(r)) continue;
                try { if (!Directory.Exists(r) || !UpdateGuard.Pending(r)) continue; } catch (Exception) { continue; }
                pendRoot = r;
                Log("Найдено незаконченное обновление в " + r);
                ShowPending(r);
                return true;
            }
            return false;
        }

        void ShowPending(string r)
        {
            UpdateJournal j = UpdateGuard.Load(r);
            if (j == null || j.State != "running") { pendRoot = null; HubHome(); return; }
            Page p = NewPage("ОБНОВЛЕНИЕ НЕ ЗАКОНЧЕНО");
            LW(p, "Прошлое обновление GAMMA оборвалось (" + j.Started.Replace('T', ' ') + "): часть модов может остаться наполовину заменённой — игра тогда вылетает с ошибкой вроде «Can't find include file».", CWarn);
            L(p, "", CText);
            if (j.Copied) LW(p, "Старые моды лежат в резерве (" + j.Backup + ") — верну их как были. Это безопасно.", CText);
            else LW(p, "Копия старых модов не успела записаться, а значит игра ещё не менялась — просто уберу недоделанную копию.", CText);
            string keys = "[Enter/R] Вернуть как было (рекомендую)   [Esc] В меню";
            if (j.Copied && j.BuildPath.Length > 0 && File.Exists(j.BuildPath))
            {
                L(p, "", CText);
                LW(p, "Или продолжить обновление с места (файл сборки " + Path.GetFileName(j.BuildPath) + "): [U] — откроется обычный экран загрузки сборки, там снова [U].", CMuted);
                string bp = j.BuildPath;
                p.Hot[ConsoleKey.U] = delegate () { StartLoad(bp, r); };
                keys = "[Enter/R] Вернуть как было (рекомендую)   [U] Продолжить   [Esc] В меню";
            }
            p.Keys = keys;
            p.OnEnter = delegate (int i) { RestorePending(r, j); };
            p.Hot[ConsoleKey.R] = delegate () { RestorePending(r, j); };
            pg = p; screen = 3;
        }

        void RestorePending(string r, UpdateJournal j)
        {
            WhenClosed(delegate ()
            {
                RunJob("Возвращаю моды как были…", delegate (Job w)
                {
                    if (simulate && !simPendReal) { SimWork(w, "возвращаю моды", 500); return 0; }
                    return UpdateGuard.Rollback(r, j, Log, delegate (double f, string lb) { w.Pct = f; w.Label = lb; });
                }, delegate (Job w)
                {
                    if (w.Err != null)
                    {
                        InfoPage("НЕ ПОЛУЧИЛОСЬ ВЕРНУТЬ МОДЫ", CBad, w.Err, "", "Резерв остался в GAMMA Coop\\backup — ничего не потеряно. Закрой игру и MO2 и попробуй ещё раз.");
                        return;
                    }
                    pendRoot = null;
                    InfoPage("ГОТОВО", COk, "Моды возвращены как были.", "", "Игру можно запускать. Обновить GAMMA до версии друга можно снова: [2] Загрузить сборку → [U].");
                });
            });
        }

        // ------------------------------------------------------------ headless simulation of the hub flows (no disk writes)
        static ConsoleKeyInfo Key(ConsoleKey k, char c) { return new ConsoleKeyInfo(c, k, false, false, false); }

        void RunEngineSim()
        {
            for (int i = 0; i < 6000 && eng.Phase != Phase.Done && eng.Phase != Phase.Failed; i++)
                eng.Pump(eng.Phase == Phase.RateLimit ? 500 : 30);
        }

        string RunHubSim(Action<string> emit)
        {
            syncJobs = true;
            int frames = 0;
            Action<string> em = delegate (string s) { emit(s); frames++; };
            ConsoleKeyInfo enter = Key(ConsoleKey.Enter, '\r'), esc = Key(ConsoleKey.Escape, (char)27);
            screen = 3; pg = null;
            em("hub: menu");
            HubKey(Key(ConsoleKey.DownArrow, '\0')); em("hub: menu, [2] selected");

            // [3] save
            HubEnter(2); em("hub: [3] build saved");
            HubKey(enter);

            // [2] load: the same build is already installed
            simSame = true; simGammaDiff = false;
            HubEnter(1); em("hub: [2] build list");
            HubKey(enter); em("hub: [2] diff, already up to date");
            HubKey(esc); HubKey(esc);

            // [2] load: GAMMA versions differ -> [U]
            simSame = true; simGammaDiff = true;   // build already applied, GAMMA base differs: [U] must still be offered
            HubEnter(1); HubKey(enter); em("hub: [2] diff, already applied but other GAMMA version");
            HubKey(Key(ConsoleKey.U, 'u'));
            string upd = "no engine";
            if (eng != null)
            {
                bool isUpdate = eng.Update;
                bool anomalyItem = false;
                HashSet<string> seenMods = new HashSet<string>();
                for (int i = 0; i < 6000 && eng.Phase != Phase.Done && eng.Phase != Phase.Failed; i++)
                {
                    eng.Pump(eng.Phase == Phase.RateLimit ? 500 : 0.5);
                    if (i == 40) { confirmStop = true; em("hub: [U] Esc asks to confirm"); confirmStop = false; }
                    // incremental: Anomaly must never be downloaded in update mode, and only the few differing mods
                    foreach (string k in new List<string>(eng.Items.Keys)) { if (k.IndexOf("Anomaly", StringComparison.OrdinalIgnoreCase) >= 0) anomalyItem = true; if (k.StartsWith("Updated_Mod_")) seenMods.Add(k); }
                }
                em("hub: [2] update + apply, " + eng.Phase);
                upd = isUpdate + "/" + eng.Phase + (!anomalyItem && seenMods.Count == 4 ? "" : "/NOT-INCREMENTAL");
                eng.Stop(); eng = null;
            }
            screen = 3; pg = null;

            // [2] load: other GAMMA version but no CLI list of installed mods -> no [U] offered
            simNoList = true;
            HubEnter(1); HubKey(enter); em("hub: [2] diff, other GAMMA version, no [U]");
            // no CLI list: [U] is the explicit full sync with a confirm page
            Action u0 = pg != null && pg.Hot.ContainsKey(ConsoleKey.U) ? pg.Hot[ConsoleKey.U] : null;
            if (u0 != null) u0();
            em("hub: [2] full sync confirm");
            string noU = (u0 != null && pg != null && pg.Title == "ПОЛНАЯ СИНХРОНИЗАЦИЯ БАЗЫ GAMMA").ToString();
            simNoList = false;
            simSame = false;
            HubKey(esc); HubKey(esc);
            screen = 3; pg = null;

            // [2] load: plain apply
            simGammaDiff = false;
            HubEnter(1); HubKey(enter); em("hub: [2] diff, normal");
            HubKey(enter);
            string app = "no engine";
            if (eng != null)
            {
                for (int i = 0; i < 12 && eng.Phase != Phase.Done; i++) { eng.Pump(1); if (i == 3) em("hub: [2] applying"); }
                RunEngineSim();
                em("hub: [2] applied, " + eng.Phase);
                app = eng.Load + "/" + eng.Phase;
                eng.Stop(); eng = null;
            }
            screen = 3; pg = null;

            // [4] repair from the hub
            HubEnter(3); em("hub: [4] repair confirm");
            HubKey(enter);
            string rep = "no engine";
            if (eng != null)
            {
                RunEngineSim();
                rep = eng.Phase.ToString();
                eng.Stop(); eng = null;
            }
            screen = 3; pg = null;

            // [5] integrity: intact, then broken mods
            simIntegrityBad = false;
            HubEnter(4); em("hub: [5] integrity, all fine");
            string intOk = (pg != null && pg.Title == "ЦЕЛОСТНОСТЬ МОДОВ" && pg.Lines[0].StartsWith("✓")).ToString();
            HubKey(enter);
            simIntegrityBad = true;
            HubEnter(4); em("hub: [5] integrity, broken mods");
            string intBad = (pg != null && pg.Lines[0].StartsWith("✗")).ToString();
            HubKey(enter);
            simIntegrityBad = false;
            screen = 3; pg = null;

            // [H] the host builds a repair file from a friend's report
            HubKey(Key(ConsoleKey.H, 'h')); em("hub: [H] friend's reports");
            HubKey(enter); em("hub: [H] confirm");
            HubKey(enter); em("hub: [H] repair file saved");
            string hostOk = (pg != null && pg.Title == "ФАЙЛ ПОЧИНКИ ГОТОВ").ToString();
            HubKey(enter);
            screen = 3; pg = null;

            // [4] with a repair file from the host lying in Downloads
            simRepairFile = true;
            HubEnter(3); em("hub: [4] repair file from the host offered");
            string offer = (pg != null && pg.Title == "ФАЙЛ ПОЧИНКИ ОТ ХОСТА").ToString();
            HubKey(enter);
            string repZip = "no engine";
            if (eng != null)
            {
                RunEngineSim();
                em("hub: [4] repair file applied, " + eng.Phase);
                repZip = (eng.RepairZip != null && eng.Repair).ToString() + "/" + eng.Phase;
                eng.Stop(); eng = null;
            }
            simRepairFile = false;
            screen = 3; pg = null; syncJobs = false;
            return "\"hub\":\"ok\",\"hubFrames\":" + frames + ",\"hubUpdate\":\"" + upd + "\",\"hubNoU\":\"" + noU + "\",\"hubApply\":\"" + app + "\",\"hubRepair\":\"" + rep + "\",\"hubIntegrity\":\"" + intOk + "/" + intBad + "\",\"hubHostRepair\":\"" + hostOk + "\",\"hubRepairFile\":\"" + offer + "/" + repZip + "\"";
        }
    }
}
