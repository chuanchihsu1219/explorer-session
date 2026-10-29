using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

namespace ExplorerSession
{
    /// <summary>One entry of the shell windows list = one tab.</summary>
    class TabEntry
    {
        public object Com;       // IWebBrowser2 (called through dynamic)
        public long Top;
        public long Id;
        public string Path;
        public string Name;
    }

    /// <summary>Reads every File Explorer tab and builds a snapshot. Use from one thread only.</summary>
    class ShellReader
    {
        object shell;

        dynamic ShellApp()
        {
            if (shell == null) shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            return shell;
        }

        public void Reset() { shell = null; }

        public List<TabEntry> Entries()
        {
            var list = new List<TabEntry>();
            dynamic wins;
            try { wins = ShellApp().Windows(); }
            catch { Reset(); wins = ShellApp().Windows(); }
            int n = wins.Count;
            for (int i = 0; i < n; i++)
            {
                object o;
                try { o = wins.Item(i); } catch { continue; }
                if (o == null) continue;
                dynamic w = o;
                try
                {
                    string full = w.FullName;
                    if (full == null || !full.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = new TabEntry { Com = o, Top = Convert.ToInt64((object)w.HWND), Name = w.LocationName };
                    try { e.Path = w.Document.Folder.Self.Path; } catch { }
                    e.Id = Native.TabHandle(o);
                    list.Add(e);
                }
                catch { }   // a tab that is closing throws; skip it
            }
            return list;
        }

        public Snapshot Capture()
        {
            var snap = new Snapshot
            {
                Time = DateTime.Now.ToString(Store.TimeFormat),
                Kind = "live",
                ShellPid = Native.ShellPid(),
                Boot = Native.BootTime().ToString(Store.TimeFormat)
            };
            foreach (var g in Entries().GroupBy(e => e.Top))
            {
                var tabs = g.Select(e => new TabState { Id = e.Id, Path = e.Path, Name = e.Name }).ToList();
                var w = new WindowState { Hwnd = g.Key };
                TabState selected;
                w.Tabs = VisualOrder(g.Key, tabs, out selected);
                w.Selected = Math.Max(0, w.Tabs.IndexOf(selected));
                var wp = new WINDOWPLACEMENT();
                wp.length = System.Runtime.InteropServices.Marshal.SizeOf(wp);
                if (Native.GetWindowPlacement(new IntPtr(g.Key), ref wp))
                {
                    w.ShowCmd = wp.showCmd;
                    w.Rect = new[] { wp.normalPosition.Left, wp.normalPosition.Top, wp.normalPosition.Right, wp.normalPosition.Bottom };
                }
                snap.Windows.Add(w);
            }
            return snap;
        }

        /// <summary>
        /// Orders tabs as they appear on screen (unmatched names go last, in their original order) and finds the selected tab.
        /// The selected tab can only be read through UI Automation: every tab's ShellTabWindowClass reports visible.
        /// </summary>
        static List<TabState> VisualOrder(long top, List<TabState> tabs, out TabState selected)
        {
            selected = tabs.FirstOrDefault();
            if (tabs.Count < 2) return tabs;
            List<AutomationElement> items;
            try { items = TabItems(top); }
            catch { return tabs; }
            var rest = new List<TabState>(tabs);
            var ordered = new List<TabState>();
            foreach (var it in items)
            {
                string n;
                bool isSel = false;
                try
                {
                    n = it.Current.Name ?? "";
                    object p;
                    if (it.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) isSel = ((SelectionItemPattern)p).Current.IsSelected;
                }
                catch { continue; }
                var hit = rest.FirstOrDefault(t => t.Name == n) ?? rest.FirstOrDefault(t => !string.IsNullOrEmpty(t.Name) && n.StartsWith(t.Name));
                if (hit == null) continue;
                ordered.Add(hit); rest.Remove(hit);
                if (isSel) selected = hit;
            }
            ordered.AddRange(rest);
            return ordered;
        }

        public static List<AutomationElement> TabItems(long top)
        {
            var root = AutomationElement.FromHandle(new IntPtr(top));
            // Tabs live in the title bar; find the Tab control first so we never walk the file list (slow in large folders)
            var tab = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
            if (tab == null) return new List<AutomationElement>();
            return tab.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                      .Cast<AutomationElement>().ToList();
        }
    }
}
