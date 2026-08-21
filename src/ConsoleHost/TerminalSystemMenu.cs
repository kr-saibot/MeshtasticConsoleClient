using System;
using System.Runtime.InteropServices;

namespace Meshtastic.ConsoleHost
{
    internal static class TerminalSystemMenu
    {
        public const int WmSysCommand = 0x0112;
        private const uint MfString = 0x0000, MfChecked = 0x0008, MfSeparator = 0x0800;
        private const int SelectClient = 0x1E00, Font9 = 0x1E08, Font10 = 0x1E10,
            Font12 = 0x1E20, Font14 = 0x1E30, Font16 = 0x1E40, Font18 = 0x1E50,
            StartMinimized = 0x1E60, StartWithWindows = 0x1E70, DarkMode = 0x1E80,
            SelectBellSound = 0x1E90, ClearBellSound = 0x1EA0;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);

        public static void Install(IntPtr window)
        {
            var menu = GetSystemMenu(window, false);
            if (menu == IntPtr.Zero) return;
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)SelectClient, "ConsoleClient.exe auswählen …");
            AppendMenu(menu, MfString, (UIntPtr)SelectBellSound, "Alarm-Audiodatei auswählen …");
            if (!String.IsNullOrWhiteSpace(AppSettings.BellSoundPath))
                AppendMenu(menu, MfString, (UIntPtr)ClearBellSound, "Alarm-Audiodatei entfernen");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AddChecked(menu, Font9, "Schriftgröße 9", AppSettings.FontSize == 9);
            AddChecked(menu, Font10, "Schriftgröße 10", AppSettings.FontSize == 10);
            AddChecked(menu, Font12, "Schriftgröße 12", AppSettings.FontSize == 12);
            AddChecked(menu, Font14, "Schriftgröße 14", AppSettings.FontSize == 14);
            AddChecked(menu, Font16, "Schriftgröße 16", AppSettings.FontSize == 16);
            AddChecked(menu, Font18, "Schriftgröße 18", AppSettings.FontSize == 18);
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AddChecked(menu, StartMinimized, "Minimiert starten", AppSettings.StartMinimized);
            AddChecked(menu, StartWithWindows, "Mit Windows starten", AppSettings.StartsWithWindows);
            AddChecked(menu, DarkMode, "Dunkle Titelleiste", AppSettings.DarkMode);
        }

        public static void Refresh(IntPtr window) { GetSystemMenu(window, true); Install(window); }
        private static void AddChecked(IntPtr menu, int command, string text, bool value)
        { AppendMenu(menu, MfString | (value ? MfChecked : 0), (UIntPtr)command, text); }

        public static bool IsSelectClientCommand(IntPtr command) { return command.ToInt32() == SelectClient; }
        public static bool IsStartMinimizedCommand(IntPtr command) { return command.ToInt32() == StartMinimized; }
        public static bool IsStartWithWindowsCommand(IntPtr command) { return command.ToInt32() == StartWithWindows; }
        public static bool IsDarkModeCommand(IntPtr command) { return command.ToInt32() == DarkMode; }

        public static bool IsSelectBellSoundCommand(IntPtr command) { return command.ToInt32() == SelectBellSound; }
        public static bool IsClearBellSoundCommand(IntPtr command) { return command.ToInt32() == ClearBellSound; }
        public static bool TryGetFontSize(IntPtr command, out int size)
        {
            switch (command.ToInt32())
            {
                case Font9: size = 9; return true;
                case Font10: size = 10; return true;
                case Font12: size = 12; return true;
                case Font14: size = 14; return true;
                case Font16: size = 16; return true;
                case Font18: size = 18; return true;
                default: size = 0; return false;
            }
        }
    }
}
