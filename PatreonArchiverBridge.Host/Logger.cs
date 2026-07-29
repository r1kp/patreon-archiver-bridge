using System;
using System.IO;
using System.Linq;

namespace PatreonArchiverBridge.Host
{
    /// <summary>
    /// Dateibasiertes Logging mit TAGESROTATION statt einer einzigen, ewig
    /// wachsenden Datei.
    ///
    /// Vorher: EINE Datei (%TEMP%\patreon_bridge_csharp.log), die beim Start
    /// komplett geloescht wurde, sobald sie 10 MB ueberschritt - im Ernstfall war
    /// damit genau die Vorgeschichte weg, die man zur Fehlersuche gebraucht haette.
    /// Jetzt: ein eigener Ordner mit je einer Datei pro Tag und Quelle, von denen
    /// die 5 neuesten je Quelle behalten werden (Nutzer-Vorgabe: "max 5 pro Seite,
    /// aelteste wird ersetzt").
    ///   %TEMP%\PatreonArchiverLogs\bridge_YYYY-MM-DD.log      (diese Anwendung)
    ///   %TEMP%\PatreonArchiverLogs\extension_YYYY-MM-DD.log   (von der Extension
    ///                                                          per log_entry)
    /// </summary>
    public static class Logger
    {
        public const string BridgePrefix = "bridge_";
        public const string ExtensionPrefix = "extension_";
        private const int KeepFilesPerPrefix = 5;

        private static readonly object LockObj = new object();

        public static string LogDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "PatreonArchiverLogs");

        static Logger()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                // Aufraeumen genau EINMAL beim Start - nicht bei jedem Schreibvorgang
                // (der Host startet pro Native-Messaging-Verbindung neu, das reicht
                // voellig und kostet im laufenden Betrieb nichts).
                PruneOldFiles(BridgePrefix);
                PruneOldFiles(ExtensionPrefix);
            }
            catch
            {
                // Logging darf NIE etwas zum Absturz bringen.
            }
        }

        private static void PruneOldFiles(string prefix)
        {
            try
            {
                var files = Directory.GetFiles(LogDirectory, prefix + "*.log")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase) // Dateiname enthaelt ISO-Datum -> alphabetisch = chronologisch
                    .Skip(KeepFilesPerPrefix)
                    .ToList();
                foreach (var old in files)
                {
                    try { File.Delete(old); } catch { /* evtl. offen - dann beim naechsten Start */ }
                }
            }
            catch { }
        }

        private static string PathFor(string prefix) =>
            Path.Combine(LogDirectory, $"{prefix}{DateTime.Now:yyyy-MM-dd}.log");

        public static void Log(string message)
        {
            WriteLine(BridgePrefix, message);
        }

        /// <summary>Schreibt eine von der Extension gemeldete Zeile in die Extension-Tagesdatei.</summary>
        public static void LogExtension(string level, string message)
        {
            WriteLine(ExtensionPrefix, $"[{level}] {message}");
        }

        private static void WriteLine(string prefix, string message)
        {
            try
            {
                lock (LockObj)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    File.AppendAllText(PathFor(prefix), $"[{timestamp}] {message}{Environment.NewLine}");
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
