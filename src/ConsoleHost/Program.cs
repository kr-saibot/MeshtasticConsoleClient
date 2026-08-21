using System;
using System.IO;
using System.Windows.Forms;

namespace Meshtastic.ConsoleHost
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                AppSettings.Load();
                AppSettings.Save();
                Application.Run(new TerminalHostForm(args));
            }
            catch (Exception exception)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.log"),
                        exception.ToString());
                }
                catch { }

                MessageBox.Show(
                    "Der Meshtastic Console Host konnte nicht gestartet werden.\r\n\r\n" + exception.Message,
                    "Startfehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
