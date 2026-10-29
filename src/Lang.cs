using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ExplorerSession
{
    /// <summary>
    /// UI language: Traditional Chinese when the Windows display language is zh-TW / zh-HK / zh-MO (or zh-Hant),
    /// English otherwise. Override with the environment variable EXPLORER_SESSION_LANG=zh or en.
    /// </summary>
    static class L
    {
        [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();

        public static readonly bool Zh = Detect();

        static bool Detect()
        {
            string forced = Environment.GetEnvironmentVariable("EXPLORER_SESSION_LANG");
            if (!string.IsNullOrEmpty(forced)) return forced.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            try
            {
                // CultureInfo.CurrentUICulture can disagree with the Windows display language
                // (it reported en-US on a zh-TW machine), so ask Windows directly.
                string name = new CultureInfo(GetUserDefaultUILanguage()).Name;
                return name == "zh-TW" || name == "zh-HK" || name == "zh-MO" || name.StartsWith("zh-Hant");
            }
            catch { return false; }
        }

        public static string T(string zh, string en) { return Zh ? zh : en; }
    }
}
