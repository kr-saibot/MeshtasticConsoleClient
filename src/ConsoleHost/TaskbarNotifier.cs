using System;
using System.Runtime.InteropServices;

namespace Meshtastic.ConsoleHost
{
    internal static class TaskbarNotifier
    {
        private const uint FlashStop = 0;
        private const uint FlashTray = 2;
        private const uint FlashTimerNoForeground = 12;

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        public static void Start(IntPtr window) { Flash(window, FlashTray | FlashTimerNoForeground); }
        public static void Stop(IntPtr window) { Flash(window, FlashStop); }

        private static void Flash(IntPtr window, uint flags)
        {
            if (window == IntPtr.Zero) return;
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
                Window = window,
                Flags = flags,
                Count = UInt32.MaxValue,
                Timeout = 0
            };
            FlashWindowEx(ref info);
        }
    }
}
