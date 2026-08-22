using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using EasyWindowsTerminalControl;

namespace Meshtastic.ConsoleHost
{
    internal sealed class TerminalHostForm : Form, IMessageFilter
    {
        private const int WmMouseWheel = 0x020A;
        private const int WmNcButtonDown = 0x00A1;
        private const int HtClose = 20;
        private const int ScClose = 0xF060;
        private const int SysCommandMask = 0xFFF0;
        private readonly EasyTerminalControl _terminal;
        private readonly ElementHost _terminalHost;
        private readonly System.Windows.Media.MediaPlayer _bellPlayer = new System.Windows.Media.MediaPlayer();
        private NotifyIcon _trayIcon;
        private Timer _processExitTimer;
        private Timer _startupTrayTimer;
        private bool _allowClose;
        private bool _hiddenToTray;
        private bool _suppressTrayMinimize;
        private DateTime _suppressBellUntilUtc = DateTime.UtcNow.AddSeconds(5);

        public TerminalHostForm(string[] args)
        {
            Text = "Meshtastic Console Client";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 500);
            ClientSize = new Size(AppSettings.WindowWidth, AppSettings.WindowHeight);
            BackColor = Color.Black;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            InitializeTrayIcon();
            if (AppSettings.StartMinimized && !AppSettings.MinimizeToTray)
                WindowState = FormWindowState.Minimized;

            var executable = ConsoleClientLocator.Resolve(args, this);
            _terminal = new EasyTerminalControl
            {
                StartupCommandLine = Quote(executable),
                WorkingDirectory = Path.GetDirectoryName(executable),
                InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey | EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
                Win32InputMode = true
            };
            _terminal.ConPTYTerm.InterceptOutputToUITerminal = HandleTerminalOutput;

            _terminalHost = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = _terminal
            };
            Controls.Add(_terminalHost);
            Application.AddMessageFilter(this);
            Application.AddMessageFilter(new TerminalKeyboardFilter(_terminal, this));
            TerminalPresentation.HideScrollBar(_terminal);
            if (_terminal.Terminal != null)
                _terminal.Terminal.Loaded += delegate { TerminalPresentation.HideScrollBar(_terminal); };

            if (_terminal.Terminal != null)
                _terminal.Terminal.PreviewMouseWheel += OnPreviewMouseWheel;
            if (_terminal.ConPTYTerm != null)
                _terminal.ConPTYTerm.TermReady += OnTerminalReady;

