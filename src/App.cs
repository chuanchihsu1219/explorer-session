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
            // Simulations are executed by the resident instance: when launched from a packaged (MSIX) app such as
            // Claude Desktop, writes to AppData are redirected to a private copy the resident cannot see.
            if (cmd == "--simulate-unclean-logon") return Signal(SimUncleanEvent) ? 0 : 3;
            if (cmd == "--simulate-shell-restart") return Signal(SimCrashEvent) ? 0 : 3;
            if (cmd == "--quit") return Signal(QuitEvent) ? 0 : 3;

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return Signal(ActivateEvent) ? 0 : 3;   // already running: ask it to do the left-click action
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

    public class Settings
    {
        public bool AutoStartDefaultApplied { get; set; }
    }

    /// <summary>
    /// The resident instance. The UI thread only owns the tray icon; capture and restore all run on one
    /// background thread, so they never interleave.
    /// </summary>
    class Resident : ApplicationContext
    {
        const int TickSeconds = 15;
        static readonly TimeSpan StableFor = TimeSpan.FromMinutes(2);
        const int MaxClosed = 20;

        static readonly string AppName = L.T("檔案總管保存", "Explorer Session");

        readonly NotifyIcon icon;
        readonly SynchronizationContext ui;
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        readonly System.Windows.Forms.Timer timer;
        readonly MessageWindow msgWindow;

        // Background thread only
        ShellReader reader;
        Restorer restorer;
        Snapshot last;
        string lastLiveKey, seenSig, historySig;
        DateTime seenSince;
        int handledShellPid;
        Snapshot pendingRestore;   // after a crash or unclean shutdown, until it has been restored successfully
        List<ClosedItem> closed = new List<ClosedItem>();

        // Shared between UI and background (simple reads/writes only)
        volatile bool paused;
        volatile string status = L.T("啟動中", "starting");

        public Resident()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "worker" };
            // Must be STA: on an MTA thread QueryService cannot return IShellBrowser, every tab id becomes 0
            // and closed tabs are never detected (verified 2026-10-29).
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();

            icon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
                Text = AppName,
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

            ApplyAutoStartDefault();
            Post(Startup);
            timer = new System.Windows.Forms.Timer { Interval = TickSeconds * 1000 };
            timer.Tick += (s, e) => Post(Tick);
            timer.Start();
        }

        // ---------- background thread ----------

        void Post(Action a) { queue.Add(a); }

        void WorkerLoop()
        {
            reader = new ShellReader();
            restorer = new Restorer(reader);
            foreach (var a in queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Store.Log("error: " + ex); reader.Reset(); }   // no error may kill the resident
            }
        }

        void Startup()
        {
            closed = Store.Read<List<ClosedItem>>(Store.ClosedPath) ?? new List<ClosedItem>();
            var boot = Native.BootTime();
            var prev = Store.Read<SessionInfo>(Store.SessionPath);
            bool newBoot = prev == null || Math.Abs((boot - Parse(prev.Boot)).TotalMinutes) > 2;
            Store.Log("startup: boot=" + boot.ToString(Store.TimeFormat) + " newBoot=" + newBoot + " prevClean=" + (prev != null && prev.CleanExit) + " lang=" + (L.Zh ? "zh" : "en"));
            if (newBoot && prev != null && !prev.CleanExit)
            {
                var live = Store.Read<Snapshot>(Store.LivePath);
                if (live != null && live.Windows.Count > 0)
                {
                    Store.SaveHistory(live, "unclean");
                    pendingRestore = live;
                    // Let Windows' own "restore previous folder windows" run first, then fill in what it missed.
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
            RunPendingRestore(L.T("不正常關機後還原", "Restored after an unclean shutdown"));
        }

        void Tick()
        {
            if (paused) return;
            int pid = Native.ShellPid();
            if (pid == 0) return;   // no taskbar: Explorer is restarting, wait for TaskbarCreated
            if (last != null && last.ShellPid != pid && handledShellPid != pid) { OnShellRestart(false); return; }

            var snap = reader.Capture();
            if (last != null && last.ShellPid == snap.ShellPid) DetectClosed(last, snap);
            Commit(snap);
        }

        /// <summary>Write live.json, advance the history stability check, refresh the tray tooltip.</summary>
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
            status = string.Format(L.T("{0:HH:mm:ss} · {1} 窗 {2} 頁", "{0:HH:mm:ss} · {1} windows, {2} tabs"), DateTime.Now, snap.Windows.Count, snap.TabCount);
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
                    if (Native.IsWindow(new IntPtr(pw.Hwnd))) continue;   // just missed in this round
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

        /// <summary>Explorer restarted (crash or manual restart): pin the previous Explorer's last state, wait for the shell, restore.</summary>
        void OnShellRestart(bool simulated)
        {
            if (last == null) return;   // received right after our own start: that is a logon, Startup handles it
            int pid = Native.ShellPid();
            if (!simulated && pid != 0 && pid == handledShellPid) return;
            Store.Log("shell restart detected (simulated=" + simulated + ", old pid=" + last.ShellPid + ", new pid=" + pid + ")");
            if (last.Windows.Count > 0)
            {
                Store.SaveHistory(last, "precrash");
                pendingRestore = last;
            }
            ui.Post(_ => { icon.Visible = false; icon.Visible = true; }, null);   // make sure the icon is on the new taskbar
            if (!Restorer.WaitShellReady(reader, 60)) { Store.Log("shell not ready after 60s"); return; }
            Thread.Sleep(3000);
            handledShellPid = Native.ShellPid();
            RunPendingRestore(L.T("檔案總管重開後還原", "Restored after File Explorer restarted"));
            var snap = reader.Capture();   // a new Explorer run: no closed-window detection against the old one
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
                ? RestoredText(r)
                : L.T("原本的資料夾都已經開著，不需要還原", "All of those folders are already open"));
        }

        static string RestoredText(RestoreResult r)
        {
            return string.Format(L.T("已還原 {0} 個視窗、{1} 個分頁", "Restored {0} window(s), {1} tab(s)"), r.Windows, r.Tabs);
        }

        /// <summary>Left click / Start menu shortcut: an unfinished restore first, otherwise reopen the most recently closed item.</summary>
        void PrimaryAction()
        {
            if (pendingRestore != null) { RunPendingRestore(L.T("還原", "Restore")); return; }
            if (closed.Count == 0) { Balloon(AppName, L.T("沒有最近關閉的視窗或分頁", "Nothing was closed recently")); return; }
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
            if (r.Tabs == 0) Balloon(AppName, L.T("這個項目已經開著，或資料夾已不存在", "That item is already open, or the folder no longer exists"));
            Tick();
        }

        void RestoreHistory(string file)
        {
            var snap = Store.Read<Snapshot>(file);
            if (snap == null) return;
            var r = restorer.Restore(snap.Windows);
            Store.Log("restore history " + Path.GetFileName(file) + ": " + r);
            Balloon(L.T("從歷史還原", "Restore from history"),
                r.Tabs > 0 ? RestoredText(r) : L.T("這份紀錄裡的資料夾都已經開著", "All folders in that snapshot are already open"));
        }

        // ---------- shutdown and exit ----------

        void OnSessionEnding()
        {
            var done = new ManualResetEventSlim();
            Post(() =>
            {
                try
                {
                    var snap = reader.Capture();
                    // Explorer may already be closing windows: if we now see fewer tabs, keep the previous round
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
            string t = (paused ? L.T("已暫停 · ", "Paused · ") : AppName + " · ") + status;
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
            m.Items.Add(new ToolStripMenuItem((paused ? L.T("已暫停 · ", "Paused · ") : L.T("上次快照 ", "Last snapshot ")) + status) { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(L.T("找回最近關閉的（同左鍵）", "Reopen last closed (same as left click)"), null, (s, e) => Post(PrimaryAction));

            var recent = new ToolStripMenuItem(L.T("最近關閉", "Recently closed"));
            var closedCopy = closed.ToList();
            foreach (var c in closedCopy)
            {
                var item = c;
                string when = Parse(c.Time).ToString("HH:mm");
                string label = c.Kind == "tab"
                    ? when + L.T(" 分頁 · ", " tab · ") + c.Window.Tabs[0].Name
                    : string.Format(L.T("{0} 視窗 · {1} 個分頁（{2}）", "{0} window · {1} tab(s) ({2})"), when, c.Window.Tabs.Count, Snapshot.Summary(new[] { c.Window }, 3));
                recent.DropDownItems.Add(label, null, (s, e) => Post(() => ReopenClosed(item)));
            }
            if (closedCopy.Count == 0) recent.DropDownItems.Add(new ToolStripMenuItem(L.T("（沒有）", "(none)")) { Enabled = false });
            m.Items.Add(recent);

            var hist = new ToolStripMenuItem(L.T("從歷史還原", "Restore from history"));
            foreach (var h in Store.ListHistory().Take(40))
            {
                var snap = Store.Read<Snapshot>(h.File);
                if (snap == null) continue;
                string file = h.File;
                string tag = h.Kind == "precrash" ? L.T("⚠ 當掉前 ", "⚠ before crash ")
                           : h.Kind == "unclean" ? L.T("⚠ 不正常關機前 ", "⚠ before unclean shutdown ")
                           : h.Kind == "preshutdown" ? L.T("關機前 ", "before shutdown ") : "";
                string when = h.Time.Date == DateTime.Today ? h.Time.ToString("HH:mm") : h.Time.ToString("MM/dd HH:mm");
                string label = string.Format(L.T("{0} {1}· {2} 窗 {3} 頁（{4}）", "{0} {1}· {2} windows, {3} tabs ({4})"),
                    when, tag, snap.Windows.Count, snap.TabCount, Snapshot.Summary(snap.Windows, 3));
                hist.DropDownItems.Add(label, null, (s, e) => Post(() => RestoreHistory(file)));
            }
            if (hist.DropDownItems.Count == 0) hist.DropDownItems.Add(new ToolStripMenuItem(L.T("（還沒有）", "(none yet)")) { Enabled = false });
            m.Items.Add(hist);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem(L.T("暫停記錄", "Pause recording"), null, (s, e) => { paused = !paused; SetTooltip(); }) { Checked = paused });
            m.Items.Add(new ToolStripMenuItem(L.T("登入時自動啟動", "Start at sign-in"), null, (s, e) => SetAutoStart(!AutoStartEnabled(), true)) { Checked = AutoStartEnabled() });
            m.Items.Add(L.T("開啟資料夾", "Open data folder"), null, (s, e) => Process.Start("explorer.exe", "\"" + Store.Dir + "\""));
            m.Items.Add(L.T("結束", "Exit"), null, (s, e) => ExitApp());
        }

        // ---------- start at sign-in (Run key + Start menu shortcut) ----------
        // Written by the resident itself: it is launched through explorer.exe, outside any MSIX container,
        // so the registry write really takes effect.

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "ExplorerSession";
        static string ExePath { get { return Application.ExecutablePath; } }
        static readonly string ShortcutName = L.T("還原檔案總管", "Restore File Explorer");
        static string ShortcutPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName + ".lnk"); } }

        static bool AutoStartEnabled()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && string.Equals(k.GetValue(RunValue) as string, "\"" + ExePath + "\"", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Start at sign-in is on by default: enable it once on first run; after that the user's choice wins.</summary>
        void ApplyAutoStartDefault()
        {
            string path = Path.Combine(Store.Dir, "settings.json");
            var s = Store.Read<Settings>(path) ?? new Settings();
            if (s.AutoStartDefaultApplied) return;
            if (!AutoStartEnabled()) SetAutoStart(true, true);
            s.AutoStartDefaultApplied = true;
            Store.Write(path, s);
        }

        void SetAutoStart(bool on, bool notify)
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
                    lnk.Description = L.T("找回檔案總管的視窗與分頁（程式沒在跑時會先啟動它）",
                                          "Reopen File Explorer windows and tabs (starts the tool if it is not running)");
                    lnk.Save();
                }
                else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                Store.Log("auto start " + (on ? "enabled" : "disabled"));
                if (notify)
                    Balloon(AppName, on
                        ? string.Format(L.T("已設定登入時自動啟動，開始功能表也有「{0}」", "Will start at sign-in. \"{0}\" is also in the Start menu"), ShortcutName)
                        : L.T("已取消登入時自動啟動", "Will no longer start at sign-in"));
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

    /// <summary>Hidden top-level window that receives the TaskbarCreated broadcast Explorer sends when it (re)starts.</summary>
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
