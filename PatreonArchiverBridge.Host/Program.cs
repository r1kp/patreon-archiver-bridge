using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PatreonArchiverBridge.Host
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    class Program
    {
        private static readonly Stream Stdout = Console.OpenStandardOutput();
        private static readonly object WriteLock = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        static async Task Main(string[] args)
        {
            Logger.Log($"Application started. Args: {string.Join(" ", args)}");

            bool isNative = args.Length > 0 && args[0].StartsWith("chrome-extension://");

            if (isNative)
            {
                await RunNativeMessagingLoopAsync().ConfigureAwait(false);
            }
            else
            {
                LaunchGui();
            }
        }

        private static void LaunchGui()
        {
            try
            {
                string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                string uiPath = Path.Combine(currentDir, "PatreonArchiverBridge.exe");
                if (File.Exists(uiPath))
                {
                    Logger.Log($"Launching UI: {uiPath}");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = uiPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    Logger.Log($"UI executable not found at {uiPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "Failed to launch UI");
            }
        }

        private static async Task RunNativeMessagingLoopAsync()
        {
            Logger.Log("Starting Native Messaging Loop");
            using Stream stdin = Console.OpenStandardInput();
            var cts = new CancellationTokenSource();

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    byte[]? lengthBuffer = await ReadExactlyAsync(stdin, 4, cts.Token).ConfigureAwait(false);
                    if (lengthBuffer == null)
                    {
                        Logger.Log("End of stdin stream reached. Exiting Native Messaging Loop.");
                        break;
                    }

                    uint length = BitConverter.ToUInt32(lengthBuffer, 0);
                    if (length == 0) continue;

                    byte[]? payloadBuffer = await ReadExactlyAsync(stdin, (int)length, cts.Token).ConfigureAwait(false);
                    if (payloadBuffer == null)
                    {
                        Logger.Log("Unexpected end of stdin stream when reading payload. Exiting.");
                        break;
                    }

                    string json = Encoding.UTF8.GetString(payloadBuffer);
                    Logger.Log($"Received message: {json}");

                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("action", out var actionProp))
                        {
                            string? action = actionProp.GetString();
                            await DispatchActionAsync(action, root).ConfigureAwait(false);
                        }
                        else
                        {
                            Logger.Log("Message did not contain an 'action' property.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex, "Error processing JSON payload");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "Error in Native Messaging loop");
            }
        }

        private static async Task DispatchActionAsync(string? action, JsonElement msg)
        {
            Logger.Log($"Dispatching action: {action}");
            switch (action)
            {
                case "ping":
                    CommandHandlers.HandlePing();
                    break;

                case "get_default_dir":
                    CommandHandlers.HandleGetDefaultDir();
                    break;

                case "pick_folder":
                    _ = Task.Run(async () => await CommandHandlers.HandlePickFolderAsync().ConfigureAwait(false));
                    break;

                case "write_chunk":
                    string path = msg.GetProperty("path").GetString() ?? "";
                    string dataBase64 = msg.GetProperty("dataBase64").GetString() ?? "";
                    bool append = msg.GetProperty("append").GetBoolean();
                    bool isLast = msg.GetProperty("isLast").GetBoolean();
                    CommandHandlers.HandleWriteChunk(path, dataBase64, append, isLast);
                    break;

                case "download_url":
                    string dlUrl = msg.GetProperty("url").GetString() ?? "";
                    string dlPath = msg.GetProperty("path").GetString() ?? "";
                    string requestId = msg.GetProperty("requestId").GetString() ?? "";
                    _ = Task.Run(async () => await CommandHandlers.HandleDownloadUrlAsync(dlUrl, dlPath, requestId).ConfigureAwait(false));
                    break;

                case "install_ytdlp":
                    _ = Task.Run(async () => await CommandHandlers.HandleInstallYtDlpAsync().ConfigureAwait(false));
                    break;

                case "check_file_exists":
                    string checkPath = msg.GetProperty("path").GetString() ?? "";
                    CommandHandlers.HandleCheckFileExists(checkPath);
                    break;

                case "run_update":
                    CommandHandlers.HandleRunUpdate();
                    break;

                case "download":
                    string videoUrl = msg.GetProperty("url").GetString() ?? "";
                    string outDir = msg.GetProperty("outputDir").GetString() ?? "";
                    string fnTemplate = msg.GetProperty("filenameTemplate").GetString() ?? "%(title)s.%(ext)s";
                    string? format = null;
                    if (msg.TryGetProperty("options", out var options) && options.TryGetProperty("format", out var formatProp))
                    {
                        format = formatProp.GetString();
                    }
                    _ = Task.Run(async () => await CommandHandlers.HandleDownloadAsync(videoUrl, outDir, fnTemplate, format).ConfigureAwait(false));
                    break;

                default:
                    Logger.Log($"Unknown action received: {action}");
                    SendMessage(new { type = "error", message = $"Unknown action: {action}" });
                    break;
            }
        }

        private static async Task<byte[]?> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }
                offset += read;
            }
            return buffer;
        }

        public static void SendMessage(object responseObj)
        {
            try
            {
                string json = JsonSerializer.Serialize(responseObj, JsonOptions);
                Logger.Log($"Sending response: {json}");
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                byte[] lengthBytes = BitConverter.GetBytes((uint)bytes.Length);

                lock (WriteLock)
                {
                    Stdout.Write(lengthBytes, 0, 4);
                    Stdout.Write(bytes, 0, bytes.Length);
                    Stdout.Flush();
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "Failed to send response message");
            }
        }
    }
}
