using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PatreonArchiverBridge.Core
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static class BridgeCore
    {
        public const string HostName = "com.patreonarchiver.ytdlp";
        public const string ExtensionId = "pjbbdkkgldalamlfbdahhhjpppiepbjg";
        public const string SettingsKey = @"Software\PatreonArchiverBridge";

        public static bool CheckRegistryStatus()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}");
                if (key != null)
                {
                    string? manifestPath = key.GetValue("") as string;
                    if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
                    {
                        string json = File.ReadAllText(manifestPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("path", out var pathProp))
                        {
                            string? hostPath = pathProp.GetString();
                            if (!string.IsNullOrEmpty(hostPath))
                            {
                                string fullHostPath = Path.IsPathRooted(hostPath) 
                                    ? hostPath 
                                    : Path.Combine(Path.GetDirectoryName(manifestPath)!, hostPath);
                                
                                if (File.Exists(fullHostPath))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static string? FindYtDlp()
        {
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "System", "yt-dlp.exe");
            if (File.Exists(bundled)) return bundled;

            string rootBundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yt-dlp.exe");
            if (File.Exists(rootBundled)) return rootBundled;

            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string fullPath = Path.Combine(dir.Trim(), "yt-dlp.exe");
                        if (File.Exists(fullPath)) return fullPath;
                    }
                    catch { }
                }
            }
            return null;
        }

        public static string? FindFfmpeg()
        {
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "System", "ffmpeg.exe");
            if (File.Exists(bundled)) return bundled;

            string rootBundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(rootBundled)) return rootBundled;

            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string fullPath = Path.Combine(dir.Trim(), "ffmpeg.exe");
                        if (File.Exists(fullPath)) return fullPath;
                    }
                    catch { }
                }
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            
            string cand = Path.Combine(pf, "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(cand)) return cand;

            cand = Path.Combine(pf86, "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(cand)) return cand;

            return null;
        }

        public static string GetDownloadsFolder()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (key != null)
                {
                    string? path = key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return Environment.ExpandEnvironmentVariables(path);
                    }
                }
            }
            catch { }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public static string GetDefaultDownloadDir()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                if (key != null)
                {
                    string? path = key.GetValue("DefaultDownloadDir") as string;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch { }

            // Default fallback: <Downloads>/Patreon Archiver
            string downloads = GetDownloadsFolder();
            return Path.Combine(downloads, "Patreon Archiver");
        }

        public static async Task<string> GetYtdlpVersionAsync(string path)
        {
            try
            {
                var startupinfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(startupinfo);
                if (proc != null)
                {
                    string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                    if (proc.ExitCode == 0)
                    {
                        return output.Trim();
                    }
                }
            }
            catch { }
            return "unknown";
        }

        public static async Task<bool> RepairSetupAsync(Action<string>? log)
        {
            try
            {
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string hostPath = Path.Combine(currentDir, "PatreonArchiverBridge.Host.exe");

                if (!File.Exists(hostPath))
                {
                    // Fallback for debug/development environment
                    hostPath = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", "PatreonArchiverBridge.Host", "bin", "Debug", "net9.0", "PatreonArchiverBridge.Host.exe"));
                    if (!File.Exists(hostPath))
                    {
                        log?.Invoke("Error: Could not locate PatreonArchiverBridge.Host.exe binary!");
                        return false;
                    }
                }

                string systemDir = Path.Combine(Path.GetDirectoryName(hostPath)!, "System");
                if (!Directory.Exists(systemDir))
                {
                    Directory.CreateDirectory(systemDir);
                }

                string manifestPath = Path.Combine(systemDir, "bridge_manifest.json");
                var manifestData = new
                {
                    name = HostName,
                    description = "Patreon Archive Manager bridge",
                    path = hostPath.Replace("\\", "/"),
                    type = "stdio",
                    allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
                };

                string json = JsonSerializer.Serialize(manifestData, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(manifestPath, json).ConfigureAwait(false);
                log?.Invoke($"Manifest written: {manifestPath}");

                using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Google\Chrome\NativeMessagingHosts\{HostName}"))
                {
                    key.SetValue("", manifestPath);
                }
                log?.Invoke("Registry key registered successfully.");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Repair failed: {ex.Message}");
                return false;
            }
        }

        public static async Task DownloadFileWithProgressAsync(
            string url, 
            string targetPath, 
            IProgress<long>? progress, 
            Action<long>? onContentLengthReceived)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "PatreonArchiverBridgeUI");

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes.HasValue)
            {
                onContentLengthReceived?.Invoke(totalBytes.Value);
            }

            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                totalRead += bytesRead;
                progress?.Report(totalRead);
            }

            await fileStream.FlushAsync().ConfigureAwait(false);
        }
    }
}
