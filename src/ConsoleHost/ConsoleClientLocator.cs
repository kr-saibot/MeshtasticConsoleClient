using System;
using System.IO;
using System.Windows.Forms;

namespace Meshtastic.ConsoleHost
{
    internal static class ConsoleClientLocator
    {
        private const string ClientFileName = "ConsoleClient.exe";

        public static string Resolve(string[] args, IWin32Window owner)
        {
            if (args != null && args.Length > 0)
            {
                var requested = Path.GetFullPath(args[0]);
                if (!File.Exists(requested))
                    throw new FileNotFoundException("Die angegebene ConsoleClient.exe wurde nicht gefunden.", requested);
                Remember(requested);
                return requested;
            }

            var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ClientFileName);
            if (File.Exists(local))
            {
                if (!String.Equals(AppSettings.ClientPath, local, StringComparison.OrdinalIgnoreCase))
                    Remember(local);
                return local;
            }

            if (!String.IsNullOrWhiteSpace(AppSettings.ClientPath) && File.Exists(AppSettings.ClientPath))
                return AppSettings.ClientPath;

            var selected = Select(owner, AppSettings.ClientPath);
            if (selected != null)
                return selected;

            throw new OperationCanceledException("Es wurde keine ConsoleClient.exe ausgewählt.");
        }

        public static string Select(IWin32Window owner, string currentPath = null)
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "ConsoleClient.exe auswählen",
                Filter = "Meshtastic Console Client|ConsoleClient.exe|Programme (*.exe)|*.exe",
                CheckFileExists = true,
                FileName = ClientFileName,
                InitialDirectory = GetInitialDirectory(currentPath)
            })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return null;

                Remember(dialog.FileName);
                return dialog.FileName;
            }
        }

        private static void Remember(string path)
        {
            AppSettings.ClientPath = Path.GetFullPath(path);
            AppSettings.Save();
        }

        private static string GetInitialDirectory(string path)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(path) && Directory.Exists(Path.GetDirectoryName(path))
                    ? Path.GetDirectoryName(path)
                    : AppDomain.CurrentDomain.BaseDirectory;
            }
            catch { return AppDomain.CurrentDomain.BaseDirectory; }
        }
    }
}
