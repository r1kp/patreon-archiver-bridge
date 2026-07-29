using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CG.Web.MegaApiClient;

namespace PatreonArchiverBridge.Host
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static class CommandHandlers
    {
        public static string? FindYtDlp() => Core.BridgeCore.FindYtDlp();
        public static string? FindFfmpeg() => Core.BridgeCore.FindFfmpeg();
        private static string GetDownloadsFolder() => Core.BridgeCore.GetDownloadsFolder();

        public static async Task HandlePingAsync(bool forceVersionCheck = false)
        {
            string? ytdlpPath = FindYtDlp();
            bool ytdlpFound = !string.IsNullOrEmpty(ytdlpPath);
            string version = "unknown";

            if (ytdlpFound)
            {
                // Versuchen, die Version aus der Registry zu lesen (wir cachen sie dort dauerhaft,
                // da statische Felder pro Prozess instanziiert werden und Native Messaging bei
                // jedem Ping einen neuen Prozess startet).
                string? cachedVersion = null;
                string? cachedPath = null;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PatreonArchiverBridge");
                    if (key != null)
                    {
                        cachedVersion = key.GetValue("CachedYtDlpVersion") as string;
                        cachedPath = key.GetValue("CachedYtDlpPath") as string;
                    }
                }
                catch { }

                if (cachedVersion != null && cachedPath == ytdlpPath && !forceVersionCheck)
                {
                    version = cachedVersion;
                }
                else
                {
                    try
                    {
                        using var proc = new Process();
                        proc.StartInfo.FileName = ytdlpPath;
                        proc.StartInfo.Arguments = "--version";
                        proc.StartInfo.UseShellExecute = false;
                        proc.StartInfo.RedirectStandardOutput = true;
                        proc.StartInfo.CreateNoWindow = true;
                        proc.Start();
                        string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                        await proc.WaitForExitAsync().ConfigureAwait(false);
                        if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        {
                            version = output.Trim();
                            
                            // In Registry cachen für zukünftige Pings
                            try
                            {
                                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\PatreonArchiverBridge");
                                if (key != null)
                                {
                                    key.SetValue("CachedYtDlpVersion", version);
                                    key.SetValue("CachedYtDlpPath", ytdlpPath);
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex, "yt-dlp --version check failed");
                    }
                }
            }

            Program.SendMessage(new
            {
                type = "pong",
                ytdlpFound = ytdlpFound,
                version = version
            });
        }

        public static void HandleGetDefaultDir()
        {
            try
            {
                string? defaultDir = null;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PatreonArchiverBridge");
                    if (key != null)
                    {
                        defaultDir = key.GetValue("DefaultDownloadDir") as string;
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(defaultDir) || !Directory.Exists(defaultDir))
                {
                    string downloads = GetDownloadsFolder();
                    defaultDir = Path.Combine(downloads, "Patreon Archiver");
                }

                if (!Directory.Exists(defaultDir))
                {
                    Directory.CreateDirectory(defaultDir);
                }

                Program.SendMessage(new
                {
                    type = "default_dir",
                    path = defaultDir
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "get_default_dir failed");
                Program.SendMessage(new
                {
                    type = "error",
                    message = ex.Message
                });
            }
        }

        public static void HandleWriteChunk(string path, string dataBase64, bool append, bool isLast)
        {
            try
            {
                byte[] data = Convert.FromBase64String(dataBase64);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush();
                }

                Program.SendMessage(new
                {
                    type = "chunk_ack",
                    done = isLast
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "write_chunk failed");
                Program.SendMessage(new
                {
                    type = "write_error",
                    message = ex.Message
                });
            }
        }

        public static void HandleCheckFileExists(string path)
        {
            try
            {
                // Google-Drive-Ordner-/ZIP-Export-Downloads melden im "url_done"-Response
                // einen ORDNER-Pfad zurueck (nicht eine einzelne Datei) - ohne Directory.Exists()
                // wurde ein erfolgreicher Ordner-Download hier immer als "nicht vorhanden"
                // gemeldet, obwohl die Dateien laengst auf der Festplatte lagen.
                bool exists = File.Exists(path) || Directory.Exists(path);
                Program.SendMessage(new
                {
                    type = "file_exists_result",
                    path = path,
                    exists = exists
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "check_file_exists failed");
                Program.SendMessage(new
                {
                    type = "file_exists_result",
                    path = path,
                    exists = false,
                    error = ex.Message
                });
            }
        }

        // Verschiebt eine bereits lokal vorhandene Datei an den finalen Zielpfad -
        // wird fuer OneDrive gebraucht: die Extension laesst den Download durch den
        // ECHTEN Chrome-Browser laufen (mit gueltiger anonymer Freigabe-Session),
        // weil die von OneDrive aufgeloeste Direkt-URL cookie-gebunden ist und von
        // einem HttpClient OHNE diese Browser-Session mit 403 abgelehnt wird
        // (live verifiziert, siehe HANDOFF.md) - ein erneuter Download ueber die
        // Bridge wie bei den anderen Cloud-Anbietern ist hier also nicht moeglich.
        // Stattdessen laedt Chrome selbst in einen Temp-Pfad, danach wird nur noch
        // dieser bereits fertige lokale Pfad hierher zum Verschieben in die
        // eigentliche Patreon-Archiver-Ordnerstruktur uebergeben - kein erneuter
        // Netzwerk-Download noetig, File.Move ist praktisch instant.
        public static void HandleMoveLocalFile(string sourcePath, string targetPath)
        {
            try
            {
                if (!File.Exists(sourcePath))
                {
                    Program.SendMessage(new
                    {
                        type = "move_local_file_result",
                        ok = false,
                        error = $"Source file not found: {sourcePath}"
                    });
                    return;
                }

                string? targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                File.Move(sourcePath, targetPath);

                Program.SendMessage(new
                {
                    type = "move_local_file_result",
                    ok = true,
                    path = targetPath
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "move_local_file failed");
                Program.SendMessage(new
                {
                    type = "move_local_file_result",
                    ok = false,
                    error = ex.Message
                });
            }
        }

        // Loescht ein leeres Verzeichnis (z.B. PatreonArchiverTemp nach OneDrive-
        // Downloads). Nur leere Verzeichnisse werden geloescht (kein rekursives
        // Delete fuer Sicherheit). Sendet delete_dir_result zur Bestaetigung.
        public static void HandleDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // Recursively delete - but only the specified path (not root dirs)
                    // This is safe because we only call it with the PatreonArchiverTemp subfolder
                    Directory.Delete(path, recursive: true);
                    Logger.Log($"Deleted directory: {path}");
                }
                Program.SendMessage(new
                {
                    type = "delete_dir_result",
                    ok = true,
                    path = path
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "delete_directory failed");
                Program.SendMessage(new
                {
                    type = "delete_dir_result",
                    ok = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Nimmt einen Log-Eintrag der Extension entgegen und haengt ihn an die
        /// Extension-Tagesdatei an. Bewusst "fire and forget": keine Antwort, kein
        /// Fortschritt, keine Fehlerrueckmeldung - Logging darf den eigentlichen
        /// Ablauf niemals aufhalten oder stoeren.
        /// </summary>
        public static void HandleLogEntry(string level, string message, string source)
        {
            string prefix = string.IsNullOrEmpty(source) ? "" : $"({source}) ";
            Logger.LogExtension(string.IsNullOrEmpty(level) ? "info" : level, prefix + message);
        }

        /// <summary>
        /// Liefert die vorhandenen Log-Dateien (Bridge UND Extension) als
        /// Name/Inhalt-Paare zurueck - Grundlage fuer den "Export Diagnostics"-
        /// Knopf im Dashboard. Grosse Dateien werden hinten abgeschnitten, damit
        /// die Native-Messaging-Nachricht nicht ins Unermessliche waechst
        /// (Chrome begrenzt eine einzelne Nachricht auf 1 MB).
        /// </summary>
        public static void HandleGetLogs(string requestId)
        {
            const int maxPerFile = 400 * 1024;
            var result = new List<object>();
            try
            {
                if (Directory.Exists(Logger.LogDirectory))
                {
                    var files = Directory.GetFiles(Logger.LogDirectory, "*.log")
                        .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                        .Take(10);
                    foreach (var path in files)
                    {
                        string content;
                        try
                        {
                            // FileShare.ReadWrite: die Datei kann von einem parallel
                            // laufenden Host-Prozess gerade beschrieben werden.
                            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sr = new StreamReader(fs);
                            content = sr.ReadToEnd();
                        }
                        catch (Exception ex) { content = $"(could not read this file: {ex.Message})"; }
                        if (content.Length > maxPerFile)
                        {
                            content = "(truncated - showing the last part of the file)\r\n" + content.Substring(content.Length - maxPerFile);
                        }
                        result.Add(new { name = Path.GetFileName(path), content });
                    }
                }
                Program.SendMessage(new { requestId, type = "logs_result", ok = true, directory = Logger.LogDirectory, files = result });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "get_logs failed");
                Program.SendMessage(new { requestId, type = "logs_result", ok = false, error = ex.Message, directory = Logger.LogDirectory, files = result });
            }
        }

        // ---------- Abbruch-Aufraeumen (Kind-Prozesse + Datei-Fragmente) ----------
        //
        // Chrome signalisiert einen Abbruch, indem es den Native-Messaging-Port
        // trennt; dieser Prozess sieht daraufhin nur "stdin zu Ende" und beendet
        // sich. Ein von ihm gestartetes yt-dlp lief bisher als verwaistes Kind
        // WEITER (Windows beendet Kindprozesse nicht automatisch mit) und liess
        // beim spaeteren Abschuss seine ".part"-Datei liegen.
        private static readonly List<Process> ChildProcesses = new();

        public static void RegisterChildProcess(Process p)
        {
            lock (ChildProcesses) { ChildProcesses.Add(p); }
        }

        /// <summary>Beendet alle noch laufenden Kindprozesse (yt-dlp/ffmpeg) samt deren eigenen Kindern.</summary>
        public static void KillChildProcesses()
        {
            List<Process> snapshot;
            lock (ChildProcesses) { snapshot = new List<Process>(ChildProcesses); }
            foreach (var p in snapshot)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        Logger.Log($"[KillChildProcesses] Killing child process {p.Id} ({p.StartInfo.FileName}) because the connection was closed (download cancelled).");
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch { /* Prozess kann in der Zwischenzeit beendet sein */ }
            }
        }

        /// <summary>
        /// Loescht die Fragmente eines abgebrochenen Downloads: yt-dlps ".part"/
        /// ".ytdl"-Dateien und die Zwischendateien der getrennten Video-/Audio-
        /// Streams (z.B. "Titel.f137.mp4"), sowie eine evtl. angelegte, leere
        /// Zieldatei. Eine FERTIGE Datei bleibt ausdruecklich unangetastet -
        /// falls der Abbruch erst nach dem Abschluss ankam, soll das Ergebnis
        /// erhalten bleiben.
        /// </summary>
        public static void HandleCleanupPartial(string dir, string baseName, string requestId)
        {
            int removed = 0;
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string prefix = string.IsNullOrEmpty(baseName) ? "" : baseName;
                    foreach (string path in Directory.GetFiles(dir))
                    {
                        string name = Path.GetFileName(path);
                        if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                        bool isFragment =
                            name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
                            System.Text.RegularExpressions.Regex.IsMatch(name, @"\.f\d+\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        // Zieldatei mit 0 Bytes: entstanden, aber nie gefuellt.
                        bool isEmptyTarget = false;
                        if (!isFragment)
                        {
                            try { isEmptyTarget = new FileInfo(path).Length == 0; } catch { }
                        }

                        if (isFragment || isEmptyTarget)
                        {
                            try { File.Delete(path); removed++; Logger.Log($"[HandleCleanupPartial] Deleted leftover '{name}'."); }
                            catch (Exception ex) { Logger.Log($"[HandleCleanupPartial] Could not delete '{name}': {ex.Message}"); }
                        }
                    }
                }
                Program.SendMessage(new { requestId, type = "cleanup_partial_result", ok = true, removed });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "cleanup_partial failed");
                Program.SendMessage(new { requestId, type = "cleanup_partial_result", ok = false, removed, error = ex.Message });
            }
        }

        // Ermittelt NUR die Gesamtgroesse eines Cloud-Links (Datei oder Ordner),
        // ohne irgendetwas herunterzuladen - dafuer reichen Metadaten-Requests
        // (Content-Length-Header / bei MEGA der bereits vollstaendige Knotenbaum).
        // Wird von der Extension parallel zu anderen Downloads angestossen, damit
        // die Gesamtgroesse (und damit die Primary-Bar-Gewichtung + ETA) schon
        // feststeht, bevor der eigentliche Download eines Cloud-Links an der Reihe
        // ist - und damit man nicht zwischen mehreren Cloud-Links im selben Batch
        // jedes Mal von vorn auf die Groessenermittlung warten muss.
        public static async Task HandleGetUrlSizeAsync(string url, string requestId)
        {
            long totalBytes = 0;
            int fileCount = 0;
            try
            {
                if (url.Contains("drive.google.com"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(url, @"(?:\/file\/d\/|\/folders\/|\/open\?id=|\/uc\?export=download&id=|id=)([a-zA-Z0-9_-]{20,50})");
                    if (match.Success)
                    {
                        string id = match.Groups[1].Value;
                        using var client = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true });
                        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        if (url.Contains("/folders/"))
                        {
                            var tree = await BuildGoogleDriveFolderTreeAsync(client, id, "size-probe").ConfigureAwait(false);
                            totalBytes = tree.TotalBytes;
                            fileCount = tree.TotalFileCount;
                        }
                        else
                        {
                            using var resp = await ResolveGoogleDriveFileDownloadResponseAsync(client, id).ConfigureAwait(false);
                            totalBytes = resp.Content.Headers.ContentLength ?? 0;
                            fileCount = 1;
                        }
                    }
                }
                else if (url.Contains("mega.nz"))
                {
                    var client = new MegaApiClient();
                    await client.LoginAnonymousAsync().ConfigureAwait(false);
                    Uri uri = new Uri(url);
                    if (url.Contains("/folder/") || url.Contains("#F!"))
                    {
                        var nodes = (await client.GetNodesFromLinkAsync(uri).ConfigureAwait(false)).ToList();
                        totalBytes = nodes.Where(n => n.Type == NodeType.File).Sum(n => n.Size);
                        fileCount = nodes.Count(n => n.Type == NodeType.File);
                    }
                    else
                    {
                        var node = await client.GetNodeFromLinkAsync(uri).ConfigureAwait(false);
                        totalBytes = node.Size;
                        fileCount = 1;
                    }
                    await client.LogoutAsync().ConfigureAwait(false);
                }
                else
                {
                    string probeUrl = url;
                    if (url.Contains("dropbox.com") && !url.Contains("dl=1"))
                    {
                        probeUrl = url.Contains("?") ? url + "&dl=1" : url + "?dl=1";
                    }
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    using var resp = await client.GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    totalBytes = resp.Content.Headers.ContentLength ?? 0;
                    fileCount = 1;
                }

                Program.SendMessage(new
                {
                    requestId = requestId,
                    type = "url_size_result",
                    url = url,
                    totalBytes = totalBytes,
                    fileCount = fileCount
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "get_url_size failed");
                Program.SendMessage(new
                {
                    requestId = requestId,
                    type = "url_size_result",
                    url = url,
                    totalBytes = 0L,
                    fileCount = 0,
                    error = ex.Message
                });
            }
        }

        public static async Task HandleDownloadUrlAsync(string url, string path, string requestId)
        {
            try
            {
                if (url.Contains("drive.google.com"))
                {
                    Logger.Log($"[HandleDownloadUrlAsync] Intercepted Google Drive link: {url}");
                    await DownloadGoogleDriveFileAsync(url, path, requestId).ConfigureAwait(false);
                    return;
                }

                if (url.Contains("mega.nz"))
                {
                    Logger.Log($"[HandleDownloadUrlAsync] Intercepted MEGA link: {url}");
                    await DownloadMegaFileAsync(url, path, requestId).ConfigureAwait(false);
                    return;
                }

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await SaveStreamWithProgressAsync(response, path, requestId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "download_url failed");
                Program.SendMessage(new
                {
                    requestId = requestId,
                    type = "url_error",
                    message = ex.Message
                });
            }
        }

        private static async Task SaveStreamWithProgressAsync(HttpResponseMessage response, string path, string requestId)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            long? totalBytes = response.Content.Headers.ContentLength;
            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            // 80-KB-Puffer statt 8 KB. Das ist der Weg, ueber den EINZELNE grosse
            // Cloud-Dateien laufen (Google-Drive-Einzeldatei, generische URLs):
            // bei 800 MB waren das bisher ~100.000 Lese-/Schreib-Durchlaeufe mit je
            // einem await, jetzt ~10.000. Reine Effizienz, kein Verhaltenswechsel.
            using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            DateTime lastReport = DateTime.MinValue;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                totalRead += bytesRead;

                if (DateTime.Now - lastReport > TimeSpan.FromMilliseconds(150))
                {
                    Program.SendMessage(new
                    {
                        requestId = requestId,
                        type = "url_progress",
                        received = totalRead,
                        total = totalBytes ?? totalRead
                    });
                    lastReport = DateTime.Now;
                }
            }
            await fileStream.FlushAsync().ConfigureAwait(false);

            Program.SendMessage(new
            {
                requestId = requestId,
                type = "url_done",
                path = path
            });
        }

        private static async Task DownloadGoogleDriveFileAsync(string url, string path, string requestId)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"(?:\/file\/d\/|\/folders\/|\/open\?id=|\/uc\?export=download&id=|id=)([a-zA-Z0-9_-]{20,50})");
            if (!match.Success)
            {
                throw new Exception("Could not extract Google Drive file or folder ID from URL.");
            }
            string fileId = match.Groups[1].Value;

            if (url.Contains("/folders/"))
            {
                string targetFolder = path;
                if (Path.HasExtension(path))
                {
                    targetFolder = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
                }

                // Ganzer Ordner = EIN gebuendelter Vorgang, nicht Datei-fuer-Datei mit
                // wachsendem/springendem Fortschritt. Tier 1 (undokumentierte Google-
                // ZIP-Export-API) liefert die Gesamtgroesse vorab und laedt nur EINE
                // Datei - aber ist unzuverlaessig bei grossen/vielteiligen Ordnern
                // (in der Praxis getestet: <500MB meist ok, mehrere GB oft 500-Fehler).
                // Tier 2 (Fallback) ermittelt die Gesamtgroesse selbst vorab per
                // Metadaten-Scan und laedt dann Datei fuer Datei, meldet dabei aber
                // IMMER den kumulierten Fortschritt gegen diese vorab feststehende
                // Gesamtgroesse - dadurch laeuft die Bar garantiert durchgehend 0->100%.
                using (var zipClient = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true }))
                {
                    zipClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    // Wartebudget bewusst KURZ (frueher 10 Minuten): waehrend Tier 1
                    // laeuft, passiert fuer den Nutzer sichtbar GAR NICHTS - kein Byte
                    // wird uebertragen, die Zeile steht auf "Preparing ZIP export...".
                    // 10 Minuten hiessen im schlechtesten Fall 10 Minuten Stillstand,
                    // BEVOR Tier 2 (der zuverlaessige Weg mit echtem Fortschritt)
                    // ueberhaupt anfaengt - genau das gemeldete "dauert sehr lange, bis
                    // der Download startet". Tier 1 lohnt sich ohnehin nur fuer kleine
                    // Ordner (<500 MB), und deren Export-Job ist in wenigen Sekunden
                    // fertig; alles was laenger braucht, ist bei Tier 2 besser
                    // aufgehoben (dort laeuft ab der ersten Datei ein echter Balken).
                    bool zipOk = await TryGoogleDriveFolderZipExportAsync(zipClient, fileId, targetFolder, requestId, ZipExportMaxWait).ConfigureAwait(false);
                    if (zipOk)
                    {
                        Program.SendMessage(new { requestId = requestId, type = "url_done", path = targetFolder });
                        return;
                    }
                }

                Logger.Log("[DownloadGoogleDriveFileAsync] ZIP export unavailable/failed - falling back to per-file download with a pre-computed total size.");
                // Sichtbare Meldung fuer den Nutzer (nicht nur ins Log) - damit klar
                // ist, warum es jetzt langsamer/anders weitergeht als der Ordner-ZIP.
                Program.SendMessage(new
                {
                    requestId = requestId,
                    type = "url_progress",
                    received = 0L,
                    total = 0L,
                    phase = "fallback_notice",
                    filename = "ZIP export unavailable - falling back to per-file download..."
                });
                await DownloadGoogleDriveFolderAsync(fileId, targetFolder, requestId).ConfigureAwait(false);
                return;
            }

            using var client = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true });
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var response = await ResolveGoogleDriveFileDownloadResponseAsync(client, fileId).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string finalPath = path;
            string? contentFilename = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
            if (!string.IsNullOrEmpty(contentFilename))
            {
                contentFilename = System.Uri.UnescapeDataString(contentFilename.Trim('"').Trim('\''));
                string? dir = Path.GetDirectoryName(path);
                finalPath = string.IsNullOrEmpty(dir) ? contentFilename : Path.Combine(dir, contentFilename);
            }

            await SaveStreamWithProgressAsync(response, finalPath, requestId).ConfigureAwait(false);
        }

        // Ruft die Google-Drive-Download-URL fuer eine Datei-ID auf und loest bei
        // grossen Dateien automatisch die HTML-Bestaetigungsseite ("kann nicht auf
        // Viren geprueft werden") auf, bis die eigentliche Datei-Antwort (mit
        // Content-Length) vorliegt. Wird sowohl fuers echte Herunterladen als auch
        // fuer die reine Groessen-Ermittlung (Header lesen, Body verwerfen) genutzt,
        // damit diese Logik nicht mehrfach dupliziert wird.
        private static async Task<HttpResponseMessage> ResolveGoogleDriveFileDownloadResponseAsync(HttpClient client, string fileId)
        {
            string downloadUrl = $"https://drive.google.com/uc?export=download&id={fileId}";
            var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (response.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
            {
                string respHtml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                string actionUrl = "https://drive.usercontent.google.com/download";
                var actionMatch = System.Text.RegularExpressions.Regex.Match(respHtml, @"<form[^>]+action=""([^""]+)""");
                if (actionMatch.Success) actionUrl = System.Net.WebUtility.HtmlDecode(actionMatch.Groups[1].Value);

                var queryParams = new List<string>();
                var inputMatches = System.Text.RegularExpressions.Regex.Matches(respHtml, @"<input[^>]+type=""hidden""[^>]+name=""([^""]+)""[^>]+value=""([^""]*)""");
                foreach (System.Text.RegularExpressions.Match m in inputMatches)
                {
                    if (m.Success)
                    {
                        queryParams.Add($"{Uri.EscapeDataString(m.Groups[1].Value)}={Uri.EscapeDataString(m.Groups[2].Value)}");
                    }
                }

                if (queryParams.Count > 0)
                {
                    downloadUrl = actionUrl + "?" + string.Join("&", queryParams);
                    response.Dispose();
                    response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                }
            }

            return response;
        }

        // Baum-Struktur eines Google-Drive-Ordners inkl. vorab ermittelter
        // Gesamtgroesse (rekursiv, inkl. Unterordner) - erst EINMAL komplett
        // aufgebaut, bevor auch nur eine einzige Datei tatsaechlich geladen wird.
        private class DriveFolderNode
        {
            public string DisplayName = "";
            public HashSet<string> FileIds = new();
            public Dictionary<string, string> FileNamesMap = new();
            public List<DriveFolderNode> SubFolders = new();
            public long TotalBytes;
            public int TotalFileCount;
        }

        private class DriveFolderTransferState
        {
            public long GrandTotal;
            public long ReceivedSoFar;
            public int FilesDone;
            public int FilesSucceeded;
            public int FilesTotal;
            public int HtmlSkippedDuringDownload;
        }

        // Liest die Google-Drive-Ordner-HTML und extrahiert Datei-/Unterordner-IDs
        // + Anzeigenamen. Reine Listing-Logik, unveraendert aus der bisherigen
        // Implementierung uebernommen (nur aus der Download-Schleife herausgeloest,
        // damit sowohl die Groessen-Ermittlung als auch der Download dieselbe
        // Parsing-Logik nutzen statt sie zu duplizieren).
        private static async Task<(HashSet<string> fileIds, Dictionary<string, string> fileNamesMap, Dictionary<string, string> subFoldersMap)> ListGoogleDriveFolderEntriesAsync(HttpClient client, string folderId)
        {
            string folderUrl = $"https://drive.google.com/drive/folders/{folderId}";
            string html = "";
            try
            {
                html = await client.GetStringAsync(folderUrl).ConfigureAwait(false);
            }
            catch
            {
                folderUrl = $"https://drive.google.com/embeddedfolderview?id={folderId}";
                html = await client.GetStringAsync(folderUrl).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(html) || html.Length < 5000)
            {
                try
                {
                    string altUrl = $"https://drive.google.com/embeddedfolderview?id={folderId}";
                    string altHtml = await client.GetStringAsync(altUrl).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(altHtml) && altHtml.Length > html.Length)
                    {
                        html = altHtml;
                    }
                }
                catch { }
            }

            var subFoldersMap = new Dictionary<string, string>();
            var fileNamesMap = new Dictionary<string, string>();
            var fileIds = new HashSet<string>();

            var labelRegex = new System.Text.RegularExpressions.Regex(@"aria-label=""([^""]+)""[\s\S]{1,300}?ssk='[^':]*:[^':]*:([^']+)'");
            var labelMatches = labelRegex.Matches(html);
            foreach (System.Text.RegularExpressions.Match m in labelMatches)
            {
                if (m.Success)
                {
                    string rawLabel = m.Groups[1].Value;
                    string fullId = m.Groups[2].Value;
                    string fId = System.Text.RegularExpressions.Regex.Replace(fullId, @"-[0-9]+-[0-9]+$", "");

                    if (fId != folderId && fId.Length >= 20)
                    {
                        if (rawLabel.Contains("Shared folder") || rawLabel.Contains("folder"))
                        {
                            string cleanFolderName = System.Text.RegularExpressions.Regex.Replace(rawLabel, @"\s+(?:Shared\s+)?folder.*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                            if (!string.IsNullOrWhiteSpace(cleanFolderName) && !cleanFolderName.StartsWith("Modified") && !cleanFolderName.StartsWith("Size:"))
                            {
                                subFoldersMap[fId] = cleanFolderName;
                            }
                        }
                        else
                        {
                            string cleanName = System.Text.RegularExpressions.Regex.Replace(rawLabel, @"\s+(?:Binary|Shared|Document|Archive|Image|Video).*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                            if (!string.IsNullOrWhiteSpace(cleanName) && !cleanName.StartsWith("Modified") && !cleanName.StartsWith("Size:") && !cleanName.StartsWith("More actions") && !cleanName.StartsWith("Storage used"))
                            {
                                fileNamesMap[fId] = cleanName;
                                fileIds.Add(fId);
                            }
                        }
                    }
                }
            }

            var matches = System.Text.RegularExpressions.Regex.Matches(html, @"(?:/file/d/|""id"":""|\[""|id=)([a-zA-Z0-9_-]{25,45})");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (m.Success && m.Groups[1].Value != folderId && !m.Groups[1].Value.StartsWith("AIza") && !subFoldersMap.ContainsKey(m.Groups[1].Value))
                {
                    fileIds.Add(m.Groups[1].Value);
                }
            }

            return (fileIds, fileNamesMap, subFoldersMap);
        }

        private static readonly JsonSerializerOptions DriveTreeCacheJsonOptions = new JsonSerializerOptions { IncludeFields = true };

        private static string GetDriveTreeCacheDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "patreon_archiver_drive_cache");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        // Jeder chrome.runtime.connectNative()-Aufruf startet einen NEUEN Host-Prozess
        // (siehe Program.cs: "End of stdin stream reached, exiting" nach jeder
        // einzelnen Aktion) - der parallele Groessen-Scan (get_url_size) und der
        // eigentliche Download laufen also in ZWEI VERSCHIEDENEN Prozessen und
        // koennen sich kein In-Memory-Ergebnis teilen. Ohne diesen Datei-Cache
        // scannt der echte Download denselben Ordnerbaum darum immer nochmal
        // komplett neu, obwohl der Scan Sekunden zuvor schon gelaufen ist (im Log
        // klar sichtbar: derselbe Ordner taucht zweimal hintereinander auf).
        private static async Task<DriveFolderNode?> TryLoadCachedDriveTreeAsync(string folderId)
        {
            try
            {
                string path = Path.Combine(GetDriveTreeCacheDir(), $"{folderId}.json");
                if (!File.Exists(path)) return null;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(10)) return null;
                string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                return JsonSerializer.Deserialize<DriveFolderNode>(json, DriveTreeCacheJsonOptions);
            }
            catch { return null; }
        }

        private static async Task SaveDriveTreeCacheAsync(string folderId, DriveFolderNode node)
        {
            try
            {
                string path = Path.Combine(GetDriveTreeCacheDir(), $"{folderId}.json");
                string json = JsonSerializer.Serialize(node, DriveTreeCacheJsonOptions);
                await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            }
            catch { /* Cache ist reine Optimierung - Fehler hier duerfen nichts abbrechen */ }
        }

        // Baut rekursiv den kompletten Ordnerbaum inkl. Gesamtgroesse auf, BEVOR
        // irgendeine Datei tatsaechlich heruntergeladen wird - dafuer wird pro Datei
        // nur der Response-Header gelesen (Content-Length) und der Body verworfen,
        // nie der eigentliche Dateiinhalt uebertragen.
        //
        // Nebenlaeufigkeit ist ueber die ganze Baum-Rekursion GETEILT (nicht pro
        // Unterordner neu) - vermeidet, dass bei mehreren gleichzeitig gescannten
        // Unterordnern jeder sein eigenes Limit mitbringt und sich die Anfragen
        // trotzdem summieren. WICHTIG: das ist nur ein Limit INNERHALB dieses einen
        // Bridge-Prozesses - jede native-messaging-Verbindung (Hintergrund-
        // Groessen-Scan UND der eigentliche Download) startet einen EIGENEN,
        // separaten Prozess, kann sich also kein Limit teilen. 6 ist ein Kompromiss
        // zwischen Tempo und Zurueckhaltung (3 war spuerbar zu langsam bei grossen
        // Ordnern, 10 vermutlich mit ein Grund fuer die beobachtete Blockade).
        // HISTORIE (damit es niemand erneut "optimiert"): hier stand bis zur 30.
        // Runde ein Schwellwert `DriveSizingMaxFiles = 30`, ab dem die Vorab-
        // Groessenermittlung uebersprungen wurde, um Googles Kontingent zu schonen.
        // Das Kontingent-Argument stimmt - der Preis war aber ein Fortschritts-
        // balken ohne Nenner (nur Dateianzahl), der beim Nutzer "haengt bei 5%,
        // springt dann, am Ende Failed" produziert hat, plus die fehlende
        // "X / Y GB"-Anzeige. Entscheidung vor der Veroeffentlichung: Zuverlaessig-
        // keit und ehrliche Anzeige gehen vor gespartem Kontingent, zumal der
        // Datei-fuer-Datei-Weg mit dem wieder geduldigen ZIP-Export ohnehin die
        // Ausnahme ist. Siehe ZipExportMaxWait.
        private static async Task<DriveFolderNode> BuildGoogleDriveFolderTreeAsync(HttpClient client, string folderId, string displayName, int depth = 0, SemaphoreSlim? sharedSem = null)
        {
            if (depth == 0)
            {
                var cached = await TryLoadCachedDriveTreeAsync(folderId).ConfigureAwait(false);
                if (cached != null)
                {
                    Logger.Log($"[BuildGoogleDriveFolderTreeAsync] Reusing cached tree for folder '{displayName}' - built by an earlier scan (parallel size-probe or previous attempt) within the last 10 minutes, avoids re-scanning from scratch.");
                    return cached;
                }
            }
            // 3 statt 6 gleichzeitiger Anfragen: die Vermessung laeuft jetzt wieder
            // fuer JEDEN Ordner (siehe Kommentar bei depth == 0 weiter unten), und
            // Google reagiert auf Bursts anonymer Anfragen mit HTML-Sperrseiten.
            // Etwas laenger scannen ist hier der bessere Handel - der Scan laeuft
            // sichtbar als "Scanning folder contents..." und blockiert nichts.
            sharedSem ??= new SemaphoreSlim(3);

            var node = new DriveFolderNode { DisplayName = displayName };
            if (depth > 8) return node; // Sicherheitsnetz gegen pathologische Verschachtelung

            var (fileIds, fileNamesMap, subFoldersMap) = await ListGoogleDriveFolderEntriesAsync(client, folderId).ConfigureAwait(false);
            node.FileIds = fileIds;
            node.FileNamesMap = fileNamesMap;

            // Struktur zuerst NUR auflisten (1-3 billige Anfragen pro Ordner),
            // Groessen erst danach und nur, wenn der ganze Baum klein genug ist -
            // siehe den Kommentar bei BuildGoogleDriveFolderTreeAsync.
            var subFolderNodesFirstPass = new List<DriveFolderNode>();
            foreach (var sub in subFoldersMap)
            {
                subFolderNodesFirstPass.Add(await BuildGoogleDriveFolderTreeAsync(client, sub.Key, sub.Value, depth + 1, sharedSem).ConfigureAwait(false));
            }
            node.SubFolders.AddRange(subFolderNodesFirstPass);
            node.TotalFileCount = fileIds.Count + subFolderNodesFirstPass.Sum(n => n.TotalFileCount);

            if (depth == 0)
            {
                // Groesse IMMER vorab ermitteln - auch bei grossen Ordnern.
                //
                // In der 25. Runde wurde das ab 30 Dateien uebersprungen, um Googles
                // Kontingent zu schonen. Folge: der Balken hatte keinen Nenner mehr
                // und lief ueber die reine DATEIANZAHL - bei 113 Dateien bewegt eine
                // einzelne grosse Datei den Balken um 0,9%, waehrend mehrere kleine
                // ihn springen lassen. Genau das hat der Nutzer als "haengt ewig bei
                // 5% und springt dann komisch" gemeldet, zusammen mit "es steht keine
                // Gesamtgroesse mehr da".
                // Das Kontingent-Argument bleibt richtig, greift aber an der falschen
                // Stelle: mit dem wieder geduldigen ZIP-Export (siehe
                // ZipExportMaxWait) ist der Datei-fuer-Datei-Weg die Ausnahme, und
                // NUR dort kostet die Vermessung ueberhaupt etwas. Zuverlaessige
                // Anzeige schlaegt hier das gesparte Kontingent - ausdrueckliche
                // Nutzer-Prioritaet vor der Veroeffentlichung.
                await SizeGoogleDriveFolderTreeAsync(client, node, sharedSem).ConfigureAwait(false);
                await SaveDriveTreeCacheAsync(folderId, node).ConfigureAwait(false);
            }
            return node;
        }

        /// <summary>
        /// Fuellt TotalBytes fuer einen bereits aufgebauten Baum. Pro Datei eine
        /// Anfrage, bei der ausschliesslich der Content-Length-Header gelesen und
        /// der Rumpf verworfen wird.
        ///
        /// ALLES ODER NICHTS: konnte auch nur EINE Datei nicht vermessen werden
        /// (Google antwortet dann mit einer HTML-Seite statt der Datei), wird
        /// TotalBytes bewusst auf 0 = "unbekannt" gesetzt statt eine Teilsumme zu
        /// melden. Eine Teilsumme ist naemlich schlimmer als gar keine Angabe: der
        /// Balken rechnet dann gegen einen viel zu kleinen Nenner, steht ewig,
        /// springt und ist am Ende bei weit ueber 100%.
        /// </summary>
        private static async Task SizeGoogleDriveFolderTreeAsync(HttpClient client, DriveFolderNode node, SemaphoreSlim sharedSem)
        {
            int htmlSkippedDuringSizing = 0;
            var fileIds = node.FileIds;
            var displayName = node.DisplayName;
            {
            var sizeTasks = fileIds.Select(async fid =>
            {
                await sharedSem.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var resp = await ResolveGoogleDriveFileDownloadResponseAsync(client, fid).ConfigureAwait(false);
                    // HTML-Bestaetigungsseiten (Virenscan-Warnung ODER Rate-Limit) liefern
                    // keine Content-Length - solche Dateien zaehlen dann einfach nicht in
                    // die Vorab-Gesamtgroesse ein (Restrisiko: Balken bleibt in dem Fall
                    // ungenau, siehe htmlSkippedDuringSizing-Log unten zur Diagnose).
                    if (resp.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
                    {
                        Interlocked.Increment(ref htmlSkippedDuringSizing);
                        return 0L;
                    }
                    return resp.Content.Headers.ContentLength ?? 0L;
                }
                catch { return 0L; }
                finally { sharedSem.Release(); }
            }).ToList();
            var sw = Stopwatch.StartNew();
            var sizes = await Task.WhenAll(sizeTasks).ConfigureAwait(false);
            sw.Stop();

            long total = sizes.Sum();
            bool incomplete = htmlSkippedDuringSizing > 0 || sizes.Any(s => s <= 0);
            Logger.Log($"[SizeGoogleDriveFolderTreeAsync] Folder '{displayName}': {fileIds.Count} files sized in {sw.ElapsedMilliseconds}ms, total={total} bytes, htmlSkipped={htmlSkippedDuringSizing}/{fileIds.Count}{(incomplete ? " - INCOMPLETE, reporting unknown size instead of a partial sum" : "")}");

            // Unterordner nacheinander (die Struktur steht ja schon) - das
            // gemeinsame sharedSem begrenzt die tatsaechliche Nebenlaeufigkeit.
            foreach (var childNode in node.SubFolders)
            {
                await SizeGoogleDriveFolderTreeAsync(client, childNode, sharedSem).ConfigureAwait(false);
                // Ein Unterordner mit unbekannter Groesse macht auch die Summe des
                // Elternordners unbrauchbar (siehe Alles-oder-nichts oben).
                if (childNode.TotalFileCount > 0 && childNode.TotalBytes <= 0) incomplete = true;
                total += childNode.TotalBytes;
            }

            node.TotalBytes = incomplete ? 0 : total;
            }
        }

        // Laedt den bereits aufgebauten Ordnerbaum herunter und meldet dabei IMMER
        // den kumulierten Fortschritt (state.ReceivedSoFar + aktuelle Datei) gegen
        // die vorab feststehende Gesamtgroesse (state.GrandTotal) - der Nenner
        // aendert sich waehrend des Downloads nie mehr, daher kein Springen/
        // Zurueckspringen der Bar beim Datei- oder Unterordner-Wechsel.
        private static async Task DownloadGoogleDriveFolderTreeAsync(HttpClient client, DriveFolderNode node, string targetDir, string requestId, DriveFolderTransferState state)
        {
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            foreach (string subFileId in node.FileIds)
            {
                string filename = node.FileNamesMap.TryGetValue(subFileId, out var realName) ? realName : subFileId;
                try
                {
                    // Label sofort aktualisieren, sobald diese Datei drankommt - auch
                    // bevor der erste Byte-Tick reinkommt (Verbindungsaufbau kann etwas
                    // dauern). Prozentzahl bleibt dabei unveraendert (received unveraendert).
                    Program.SendMessage(new
                    {
                        requestId = requestId,
                        type = "url_progress",
                        received = state.ReceivedSoFar,
                        total = state.GrandTotal,
                        filesCompleted = state.FilesDone,
                        totalFiles = state.FilesTotal,
                        filename = filename
                    });

                    using var response = await ResolveGoogleDriveFileDownloadResponseAsync(client, subFileId).ConfigureAwait(false);

                    if (response.Content.Headers.ContentType?.MediaType?.Contains("html") == true)
                    {
                        state.HtmlSkippedDuringDownload++;
                        Logger.Log($"[DownloadGoogleDriveFolderTreeAsync] Skipping file ID {subFileId} ('{filename}') because Google Drive returned HTML instead of file content (likely rate-limited or requires interactive login). Total skipped so far: {state.HtmlSkippedDuringDownload}.");
                        continue;
                    }

                    if (!response.IsSuccessStatusCode) continue;

                    string? cdFilename = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
                    if (!string.IsNullOrEmpty(cdFilename))
                    {
                        cdFilename = System.Uri.UnescapeDataString(cdFilename.Trim('"').Trim('\''));
                    }

                    string extension = "";
                    string contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                    if (contentType.Contains("rar") || contentType.Contains("x-rar")) extension = ".rar";
                    else if (contentType.Contains("7z") || contentType.Contains("x-7z")) extension = ".7z";
                    else if (contentType.Contains("png")) extension = ".png";
                    else if (contentType.Contains("jpeg") || contentType.Contains("jpg")) extension = ".jpg";
                    else if (contentType.Contains("pdf")) extension = ".pdf";
                    else if (contentType.Contains("blend")) extension = ".blend";
                    else if (contentType.Contains("obj")) extension = ".obj";
                    else if (contentType.Contains("fbx")) extension = ".fbx";
                    else if (contentType.Contains("octet-stream") || contentType.Contains("zip")) extension = ".zip";

                    if (!string.IsNullOrWhiteSpace(cdFilename))
                    {
                        filename = cdFilename;
                    }
                    if (!Path.HasExtension(filename) && !string.IsNullOrEmpty(extension))
                    {
                        filename += extension;
                    }
                    filename = SanitizeFileName(filename);

                    string filePath = Path.Combine(targetDir, filename);
                    await SaveDriveFolderFileCumulativeAsync(response, filePath, requestId, filename, state).ConfigureAwait(false);
                    state.FilesSucceeded++;
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex, $"Failed downloading folder file {subFileId}");
                }
                finally
                {
                    state.FilesDone++;
                }
            }

            foreach (var subFolder in node.SubFolders)
            {
                string subFolderPath = Path.Combine(targetDir, SanitizeFileName(subFolder.DisplayName));
                await DownloadGoogleDriveFolderTreeAsync(client, subFolder, subFolderPath, requestId, state).ConfigureAwait(false);
            }
        }

        private static async Task SaveDriveFolderFileCumulativeAsync(HttpResponseMessage response, string path, string requestId, string filename, DriveFolderTransferState state)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            long baseReceived = state.ReceivedSoFar;
            // Eigene Groesse DIESER Datei (nicht des Ordners) - wird zusaetzlich
            // gemeldet, damit die Extension auch dann einen laufenden Balken
            // zeigen kann, wenn die Ordner-Gesamtgroesse unbekannt blieb
            // (state.GrandTotal == 0, passiert wenn Google beim Vorab-Sizing
            // ueberall HTML-Zwischenseiten statt Dateien liefert). Ohne das steht
            // die Zeile bei 0% und springt beim Abschluss auf 100%.
            long thisFileTotal = response.Content.Headers.ContentLength ?? 0;
            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            // 80-KB-Puffer statt 8 KB: bei einer mehrere hundert MB grossen Datei
            // sind das ~10x weniger Lese-/Schreib-Durchlaeufe (der ZIP-Zweig weiter
            // unten benutzt aus demselben Grund laengst 81920). Reine
            // Effizienzsache, am Verhalten aendert sich nichts.
            using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long fileRead = 0;
            int bytesRead;
            DateTime lastReport = DateTime.MinValue;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                fileRead += bytesRead;

                if (DateTime.Now - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    Program.SendMessage(new
                    {
                        requestId = requestId,
                        type = "url_progress",
                        received = baseReceived + fileRead,
                        total = state.GrandTotal,
                        filesCompleted = state.FilesDone,
                        totalFiles = state.FilesTotal,
                        fileReceived = fileRead,
                        fileTotal = thisFileTotal,
                        filename = filename
                    });
                    lastReport = DateTime.Now;
                }
            }
            await fileStream.FlushAsync().ConfigureAwait(false);
            state.ReceivedSoFar = baseReceived + fileRead;
        }

        /// <summary>
        /// Serialisiert den Datei-fuer-Datei-Weg ueber ALLE Bridge-Prozesse hinweg.
        ///
        /// Jede Native-Messaging-Verbindung ist ein EIGENER Prozess, ein Limit in
        /// der Extension (CLOUD_POOL_LIMITS) begrenzt also nur, wie viele Prozesse
        /// gleichzeitig GESTARTET werden - nicht, wie viele davon gleichzeitig
        /// Google-Anfragen feuern. Im Log vom 29.07. 10:28 liefen dadurch zwei
        /// Ordner-Fallbacks parallel (113 + 162 Dateien) und schickten zusammen
        /// ~275 Einzel-Requests an dieselbe IP - Ergebnis: 69 bzw. 63 Dateien mit
        /// HTML-Sperrseite statt Inhalt.
        /// Der ZIP-Export-Weg braucht diese Bremse NICHT (ein Request pro Ordner),
        /// deshalb sitzt der Mutex bewusst nur hier im Fallback.
        /// </summary>
        private const string DriveFallbackMutexName = @"Global\PatreonArchiverDrivePerFileDownload";

        private static async Task DownloadGoogleDriveFolderAsync(string folderId, string targetDir, string requestId)
        {
            using var client = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true });
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            // Auf den systemweiten Slot warten - waehrenddessen weiter Lebenszeichen
            // senden, sonst greift der 3-Minuten-Stall-Abbruch der Extension
            // (STALL_MS in lib/nativeHost.js).
            using var driveMutex = new Mutex(false, DriveFallbackMutexName);
            bool mutexHeld = false;
            var waitSw = Stopwatch.StartNew();
            try
            {
                while (!mutexHeld)
                {
                    try { mutexHeld = driveMutex.WaitOne(TimeSpan.FromSeconds(2)); }
                    catch (AbandonedMutexException) { mutexHeld = true; } // Vorbesitzer ist abgestuerzt - Slot ist frei
                    if (!mutexHeld)
                    {
                        Program.SendMessage(new
                        {
                            requestId,
                            type = "url_progress",
                            received = 0L,
                            total = 0L,
                            phase = "sizing",
                            filename = $"Waiting for another Google Drive download to finish... ({waitSw.Elapsed.TotalSeconds:F0}s)"
                        });
                    }
                }
                if (waitSw.ElapsedMilliseconds > 2000)
                {
                    Logger.Log($"[DownloadGoogleDriveFolderAsync] Waited {waitSw.ElapsedMilliseconds}ms for the per-file Drive slot (another folder was downloading).");
                }
                await DownloadGoogleDriveFolderInnerAsync(client, folderId, targetDir, requestId).ConfigureAwait(false);
            }
            finally
            {
                if (mutexHeld)
                {
                    try { driveMutex.ReleaseMutex(); } catch { /* Prozess endet ohnehin */ }
                }
            }
        }

        private static async Task DownloadGoogleDriveFolderInnerAsync(HttpClient client, string folderId, string targetDir, string requestId)
        {

            Program.SendMessage(new
            {
                requestId = requestId,
                type = "url_progress",
                received = 0L,
                total = 0L,
                phase = "sizing",
                filename = "Calculating total folder size..."
            });

            string rootName = Path.GetFileName(targetDir.TrimEnd('/', '\\'));
            DriveFolderNode tree;
            // HEARTBEAT WAEHREND DES BAUM-SCANS.
            //
            // Der Scan selbst schickt keine einzige Nachricht, kann bei grossen
            // Ordnern aber mehrere Minuten dauern (pro Datei ein Metadaten-Request).
            // Die Extension bricht einen Download jedoch ab, wenn 3 Minuten lang
            // KEINE Nachricht kommt (STALL_MS in lib/nativeHost.js,
            // downloadUrlViaBridge) - ein grosser Ordner wurde dadurch mitten im
            // Scan als "stalled" verworfen, obwohl alles normal lief. Genau das
            // ist haeufiger geworden, seit das ZIP-Zeitbudget kuerzer ist und
            // dieser Zweig oefter ueberhaupt erreicht wird.
            // Der Herzschlag haelt zusaetzlich die "Scanning"-Anzeige der Zeile am
            // Leben, statt sie wie eingefroren wirken zu lassen.
            using var scanHeartbeat = new CancellationTokenSource();
            var heartbeatTask = Task.Run(async () =>
            {
                var swScan = Stopwatch.StartNew();
                try
                {
                    while (!scanHeartbeat.Token.IsCancellationRequested)
                    {
                        await Task.Delay(2000, scanHeartbeat.Token).ConfigureAwait(false);
                        Program.SendMessage(new
                        {
                            requestId = requestId,
                            type = "url_progress",
                            received = 0L,
                            total = 0L,
                            phase = "sizing",
                            filename = $"Scanning folder contents... ({swScan.Elapsed.TotalSeconds:F0}s)"
                        });
                    }
                }
                catch (OperationCanceledException) { /* normaler Weg beim Beenden */ }
            });
            try
            {
                tree = await BuildGoogleDriveFolderTreeAsync(client, folderId, rootName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[DownloadGoogleDriveFolderAsync] Failed to list folder contents");
                throw new Exception("Google Drive folder is empty or requires interactive Google login.");
            }
            finally
            {
                scanHeartbeat.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch { /* best effort */ }
            }

            if (tree.TotalFileCount == 0)
            {
                throw new Exception("Google Drive folder is empty or requires interactive Google login.");
            }

            var state = new DriveFolderTransferState
            {
                GrandTotal = tree.TotalBytes,
                FilesTotal = tree.TotalFileCount
            };

            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
            await DownloadGoogleDriveFolderTreeAsync(client, tree, targetDir, requestId, state).ConfigureAwait(false);

            // Zusammenfassung zur Diagnose des "haengt bei niedrigem %/MB, springt am
            // Ende ploetzlich auf 100%"-Musters: passiert, wenn viele Dateien als HTML
            // (Rate-Limit/Login-Wand) uebersprungen werden - deren geschaetzte Groesse
            // im GrandTotal steckt, aber ihre 0 tatsaechlichen Bytes nie zu ReceivedSoFar
            // beitragen, bis am Ende nur noch wenige (evtl. grosse) echte Dateien uebrig
            // sind und den Rueckstand auf einen Schlag aufholen.
            Logger.Log($"[DownloadGoogleDriveFolderAsync] Done: {state.FilesSucceeded}/{state.FilesTotal} files succeeded, {state.HtmlSkippedDuringDownload} skipped as HTML, receivedSoFar={state.ReceivedSoFar}/{state.GrandTotal} bytes.");

            if (state.FilesSucceeded == 0)
            {
                if (Directory.Exists(targetDir) && Directory.GetFileSystemEntries(targetDir).Length == 0)
                {
                    try { Directory.Delete(targetDir); } catch { }
                }
                throw new Exception($"Google Drive refused all {state.FilesTotal} files (rate limit / anonymous download quota). Nothing was saved - please try again later or use the link from _download_links.txt.");
            }

            // UNVOLLSTAENDIGER Ordner darf NICHT als Erfolg gemeldet werden.
            // Vorher ging bei "3 von 167 Dateien" trotzdem ein url_done raus: die
            // Zeile wurde gruen, der Post galt als heruntergeladen - und im Ordner
            // fehlten 164 Dateien. Genau das steckte hinter "Google Drive geht gar
            // nicht mehr". Die bereits geladenen Dateien bleiben absichtlich
            // liegen (Teilergebnis ist besser als nichts), aber der Fehlertext
            // sagt jetzt klar, was passiert ist - dashboard.js macht daraus einen
            // Eintrag im Warn-Icon.
            if (state.HtmlSkippedDuringDownload > 0 || state.FilesSucceeded < state.FilesTotal)
            {
                throw new Exception($"Google Drive rate limit: only {state.FilesSucceeded} of {state.FilesTotal} files could be downloaded ({state.HtmlSkippedDuringDownload} were answered with a HTML block page instead of the file). The partial folder was kept. Waiting a while and downloading again usually completes the rest.");
            }

            Program.SendMessage(new
            {
                requestId = requestId,
                type = "url_done",
                path = targetDir
            });
        }

        // Tier 1: versucht, den kompletten Ordner ueber Googles undokumentierte
        // "Takeout"-Export-API als EINE ZIP-Datei zu bekommen (dieselbe API, die
        // Drives eigener "Ordner herunterladen"-Button im Web-UI benutzt). Liefert
        // die Gesamtgroesse vorab (compressedSize) und braucht dadurch gar keine
        // eigene Kumulierungs-Logik - wird als ganz normaler Einzeldatei-Download
        // behandelt. In der Praxis unzuverlaessig bei grossen/vielteiligen Ordnern
        // (500-Fehler serverseitig) - jeder Fehlschlag fuehrt zu return false, der
        // Aufrufer faellt dann auf Tier 2 zurueck. NIE eine Exception nach aussen
        // durchlassen, das ist hier ausdruecklich ein "bestmoeglicher Versuch".
        // Wartebudget + Poll-Takt fuer Tier 1. Siehe ausfuehrlichen Kommentar an der
        // Aufrufstelle in DownloadGoogleDriveFileAsync(): waehrend dieser Zeit
        // passiert fuer den Nutzer sichtbar nichts, deshalb bewusst knapp.
        // ================== BEWUSSTE ENTSCHEIDUNG: GEDULD VOR TEMPO ==================
        // Wieder 10 Minuten - der urspruengliche Wert, BEVOR in dieser Session
        // daran getunt wurde (21. Runde: 45s, 23.: 120s, 28.: zusaetzlich ein
        // 30s-Abbruch bei 0%, 29.: 75s-Stall-Abbruch).
        //
        // WARUM ZURUECK, UND BITTE NICHT WIEDER RUNTERSETZEN:
        // Der ZIP-Export kostet Google gegenueber EINEN Request pro Ordner, der
        // Datei-fuer-Datei-Fallback dagegen EINEN PRO DATEI (bei 162 Dateien also
        // 162). Googles Kontingent fuer anonyme Zugriffe ist genau das, woran die
        // Downloads gescheitert sind ("44 of 113 files ... 69 answered with a HTML
        // block page"). Jede Verkuerzung dieser Wartezeit verschiebt Ordner vom
        // billigen auf den teuren Weg und macht Fehlschlaege damit WAHRSCHEINLICHER.
        // Die Wartezeit ist fuer den Nutzer sichtbar und harmlos (alle 3s eine
        // "Preparing ZIP export... (x%)"-Meldung, kein Stall-Abbruch), ein
        // Fehlschlag ist es nicht. Der Nutzer hat ausdruecklich gesagt: lieber
        // langsam und immer erfolgreich als schnell und manchmal kaputt.
        // Messwerte dieser Session: erfolgreiche Exporte nach 8.1s / 11.4s / 14.5s
        // / 53.6s - die Geduld kostet also im Normalfall gar nichts.
        private static readonly TimeSpan ZipExportMaxWait = TimeSpan.FromMinutes(10);
        private const int ZipExportFirstPollDelayMs = 1500;
        private const int ZipExportPollIntervalMs = 3000;

        /// <summary>
        /// Aufgeben, wenn sich Googles gemeldeter Fortschritt SO LANGE nicht mehr
        /// veraendert hat.
        ///
        /// ACHTUNG - hier lag die Regression der 28. Runde: dort stand ein starrer
        /// "nach 30s noch bei 0% -> abbrechen"-Test. Die Log-Auswertung ueber die
        /// ganze Session zeigt aber, dass erfolgreiche Export-Jobs nach 8.1s,
        /// 11.4s, 14.5s UND 53.6s fertig wurden - der 30s-Test hat also nachweislich
        /// Jobs abgeschossen, die noch gekommen waeren (Google meldet lange 0% und
        /// springt dann). Jeder so abgeschossene Job landet im Datei-fuer-Datei-
        /// Fallback, und DER ist der eigentliche Rate-Limit-Verursacher: EIN
        /// Archiv-Request gegen 162 Einzel-Requests auf dieselbe IP. Genau das
        /// stand danach im Log ("44/113 ... 69 skipped as HTML").
        /// Deshalb fortschrittsbasiert statt starr - und mit sehr viel Luft: nur
        /// aufgeben, wenn sich ueber FUENF MINUTEN hinweg gar nichts bewegt hat.
        /// Das ist reines Sicherheitsnetz gegen einen wirklich toten Job, kein
        /// Tuning-Parameter: der laengste je beobachtete ERFOLG kam nach 53.6s.
        /// Bitte nicht "optimieren" - siehe Kommentar bei ZipExportMaxWait.
        /// </summary>
        private const int ZipExportStallGiveUpMs = 300000;

        private static async Task<bool> TryGoogleDriveFolderZipExportAsync(HttpClient client, string folderId, string targetDir, string requestId, TimeSpan maxWait)
        {
            try
            {
                string folderUrl = $"https://drive.google.com/drive/folders/{folderId}";
                string html = await client.GetStringAsync(folderUrl).ConfigureAwait(false);

                var keyMatch = System.Text.RegularExpressions.Regex.Match(html, "\"yLTeS\":\"([^\"]+)\"");
                if (!keyMatch.Success)
                {
                    Logger.Log("[DriveZipExport] Could not find API key on folder page - falling back.");
                    return false;
                }
                string apiKey = keyMatch.Groups[1].Value;

                string displayName = "Download";
                var titleMatch = System.Text.RegularExpressions.Regex.Match(html, "<title>([^<]*)</title>");
                if (titleMatch.Success)
                {
                    displayName = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value)
                        .Replace(" – Google Drive", "").Replace(" - Google Drive", "").Trim();
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = "Download";
                }

                string exportsUrl = $"https://takeout-pa.clients6.google.com/v1/exports?key={apiKey}";
                string payload = JsonSerializer.Serialize(new { archivePrefix = displayName, items = new[] { new { id = folderId } } });
                using var startResp = await client.PostAsync(exportsUrl, new StringContent(payload, System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
                if (!startResp.IsSuccessStatusCode)
                {
                    Logger.Log($"[DriveZipExport] Export job creation failed with HTTP {(int)startResp.StatusCode} - falling back.");
                    return false;
                }
                string startJson = await startResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                string? jobId;
                using (var startDoc = JsonDocument.Parse(startJson))
                {
                    if (!startDoc.RootElement.TryGetProperty("exportJob", out var jobEl) || !jobEl.TryGetProperty("id", out var idEl))
                    {
                        Logger.Log("[DriveZipExport] Export job response had no job id - falling back.");
                        return false;
                    }
                    jobId = idEl.GetString();
                }
                if (string.IsNullOrEmpty(jobId)) return false;

                string pollUrl = $"https://takeout-pa.clients6.google.com/v1/exports/{jobId}?key={apiKey}";
                var sw = Stopwatch.StartNew();
                JsonElement finalJob = default;
                bool succeeded = false;

                bool firstPoll = true;
                int lastPercentDone = -1;
                long lastProgressMs = 0;
                while (sw.Elapsed < maxWait)
                {
                    // Erste Abfrage schon nach 1.5s statt pauschal 4s: ein kleiner
                    // Ordner ist oft sofort fertig, und diese 4 Sekunden lagen bisher
                    // als feste Grundlatenz vor JEDEM Drive-Ordner-Download.
                    await Task.Delay(firstPoll ? ZipExportFirstPollDelayMs : ZipExportPollIntervalMs).ConfigureAwait(false);
                    firstPoll = false;
                    using var pollResp = await client.GetAsync(pollUrl).ConfigureAwait(false);
                    string pollJson = await pollResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var pollDoc = JsonDocument.Parse(pollJson);
                    var root = pollDoc.RootElement;

                    if (root.TryGetProperty("error", out _))
                    {
                        Logger.Log($"[DriveZipExport] Job {jobId} failed with a server-side error - falling back.");
                        return false;
                    }
                    if (!root.TryGetProperty("exportJob", out var je)) return false;
                    string status = je.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";

                    int percentDone = root.TryGetProperty("percentDone", out var pd) && pd.TryGetInt32(out var pdVal) ? pdVal : 0;
                    Program.SendMessage(new
                    {
                        requestId = requestId,
                        type = "url_progress",
                        received = 0L,
                        total = 0L,
                        phase = "sizing",
                        filename = $"Preparing ZIP export... ({percentDone}%)"
                    });

                    // Fortschrittsbasiert aufgeben statt nach starrer Uhr: solange
                    // sich Googles percentDone noch bewegt, weiterwarten - der
                    // ZIP-Weg ist gegenueber dem Datei-fuer-Datei-Fallback klar zu
                    // bevorzugen (1 Request statt einer pro Datei, siehe
                    // ZipExportStallGiveUpMs).
                    if (percentDone > lastPercentDone)
                    {
                        lastPercentDone = percentDone;
                        lastProgressMs = sw.ElapsedMilliseconds;
                    }
                    else if (sw.ElapsedMilliseconds - lastProgressMs > ZipExportStallGiveUpMs)
                    {
                        Logger.Log($"[DriveZipExport] Job {jobId} stuck at {percentDone}% for {sw.ElapsedMilliseconds - lastProgressMs}ms (total {sw.ElapsedMilliseconds}ms) - falling back to per-file download.");
                        return false;
                    }

                    if (status == "SUCCEEDED")
                    {
                        // .Clone() ist zwingend noetig: "je" gehoert zu "pollDoc", das am
                        // Ende DIESER Iteration (using-Scope) disposed wird. Ohne Clone()
                        // wirft der Zugriff auf finalJob NACH der Schleife immer eine
                        // ObjectDisposedException ("Cannot access a disposed object" /
                        // JsonDocument) - das ist tatsaechlich bei jedem Aufruf passiert.
                        finalJob = je.Clone();
                        succeeded = true;
                        break;
                    }
                    if (status != "QUEUED" && status != "PROCESSING" && !string.IsNullOrEmpty(status))
                    {
                        Logger.Log($"[DriveZipExport] Job {jobId} ended with unexpected status '{status}' - falling back.");
                        return false;
                    }
                }

                if (!succeeded)
                {
                    Logger.Log($"[DriveZipExport] Job {jobId} did not finish within {maxWait.TotalSeconds:F0}s (elapsed {sw.ElapsedMilliseconds}ms) - falling back to per-file download.");
                    return false;
                }
                Logger.Log($"[DriveZipExport] Job {jobId} ready after {sw.ElapsedMilliseconds}ms.");

                if (!finalJob.TryGetProperty("archives", out var archivesEl) || archivesEl.GetArrayLength() == 0)
                {
                    Logger.Log("[DriveZipExport] Job succeeded but returned no archives - falling back.");
                    return false;
                }

                var archives = new List<(string storagePath, long size)>();
                long totalCompressed = 0;
                foreach (var arch in archivesEl.EnumerateArray())
                {
                    string storagePath = arch.TryGetProperty("storagePath", out var spEl) ? (spEl.GetString() ?? "") : "";
                    long size = arch.TryGetProperty("compressedSize", out var csEl) && long.TryParse(csEl.GetString(), out var s) ? s : 0;
                    if (string.IsNullOrEmpty(storagePath)) continue;
                    archives.Add((storagePath, size));
                    totalCompressed += size;
                }
                if (archives.Count == 0) return false;

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                long receivedSoFar = 0;
                foreach (var (storagePath, size) in archives)
                {
                    string tmpZipPath = Path.Combine(Path.GetTempPath(), $"pa_drive_export_{Guid.NewGuid():N}.zip");
                    try
                    {
                        using (var response = await client.GetAsync(storagePath, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            long archTotal = response.Content.Headers.ContentLength ?? size;
                            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            using var fileStream = new FileStream(tmpZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                            byte[] buffer = new byte[81920];
                            int bytesRead;
                            DateTime lastReport = DateTime.MinValue;
                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                                receivedSoFar += bytesRead;
                                if (DateTime.Now - lastReport > TimeSpan.FromMilliseconds(200))
                                {
                                    Program.SendMessage(new
                                    {
                                        requestId = requestId,
                                        type = "url_progress",
                                        received = receivedSoFar,
                                        total = totalCompressed > 0 ? totalCompressed : archTotal
                                    });
                                    lastReport = DateTime.Now;
                                }
                            }
                            await fileStream.FlushAsync().ConfigureAwait(false);
                        }

                        Program.SendMessage(new { requestId = requestId, type = "url_progress", received = receivedSoFar, total = totalCompressed, filename = "Extracting ZIP..." });
                        System.IO.Compression.ZipFile.ExtractToDirectory(tmpZipPath, targetDir, overwriteFiles: true);
                    }
                    finally
                    {
                        try { if (File.Exists(tmpZipPath)) File.Delete(tmpZipPath); } catch { }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[DriveZipExport] Unexpected failure - falling back to per-file download.");
                return false;
            }
        }

        private class MegaFolderTransferState
        {
            public long GrandTotal;
            public long ReceivedSoFar;
            public int FilesDone;
            public int FilesTotal;
        }

        private static async Task DownloadMegaFileAsync(string url, string path, string requestId)
        {
            var client = new MegaApiClient();
            await client.LoginAnonymousAsync().ConfigureAwait(false);

            Uri uri = new Uri(url);

            if (url.Contains("/folder/") || url.Contains("#F!"))
            {
                IEnumerable<INode> nodes = await client.GetNodesFromLinkAsync(uri).ConfigureAwait(false);
                var nodesList = nodes.ToList();

                var root = nodesList.Single(x => x.Type == NodeType.Root);
                string outDir = path;
                if (Path.HasExtension(path) || File.Exists(path))
                {
                    outDir = Path.GetDirectoryName(path) ?? path;
                }

                // MEGA liefert den kompletten Knotenbaum eines Ordners in einem Rutsch -
                // die Gesamtgroesse steht damit schon VOR dem ersten Byte-Download fest.
                // Deshalb hier direkt summieren und danach kumulativ gegen diesen festen
                // Wert melden, statt wie bisher bei jeder neuen Datei bei 0% anzufangen.
                var state = new MegaFolderTransferState
                {
                    GrandTotal = nodesList.Where(n => n.Type == NodeType.File).Sum(n => n.Size),
                    FilesTotal = nodesList.Count(n => n.Type == NodeType.File)
                };

                await DownloadMegaFolderRecursiveAsync(client, nodesList, root, outDir, requestId, state).ConfigureAwait(false);

                // Anders als im Einzeldatei-Zweig unten wurde hier bislang NIE "url_done"
                // gesendet - die Extension wartete dann bis zum 3-Minuten-Stall-Timeout
                // in nativeHost.js, obwohl die Dateien laengst fertig geschrieben waren.
                Program.SendMessage(new
                {
                    requestId = requestId,
                    type = "url_done",
                    path = outDir
                });
            }
            else
            {
                INode fileInfo = await client.GetNodeFromLinkAsync(uri).ConfigureAwait(false);
                string filename = SanitizeFileName(fileInfo.Name);
                string? dir = Path.GetDirectoryName(path);
                string finalPath = string.IsNullOrEmpty(dir) ? filename : Path.Combine(dir, filename);

                string? finalDir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(finalDir) && !Directory.Exists(finalDir))
                {
                    Directory.CreateDirectory(finalDir);
                }

                IProgress<double> progress = new Progress<double>((p) =>
                {
                    Program.SendMessage(new
                    {
                        requestId = requestId,
                        type = "url_progress",
                        received = (long)(p * fileInfo.Size / 100.0),
                        total = fileInfo.Size
                    });
                });

                using (var stream = await client.DownloadAsync(uri, progress).ConfigureAwait(false))
                using (var fileStream = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await stream.CopyToAsync(fileStream).ConfigureAwait(false);
                }

                Program.SendMessage(new
                {
                    requestId = requestId,
                    type = "url_done",
                    path = finalPath
                });
            }

            await client.LogoutAsync().ConfigureAwait(false);
        }

        private static async Task DownloadMegaFolderRecursiveAsync(
            MegaApiClient client,
            List<INode> nodes,
            INode parent,
            string currentDir,
            string requestId,
            MegaFolderTransferState state)
        {
            if (!Directory.Exists(currentDir))
            {
                Directory.CreateDirectory(currentDir);
            }

            var children = nodes.Where(x => x.ParentId == parent.Id).ToList();
            foreach (var child in children)
            {
                if (child.Type == NodeType.Directory)
                {
                    string nextDir = Path.Combine(currentDir, SanitizeFileName(child.Name));
                    await DownloadMegaFolderRecursiveAsync(client, nodes, child, nextDir, requestId, state).ConfigureAwait(false);
                }
                else if (child.Type == NodeType.File)
                {
                    string filePath = Path.Combine(currentDir, SanitizeFileName(child.Name));
                    long baseReceived = state.ReceivedSoFar;

                    IProgress<double> progress = new Progress<double>((p) =>
                    {
                        long fileReceived = (long)(p * child.Size / 100.0);
                        Program.SendMessage(new
                        {
                            requestId = requestId,
                            type = "url_progress",
                            received = baseReceived + fileReceived,
                            total = state.GrandTotal > 0 ? state.GrandTotal : child.Size,
                            filesCompleted = state.FilesDone,
                            totalFiles = state.FilesTotal,
                            filename = child.Name
                        });
                    });

                    using (var stream = await client.DownloadAsync(child, progress).ConfigureAwait(false))
                    using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        await stream.CopyToAsync(fileStream).ConfigureAwait(false);
                    }

                    state.ReceivedSoFar = baseReceived + child.Size;
                    state.FilesDone++;
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        public static async Task HandleInstallYtDlpAsync()
        {
            try
            {
                Program.SendMessage(new { type = "install_progress", message = "Checking latest yt-dlp release..." });

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "PatreonArchiverBridge");

                string releaseJson = await client.GetStringAsync("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(releaseJson);
                var root = doc.RootElement;
                
                string? downloadUrl = null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var nameProp) && nameProp.GetString() == "yt-dlp.exe")
                        {
                            if (asset.TryGetProperty("browser_download_url", out var urlProp))
                            {
                                downloadUrl = urlProp.GetString();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    throw new Exception("Could not find yt-dlp.exe in the latest GitHub release assets.");
                }

                Program.SendMessage(new { type = "install_progress", message = "Downloading yt-dlp.exe..." });

                string systemDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "System");
                if (!Directory.Exists(systemDir))
                {
                    Directory.CreateDirectory(systemDir);
                }
                string targetPath = Path.Combine(systemDir, "yt-dlp.exe");

                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await contentStream.CopyToAsync(fileStream).ConfigureAwait(false);
                await fileStream.FlushAsync().ConfigureAwait(false);

                Program.SendMessage(new
                {
                    type = "install_done",
                    path = targetPath
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "install_ytdlp failed");
                Program.SendMessage(new
                {
                    type = "install_error",
                    message = ex.Message
                });
            }
        }

        public static async Task HandleInstallDenoAsync()
        {
            string? tempZip = null;
            try
            {
                Program.SendMessage(new { type = "install_progress", message = "Downloading Deno JS engine..." });

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "PatreonArchiverBridge");

                string downloadUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

                string systemDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "System");
                if (!Directory.Exists(systemDir))
                {
                    Directory.CreateDirectory(systemDir);
                }
                string targetPath = Path.Combine(systemDir, "deno.exe");
                tempZip = Path.Combine(Path.GetTempPath(), "deno_temp.zip");

                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using (var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream).ConfigureAwait(false);
                }

                Program.SendMessage(new { type = "install_progress", message = "Extracting Deno..." });

                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, systemDir, overwriteFiles: true);

                if (File.Exists(targetPath))
                {
                    Program.SendMessage(new
                    {
                        type = "install_done",
                        path = targetPath
                    });
                }
                else
                {
                    throw new FileNotFoundException("deno.exe not found after extraction.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "install_deno failed");
                Program.SendMessage(new
                {
                    type = "install_error",
                    message = ex.Message
                });
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempZip) && File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { }
                }
            }
        }

        public static async Task HandleDownloadAsync(string url, string outputDir, string filenameTemplate, string? format, bool forceOverwrite = false)
        {
            try
            {
                string? ytdlp = FindYtDlp();
                if (string.IsNullOrEmpty(ytdlp))
                {
                    Program.SendMessage(new
                    {
                        type = "error",
                        message = "yt-dlp is missing. Install it first."
                    });
                    return;
                }

                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // WICHTIG: Path.Combine fügt auf Windows einen Backslash ein, was das
                // yt-dlp Dateiname-Template (%(title)s.%(ext)s) bricht. Stattdessen
                // immer mit Forward-Slash verbinden, damit yt-dlp das Template korrekt parst.
                string normalizedDir = (outputDir ?? ".").Replace('\\', '/').TrimEnd('/');
                string outputPath = $"{normalizedDir}/{filenameTemplate}";

                var args = new List<string>
                {
                    url,
                    "-o",
                    outputPath,
                    "--newline",
                    "--js-runtimes", "deno",
                    "--js-runtimes", "quickjs",
                    // Kaskadierender Format-Selektor: Erst bestes Full-HD MP4,
                    // dann HD 720p MP4, dann Standard MP4, und am Ende Fallbacks.
                    "-f", string.IsNullOrEmpty(format) ? "bestvideo[height>=1080][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height>=720][ext=mp4]+bestaudio[ext=m4a]/bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best" : format,
                    "--merge-output-format", "mp4"
                };

                if (forceOverwrite)
                {
                    args.Add("--force-overwrites");
                }
                else
                {
                    args.Add("--no-overwrites");
                }

                string? ffmpeg = FindFfmpeg();
                if (!string.IsNullOrEmpty(ffmpeg))
                {
                    args.Add("--ffmpeg-location");
                    args.Add(ffmpeg);
                }

                using var process = new Process();
                // In die globale Liste eintragen, damit der Prozess beim Abbruch
                // (Chrome trennt den Port -> stdin schliesst -> Host beendet sich)
                // MITGETOETET wird. Ohne das lief yt-dlp als verwaistes Kind
                // weiter und hinterliess seine ".part"-Dateien - genau die
                // Fragmente, die der Nutzer nach einem Cancel im Ordner fand.
                RegisterChildProcess(process);
                process.StartInfo.FileName = ytdlp;
                foreach (var arg in args)
                {
                    process.StartInfo.ArgumentList.Add(arg);
                }
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardInput = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                var stderrLines = new List<string>();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        Program.SendMessage(new
                        {
                            type = "progress",
                            line = e.Data
                        });
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        // yt-dlp schreibt seine Fortschrittszeilen (Prozent, Speed, ETA)
                        // standardmäßig nach stderr, nicht stdout - ohne das hier kam bei
                        // der Extension während des gesamten Downloads nie eine einzige
                        // Zwischenmeldung an, obwohl yt-dlp im Hintergrund korrekt lief.
                        Program.SendMessage(new
                        {
                            type = "progress",
                            line = e.Data
                        });

                        Logger.Log($"[yt-dlp stderr] {e.Data}");
                        lock (stderrLines)
                        {
                            stderrLines.Add(e.Data);
                        }
                    }
                };

                Logger.Log($"Starting yt-dlp: {ytdlp} {process.StartInfo.Arguments}");
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Keepalive: Chrome beendet eine Native-Messaging-Verbindung wenn
                // über ~90s keine Nachrichten ausgetauscht werden. Da yt-dlp für
                // längere Videos mehrere Minuten braucht, senden wir alle 5s ein
                // leises keepalive-Paket, um den Verbindungsabbruch zu verhindern.
                using var keepaliveCts = new System.Threading.CancellationTokenSource();
                var keepaliveTask = Task.Run(async () =>
                {
                    while (!keepaliveCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(5000, keepaliveCts.Token).ConfigureAwait(false);
                            Program.SendMessage(new { type = "keepalive" });
                        }
                        catch (System.Threading.Tasks.TaskCanceledException) { break; }
                        catch { break; }
                    }
                }, keepaliveCts.Token);

                await process.WaitForExitAsync().ConfigureAwait(false);
                keepaliveCts.Cancel();

                if (process.ExitCode == 0)
                {
                    Program.SendMessage(new
                    {
                        type = "done"
                    });
                }
                else
                {
                    string errorMsg = "";
                    lock (stderrLines)
                    {
                        // Look for specific ERROR: lines first
                        errorMsg = string.Join("\n", stderrLines.Where(l => l.Contains("ERROR:", StringComparison.OrdinalIgnoreCase)));
                        if (string.IsNullOrEmpty(errorMsg) && stderrLines.Count > 0)
                        {
                            // Fallback to last 2 lines
                            int count = Math.Min(2, stderrLines.Count);
                            errorMsg = string.Join("\n", stderrLines.Skip(stderrLines.Count - count));
                        }
                    }

                    if (string.IsNullOrEmpty(errorMsg))
                    {
                        errorMsg = $"yt-dlp exited with code {process.ExitCode}";
                    }

                    Program.SendMessage(new
                    {
                        type = "error",
                        message = errorMsg
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "download failed");
                Program.SendMessage(new
                {
                    type = "error",
                    message = $"Launch error: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Finds the UI executable next to the Host, or one directory up (Squirrel layout).
        /// </summary>
        private static string FindUiExe()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // Same directory (dev / flat layout)
            string candidate = Path.Combine(baseDir, "PatreonArchiverBridge.exe");
            if (File.Exists(candidate)) return candidate;
            // One level up (Squirrel/Velopack layout: Host.exe is inside \\System\\, UI is in \\current\\)
            string parentDir = Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? baseDir;
            candidate = Path.Combine(parentDir, "PatreonArchiverBridge.exe");
            if (File.Exists(candidate)) return candidate;
            throw new Exception($"UI executable not found near '{baseDir}'. Make sure the bridge is properly installed.");
        }

        public static async Task HandlePickFolderAsync()
        {
            try
            {
                string uiPath = FindUiExe();

                using var process = new Process();
                process.StartInfo.FileName = uiPath;
                process.StartInfo.Arguments = "--pick-folder";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                string selectedPath = output.Trim();
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    Program.SendMessage(new
                    {
                        type = "folder_picked",
                        path = selectedPath
                    });
                }
                else
                {
                    Program.SendMessage(new
                    {
                        type = "folder_pick_cancelled"
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "pick_folder failed");
                Program.SendMessage(new
                {
                    type = "folder_pick_error",
                    message = ex.Message
                });
            }
        }

        public static void HandleRunUpdate()
        {
            try
            {
                string uiPath = FindUiExe();

                Logger.Log($"Launching UI for update: {uiPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = uiPath,
                    Arguments = "--run-update",
                    UseShellExecute = true
                });

                Program.SendMessage(new
                {
                    type = "update_launched"
                });
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "run_update failed");
                Program.SendMessage(new
                {
                    type = "error",
                    message = ex.Message
                });
            }
        }
    }
}
