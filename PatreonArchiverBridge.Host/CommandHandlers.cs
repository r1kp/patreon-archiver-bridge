using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PatreonArchiverBridge.Host
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static class CommandHandlers
    {
        public static string? FindYtDlp() => Core.BridgeCore.FindYtDlp();
        public static string? FindFfmpeg() => Core.BridgeCore.FindFfmpeg();
        private static string GetDownloadsFolder() => Core.BridgeCore.GetDownloadsFolder();

        public static void HandlePing()
        {
            bool ytdlpFound = !string.IsNullOrEmpty(FindYtDlp());
            string version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            if (version.Split('.').Length == 4)
            {
                version = version.Substring(0, version.LastIndexOf('.'));
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
                bool exists = File.Exists(path);
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

        public static async Task HandleDownloadUrlAsync(string url, string path, string requestId)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                byte[] buffer = new byte[8192];
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

        public static async Task HandleDownloadAsync(string url, string outputDir, string filenameTemplate, string? format)
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

                string outputPath = Path.Combine(outputDir ?? ".", filenameTemplate);

                var args = new List<string>
                {
                    url,
                    "-o",
                    outputPath
                };

                if (!string.IsNullOrEmpty(format))
                {
                    args.Add("-f");
                    args.Add(format);
                }

                string? ffmpeg = FindFfmpeg();
                if (!string.IsNullOrEmpty(ffmpeg))
                {
                    args.Add("--ffmpeg-location");
                    args.Add(ffmpeg);
                }

                using var process = new Process();
                process.StartInfo.FileName = ytdlp;
                process.StartInfo.Arguments = string.Join(" ", args.Select(a => $"\"{a.Replace("\"", "\\\"")}\""));
                process.StartInfo.UseShellExecute = false;
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
                        Logger.Log($"[yt-dlp stderr] {e.Data}");
                        lock (stderrLines)
                        {
                            stderrLines.Add(e.Data);
                        }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync().ConfigureAwait(false);

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
            // One level up (Squirrel/Velopack layout: Host.exe is inside \System\, UI is in \current\)
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
