using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ExplorerSession
{
    static class Program
    {
        const string MutexName = @"Local\ExplorerSession.Instance";
        public const string ActivateEvent = @"Local\ExplorerSession.Activate";
        public const string SimCrashEvent = @"Local\ExplorerSession.SimulateShellRestart";
        public const string QuitEvent = @"Local\ExplorerSession.Quit";
        public const string SimUncleanEvent = @"Local\ExplorerSession.SimulateUncleanLogon";

        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDpiAwarenessContext(Native.DPI_PER_MONITOR_V2);
            Store.Init();
            string cmd = args.Length > 0 ? args[0] : "";

            if (cmd == "--snapshot-once")
            {
                var snap = new ShellReader().Capture();
                Store.Write(args.Length > 1 ? args[1] : Path.Combine(Store.Dir, "snapshot-once.json"), snap);
                return 0;
            }
            if (cmd == "--restore" && args.Length > 1)
            {
                var snap = Store.Read<Snapshot>(args[1]);
                if (snap == null) return 2;
                var r = new Restorer(new ShellReader()).Restore(snap.Windows);
                Store.Log("--restore " + args[1] + ": " + r + (r.Notes.Count > 0 ? " | " + string.Join(" | ", r.Notes) : ""));
                return r.Failed == 0 ? 0 : 1;
            }
            // 模擬指令一律交給常駐程式執行：從 Claude Desktop 的 shell 寫 AppData 會被 MSIX 容器導向私有副本，常駐程式看不到
            if (cmd == "--simulate-unclean-logon") return Signal(SimUncleanEvent) ? 0 : 3;
            if (cmd == "--simulate-shell-restart") return Signal(SimCrashEvent) ? 0 : 3;
            if (cmd == "--quit") return Signal(QuitEvent) ? 0 : 3;

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return Signal(ActivateEvent) ? 0 : 3;   // 已有常駐：請它執行左鍵動作
                Application.EnableVisualStyles();
                Application.Run(new Resident());
                return 0;
            }
        }

        static bool Signal(string name)
        {
            EventWaitHandle h;
            if (!EventWaitHandle.TryOpenExisting(name, out h)) return false;
            h.Set(); h.Dispose();
            return true;
        }
    }

    /// <summary>常駐程式：UI 執行緒只管系統匣；讀取與還原全部排進同一條背景執行緒，彼此不會交錯。</summary>
    class Resident : ApplicationContext
    {
        const int TickSeconds = 15;
        static readonly TimeSpan StableFor = TimeSpan.FromMinutes(2);
        const int MaxClosed = 20;
        static readonly string[] Pinned = { "precrash", "preshutdown", "unclean" };

        readonly NotifyIcon icon;
        readonly SynchronizationContext ui;
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        readonly System.Windows.Forms.Timer timer;
        readonly MessageWindow msgWindow;

        // 以下只在背景執行緒存取
        ShellReader reader;
        Restorer restorer;
        Snapshot last;
        string lastLiveKey, seenSig, historySig;
        DateTime seenSince;
        int handledShellPid;
        Snapshot pendingRestore;   // 當掉或不正常關機後、還沒成功還原的那一份
        List<ClosedItem> closed = new List<ClosedItem>();

        // UI 與背景共用（只做簡單讀寫）
        volatile bool paused;
        volatile string status = "啟動中";

        public Resident()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "worker" };
            // 必須是 STA：在 MTA 上 QueryService 取不到 IShellBrowser，分頁 ID 全部變 0，關閉分頁就偵測不到（10-29 實測）
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();

            icon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
                Text = "檔案總管保存",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            icon.ContextMenuStrip.Opening += (s, e) => BuildMenu();
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Post(PrimaryAction); };

            msgWindow = new MessageWindow();
            msgWindow.TaskbarCreated += () => Post(() => OnShellRestart(false));
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => OnSessionEnding();

            WatchEvent(Program.ActivateEvent, () => Post(PrimaryAction));
            WatchEvent(Program.SimCrashEvent, () => Post(() => OnShellRestart(true)));
            WatchEvent(Program.QuitEvent, () => ui.Post(_ => ExitApp(), null));
            WatchEvent(Program.SimUncleanEvent, () => Post(() =>
            {
                Store.Log("simulated unclean logon");
                var live = Store.Read<Snapshot>(Store.LivePath);
                if (live == null || live.Windows.Count == 0) return;
                Store.SaveHistory(live, "unclean");
                pendingRestore = live;
                RestoreAfterUncleanLogon();
            }));

            Post(Startup);
            timer = new System.Windows.Forms.Timer { Interval = TickSeconds * 1000 };
            timer.Tick += (s, e) => Post(Tick);
            timer.Start();
        }

        // ---------- 背景執行緒 ----------

        void Post(Action a) { queue.Add(a); }

        void WorkerLoop()
        {
            reader = new ShellReader();
            restorer = new Restorer(reader);
            foreach (var a in queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Store.Log("error: " + ex); reader.Reset(); }   // 任何錯誤都不能讓常駐程式死掉
            }
        }

        void Startup()
        {
            closed = Store.Read<List<ClosedItem>>(Store.ClosedPath) ?? new List<ClosedItem>();
            var boot = Native.BootTime();
            var prev = Store.Read<SessionInfo>(Store.SessionPath);
            bool newBoot = prev == null || Math.Abs((boot - Parse(prev.Boot)).TotalMinutes) > 2;
            Store.Log("startup: boot=" + boot.ToString(Store.TimeFormat) + " newBoot=" + newBoot + " prevClean=" + (prev != null && prev.CleanExit));
            if (newBoot && prev != null && !prev.CleanExit)
            {
                var live = Store.Read<Snapshot>(Store.LivePath);
                if (live != null && live.Windows.Count > 0)
                {
                    Store.SaveHistory(live, "unclean");
                    pendingRestore = live;
                    // 先讓 Windows 內建還原跑完，再補它沒開的
                    new System.Threading.Timer(_ => Post(RestoreAfterUncleanLogon), null, 30000, Timeout.Infinite);
                }
            }
            Store.Write(Store.SessionPath, new SessionInfo { Boot = boot.ToString(Store.TimeFormat), CleanExit = false });
            Tick();
        }

        void RestoreAfterUncleanLogon()
        {
            if (pendingRestore == null) return;
            Restorer.WaitShellReady(reader, 60);
            RunPendingRestore("不正常關機後還原");
        }

        void Tick()
        {
            if (paused) return;
            int pid = Native.ShellPid();
            if (pid == 0) return;   // 工作列不在：檔案總管正在重開，等 TaskbarCreated
            if (last != null && last.ShellPid != pid && handledShellPid != pid) { OnShellRestart(false); return; }

            var snap = reader.Capture();
            if (last != null && last.ShellPid == snap.ShellPid) DetectClosed(last, snap);
            Commit(snap);
        }

        /// <summary>寫 live.json、推進歷史紀錄的穩定判斷、更新系統匣提示。</summary>
        void Commit(Snapshot snap)
        {
            string key = snap.ContentKey();
            if (key != lastLiveKey) { Store.Write(Store.LivePath, snap); lastLiveKey = key; }

            string sig = snap.Signature();
            if (sig != seenSig) { seenSig = sig; seenSince = DateTime.Now; }
            else if (DateTime.Now - seenSince >= StableFor && sig != historySig && snap.Windows.Count > 0)
            {
                Store.SaveHistory(snap, "stable");
                historySig = sig;
            }
            last = snap;
            status = string.Format("{0:HH:mm:ss} · {1} 窗 {2} 頁", DateTime.Now, snap.Windows.Count, snap.TabCount);
            ui.Post(_ => SetTooltip(), null);
        }

        void DetectClosed(Snapshot prev, Snapshot cur)
        {
            var curWins = cur.Windows.ToDictionary(w => w.Hwnd);
            bool changed = false;
            foreach (var pw in prev.Windows)
            {
                WindowState cw;
                if (!curWins.TryGetValue(pw.Hwnd, out cw))
                {
                    if (Native.IsWindow(new IntPtr(pw.Hwnd))) continue;   // 只是這一輪沒讀到
                    closed.Insert(0, new ClosedItem { Time = cur.Time, Kind = "window", Window = pw });
                    changed = true;
                    continue;
                }
                var ids = new HashSet<long>(cw.Tabs.Select(t => t.Id));
                foreach (var t in pw.Tabs.Where(t => t.Id != 0 && !ids.Contains(t.Id)))
                {
                    closed.Insert(0, new ClosedItem
                    {
                        Time = cur.Time, Kind = "tab",
                        Window = new WindowState { Hwnd = pw.Hwnd, Tabs = new List<TabState> { t } }
                    });
                    changed = true;
                }
            }
            if (!changed) return;
            if (closed.Count > MaxClosed) closed.RemoveRange(MaxClosed, closed.Count - MaxClosed);
            Store.Write(Store.ClosedPath, closed);
        }

        /// <summary>檔案總管重開（當掉或手動重啟）：釘選上一個檔案總管的最後狀態，等殼層就緒後自動還原。</summary>
        void OnShellRestart(bool simulated)
        {
            if (last == null) return;   // 程式剛啟動就收到：那是登入，交給 Startup 處理
            int pid = Native.ShellPid();
            if (!simulated && pid != 0 && pid == handledShellPid) return;
            Store.Log("shell restart detected (simulated=" + simulated + ", old pid=" + last.ShellPid + ", new pid=" + pid + ")");
            if (last.Windows.Count > 0)
            {
                Store.SaveHistory(last, "precrash");
                pendingRestore = last;
            }
            ui.Post(_ => { icon.Visible = false; icon.Visible = true; }, null);   // 確保圖示掛回新的工作列
            if (!Restorer.WaitShellReady(reader, 60)) { Store.Log("shell not ready after 60s"); return; }
            Thread.Sleep(3000);
            handledShellPid = Native.ShellPid();
            RunPendingRestore("檔案總管重開後還原");
            var snap = reader.Capture();   // 新的一次執行：不做關閉偵測
            Commit(snap);
        }

        void RunPendingRestore(string title)
        {
            var target = pendingRestore;
            if (target == null) return;
            var r = restorer.Restore(target.Windows);
            Store.Log(title + ": " + r + (r.Notes.Count > 0 ? " | " + string.Join(" | ", r.Notes) : ""));
            if (r.Failed == 0) pendingRestore = null;
            Balloon(title, r.Tabs > 0
                ? string.Format("已還原 {0} 個視窗、{1} 個分頁", r.Windows, r.Tabs)
                : "原本的資料夾都已經開著，不需要還原");
        }

        /// <summary>左鍵／開始功能表捷徑：有沒還原成功的就先還原它，否則找回最近關閉的一筆。</summary>
        void PrimaryAction()
        {
            if (pendingRestore != null) { RunPendingRestore("還原"); return; }
            if (closed.Count == 0) { Balloon("檔案總管保存", "沒有最近關閉的視窗或分頁"); return; }
            ReopenClosed(closed[0]);
        }

        void ReopenClosed(ClosedItem item)
        {
            var r = item.Kind == "tab"
                ? restorer.RestoreTab(item.Window.Hwnd, item.Window.Tabs[0])
                : restorer.Restore(new[] { item.Window });
            closed.Remove(item);
            Store.Write(Store.ClosedPath, closed);
            Store.Log("reopen closed " + item.Kind + ": " + r);
            if (r.Tabs == 0) Balloon("檔案總管保存", "這個項目已經開著，或資料夾已不存在");
            Tick();
        }

        void RestoreHistory(string file)
        {
            var snap = Store.Read<Snapshot>(file);
            if (snap == null) return;
            var r = restorer.Restore(snap.Windows);
            Store.Log("restore history " + Path.GetFileName(file) + ": " + r);
            Balloon("從歷史還原", r.Tabs > 0 ? string.Format("已還原 {0} 個視窗、{1} 個分頁", r.Windows, r.Tabs) : "這份紀錄裡的資料夾都已經開著");
        }

        // ---------- 關機與結束 ----------

        void OnSessionEnding()
        {
            var done = new ManualResetEventSlim();
            Post(() =>
            {
                try
                {
                    var snap = reader.Capture();
                    // 關機時檔案總管可能已經在收視窗：讀到的比上一輪少，就用上一輪
                    if (last != null && snap.TabCount < last.TabCount) snap = last;
                    if (snap.Windows.Count > 0) { Store.Write(Store.LivePath, snap); Store.SaveHistory(snap, "preshutdown"); }
                    MarkCleanExit();
                }
                finally { done.Set(); }
            });
            done.Wait(4000);
        }

        void MarkCleanExit()
        {
            var s = Store.Read<SessionInfo>(Store.SessionPath) ?? new SessionInfo { Boot = Native.BootTime().ToString(Store.TimeFormat) };
            s.CleanExit = true;
            Store.Write(Store.SessionPath, s);
            Store.Log("clean exit");
        }

        void ExitApp()
        {
            var done = new ManualResetEventSlim();
            Post(() => { try { MarkCleanExit(); } finally { done.Set(); } });
            done.Wait(3000);
            icon.Visible = false;
            ExitThread();
        }

        // ---------- UI ----------

        void SetTooltip()
        {
            string t = (paused ? "已暫停 · " : "檔案總管保存 · ") + status;
            icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void Balloon(string title, string text)
        {
            ui.Post(_ => icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info), null);
        }

        void BuildMenu()
        {
            var m = icon.ContextMenuStrip;
            m.Items.Clear();
            m.Items.Add(new ToolStripMenuItem((paused ? "已暫停 · " : "上次快照 ") + status) { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("找回最近關閉的（同左鍵）", null, (s, e) => Post(PrimaryAction));

            var recent = new ToolStripMenuItem("最近關閉");
            var closedCopy = closed.ToList();
            foreach (var c in closedCopy)
            {
                var item = c;
                string when = Parse(c.Time).ToString("HH:mm");
                string label = c.Kind == "tab"
                    ? when + " 分頁 · " + c.Window.Tabs[0].Name
                    : string.Format("{0} 視窗 · {1} 個分頁（{2}）", when, c.Window.Tabs.Count, Snapshot.Summary(new[] { c.Window }, 3));
                recent.DropDownItems.Add(label, null, (s, e) => Post(() => ReopenClosed(item)));
            }
            if (closedCopy.Count == 0) recent.DropDownItems.Add(new ToolStripMenuItem("（沒有）") { Enabled = false });
            m.Items.Add(recent);

            var hist = new ToolStripMenuItem("從歷史還原");
            foreach (var h in Store.ListHistory().Take(40))
            {
                var snap = Store.Read<Snapshot>(h.File);
                if (snap == null) continue;
                string file = h.File;
                string tag = h.Kind == "precrash" ? "⚠ 當掉前 " : h.Kind == "unclean" ? "⚠ 不正常關機前 " : h.Kind == "preshutdown" ? "關機前 " : "";
                string when = h.Time.Date == DateTime.Today ? h.Time.ToString("HH:mm") : h.Time.ToString("MM/dd HH:mm");
                string label = string.Format("{0} {1}· {2} 窗 {3} 頁（{4}）", when, tag, snap.Windows.Count, snap.TabCount, Snapshot.Summary(snap.Windows, 3));
                hist.DropDownItems.Add(label, null, (s, e) => Post(() => RestoreHistory(file)));
            }
            if (hist.DropDownItems.Count == 0) hist.DropDownItems.Add(new ToolStripMenuItem("（還沒有）") { Enabled = false });
            m.Items.Add(hist);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("暫停記錄", null, (s, e) => { paused = !paused; SetTooltip(); }) { Checked = paused });
            m.Items.Add(new ToolStripMenuItem("登入時自動啟動", null, (s, e) => SetAutoStart(!AutoStartEnabled())) { Checked = AutoStartEnabled() });
            m.Items.Add("開啟資料夾", null, (s, e) => Process.Start("explorer.exe", "\"" + Store.Dir + "\""));
            m.Items.Add("結束", null, (s, e) => ExitApp());
        }

        // ---------- 登入時自動啟動（Run 機碼＋開始功能表捷徑）----------
        // 由常駐程式自己寫：它是經由 explorer.exe 啟動、在 MSIX 容器外，寫入才會真的生效

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "ExplorerSession";
        static string ExePath { get { return Application.ExecutablePath; } }
        static string ShortcutPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "還原檔案總管.lnk"); } }

        static bool AutoStartEnabled()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && string.Equals(k.GetValue(RunValue) as string, "\"" + ExePath + "\"", StringComparison.OrdinalIgnoreCase);
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + ExePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
                if (on)
                {
                    dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    dynamic lnk = ws.CreateShortcut(ShortcutPath);
                    lnk.TargetPath = ExePath;
                    lnk.Description = "找回檔案總管的視窗與分頁（程式沒在跑時會先啟動它）";
                    lnk.Save();
                }
                else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                Store.Log("auto start " + (on ? "enabled" : "disabled"));
                Balloon("檔案總管保存", on ? "已設定登入時自動啟動，開始功能表也有「還原檔案總管」" : "已取消登入時自動啟動");
            }
            catch (Exception ex) { Store.Log("auto start toggle failed: " + ex.Message); }
        }

        void WatchEvent(string name, Action onSignal)
        {
            var h = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            var t = new Thread(() => { while (true) { h.WaitOne(); onSignal(); } }) { IsBackground = true };
            t.Start();
        }

        static DateTime Parse(string s)
        {
            DateTime t;
            return DateTime.TryParse(s, out t) ? t : DateTime.MinValue;
        }
    }

    /// <summary>隱藏的頂層視窗：收 TaskbarCreated 廣播（檔案總管重開時發出）。</summary>
    class MessageWindow : NativeWindow
    {
        readonly int wmTaskbarCreated = (int)Native.RegisterWindowMessage("TaskbarCreated");
        public event Action TaskbarCreated;
        public MessageWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == wmTaskbarCreated && TaskbarCreated != null) TaskbarCreated();
            base.WndProc(ref m);
        }
    }
}
