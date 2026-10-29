using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ExplorerSession
{
    class RestoreResult
    {
        public int Windows, Tabs, Skipped, Failed;
        public List<string> Notes = new List<string>();
        public override string ToString()
        {
            return string.Format("restored {0} windows / {1} tabs, skipped {2}, failed {3}", Windows, Tabs, Skipped, Failed);
        }
    }

    /// <summary>還原引擎：所有觸發點共用。已經開著的路徑一律跳過。</summary>
    class Restorer
    {
        readonly ShellReader reader;
        public Restorer(ShellReader reader) { this.reader = reader; }

        public static bool WaitShellReady(ShellReader reader, int timeoutSec)
        {
            var until = DateTime.Now.AddSeconds(timeoutSec);
            while (DateTime.Now < until)
            {
                if (Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                {
                    try { reader.Reset(); reader.Entries(); return true; } catch { }
                }
                Thread.Sleep(1000);
            }
            return false;
        }

        public RestoreResult Restore(IEnumerable<WindowState> windows)
        {
            var r = new RestoreResult();
            var open = new HashSet<string>(reader.Entries().Where(e => e.Path != null).Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var win in windows)
            {
                var keep = new List<TabState>();
                foreach (var t in win.Tabs)
                {
                    if (open.Contains(t.Path ?? "")) { r.Skipped++; continue; }
                    if (!Restorable(t.Path)) { r.Skipped++; r.Notes.Add("unrestorable: " + t.Name + " [" + t.Path + "]"); continue; }
                    keep.Add(t);
                }
                if (keep.Count == 0) continue;
                int sel = keep.IndexOf(win.Tabs.ElementAtOrDefault(win.Selected));
                RestoreWindow(win, keep, Math.Max(0, sel), r);
            }
            return r;
        }

        /// <summary>把一個分頁加回原本的視窗；原視窗不在了就開新視窗。</summary>
        public RestoreResult RestoreTab(long hwnd, TabState tab)
        {
            var r = new RestoreResult();
            if (!Restorable(tab.Path)) { r.Skipped++; return r; }
            var mine = reader.Entries().Where(e => e.Top == hwnd).ToList();
            if (Native.IsWindow(new IntPtr(hwnd)) && mine.Count > 0)
            {
                if (AddTab(hwnd, tab.Path)) { r.Tabs++; return r; }
            }
            var w = new WindowState { Tabs = new List<TabState> { tab }, ShowCmd = 0 };
            RestoreWindow(w, w.Tabs, 0, r);
            return r;
        }

        void RestoreWindow(WindowState win, List<TabState> tabs, int sel, RestoreResult r)
        {
            long top = OpenWindow(tabs[0].Path);
            if (top == 0) { r.Failed += tabs.Count; r.Notes.Add("open window failed: " + tabs[0].Path); return; }
            r.Windows++; r.Tabs++;
            for (int i = 1; i < tabs.Count; i++)
            {
                if (AddTab(top, tabs[i].Path)) { r.Tabs++; continue; }
                // 新增分頁指令失效（例如 Windows 更新後）：其餘分頁各開一個視窗，至少資料夾都回得來
                r.Notes.Add("new-tab failed, falling back to separate windows");
                for (int j = i; j < tabs.Count; j++)
                {
                    if (OpenWindow(tabs[j].Path) != 0) { r.Windows++; r.Tabs++; } else r.Failed++;
                }
                sel = 0;
                break;
            }
            if (win.Rect != null && win.Rect.Length == 4 && win.ShowCmd != 0) Place(top, win);
            if (sel > 0) SelectTab(top, sel);
        }

        public static bool Restorable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith("::")) return true;
            if (!path.Contains("\\")) return false;   // 搜尋結果等：路徑只是顯示字串
            if (path.StartsWith(@"\\"))
            {
                // 網路路徑可能離線，檢查最多等 2 秒
                var t = Task.Run(() => Directory.Exists(path));
                return t.Wait(2000) && t.Result;
            }
            return Directory.Exists(path);
        }

        static string Target(string path) { return path.StartsWith("::") ? "shell:" + path : path; }

        long OpenWindow(string path)
        {
            var before = new HashSet<long>(reader.Entries().Select(e => e.Top));
            // 只有含空白才加引號：磁碟根目錄 C:\ 加了引號會變成 "C:\" 而被當成跳脫字元
            string arg = Target(path);
            if (arg.Contains(" ")) arg = "\"" + arg + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = false });
            var until = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < until)
            {
                Thread.Sleep(250);
                var fresh = reader.Entries().FirstOrDefault(e => !before.Contains(e.Top));
                if (fresh != null)
                {
                    // 等第一個分頁載入完成，後面送新增分頁指令才有對象
                    var ready = DateTime.Now.AddSeconds(3);
                    while (fresh.Path == null && DateTime.Now < ready)
                    {
                        Thread.Sleep(200);
                        fresh = reader.Entries().FirstOrDefault(e => e.Top == fresh.Top) ?? fresh;
                    }
                    return fresh.Top;
                }
            }
            return 0;
        }

        bool AddTab(long top, string path)
        {
            var mine = reader.Entries().Where(e => e.Top == top).ToList();
            var anyTab = mine.FirstOrDefault(e => e.Id != 0);
            if (anyTab == null) return false;
            var known = new HashSet<long>(mine.Select(e => e.Id));
            Native.SendMessage(new IntPtr(anyTab.Id), Native.WM_COMMAND, new IntPtr(Native.CMD_NEW_TAB), IntPtr.Zero);
            var until = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < until)
            {
                Thread.Sleep(150);
                var fresh = reader.Entries().FirstOrDefault(e => e.Top == top && !known.Contains(e.Id));
                if (fresh != null)
                {
                    ((dynamic)fresh.Com).Navigate2(Target(path));
                    Thread.Sleep(300);
                    return true;
                }
            }
            return false;
        }

        static void Place(long top, WindowState win)
        {
            var wp = new WINDOWPLACEMENT();
            wp.length = System.Runtime.InteropServices.Marshal.SizeOf(wp);
            Native.GetWindowPlacement(new IntPtr(top), ref wp);
            wp.normalPosition = new RECT { Left = win.Rect[0], Top = win.Rect[1], Right = win.Rect[2], Bottom = win.Rect[3] };
            wp.showCmd = win.ShowCmd;
            Native.SetWindowPlacement(new IntPtr(top), ref wp);
        }

        static void SelectTab(long top, int index)
        {
            Thread.Sleep(400);
            try
            {
                var items = ShellReader.TabItems(top);
                if (index < items.Count)
                    ((SelectionItemPattern)items[index].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            }
            catch (Exception ex) { Store.Log("select tab failed: " + ex.Message); }
        }
    }
}