            Shown += delegate
            {
                TerminalPresentation.SetFontSize(_terminal, AppSettings.FontSize);
                if (AppSettings.StartMinimized && AppSettings.MinimizeToTray)
                {
                    _startupTrayTimer = new Timer { Interval = 100 };
                    _startupTrayTimer.Tick += delegate
                    {
                        StopStartupTrayTimer();
                        HideToTray();
                    };
                    _startupTrayTimer.Start();
                }
                else if (!AppSettings.StartMinimized)
                    _terminal.Focus();
            };
            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && AppSettings.MinimizeToTray && !_suppressTrayMinimize) HideToTray();
            };
            ResizeEnd += delegate { SaveWindowSize(); };
            Activated += delegate { TaskbarNotifier.Stop(Handle); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TerminalSystemMenu.Install(Handle);
            WindowAppearance.SetDarkTitleBar(Handle, AppSettings.DarkMode);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcButtonDown && message.WParam.ToInt32() == HtClose)
            {
                WindowState = FormWindowState.Minimized;
                return;
            }

            if (message.Msg == TerminalSystemMenu.WmSysCommand)
            {
                if ((message.WParam.ToInt32() & SysCommandMask) == ScClose)
                {
                    _allowClose = true;
                    Close();
                    return;
                }

                if (TerminalSystemMenu.IsSelectClientCommand(message.WParam))
                {
                    SelectAndRestartConsoleClient();
                    return;
                }

                if (TerminalSystemMenu.IsStartMinimizedCommand(message.WParam))
                {
                    AppSettings.StartMinimized = !AppSettings.StartMinimized;
                    AppSettings.Save();
                    TerminalSystemMenu.Refresh(Handle);
                    return;
                }

                if (TerminalSystemMenu.IsMinimizeToTrayCommand(message.WParam))
                {
                    AppSettings.MinimizeToTray = !AppSettings.MinimizeToTray;
                    AppSettings.Save();
                    TerminalSystemMenu.Refresh(Handle);
                    return;
                }

                if (TerminalSystemMenu.IsStartWithWindowsCommand(message.WParam))
                {
                    if (!AppSettings.SetStartsWithWindows(!AppSettings.StartsWithWindows))
                        MessageBox.Show(this, "Die Windows-Startoption konnte nicht geändert werden.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    TerminalSystemMenu.Refresh(Handle);
                    return;
                }

                if (TerminalSystemMenu.IsSelectBellSoundCommand(message.WParam))
                {
                    SelectBellSound();
                    return;
                }
                if (TerminalSystemMenu.IsClearBellSoundCommand(message.WParam))
                {
                    AppSettings.BellSoundPath = null;
                    AppSettings.Save();
                    _bellPlayer.Stop();
                    TerminalSystemMenu.Refresh(Handle);
                    return;
                }

                if (TerminalSystemMenu.IsDarkModeCommand(message.WParam))
                {
                    AppSettings.DarkMode = !AppSettings.DarkMode;
                    AppSettings.Save();
                    WindowAppearance.SetDarkTitleBar(Handle, AppSettings.DarkMode);
                    TerminalSystemMenu.Refresh(Handle);
                    return;
                }

                int fontSize;
                if (TerminalSystemMenu.TryGetFontSize(message.WParam, out fontSize))
                {
                    TerminalPresentation.SetFontSize(_terminal, fontSize);
                    AppSettings.FontSize = fontSize;
                    AppSettings.Save();
                    TerminalSystemMenu.Refresh(Handle);
                    return;
                }
            }
            base.WndProc(ref message);
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg != WmMouseWheel || _terminalHost.IsDisposed || !_terminalHost.Visible)
                return false;

            var screenPosition = Cursor.Position;
            if (!_terminalHost.RectangleToScreen(_terminalHost.ClientRectangle).Contains(screenPosition))
                return false;

            var clientPosition = _terminalHost.PointToClient(screenPosition);
            var delta = unchecked((short)(((long)message.WParam >> 16) & 0xffff));
            return SendMouseWheel(delta, clientPosition.X, clientPosition.Y, _terminalHost.ClientSize.Width, _terminalHost.ClientSize.Height);
        }

        private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var terminal = _terminal.Terminal;
            if (terminal == null)
                return;

            var position = e.GetPosition(terminal);
            if (SendMouseWheel(e.Delta, position.X, position.Y, terminal.ActualWidth, terminal.ActualHeight))
                e.Handled = true;
        }

        private bool SendMouseWheel(int delta, double x, double y, double width, double height)
        {
            var terminal = _terminal.Terminal;
            var conPty = _terminal.ConPTYTerm;
            if (delta == 0 || terminal == null || conPty == null || !conPty.TermProcIsStarted || width <= 0 || height <= 0)
                return false;

            var columns = Math.Max(1, terminal.Columns);
            var rows = Math.Max(1, terminal.Rows);
            var column = Math.Max(1, Math.Min(columns, (int)(x * columns / width) + 1));
            var row = Math.Max(1, Math.Min(rows, (int)(y * rows / height) + 1));
            var button = delta > 0 ? 64 : 65;
            var steps = Math.Max(1, Math.Abs(delta) / 120);
            var input = new StringBuilder();
            for (var step = 0; step < steps; step++)
                input.Append("\u001b[<").Append(button).Append(';').Append(column).Append(';').Append(row).Append('M');

            conPty.WriteToTerm(input.ToString().AsSpan());
            return true;
        }

        private async void SelectAndRestartConsoleClient()
        {
            var currentPath = _terminal.WorkingDirectory == null
                ? null
                : Path.Combine(_terminal.WorkingDirectory, "ConsoleClient.exe");
            var executable = ConsoleClientLocator.Select(this, currentPath);
            if (executable == null)
                return;

            StopProcessExitTimer();
            var conPty = new TermPTY();
            conPty.InterceptOutputToUITerminal = HandleTerminalOutput;
            conPty.TermReady += OnTerminalReady;

            _terminal.StartupCommandLine = Quote(executable);
            _terminal.WorkingDirectory = Path.GetDirectoryName(executable);
            _suppressBellUntilUtc = DateTime.UtcNow.AddSeconds(5);
            await _terminal.RestartTerm(conPty, true);
            TerminalPresentation.HideScrollBar(_terminal);
            _terminal.Focus();
        }

        private void StopProcessExitTimer()
        {
            if (_processExitTimer == null)
                return;

            _processExitTimer.Stop();
            _processExitTimer.Dispose();
            _processExitTimer = null;
        }

        private void StopStartupTrayTimer()
        {
            if (_startupTrayTimer == null) return;
            _startupTrayTimer.Stop();
            _startupTrayTimer.Dispose();
            _startupTrayTimer = null;
        }

        private void InitializeTrayIcon()
        {

            var menu = new ContextMenuStrip();
            menu.Items.Add("Öffnen", null, delegate { RestoreFromTray(); });
            menu.Items.Add("Beenden", null, delegate
            {
                _allowClose = true;
                Close();
            });

            _trayIcon = new NotifyIcon
            {
                Text = Text,
                Icon = Icon ?? SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = false
            };
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };
        }

        private void HideToTray()
        {
            if (_hiddenToTray || _allowClose) return;
            _hiddenToTray = true;
            Hide();
            _trayIcon.Visible = true;
        }

        private void RestoreFromTray()
        {
            if (!_hiddenToTray) return;
            _trayIcon.Visible = false;
            _hiddenToTray = false;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            _terminal.Focus();
        }

        private void ShowAlarmInTaskbar()
        {
            if (!_hiddenToTray) return;
            _suppressTrayMinimize = true;
            _trayIcon.Visible = false;
            _hiddenToTray = false;
            Show();
            WindowState = FormWindowState.Minimized;
            BeginInvoke(new Action(delegate { _suppressTrayMinimize = false; }));
        }
        private void HandleTerminalOutput(ref Span<char> output)
        {
            if (output.IndexOf('\a') >= 0 && IsHandleCreated && DateTime.UtcNow >= _suppressBellUntilUtc)
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        ShowAlarmInTaskbar();
                        TaskbarNotifier.Start(Handle);
                        PlayBellSound();
                    }));
                }
                catch { }
            }

            TerminalOutputSanitizer.Sanitize(ref output);
        }

        private void SelectBellSound()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Audiodatei für Terminal-BEL auswählen",
                Filter = "Audiodateien (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma|Alle Dateien (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = GetBellSoundDirectory()
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                AppSettings.BellSoundPath = dialog.FileName;
                AppSettings.Save();
                TerminalSystemMenu.Refresh(Handle);
                PlayBellSound();
            }
        }

        private static string GetBellSoundDirectory()
        {
            try
            {
                var directory = Path.GetDirectoryName(AppSettings.BellSoundPath);
                return Directory.Exists(directory) ? directory : AppDomain.CurrentDomain.BaseDirectory;
            }
            catch { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        private void PlayBellSound()
        {
            if (String.IsNullOrWhiteSpace(AppSettings.BellSoundPath) || !File.Exists(AppSettings.BellSoundPath)) return;
            try
            {
                _bellPlayer.Stop();
                _bellPlayer.Open(new Uri(AppSettings.BellSoundPath, UriKind.Absolute));
                _bellPlayer.Position = TimeSpan.Zero;
                _bellPlayer.Play();
            }
            catch { }
        }


        private static void SanitizeTerminalOutput(ref Span<char> output)
        {
            for (var index = 0; index < output.Length; index++)
            {
                var current = output[index];
                if (Char.IsHighSurrogate(current))
                {
                    output[index] = '?';
                    if (index + 1 < output.Length && Char.IsLowSurrogate(output[index + 1]))
                    {
                        output[index + 1] = ' ';
                        index++;
                    }
                    continue;
                }

                if (Char.IsLowSurrogate(current))
                {
                    output[index] = '?';
                    continue;
                }

                if (current >= '\u2600' && current <= '\u27BF')
                {
                    output[index] = '?';
                    if (index + 1 < output.Length && output[index + 1] == '\uFE0F')
                    {
                        output[index + 1] = ' ';
                        index++;
                    }
                }
            }
        }

        private void OnTerminalReady(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<object, EventArgs>(OnTerminalReady), sender, e);
                return;
            }

            var process = _terminal.ConPTYTerm == null ? null : _terminal.ConPTYTerm.Process;
            if (process == null)
                return;

            if (_processExitTimer != null)
            {
                _processExitTimer.Stop();
                _processExitTimer.Dispose();
            }

            _processExitTimer = new Timer { Interval = 250 };
            _processExitTimer.Tick += delegate
            {
                if (!process.HasExited)
                    return;

                _processExitTimer.Stop();
                _processExitTimer.Dispose();
                _processExitTimer = null;
                _allowClose = true;
                Close();
            };
            _processExitTimer.Start();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            SaveWindowSize();

            if (_allowClose || e.CloseReason == CloseReason.WindowsShutDown)
                return;

            if (e.CloseReason != CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            var answer = MessageBox.Show(
                this,
                "Der Meshtastic Console Client läuft möglicherweise noch. Wirklich beenden?",
                "Programm beenden",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;

            _allowClose = true;
            BeginInvoke(new Action(Close));
        }

        private void SaveWindowSize()
        {
            if (WindowState != FormWindowState.Normal)
                return;

            if (ClientSize.Width < 640 || ClientSize.Height < 400)
                return;

            AppSettings.WindowWidth = ClientSize.Width;
            AppSettings.WindowHeight = ClientSize.Height;
            AppSettings.Save();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bellPlayer.Close();
                StopStartupTrayTimer();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                Application.RemoveMessageFilter(this);
                if (_processExitTimer != null)
                {
                    _processExitTimer.Stop();
                    _processExitTimer.Dispose();
                    _processExitTimer = null;
                }
            }
            base.Dispose(disposing);
        }

        private static string ResolveConsoleClient(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                var requested = Path.GetFullPath(args[0]);
                if (!File.Exists(requested))
                    throw new FileNotFoundException("Die angegebene ConsoleClient.exe wurde nicht gefunden.", requested);
                return requested;
            }

            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ConsoleClient.exe"),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ConsoleClient", "bin", "Debug", "net472", "ConsoleClient.exe")),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ConsoleClient", "bin", "Release", "net472", "ConsoleClient.exe"))
            };

            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null)
                return found;

            using (var dialog = new OpenFileDialog
            {
                Title = "ConsoleClient.exe auswählen",
                Filter = "Meshtastic Console Client|ConsoleClient.exe|Programme (*.exe)|*.exe",
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                    return dialog.FileName;
            }

            throw new OperationCanceledException("Es wurde keine ConsoleClient.exe ausgewählt.");
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
