using System;
using System.IO;

namespace ExtremeRagdoll
{
    internal static class ER_Log
    {
        private static readonly string DefaultPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                         "Mount and Blade II Bannerlord", "Logs", "ExtremeRagdoll", "er_log.txt");

        private static string _path = DefaultPath;
        private static readonly object _sync = new object();
        private const long MaxBytes   = 5L * 1024 * 1024; // 5 MB cap
        private const int  MaxBackups = 3;                // keep .1 .. .3

        static ER_Log()
        {
            try
            {
                EnsureDirectory();
                RotateIfNeeded();
            }
            catch
            {
                // ignore initialization failures so logging never throws
            }
        }

        internal static string LogFilePath => _path;

        public static void Info(string msg) => Write("INFO", msg, null);
        public static void Warn(string msg) => Write("WARN", msg, null);
        public static void Error(string msg, Exception ex = null) => Write("ERROR", msg, ex);

        private static void Write(string level, string msg, Exception ex)
        {
            bool enabled;
            try
            {
                enabled = ER_Config.DebugLogging || level == "ERROR";
            }
            catch
            {
                enabled = true; // log even if MCM not ready
            }
            if (!enabled) return;

            lock (_sync)
            {
                try
                {
                    EnsureDirectory();
                    RotateIfNeeded();

                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
                    if (ex != null) line += $" :: {ex.GetType().Name}: {ex.Message}";
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch
                {
                    // never throw from logging
                }
            }
        }

        private static void EnsureDirectory()
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                for (int i = MaxBackups - 1; i >= 1; i--)
                {
                    var src = _path + "." + i;
                    var dst = _path + "." + (i + 1);
                    if (File.Exists(dst)) File.Delete(dst);
                    if (File.Exists(src)) File.Move(src, dst);
                }

                var first = _path + ".1";
                if (File.Exists(first)) File.Delete(first);
                File.Move(_path, first);
            }
            catch
            {
                // ignore rotation failures so logging never throws
            }
        }
    }
}
