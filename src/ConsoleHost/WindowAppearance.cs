using System;
using System.Runtime.InteropServices;

namespace Meshtastic.ConsoleHost
{
    internal static class WindowAppearance
    {
        private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpFrameChanged = 0x0020;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        public static void SetDarkTitleBar(IntPtr window, bool enabled)
        {
            if (window == IntPtr.Zero) return;
            var value = enabled ? 1 : 0;
            try
            {
                DwmSetWindowAttribute(window, 20, ref value, sizeof(int));
                DwmSetWindowAttribute(window, 19, ref value, sizeof(int));
                SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
            }
            catch { }
        }
    }
}
