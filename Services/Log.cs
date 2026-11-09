using System;
using System.IO;

namespace InteractiveMenu.Services
{
    /// <summary>Простой лог в %AppData%\InteractiveMenu\log.txt (обрезается, когда разрастается).</summary>
    public static class Log
    {
        private static readonly object Lock = new object();

        public static string FilePath => Path.Combine(ConfigService.Dir, "log.txt");

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(ConfigService.Dir);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > 512 * 1024) file.Delete();
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // лог не должен ронять приложение
            }
        }
    }
}
