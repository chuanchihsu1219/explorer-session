using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

namespace ExplorerSession
{
    /// <summary>一筆殼層視窗清單項目＝一個分頁。</summary>
    class TabEntry
    {
        public object Com;       // IWebBrowser2（dynamic 呼叫用）
        public long Top;
        public long Id;
        public string Path;
        public string Name;
    }

    /// <summary>讀取目前所有檔案總管分頁，並組成快照。必須只在同一個執行緒上使用。</summary>
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
                catch { }   // 正在關閉的分頁會丟例外，略過
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
        /// 依畫面上的分頁標籤順序排列（名稱對不上的放最後、維持原順序），並找出選中的分頁。
        /// 選中的分頁只能從 UIA 判斷：每個分頁的 ShellTabWindowClass 都回報 visible。
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
            // 分頁列在標題列裡；只往下找 Tab 控制項，避免走進檔案清單（大型資料夾會很慢）
            var tab = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
            if (tab == null) return new List<AutomationElement>();
            return tab.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                      .Cast<AutomationElement>().ToList();
        }
    }
}
