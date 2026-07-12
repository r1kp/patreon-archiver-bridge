using System;
using System.IO;

namespace PatreonArchiverBridge.Host
{
    public static class Logger
    {
        private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "patreon_bridge_csharp.log");
        private static readonly object LockObj = new object();

        static Logger()
        {
            try
            {
                var fileInfo = new FileInfo(LogFilePath);
                if (fileInfo.Exists && fileInfo.Length > 10 * 1024 * 1024)
                {
                    fileInfo.Delete();
                }
            }
            catch
            {
                // Ignore initialization errors
            }
        }

        public static void Log(string message)
        {
            try
            {
                lock (LockObj)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    File.AppendAllText(LogFilePath, $"[{timestamp}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Ignore logging errors to prevent crash
            }
        }

        public static void LogException(Exception ex, string context = "")
        {
            Log($"[ERROR] {context}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }
    }
}
