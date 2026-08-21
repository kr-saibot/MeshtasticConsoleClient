using System;
using System.Windows.Forms;
using EasyWindowsTerminalControl;

namespace Meshtastic.ConsoleHost
{
    internal sealed class TerminalKeyboardFilter : IMessageFilter
    {
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;

        private readonly EasyTerminalControl _terminal;
        private readonly Form _owner;

        public TerminalKeyboardFilter(EasyTerminalControl terminal, Form owner)
        {
            _terminal = terminal;
            _owner = owner;
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (_owner.IsDisposed || Form.ActiveForm != _owner)
                return false;

            var isKeyDown = message.Msg == WmKeyDown || message.Msg == WmSysKeyDown;
            var isKeyUp = message.Msg == WmKeyUp || message.Msg == WmSysKeyUp;
            if (!isKeyDown && !isKeyUp)
                return false;

            string input;
            switch ((Keys)message.WParam.ToInt32())
            {
                case Keys.Tab:
                    input = (Control.ModifierKeys & Keys.Shift) != 0 ? "\u001b[Z" : "\t";
                    break;
                case Keys.Left:
                    input = "\u001b[D";
                    break;
                case Keys.Up:
                    input = "\u001b[A";
                    break;
                case Keys.Right:
                    input = "\u001b[C";
                    break;
                case Keys.Down:
                    input = "\u001b[B";
                    break;
                default:
                    return false;
            }

            if (isKeyDown && _terminal.ConPTYTerm != null && _terminal.ConPTYTerm.TermProcIsStarted)
                _terminal.ConPTYTerm.WriteToTerm(input.AsSpan());
            return true;
        }
    }
}
