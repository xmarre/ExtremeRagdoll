using System;
using System.IO;

namespace ExtremeRagdoll
{
    internal static class ER_Log
    {
        private static readonly string LogPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "Mount and Blade II Bannerlord", "Logs", "ExtremeRagdoll", "er_log.txt");

        internal static string LogFilePath => LogPath;

        private static void Write(string level, string msg, Exception ex = null)
        {
            bool enabled = true;
            try
            {
                enabled = ER_Config.DebugLogging;
            }
            catch
            {
                enabled = true; // log even if MCM not ready
            }
            if (!enabled) return;
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
                if (ex != null) line += $" :: {ex.GetType().Name}: {ex.Message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { /* never throw from logging */ }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", msg, ex);
    }
}
