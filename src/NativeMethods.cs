using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ExplorerSession
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT minPosition, maxPosition;
        public RECT normalPosition;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider_
    {
        [PreserveSig] int QueryService(ref Guid sid, ref Guid iid, out IntPtr ppv);
    }

    // 只用到 GetWindow：每個分頁的 IShellBrowser 回傳該分頁專屬的 ShellTabWindowClass，
    // 切換資料夾時不會變，所以拿來當分頁 ID（2026-10-29 spike 驗證）。
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser_
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd);
    }

    static class Native
    {
        public const uint WM_COMMAND = 0x0111;
        // Explorer 內部的「新增分頁」指令，沒有官方文件；失效時還原引擎會改成各開一個視窗。
        public const int CMD_NEW_TAB = 0xA21B;

        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();

        public static readonly IntPtr DPI_PER_MONITOR_V2 = new IntPtr(-4);

        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");

        /// <summary>分頁 ID：該分頁的 ShellTabWindowClass 視窗代號；取不到回 0。</summary>
        public static long TabHandle(object webBrowser)
        {
            var sp = webBrowser as IServiceProvider_;
            if (sp == null) return 0;
            Guid sid = SID_STopLevelBrowser;
            Guid iid = typeof(IShellBrowser_).GUID;
            IntPtr p;
            if (sp.QueryService(ref sid, ref iid, out p) != 0 || p == IntPtr.Zero) return 0;
            try
            {
                var sb = Marshal.GetObjectForIUnknown(p) as IShellBrowser_;
                if (sb == null) return 0;
                IntPtr h;
                return sb.GetWindow(out h) == 0 ? h.ToInt64() : 0;
            }
            finally { Marshal.Release(p); }
        }

        public static int ShellPid()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(tray, out pid);
            return (int)pid;
        }

        static DateTime? bootTime;

        /// <summary>開機時間取 WMI 的 LastBootUpTime：用 GetTickCount64 推算會跟實際差好幾分鐘（實測 14 天差 5 分鐘）。</summary>
        public static DateTime BootTime()
        {
            if (bootTime == null)
            {
                try
                {
                    using (var s = new System.Management.ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                        foreach (System.Management.ManagementObject o in s.Get())
                            bootTime = System.Management.ManagementDateTimeConverter.ToDateTime((string)o["LastBootUpTime"]);
                }
                catch { }
                if (bootTime == null) bootTime = DateTime.Now - TimeSpan.FromMilliseconds(GetTickCount64());
                var t = bootTime.Value;
                bootTime = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
            }
            return bootTime.Value;
        }
    }
}
