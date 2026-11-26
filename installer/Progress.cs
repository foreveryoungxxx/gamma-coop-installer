using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace GammaCoop
{
    // root\GAMMA Coop\progress.json: also the "this folder is ours" marker. Every write is atomic and durable
    // (tmp file with WriteThrough + Flush(true), then File.Replace), so a power cut never leaves half a file.
    public class ProgressFile
    {
        public static readonly string[] Ids = { "tool", "config", "install", "check", "coop", "vc", "shortcut" };
        public static readonly string[] Names = {
            "Программа установки", "Настройка", "Anomaly, GAMMA и моды", "Проверка Anomaly",
            "Кооп xrRazom и озвучка", "Библиотеки Visual C++", "Ярлык и инструкция" };
        const string TimeFmt = "yyyy-MM-dd'T'HH:mm:ss";

        public static Action<string> Log;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string from, string to, int flags);   // 1 = replace existing, 8 = write through

        readonly object lk = new object();
        readonly string file;
        public string Build = "", PassportVersion = "", Root = "", Started = "", Updated = "", Last = "", LastError = "";
        public bool Running, Finished;
        public readonly Dictionary<string, string> Stages = new Dictionary<string, string>();

        ProgressFile(string root) { Root = root; file = Safety.ProgressPath(root); }

        public static string NameOf(string id)
        {
            int i = Array.IndexOf(Ids, id);
            return i >= 0 ? Names[i] : id;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            return d.TryGetValue(k, out o) && o != null ? o.ToString() : "";
        }

        static bool Bool(Dictionary<string, object> d, string k)
        {
            object o;
            return d.TryGetValue(k, out o) && o is bool && (bool)o;
        }

        // null when there is no file; an empty record when the file is unreadable (it is still the "ours" marker)
        public static ProgressFile Load(string root)
        {
            string f = Safety.ProgressPath(root);
            if (!File.Exists(f)) return null;
            ProgressFile p = new ProgressFile(root);
            try
            {
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) return p;
                p.Build = Str(d, "build"); p.PassportVersion = Str(d, "passportVersion"); p.Started = Str(d, "started"); p.Updated = Str(d, "updated");
                p.Last = Str(d, "last"); p.LastError = Str(d, "lastError");
                p.Running = Bool(d, "running"); p.Finished = Bool(d, "finished");
                Dictionary<string, object> st = d.ContainsKey("stages") ? d["stages"] as Dictionary<string, object> : null;
                if (st != null) foreach (KeyValuePair<string, object> kv in st) if (kv.Value != null) p.Stages[kv.Key] = kv.Value.ToString();
            }
            catch (Exception e) { Say("progress.json не прочитан: " + e.Message); }
            return p;
        }

        // new record (running) written at once
        public static ProgressFile Create(string root, string build, string passportVersion)
        {
            ProgressFile p = new ProgressFile(root);
            p.Build = build; p.PassportVersion = passportVersion;
            p.Started = DateTime.Now.ToString(TimeFmt, CultureInfo.InvariantCulture);
            p.Running = true;
            p.Save();
            return p;
        }

        static void Say(string s)
        {
            Action<string> l = Log;
            if (l != null) { try { l(s); } catch (Exception) { } }
        }

        public bool IsDone(string id) { lock (lk) { string s; return Stages.TryGetValue(id, out s) && s == "done"; } }

        public DateTime UpdatedTime
        {
            get
            {
                DateTime t;
                if (DateTime.TryParse(Updated, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
                return DateTime.MinValue;
            }
        }

        // the stage to show as "stopped at": the running one, or the first one not done yet
        public string StopStage()
        {
            lock (lk)
            {
                if (Last.Length > 0 && !IsDoneNoLock(Last)) return Last;
                foreach (string id in Ids) if (!IsDoneNoLock(id)) return id;
                return Ids[Ids.Length - 1];
            }
        }

        bool IsDoneNoLock(string id) { string s; return Stages.TryGetValue(id, out s) && s == "done"; }

        public void MarkDone(string id) { lock (lk) { Stages[id] = "done"; Save(); } }
        public void Unmark(string id) { lock (lk) { if (Stages.Remove(id)) Save(); } }
        public void SetLast(string id) { lock (lk) { Last = id; Save(); } }
        public void SetRunning(bool running, string err) { lock (lk) { Running = running; if (err != null) LastError = err; else if (running) LastError = ""; Save(); } }
        public void SetFinished() { lock (lk) { Finished = true; Running = false; LastError = ""; Save(); } }
        public void Beat() { lock (lk) { Save(); } }

        // call under lk
        void Save()
        {
            Updated = DateTime.Now.ToString(TimeFmt, CultureInfo.InvariantCulture);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["version"] = 1;
            d["build"] = Build; d["passportVersion"] = PassportVersion; d["root"] = Root;
            d["started"] = Started; d["updated"] = Updated;
            d["running"] = Running; d["finished"] = Finished;
            Dictionary<string, object> st = new Dictionary<string, object>();
            foreach (string id in Ids) { string s; if (Stages.TryGetValue(id, out s)) st[id] = s; }
            d["stages"] = st;
            d["last"] = Last; d["lastError"] = LastError;
            byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(d));
            string tmp = file + ".tmp";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (FileStream f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        f.Write(bytes, 0, bytes.Length);
                        f.Flush(true);
                    }
                    // one rename-with-replace call: File.Replace is several steps and a kill in the middle can leave no progress.json at all
                    if (!MoveFileEx(tmp, file, 1 | 8)) throw new IOException("MoveFileEx: ошибка " + Marshal.GetLastWin32Error());
                    return;
                }
                catch (Exception e)
                {
                    if (attempt >= 3) { Say("progress.json не записан: " + e.Message); return; }
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
