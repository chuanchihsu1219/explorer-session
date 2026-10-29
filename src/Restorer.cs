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

    /// <summary>Restore engine shared by every trigger. Paths that are already open are always skipped.</summary>
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

        /// <summary>Put a tab back into its original window, or open a new window if that window is gone.</summary>
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
                // The new-tab command stopped working (e.g. after a Windows update): open the remaining tabs as separate windows
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
            if (!path.Contains("\\")) return false;   // search results etc.: the path is only a display string
            if (path.StartsWith(@"\\"))
            {
                // a network path may be offline; wait at most 2 seconds
                var t = Task.Run(() => Directory.Exists(path));
                return t.Wait(2000) && t.Result;
            }
            return Directory.Exists(path);
        }

        static string Target(string path) { return path.StartsWith("::") ? "shell:" + path : path; }

        long OpenWindow(string path)
        {
            var before = new HashSet<long>(reader.Entries().Select(e => e.Top));
            // Quote only when needed: a quoted drive root "C:\" would read the backslash as an escape
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
                    // wait for the first tab to load so the new-tab command has a target
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
