using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GammaCoop
{
    public enum FolderState { NotExists, Empty, Ours, ExistingGamma, Foreign }

    // disclaimer: where the official copy lives. Url empty = no public page yet.
    public static class Official
    {
        public const string Url = "github.com/foreveryoungxxx/gamma-coop-installer";
        public static string Where { get { return Url.Length > 0 ? Url : "только у автора сборки"; } }
        public const string NotLiable = "Автор не отвечает за изменённые копии и файлы со сторонних сайтов.";
    }

    // "защита от дурака": which folders the installer may use and delete from. No dependency on the UI classes
    // (make_pack.ps1 compiles Coop.cs + Safety.cs only).
    public static class Safety
    {
        public static Action<string> Log;

        const string Personal = "Тут личные файлы (фото, документы) — выбери другую папку, например D:\\GAMES\\STALKER_GAMMA";
        const string SystemMsg = "Системная папка Windows — сюда ставить нельзя";
        const string AppMsg = "Это папка самого установщика";
        const string JunctionMsg = "В пути есть ярлык-перенаправление (junction) — выбери обычную папку";
        public const int MaxPath = 60;

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr p);

        class Prot
        {
            public string Dir, Kind;   // Kind: personal | system | app
            public bool Strict;        // strict: nothing may be deleted even inside it
        }

        static void Say(string s)
        {
            Action<string> l = Log;
            if (l != null) { try { l(s); } catch (Exception) { } }
        }

        static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Trim().Length == 0) return null;
            try
            {
                string f = Path.GetFullPath(s.Trim()).TrimEnd('\\');
                return f.Length <= 2 ? null : f;
            }
            catch (Exception) { return null; }
        }

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        static bool Under(string child, string parent) { return child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase); }

        public static string Downloads()
        {
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

        static void Add(List<Prot> l, string dir, string kind, bool strict)
        {
            string n = Norm(dir);
            if (n == null) return;
            Prot p = new Prot();
            p.Dir = n; p.Kind = kind; p.Strict = strict;
            l.Add(p);
        }

        static List<Prot> Protected(string appDir)
        {
            List<Prot> l = new List<Prot>();
            Environment.SpecialFolder[] strict = { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData };
            Environment.SpecialFolder[] sys = { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData };
            Environment.SpecialFolder[] pers = { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonPictures };
            foreach (Environment.SpecialFolder f in strict) Add(l, Environment.GetFolderPath(f), "system", true);
            foreach (Environment.SpecialFolder f in sys) Add(l, Environment.GetFolderPath(f), "system", false);
            foreach (Environment.SpecialFolder f in pers) Add(l, Environment.GetFolderPath(f), "personal", false);
            Add(l, Downloads(), "personal", false);
            foreach (string v in new string[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }) Add(l, Environment.GetEnvironmentVariable(v), "personal", false);
            foreach (string v in new string[] { "TEMP", "TMP" }) Add(l, Environment.GetEnvironmentVariable(v), "system", false);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(up)) { try { Add(l, Path.GetDirectoryName(up.TrimEnd('\\')), "system", false); } catch (Exception) { } }
            string[] onDrive = { "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users" };
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed) continue;
                    foreach (string s in onDrive) Add(l, Path.Combine(d.Name, s), "system", s != "Users");
                }
            }
            catch (Exception) { }
            if (appDir != null) Add(l, appDir, "app", false);
            return l;
        }

        static string MsgFor(Prot p)
        {
            if (p.Kind == "personal") return Personal;
            if (p.Kind == "app") return AppMsg;
            return SystemMsg;
        }

        // null = fine, otherwise a short Russian reason
        public static string PathProblem(string p, string appDir)
        {
            if (p == null) p = "";
            p = p.Trim();
            if (p.Length == 0) return "Папка не указана";
            if (!Regex.IsMatch(p, @"^[A-Za-z]:\\"))
                return @"Нужен полный путь, например D:\GAMES\STALKER_GAMMA";
            if (Regex.IsMatch(p, @"^[A-Za-z]:\\*$"))
                return @"Нужна папка, а не корень диска, например D:\GAMES\STALKER_GAMMA";
            if (!Regex.IsMatch(p, @"^[A-Za-z]:(\\[A-Za-z0-9_\-]+)+\\?$"))
                return "Только латиница, цифры, _ и - (без пробелов, точек и русских букв)";
            string[] bad = { "program", "programdata", "windows", "desktop", "documents", "downloads" };
            foreach (string seg in p.Substring(3).ToLowerInvariant().Split('\\'))
            {
                if (seg.StartsWith("onedrive")) return "Не клади игру в OneDrive";
                if (Array.IndexOf(bad, seg) >= 0) return "Эта системная папка не подходит";
            }
            if (p.Length > MaxPath) return "Слишком длинный путь (" + p.Length + " символов, можно до " + MaxPath + ") — выбери покороче";

            DriveInfo di;
            try
            {
                di = new DriveInfo(p.Substring(0, 1));
                if (di.DriveType == DriveType.Network) return "Сетевой диск не подходит — выбери диск внутри компьютера";
                if (di.DriveType == DriveType.Removable || di.DriveType == DriveType.CDRom) return "Съёмный диск (флешка, внешний диск, CD) не подходит — выбери диск внутри компьютера";
                if (di.DriveType != DriveType.Fixed) return "Диск " + p.Substring(0, 2) + " не найден";
                if (!di.IsReady) return "Диск " + p.Substring(0, 2) + " недоступен";
                string fmt = di.DriveFormat;
                if (!string.Equals(fmt, "NTFS", StringComparison.OrdinalIgnoreCase) && !string.Equals(fmt, "ReFS", StringComparison.OrdinalIgnoreCase))
                {
                    if (fmt.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || string.Equals(fmt, "exFAT", StringComparison.OrdinalIgnoreCase))
                        return "Диск в формате FAT32/exFAT не подходит — нужны файлы больше 4 ГБ и права NTFS";
                    return "Диск в формате " + fmt + " не подходит — нужен NTFS";
                }
            }
            catch (Exception e) { return "Диск " + p.Substring(0, 2) + " недоступен (" + e.Message + ")"; }

            string full = Norm(p);
            if (full == null) return "Путь не распознан";
            // several folders can match (Pictures is inside the user profile): the most specific one gives the message
            Prot hit = null;
            foreach (Prot pr in Protected(appDir))
            {
                if (!(Same(full, pr.Dir) || Under(full, pr.Dir) || Under(pr.Dir, full))) continue;
                if (hit == null || pr.Dir.Length > hit.Dir.Length) hit = pr;
            }
            if (hit != null) return MsgFor(hit);

            string cur = full.Substring(0, 3);
            foreach (string seg in full.Substring(3).Split('\\'))
            {
                cur = Path.Combine(cur, seg);
                if (Directory.Exists(cur))
                {
                    if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0) return JunctionMsg;
                }
                else if (File.Exists(cur)) return "Это файл, а не папка: " + cur;
                else break;
            }
            return null;
        }

        // ------------------------------------------------------------ what is inside the chosen folder
        public static string ProgressPath(string root) { return Path.Combine(root, @"GAMMA Coop\progress.json"); }

        public static FolderState Inspect(string root) { int n; return Inspect(root, out n); }

        // count = number of items in the folder (hidden and system ones included) when it is Foreign
        public static FolderState Inspect(string root, out int count)
        {
            count = 0;
            try
            {
                if (!Directory.Exists(root)) return FolderState.NotExists;
                if (File.Exists(ProgressPath(root))) return FolderState.Ours;
                if (File.Exists(Path.Combine(root, @"GAMMA\ModOrganizer.exe")) && Directory.Exists(Path.Combine(root, @"ANOMALY\bin"))) return FolderState.ExistingGamma;
                foreach (string s in Directory.EnumerateFileSystemEntries(root)) count++;
                return count == 0 ? FolderState.Empty : FolderState.Foreign;
            }
            catch (Exception) { count = 1; return FolderState.Foreign; }   // cannot even read it: do not touch
        }

        public static string ForeignText(int n) { return "В папке уже есть файлы (" + n + " шт.) — нужна пустая папка"; }

        // ------------------------------------------------------------ delete guard
        static void Refuse(string path, string why)
        {
            string m = "Отказ удалять вне папки игры: " + path;
            Say(m + " (" + why + ")");
            throw new Exception(m);
        }

        static bool IsReparse(string p)
        {
            try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return false; }
        }

        // the base itself must not be a protected folder or contain one; strict ones also forbid anything inside
        static string BaseProblem(string fb)
        {
            foreach (Prot pr in Protected(null))
            {
                if (Same(fb, pr.Dir) || Under(pr.Dir, fb)) return "папка " + pr.Dir + " защищена";
                if (pr.Strict && Under(fb, pr.Dir)) return "внутри системной папки " + pr.Dir;
            }
            return null;
        }

        static string Guard(string baseDir, string path, bool checkBase)
        {
            string fb = Norm(baseDir), ft = Norm(path);
            if (fb == null || ft == null) Refuse(path, "пустой путь");
            if (!Under(ft, fb)) Refuse(path, "не внутри " + fb);
            if (checkBase)
            {
                string bp = BaseProblem(fb);
                if (bp != null) Refuse(path, bp);
            }
            for (string cur = ft; cur.Length > fb.Length; cur = Path.GetDirectoryName(cur))
                if (Directory.Exists(cur) || File.Exists(cur))
                    if (IsReparse(cur)) Refuse(path, "перенаправление (junction/symlink): " + cur);
            return ft;
        }

        // checkBase=false only for make_pack: the output folder may legitimately be the user's Desktop or Documents
        public static void DeleteDir(string baseDir, string path) { DeleteDir(baseDir, path, true); }

        public static void DeleteDir(string baseDir, string path, bool checkBase)
        {
            string ft = Guard(baseDir, path, checkBase);
            if (!Directory.Exists(ft)) return;
            try { Directory.Delete(ft, true); }
            catch (Exception)
            {
                foreach (string f in Directory.GetFiles(ft, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(ft, true);
            }
        }

        // throws unless path is inside baseDir and no part of it is a junction/symlink (used before writing into the game folder)
        public static void GuardWrite(string baseDir, string path) { Guard(baseDir, path, true); }

        // per-file variant: the base folder was checked once before (the base check is slow)
        public static void GuardWrite(string baseDir, string path, bool checkBase) { Guard(baseDir, path, checkBase); }

        public static void DeleteFile(string baseDir, string path) { DeleteFile(baseDir, path, true); }

        public static void DeleteFile(string baseDir, string path, bool checkBase)
        {
            string ft = Guard(baseDir, path, checkBase);
            File.Delete(ft);
        }
    }
}
