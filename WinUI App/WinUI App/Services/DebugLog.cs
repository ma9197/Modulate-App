using System;
using System.IO;
using System.Text;
using Windows.Storage;

namespace WinUI_App.Services
{
    public static class DebugLog
    {
        private static readonly object _lock = new();

        private static string GetLogPath()
        {
            try
            {
                var folder = Path.Combine(ApplicationData.Current.LocalFolder.Path, "logs");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "app.log");
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "app.log");
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                System.Diagnostics.Debug.WriteLine(line);

                lock (_lock)
                {
                    File.AppendAllText(GetLogPath(), line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // ignore logging failures
            }
        }
    }
}


