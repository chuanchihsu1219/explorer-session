using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ExplorerSession
{
    public class TabState
    {
        public long Id { get; set; }          // ShellTabWindowClass 代號；只在同一次檔案總管執行內有意義
        public string Path { get; set; }      // 檔案系統路徑，或 ::{CLSID}
        public string Name { get; set; }
    }

    public class WindowState
    {
        public long Hwnd { get; set; }
        public List<TabState> Tabs { get; set; }
        public int ShowCmd { get; set; }
        public int[] Rect { get; set; }       // normal position（per-monitor v2 實體像素）
        public int Selected { get; set; }     // Tabs 內的索引
        public WindowState() { Tabs = new List<TabState>(); }
    }

    public class Snapshot
    {
        public string Time { get; set; }
        public string Kind { get; set; }      // live / stable / precrash / preshutdown / unclean
        public int ShellPid { get; set; }
        public string Boot { get; set; }
        public List<WindowState> Windows { get; set; }
        public Snapshot() { Windows = new List<WindowState>(); }

        public int TabCount { get { return Windows.Sum(w => w.Tabs.Count); } }

        /// <summary>組成簽章：只看視窗與分頁的身分，不看路徑。</summary>
        public string Signature()
        {
            return string.Join(";", Windows.OrderBy(w => w.Hwnd).Select(w =>
                w.Hwnd + ":" + string.Join(",", w.Tabs.Select(t => t.Id).OrderBy(i => i))));
        }

        /// <summary>內容鍵：路徑、順序、選中分頁、位置；用來判斷 live.json 要不要覆寫。</summary>
        public string ContentKey()
        {
            var sb = new StringBuilder();
            foreach (var w in Windows)
            {
                sb.Append(w.Hwnd).Append('|').Append(w.ShowCmd).Append('|').Append(w.Selected).Append('|');
                if (w.Rect != null) sb.Append(string.Join(",", w.Rect));
                foreach (var t in w.Tabs) sb.Append('|').Append(t.Id).Append('=').Append(t.Path);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        public static string Summary(IEnumerable<WindowState> windows, int maxNames)
        {
            var names = windows.SelectMany(w => w.Tabs).Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            string s = string.Join("、", names.Take(maxNames));
            if (names.Count > maxNames) s += "…";
            return s;
        }
    }

    public class ClosedItem
    {
        public string Time { get; set; }
        public string Kind { get; set; }      // window / tab
        public WindowState Window { get; set; }   // kind=tab 時只含那一個分頁，Hwnd 是原本所在的視窗
    }

    public class SessionInfo
    {
        public string Boot { get; set; }
        public bool CleanExit { get; set; }
    }

    public class HistoryEntry
    {
        public DateTime Time;
        public string Kind;
        public string File;
        public bool Pinned { get { return Kind != "stable"; } }
    }

    static class Store
    {
        public static readonly string Dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExplorerSession");
        public static readonly string HistoryDir = System.IO.Path.Combine(Dir, "history");
        public static string LivePath { get { return System.IO.Path.Combine(Dir, "live.json"); } }
        public static string ClosedPath { get { return System.IO.Path.Combine(Dir, "closed.json"); } }
        public static string SessionPath { get { return System.IO.Path.Combine(Dir, "session.json"); } }
        static string LogPath { get { return System.IO.Path.Combine(Dir, "log.txt"); } }

        public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss";
        const string FileTimeFormat = "yyyyMMdd-HHmmss";
        static readonly object logLock = new object();

        static JavaScriptSerializer Json() { var j = new JavaScriptSerializer(); j.MaxJsonLength = 64 * 1024 * 1024; return j; }

        public static void Init() { Directory.CreateDirectory(HistoryDir); }

        public static void Write<T>(string path, T obj)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Json().Serialize(obj), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static T Read<T>(string path) where T : class
        {
            try { return File.Exists(path) ? Json().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch (Exception ex) { Log("read failed " + path + ": " + ex.Message); return null; }
        }

        public static void SaveHistory(Snapshot s, string kind)
        {
            var copy = Json().Deserialize<Snapshot>(Json().Serialize(s));
            copy.Kind = kind;
            string name = DateTime.Now.ToString(FileTimeFormat) + "-" + kind + ".json";
            Write(System.IO.Path.Combine(HistoryDir, name), copy);
            Log("history " + kind + ": " + copy.Windows.Count + " windows, " + copy.TabCount + " tabs");
            Prune(DateTime.Now);
        }

        public static List<HistoryEntry> ListHistory()
        {
            var list = new List<HistoryEntry>();
            if (!Directory.Exists(HistoryDir)) return list;
            foreach (var f in Directory.GetFiles(HistoryDir, "*.json"))
            {
                string n = System.IO.Path.GetFileNameWithoutExtension(f);
                if (n.Length < 17) continue;
                DateTime t;
                if (!DateTime.TryParseExact(n.Substring(0, 15), FileTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                list.Add(new HistoryEntry { Time = t, Kind = n.Substring(16), File = f });
            }
            return list.OrderByDescending(e => e.Time).ToList();
        }

        /// <summary>保留規則：1 小時內全留；1–24 小時每 15 分鐘留最後一份；1–7 天每天留最後一份；釘選的留 30 天。</summary>
        public static void Prune(DateTime now)
        {
            var keepBuckets = new HashSet<string>();
            foreach (var e in ListHistory())   // 由新到舊，每個區間第一個看到的就是最後一份
            {
                var age = now - e.Time;
                bool keep;
                if (e.Pinned) keep = age <= TimeSpan.FromDays(30);
                else if (age <= TimeSpan.FromHours(1)) keep = true;
                else if (age <= TimeSpan.FromHours(24))
                    keep = keepBuckets.Add("q" + e.Time.ToString("yyyyMMddHH") + (e.Time.Minute / 15));
                else if (age <= TimeSpan.FromDays(7))
                    keep = keepBuckets.Add("d" + e.Time.ToString("yyyyMMdd"));
                else keep = false;
                if (!keep) { try { File.Delete(e.File); } catch { } }
            }
        }

        public static void Log(string msg)
        {
            lock (logLock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        string old = System.IO.Path.Combine(Dir, "log.old.txt");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("MM-dd HH:mm:ss ") + msg + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(System.IO.Path.Combine(Dir, "log-error.txt"), ex + Environment.NewLine); } catch { }
                }
            }
        }
    }
}
