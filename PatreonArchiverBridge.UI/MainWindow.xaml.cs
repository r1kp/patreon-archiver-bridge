using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using Microsoft.Win32;
using System.IO.Compression;
using System.Windows.Controls;
using PatreonArchiverBridge.UI.Shared;

using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PatreonArchiverBridge.UI
{
    public partial class MainWindow : Window
    {
        private const string HostName = "com.patreonarchiver.ytdlp";
        private const string ExtensionId = "pjbbdkkgldalamlfbdahhhjpppiepbjg";
        private static readonly string SettingsKey = @"Software\PatreonArchiverBridge";

        // Social and Support URLs
        private const string TelegramUrl = "https://t.me/r1kpz";
        private const string CoffeeUrl = "https://buymeacoffee.com/r1kp";
        private const string GitHubUrl = "https://github.com/r1kp/patreon-archiver-bridge";
        private const string ChromeStoreUrl = "https://chromewebstore.google.com/detail/pjbbdkkgldalamlfbdahhhjpppiepbjg/reviews"; // REPLACE WITH ACTUAL URL LATER

        private bool _isDarkTheme;
        private string? _latestYtdlpVersion; // null until "Check for updates" has run once, matching original latest_ytdlp_cache
        private string? _cachedYtdlpVersion;
        private bool _isTransitioningToUpdate = false;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // DWM attribute constants
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000;

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST)
            {
                handled = true;
                return (IntPtr)HTCLIENT;
            }
            return IntPtr.Zero;
        }

        private void ApplyDwmBackdrop(IntPtr hwnd, bool isDark)
        {
            // 1. Tell DWM whether we're dark or light (affects backdrop tinting)
            int useDarkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));

            // 2. Extend DWM frame into the entire client area so the backdrop covers everything
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // 3. Request Acrylic backdrop (value 3) via Windows 11 22H2+ API
            int backdropType = 3; // DWMSBT_TRANSIENTWINDOW = Acrylic
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;

                // Window style: keep caption for DWM animations, remove sysmenu to prevent ghost buttons
                int style = GetWindowLong(hwnd, GWL_STYLE);
                style |= WS_CAPTION | WS_MINIMIZEBOX;
                style &= ~WS_SYSMENU;
                SetWindowLong(hwnd, GWL_STYLE, style);

                // Rounded corners via DWM
                int cornerPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                // Apply Acrylic glass backdrop
                bool isDark = ReadThemePreference();
                ApplyDwmBackdrop(hwnd, isDark);

                // Add WndProc hook to handle WM_NCHITTEST so caption buttons are 100% clickable
                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                source.AddHook(new System.Windows.Interop.HwndSourceHook(WndProc));
            }
            catch { }
        }

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            
            // Configure explicit shutdown to allow closing MainWindow when starting the update progress window
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Set window icon from embedded resources
            try
            {
                var iconUri = new Uri("pack://application:,,,/PatreonArchiverBridge.UI.Shared;component/Resources/bridge_icon.ico", UriKind.Absolute);
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            }
            catch { }

            // Auto refresh status every 1 hour in background
            var refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromHours(1)
            };
            refreshTimer.Tick += async (s, e) => {
                await RefreshStatusAsync();
            };
            refreshTimer.Start();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Slide-up and fade-in content on startup
            RootBorder.Opacity = 0.0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.35));
            var scaleUp = new DoubleAnimation(0.95, 1.0, TimeSpan.FromSeconds(0.35)) { EasingFunction = ease };
            var translateUp = new DoubleAnimation(25, 0, TimeSpan.FromSeconds(0.35)) { EasingFunction = ease };

            RootBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            WindowTranslate.BeginAnimation(TranslateTransform.YProperty, translateUp);

            // Initialize theme state (from Registry if saved, default to Light for original replica)
            _isDarkTheme = ReadThemePreference();
            ThemeManager.SetTheme(_isDarkTheme);
            ChkSettingsDark.IsChecked = _isDarkTheme;
            UpdateThemeIcons();

            // Set dynamic version string in UI
            string currentVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            TxtVersion.Text = $"Bridge Version {currentVersion}";

            // Refresh dashboard status
            await RefreshStatusAsync();

            // Check for app updates
            CheckForAppUpdatesAsync();
        }

        private async Task RefreshStatusAsync()
        {
            // 1. Check Registry Integration
            // Original: yellow (not red) when missing - registry issues are a
            // "needs a repair click" state, not a hard failure state.
            bool isRegistered = CheckRegistryStatus();
            if (isRegistered)
            {
                DotRegistry.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                TxtRegistry.Text = "Connected to Chrome";
                BtnRegister.Visibility = Visibility.Collapsed;
            }
            else
            {
                DotRegistry.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                TxtRegistry.Text = "Registry links missing";
                BtnRegister.Visibility = Visibility.Visible;
                BtnRegister.Content = "Repair";
            }

            // 2. Check yt-dlp - four distinct states, matching original ytdlp_status:
            // "missing" / "update" / "ok" (no separate "checking" UI state needed
            // here since the check itself is fast/synchronous in C#, unlike the
            // original's background-thread workaround for a slow subprocess call).
            string? ytdlpPath = FindYtDlp();
            bool hasYtdlp = !string.IsNullOrEmpty(ytdlpPath);
            string ytdlpVersion = "unknown";
            bool ytdlpUpdateAvailable = false;

            if (!hasYtdlp)
            {
                _cachedYtdlpVersion = null; // Clear cache
                DotYtdlp.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                TxtYtdlp.Text = "Not installed";
                BtnYtdlpAction.Content = "Install";
                BtnYtdlpAction.Visibility = Visibility.Visible;
            }
            else
            {
                // Cache version string to avoid slow subprocess calls
                if (string.IsNullOrEmpty(_cachedYtdlpVersion) || _cachedYtdlpVersion == "unknown")
                {
                    _cachedYtdlpVersion = await GetYtdlpVersionAsync(ytdlpPath!);
                }
                ytdlpVersion = _cachedYtdlpVersion;

                ytdlpUpdateAvailable = !string.IsNullOrEmpty(_latestYtdlpVersion)
                    && _latestYtdlpVersion != "unknown"
                    && ytdlpVersion != "unknown"
                    && _latestYtdlpVersion != ytdlpVersion;

                if (ytdlpUpdateAvailable)
                {
                    DotYtdlp.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                    TxtYtdlp.Text = $"Update available ({ytdlpVersion} \u2192 {_latestYtdlpVersion})";
                    BtnYtdlpAction.Content = "Update";
                    BtnYtdlpAction.Visibility = Visibility.Visible;
                }
                else
                {
                    DotYtdlp.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                    TxtYtdlp.Text = $"Installed ({ytdlpVersion})";
                    BtnYtdlpAction.Visibility = Visibility.Collapsed;
                }
            }

            // 3. Check ffmpeg
            string? ffmpegPath = FindFfmpeg();
            bool hasFfmpeg = !string.IsNullOrEmpty(ffmpegPath);
            if (hasFfmpeg)
            {
                DotFfmpeg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                TxtFfmpeg.Text = "Found";
                BtnFfmpegAction.Visibility = Visibility.Collapsed;
            }
            else
            {
                DotFfmpeg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                TxtFfmpeg.Text = "Not installed";
                BtnFfmpegAction.Content = "Install";
                BtnFfmpegAction.Visibility = Visibility.Visible;
            }

            // 4. Update Header Banner - exact 5-state logic from updateMainStatusHeader():
            // missing-count + has-updates combine independently, matching the
            // original's isChecking / missing+update / missing / update / ok branches.
            int missingCount = (isRegistered ? 0 : 1) + (hasYtdlp ? 0 : 1) + (hasFfmpeg ? 0 : 1);
            bool hasInstalls = missingCount > 0;
            bool hasUpdates = ytdlpUpdateAvailable;
            if (hasInstalls && hasUpdates)
            {
                BrdStatusCircle.SetResourceReference(Panel.BackgroundProperty, "RedBgBrush");
                PathStatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "RedBrush");
                PathStatusIcon.Data = Geometry.Parse("M 12 9 V 13 M 12 17 H 12.01 M 10.29 3.86 L 1.82 18 A 2 2 0 0 0 3.54 21 H 20.46 A 2 2 0 0 0 22.18 18 L 13.71 3.86 A 2 2 0 0 0 10.29 3.86 Z");
                string plural = missingCount > 1 ? "s" : "";
                TxtStatusTitle.Text = $"{missingCount} component{plural} missing \u2013 update available";
                TxtStatusSub.Text = "Install missing components and update the engine for full functionality.";
            }
            else if (hasInstalls)
            {
                BrdStatusCircle.SetResourceReference(Panel.BackgroundProperty, "RedBgBrush");
                PathStatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "RedBrush");
                PathStatusIcon.Data = Geometry.Parse("M 12 9 V 13 M 12 17 H 12.01 M 10.29 3.86 L 1.82 18 A 2 2 0 0 0 3.54 21 H 20.46 A 2 2 0 0 0 22.18 18 L 13.71 3.86 A 2 2 0 0 0 10.29 3.86 Z");
                string plural = missingCount > 1 ? "s" : "";
                TxtStatusTitle.Text = $"{missingCount} component{plural} not installed";
                TxtStatusSub.Text = "Click Install on the missing items below to complete setup.";
            }
            else if (hasUpdates)
            {
                BrdStatusCircle.SetResourceReference(Panel.BackgroundProperty, "YellowBgBrush");
                PathStatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "YellowBrush");
                PathStatusIcon.Data = Geometry.Parse("M 12 9 V 13 M 12 17 H 12.01 M 10.29 3.86 L 1.82 18 A 2 2 0 0 0 3.54 21 H 20.46 A 2 2 0 0 0 22.18 18 L 13.71 3.86 A 2 2 0 0 0 10.29 3.86 Z");
                TxtStatusTitle.Text = "Update available";
                TxtStatusSub.Text = "An engine update is recommended for best stability.";
            }
            else
            {
                BrdStatusCircle.SetResourceReference(Panel.BackgroundProperty, "GreenBgBrush");
                PathStatusIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "GreenBrush");
                PathStatusIcon.Data = Geometry.Parse("M 20 6 L 9 17 L 4 12"); // Checkmark
                TxtStatusTitle.Text = "Everything is working";
                TxtStatusSub.Text = "Feel free to close this window. The extension runs in the background when it's needed.";
            }
        }

        private bool CheckRegistryStatus() => Core.BridgeCore.CheckRegistryStatus();

        private async void BtnRegister_Click(object sender, RoutedEventArgs e)
        {
            BtnRegister.IsEnabled = false;
            LogInfo("Starting Native Host Registry Registration / Repair...");
            bool ok = await RepairSetupAsync(log: message => {
                LogInfo($"[Registry Repair] {message}");
            });
            BtnRegister.IsEnabled = true;
            if (!ok)
            {
                LogException("Registry Repair", new Exception("RepairSetupAsync failed to write registry entry or write native host manifest file. Check detailed trace logs."));
                MessageBox.Show("Repair failed. Check the log folder for details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                LogInfo("Registry Registration / Repair completed successfully.");
            }
            await RefreshStatusAsync();
        }

        private Task<bool> RepairSetupAsync(Action<string>? log) => Core.BridgeCore.RepairSetupAsync(log);

        private async void BtnYtdlpAction_Click(object sender, RoutedEventArgs e)
        {
            string systemDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "System");
            if (!Directory.Exists(systemDir))
            {
                Directory.CreateDirectory(systemDir);
            }
            string targetPath = Path.Combine(systemDir, "yt-dlp.exe");
            string ytdlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

            TxtYtdlp.Text = "Downloading video engine...";
            await DownloadWithProgressAsync(ytdlpUrl, targetPath, ProgressYtdlp, BtnYtdlpAction, () => {
                _cachedYtdlpVersion = null; // Clear cache
                DotYtdlp.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                TxtYtdlp.Text = "Installed (up to date)";
                BtnYtdlpAction.Visibility = Visibility.Collapsed;
                ShowNotification("Video engine (yt-dlp) successfully installed!");
            });
        }

        private async void BtnFfmpegAction_Click(object sender, RoutedEventArgs e)
        {
            string systemDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "System");
            if (!Directory.Exists(systemDir))
            {
                Directory.CreateDirectory(systemDir);
            }
            string ffmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            string tempZip = Path.Combine(Path.GetTempPath(), "ffmpeg_temp.zip");

            TxtFfmpeg.Text = "Downloading FFmpeg merger zip...";
            await DownloadWithProgressAsync(ffmpegUrl, tempZip, ProgressFfmpeg, BtnFfmpegAction, () => {
                try
                {
                    LogInfo("Beginning FFmpeg extraction...");
                    TxtFfmpeg.Text = "Extracting FFmpeg merger binaries...";
                    using (var archive = System.IO.Compression.ZipFile.OpenRead(tempZip))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                                entry.FullName.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                string dest = Path.Combine(systemDir, Path.GetFileName(entry.FullName));
                                LogInfo($"Extracting {entry.FullName} -> {dest}");
                                entry.ExtractToFile(dest, true);
                            }
                        }
                    }
                    LogInfo("FFmpeg extraction completed successfully.");
                    DotFfmpeg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                    TxtFfmpeg.Text = "Found";
                    BtnFfmpegAction.Visibility = Visibility.Collapsed;
                    ShowNotification("FFmpeg merger successfully installed!");
                }
                catch (Exception ex)
                {
                    LogException("FFmpeg Extraction", ex);
                    MessageBox.Show($"FFmpeg extraction failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempZip))
                        {
                            File.Delete(tempZip);
                            LogInfo("Cleaned up temp FFmpeg zip file.");
                        }
                    }
                    catch (Exception ex) { LogException("Clean up temp FFmpeg zip", ex); }
                }
            });
        }

        private async Task DownloadWithProgressAsync(string url, string targetPath, System.Windows.Controls.ProgressBar progressBar, System.Windows.Controls.Button actionButton, Action onComplete)
        {
            try
            {
                progressBar.Visibility = Visibility.Visible;
                actionButton.IsEnabled = false;
                progressBar.IsIndeterminate = true;

                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "PatreonArchiverBridgeUI");

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                if (totalBytes.HasValue)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Maximum = totalBytes.Value;
                }

                using (var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(true))
                {
                    using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(true)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(true);
                            totalRead += bytesRead;
                            if (totalBytes.HasValue)
                            {
                                Dispatcher.Invoke(() => {
                                    progressBar.Value = totalRead;
                                });
                            }
                        }
                        await fileStream.FlushAsync().ConfigureAwait(true);
                    }
                }

                onComplete();
            }
            catch (HttpRequestException httpEx) when (httpEx.InnerException is System.Net.Sockets.SocketException || 
                                                    httpEx.Message.Contains("host") || 
                                                    httpEx.Message.Contains("connection"))
            {
                LogError($"Download component from URL: {url}", httpEx);
                MessageBox.Show("Download failed: No internet connection or server unreachable.", "Network Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (TaskCanceledException tcEx)
            {
                LogError($"Download component timeout/cancelled: {url}", tcEx);
                MessageBox.Show("Download failed: Connection timed out. Please check your internet speed and try again.", "Timeout Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (TimeoutException tEx)
            {
                LogError($"Download component timeout: {url}", tEx);
                MessageBox.Show("Download failed: Connection timed out. Please check your internet speed and try again.", "Timeout Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (IOException ioEx)
            {
                LogError($"Download component disk error: {targetPath}", ioEx);
                MessageBox.Show($"Download failed: Disk error or file blocked by Antivirus.\nDetails: {ioEx.Message}", "Disk Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                LogError($"Download component unknown error: {url}", ex);
                MessageBox.Show($"Download failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
                progressBar.Value = 0;
                actionButton.IsEnabled = true;
                await RefreshStatusAsync();
            }
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            _isDarkTheme = !_isDarkTheme;
            ChkSettingsDark.IsChecked = _isDarkTheme;
            TriggerThemeTransition();
        }

        private void ChkSettingsDark_Click(object sender, RoutedEventArgs e)
        {
            bool isDark = ChkSettingsDark.IsChecked == true;
            if (isDark == _isDarkTheme) return;

            _isDarkTheme = isDark;
            TriggerThemeTransition();
        }

        private void TriggerThemeTransition()
        {
            // Replicate smooth HTML/CSS transition by cross-fading a visual snapshot of the window
            try
            {
                int width = (int)ActualWidth;
                int height = (int)ActualHeight;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);

                var overlay = new System.Windows.Controls.Image
                {
                    Source = rtb,
                    Width = width,
                    Height = height,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(-1)
                };

                RootGrid.Children.Add(overlay);
                Grid.SetRowSpan(overlay, 99);
                Grid.SetColumnSpan(overlay, 99);
                Panel.SetZIndex(overlay, 200);

                ThemeManager.SetTheme(_isDarkTheme);
                // Update DWM backdrop for the new theme
                try
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(this);
                    ApplyDwmBackdrop(h.Handle, _isDarkTheme);
                }
                catch { }
                UpdateThemeIcons();
                SaveThemePreference(_isDarkTheme);

                var fadeAnimation = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.25))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                fadeAnimation.Completed += (s, ev) =>
                {
                    RootGrid.Children.Remove(overlay);
                };
                overlay.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
            }
            catch
            {
                ThemeManager.SetTheme(_isDarkTheme);
                UpdateThemeIcons();
                SaveThemePreference(_isDarkTheme);
            }
        }

        private void UpdateThemeIcons()
        {
            if (_isDarkTheme)
            {
                PathSun.Visibility = Visibility.Visible;
                PathMoon.Visibility = Visibility.Collapsed;
            }
            else
            {
                PathSun.Visibility = Visibility.Collapsed;
                PathMoon.Visibility = Visibility.Visible;
            }
        }

        private void BtnSettingsOpen_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.18));
            var scaleUp = new DoubleAnimation(0.96, 1.0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };
            var translateUp = new DoubleAnimation(15, 0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };
            
            // Create blur dynamically with high quality rendering bias to eliminate flicker and keep fonts sharp
            var blur = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 0,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            };
            MainLayout.Effect = blur;
            
            var blurOpen = new DoubleAnimation(0.0, 15.0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };

            SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            ModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            ModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateUp);
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurOpen);
        }

        private void BtnSettingsClose_Click(object sender, RoutedEventArgs e)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.15));
            var scaleDown = new DoubleAnimation(1.0, 0.96, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
            var translateDown = new DoubleAnimation(0, 15, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
            
            var blur = MainLayout.Effect as System.Windows.Media.Effects.BlurEffect;
            if (blur != null)
            {
                var blurClose = new DoubleAnimation(blur.Radius, 0.0, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
                blurClose.Completed += (s, ev) =>
                {
                    MainLayout.Effect = null; // Completely remove effect to restore ClearType text subpixel rendering
                };
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurClose);
            }

            fadeOut.Completed += async (s, ev) =>
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                await RefreshStatusAsync();
            };

            SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            ModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
            ModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateDown);
        }

        private void SettingsOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource == SettingsOverlay)
            {
                BtnSettingsClose_Click(this, new RoutedEventArgs());
            }
        }

        // --- Settings Page Button Handlers ---
        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logDir = Path.Combine(appData, "PatreonArchiverBridge", "Logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                Process.Start("explorer.exe", logDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open logs folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnTelegram_Click(object sender, RoutedEventArgs e) => OpenUrl(TelegramUrl);
        private void BtnCoffee_Click(object sender, RoutedEventArgs e) => OpenUrl(CoffeeUrl);
        private void BtnGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);
        private void BtnRate_Click(object sender, RoutedEventArgs e) => OpenUrl(ChromeStoreUrl);

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- Application Update Flows (Velopack/Simulation) ---
        private async void CheckForAppUpdatesAsync()
        {
            try
            {
                var source = new Velopack.Sources.GithubSource("https://github.com/r1kp/patreon-archiver-bridge", null, false);
                var mgr = new Velopack.UpdateManager(source);

                var updateInfo = await mgr.CheckForUpdatesAsync().ConfigureAwait(true);
                if (updateInfo != null && updateInfo.TargetFullRelease != null)
                {
                    string newVersion = updateInfo.TargetFullRelease.Version.ToString();
                    string currentVersion = mgr.CurrentVersion?.ToString() ?? "1.0.0";
                    string notes = updateInfo.TargetFullRelease.NotesMarkdown ?? "• Security and performance updates.\n• Minor bug fixes.";

                    ShowUpdateOverlay(currentVersion, newVersion, notes);
                }
            }
            catch (Exception ex)
            {
                // Do not write detailed error files if the application runs as a portable build/development build (Velopack expects setup installation)
                if (ex.GetType().Name == "NotInstalledException" || ex.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
                {
                    LogInfo("Velopack app update check: Skipped (Application is not installed via setup/installer).");
                }
                else
                {
                    LogError("Velopack app update check", ex);
                }
                System.Diagnostics.Debug.WriteLine($"Velopack check failed: {ex.Message}");
            }
        }

        private void ShowUpdateOverlay(string currentVer, string newVer, string releaseNotes)
        {
            TxtUpdateVersions.Text = $"Version {currentVer} → {newVer}";
            TxtReleaseNotes.Text = releaseNotes;

            UpdateOverlay.Visibility = Visibility.Visible;
            
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.18));
            var scaleUp = new DoubleAnimation(0.96, 1.0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };
            var translateUp = new DoubleAnimation(15, 0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };
            
            var blur = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 0,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            };
            MainLayout.Effect = blur;
            
            var blurOpen = new DoubleAnimation(0.0, 15.0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };

            UpdateOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            UpdateModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            UpdateModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            UpdateModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateUp);
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurOpen);
        }

        private void HideUpdateOverlay(bool showAlertIcon)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.15));
            var scaleDown = new DoubleAnimation(1.0, 0.96, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
            var translateDown = new DoubleAnimation(0, 15, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
            
            var blur = MainLayout.Effect as System.Windows.Media.Effects.BlurEffect;
            if (blur != null)
            {
                var blurClose = new DoubleAnimation(blur.Radius, 0.0, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
                blurClose.Completed += (s, ev) => { MainLayout.Effect = null; };
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurClose);
            }

            fadeOut.Completed += (s, ev) =>
            {
                UpdateOverlay.Visibility = Visibility.Collapsed;
                if (showAlertIcon)
                {
                    GridUpdateAlert.Visibility = Visibility.Visible;
                }
            };

            UpdateOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            UpdateModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            UpdateModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
            UpdateModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateDown);
        }

        private void ShowWhatsNewOverlay(string version, string changelog)
        {
            TxtWhatsNewVersion.Text = $"Welcome to v{version}";
            TxtWhatsNewContent.Text = changelog;

            WhatsNewOverlay.Visibility = Visibility.Visible;
            
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.18));
            var scaleUp = new DoubleAnimation(0.96, 1.0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };
            var translateUp = new DoubleAnimation(15, 0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };
            
            var blur = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 0,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
            };
            MainLayout.Effect = blur;
            
            var blurOpen = new DoubleAnimation(0.0, 15.0, TimeSpan.FromSeconds(0.25)) { EasingFunction = ease };

            WhatsNewOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            WhatsNewModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            WhatsNewModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            WhatsNewModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateUp);
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurOpen);
        }

        private void HideWhatsNewOverlay()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.15));
            var scaleDown = new DoubleAnimation(1.0, 0.96, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
            var translateDown = new DoubleAnimation(0, 15, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
            
            var blur = MainLayout.Effect as System.Windows.Media.Effects.BlurEffect;
            if (blur != null)
            {
                var blurClose = new DoubleAnimation(blur.Radius, 0.0, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
                blurClose.Completed += (s, ev) => { MainLayout.Effect = null; };
                blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurClose);
            }

            fadeOut.Completed += (s, ev) =>
            {
                WhatsNewOverlay.Visibility = Visibility.Collapsed;
            };

            WhatsNewOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            WhatsNewModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            WhatsNewModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
            WhatsNewModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateDown);
        }

        private void BtnUpdateLater_Click(object sender, RoutedEventArgs e)
        {
            HideUpdateOverlay(showAlertIcon: true);
        }

        private void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            HideUpdateOverlay(showAlertIcon: false);
            _isTransitioningToUpdate = true;
            
            var progressWindow = new UpdateProgressWindow(this);
            progressWindow.Show();
            
            this.Close(); // Close MainWindow completely during updates!
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // Explicitly shut down application only if we're not transitioning to the update window
            if (!_isTransitioningToUpdate)
            {
                Application.Current.Shutdown();
            }
        }

        public void ShowWhatsNewOverlaySimulated()
        {
            ShowWhatsNewOverlay("1.1.0", "• Improved download stability and speed.\n• Fixed minimizing and closing window borders.\n• Added seamless background update notifications.\n• Cleaned UI alignment and styling.");
        }

        private void BtnWhatsNewClose_Click(object sender, RoutedEventArgs e)
        {
            HideWhatsNewOverlay();
        }

        private void BtnUpdateAlert_Click(object sender, RoutedEventArgs e)
        {
            GridUpdateAlert.Visibility = Visibility.Collapsed;
            ShowUpdateOverlay("1.0.0", "1.1.0", "• Improved download stability and speed.\n• Fixed minimizing and closing window borders.\n• Added seamless background update notifications.\n• Cleaned UI alignment and styling.");
        }

        private void Hyperlink_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(GitHubUrl);
        }





        // --- Helper Helpers ---
        private string GetDefaultDownloadDir() => Core.BridgeCore.GetDefaultDownloadDir();
        private string? FindYtDlp() => Core.BridgeCore.FindYtDlp();
        private string? FindFfmpeg() => Core.BridgeCore.FindFfmpeg();
        private Task<string> GetYtdlpVersionAsync(string path) => Core.BridgeCore.GetYtdlpVersionAsync(path);

        public bool ReadThemePreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                if (key != null)
                {
                    object? val = key.GetValue("ThemeDark");
                    if (val != null)
                    {
                        return Convert.ToInt32(val) == 1;
                    }
                }
            }
            catch { }
            return false; // Default Light
        }

        private void SaveThemePreference(bool isDark)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
                key.SetValue("ThemeDark", isDark ? 1 : 0);
            }
            catch { }
        }

        private void BtnCloseNotification_Click(object sender, RoutedEventArgs e)
        {
            HideNotification();
        }

        private void ShowNotification(string message)
        {
            TxtNotification.Text = message;
            NotificationBanner.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slideDown = new ThicknessAnimation(
                new Thickness(0, -60, 0, 0),
                new Thickness(0, 16, 0, 0),
                TimeSpan.FromSeconds(0.45)) { EasingFunction = ease };

            NotificationBanner.BeginAnimation(FrameworkElement.MarginProperty, slideDown);

            // Auto-hide after 5 seconds
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                HideNotification();
            };
            timer.Start();
        }

        private void HideNotification()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slideUp = new ThicknessAnimation(
                new Thickness(0, 16, 0, 0),
                new Thickness(0, -60, 0, 0),
                TimeSpan.FromSeconds(0.3)) { EasingFunction = ease };

            NotificationBanner.BeginAnimation(FrameworkElement.MarginProperty, slideUp);
        }



        private string GetEngineStatusWarningText()
        {
            var issues = new System.Collections.Generic.List<string>();
            
            string? ytdlpPath = FindYtDlp();
            string? ffmpegPath = FindFfmpeg();
            bool hasYtdlp = !string.IsNullOrEmpty(ytdlpPath);
            bool hasFfmpeg = !string.IsNullOrEmpty(ffmpegPath);
            bool isRegistered = CheckRegistryStatus();

            if (!hasYtdlp)
                issues.Add("• Video engine (yt-dlp) is not installed.");
            if (!hasFfmpeg)
                issues.Add("• Video merger (FFmpeg) is not installed.");
            if (!isRegistered)
                issues.Add("• Chrome extension registry link is missing.");

            if (hasYtdlp && !string.IsNullOrEmpty(_latestYtdlpVersion) && _latestYtdlpVersion != "unknown")
            {
                string localVer = _cachedYtdlpVersion ?? "unknown";
                if (localVer != "unknown" && _latestYtdlpVersion != localVer)
                {
                    issues.Add($"• Update available for video engine (v{localVer} \u2192 v{_latestYtdlpVersion}).");
                }
            }

            if (issues.Count > 0)
            {
                return "✗ The following components need attention:\n" + string.Join("\n", issues);
            }
            return "";
        }

        public static void LogInfo(string message)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logDir = Path.Combine(appData, "PatreonArchiverBridge", "Logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string logFile = Path.Combine(logDir, $"bridge_log_{DateTime.Now:yyyy-MM-dd}.log");
                string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] INFO: {message}{Environment.NewLine}";
                File.AppendAllText(logFile, logLine);
                CleanupOldLogs(logDir);
            }
            catch
            {
                // Silent fallback to prevent logging errors from breaking execution
            }
        }

        public static void LogException(string context, Exception ex)
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logDir = Path.Combine(appData, "PatreonArchiverBridge", "Logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string logFile = Path.Combine(logDir, $"bridge_error_{DateTime.Now:yyyy-MM-dd}.log");
                
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"Context: {context}");
                sb.AppendLine($"OS Version: {Environment.OSVersion}");
                sb.AppendLine($"64-Bit OS: {Environment.Is64BitOperatingSystem}");
                sb.AppendLine($"Message: {ex.Message}");
                sb.AppendLine($"Exception Type: {ex.GetType().FullName}");
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(ex.StackTrace);
                if (ex.InnerException != null)
                {
                    sb.AppendLine("Inner Exception:");
                    sb.AppendLine($"  Message: {ex.InnerException.Message}");
                    sb.AppendLine($"  Type: {ex.InnerException.GetType().FullName}");
                    sb.AppendLine("  Stack Trace:");
                    sb.AppendLine(ex.InnerException.StackTrace);
                }
                sb.AppendLine("================================================================================");
                sb.AppendLine();

                File.AppendAllText(logFile, sb.ToString());
                CleanupOldLogs(logDir);
                LogInfo($"ERROR in {context}: {ex.Message}");
            }
            catch
            {
                // Silent fallback to prevent logging errors from breaking execution
            }
        }

        public static void LogError(string context, Exception ex)
        {
            LogException(context, ex);
        }

        private static void CleanupOldLogs(string logDir)
        {
            try
            {
                var dir = new DirectoryInfo(logDir);
                if (!dir.Exists) return;

                var files = dir.GetFiles("*.log");
                if (files.Length > 10)
                {
                    // Sort by LastWriteTime ascending (oldest first)
                    var sorted = System.Linq.Enumerable.ToArray(
                        System.Linq.Enumerable.OrderBy(files, f => f.LastWriteTime)
                    );

                    int deleteCount = sorted.Length - 10;
                    for (int i = 0; i < deleteCount; i++)
                    {
                        try
                        {
                            sorted[i].Delete();
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}