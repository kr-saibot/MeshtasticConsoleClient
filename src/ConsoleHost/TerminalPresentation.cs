using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace Meshtastic.ConsoleHost
{
    internal static class TerminalPresentation
    {
        public static void HideScrollBar(EasyTerminalControl terminal)
        {
            if (terminal == null || terminal.Terminal == null)
                return;

            var field = terminal.Terminal.GetType().GetField("scrollbar", BindingFlags.Instance | BindingFlags.NonPublic);
            var scrollBar = field == null ? null : field.GetValue(terminal.Terminal) as ScrollBar;
            if (scrollBar != null)
                scrollBar.Visibility = Visibility.Collapsed;
        }

        public static void SetFontSize(EasyTerminalControl terminal, int size)
        {
            if (terminal == null || terminal.Terminal == null)
                return;

            terminal.FontSizeWhenSettingTheme = size;
            terminal.Terminal.SetTheme(CreateTheme(), terminal.FontFamilyWhenSettingTheme.Source, (short)size, Colors.Black);
            HideScrollBar(terminal);
        }

        private static TerminalTheme CreateTheme()
        {
            return new TerminalTheme
            {
                DefaultBackground = 0x00000000,
                DefaultForeground = 0x00C0C0C0,
                DefaultSelectionBackground = 0x00808080,
                CursorStyle = CursorStyle.BlinkingBlockDefault,
                ColorTable = new uint[]
                {
                    0x00000000, 0x00000080, 0x00008000, 0x00008080,
                    0x00800000, 0x00800080, 0x00808000, 0x00C0C0C0,
                    0x00808080, 0x000000FF, 0x0000FF00, 0x0000FFFF,
                    0x00FF0000, 0x00FF00FF, 0x00FFFF00, 0x00FFFFFF
                }
            };
        }
    }
}
