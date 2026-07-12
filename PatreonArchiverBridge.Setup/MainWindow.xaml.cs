using System;
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PatreonArchiverBridge.Core;

namespace PatreonArchiverBridge.Setup
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
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
            int useDarkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));

            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

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

                int style = GetWindowLong(hwnd, GWL_STYLE);
                style |= WS_CAPTION | WS_MINIMIZEBOX;
                style &= ~WS_SYSMENU;
                SetWindowLong(hwnd, GWL_STYLE, style);

                int cornerPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                ApplyDwmBackdrop(hwnd, false); // Setup uses light mode by default

                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                source.AddHook(new System.Windows.Interop.HwndSourceHook(WndProc));
            }
            catch { }
        }

        private int _currentStep = 0;
        private readonly string[] _stepNames = { "welcome", "options", "extras", "progress", "complete" };
        
        private string _justMePath = "";
        private string _allUsersPath = "";

        private bool _isExistingInstallation = false;
        private string _existingInstallPath = "";
        private bool _existingInstallScopeAllUsers = false;

        private const string LicenseText = @"Patreon Archiver Bridge - License Agreement (PolyForm Noncommercial 1.0.0)

This software is licensed under the PolyForm Noncommercial License 1.0.0.
Full text: https://polyformproject.org/licenses/noncommercial/1.0.0

In short: You may use, copy, modify, and distribute this software and any
modified versions of it — including publishing your own changes — but ONLY
for noncommercial purposes. Any commercial use, including selling this
software or any modified version of it, is not permitted under this license.

Personal use, research, education, hobby projects, and use by charitable,
educational, or public research organizations are explicitly permitted.

Required Notice: Copyright r1kp (https://github.com/r1kp/patreon-archiver-bridge)

AS FAR AS THE LAW ALLOWS, THE SOFTWARE COMES AS IS, WITHOUT ANY WARRANTY OR
CONDITION, AND THE LICENSOR WILL NOT BE LIABLE TO YOU FOR ANY DAMAGES
ARISING OUT OF THESE TERMS OR THE USE OR NATURE OF THE SOFTWARE, UNDER ANY
KIND OF LEGAL CLAIM.

By clicking ""I Agree"", you accept the full terms of the PolyForm
Noncommercial License 1.0.0 linked above.";

        private string DetectExistingInstallation()
        {
            try
            {
                // 1. Check HKCU
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PatreonArchiverBridge"))
                {
                    if (key != null)
                    {
                        var path = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            _existingInstallScopeAllUsers = false;
                            return path;
                        }
                    }
                }

                // 2. Check HKLM
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PatreonArchiverBridge"))
                {
                    if (key != null)
                    {
                        var path = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            _existingInstallScopeAllUsers = true;
                            return path;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking existing installation: {ex.Message}");
            }
            return "";
        }

        public MainWindow()
        {
            InitializeComponent();
            TxtEula.Text = LicenseText;

            try
            {
                var iconUri = new Uri("pack://application:,,,/PatreonArchiverBridge.UI.Shared;component/Resources/setup_icon.ico", UriKind.Absolute);
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);

                // Decode the ICO file to grab the largest (highest quality) frame for the sidebar logo
                var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(iconUri, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                System.Windows.Media.Imaging.BitmapFrame? bestFrame = null;
                int maxW = 0;
                foreach (var frame in decoder.Frames)
                {
                    if (frame.PixelWidth > maxW)
                    {
                        maxW = frame.PixelWidth;
                        bestFrame = frame;
                    }
                }
                if (bestFrame != null)
                {
                    ImgLogo.Source = bestFrame;
                }
            }
            catch { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Initialize target directories
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _justMePath = Path.Combine(localAppData, "Programs", "Patreon Archiver Bridge");

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            _allUsersPath = Path.Combine(programFiles, "Patreon Archiver Bridge");

            TxtDestPath.Text = _justMePath; // Default scope: Just Me

            // Detect existing installation
            string existingPath = DetectExistingInstallation();
            if (!string.IsNullOrEmpty(existingPath))
            {
                _isExistingInstallation = true;
                _existingInstallPath = existingPath;

                // Configure Welcome screen Badge
                BrdExistingInfo.Visibility = Visibility.Visible;
                TxtExistingInfo.Text = $"ℹ Existing installation detected at:\n{existingPath}\nSetup will run in Upgrade/Modify mode.";

                // Set paths to target
                _justMePath = existingPath;
                _allUsersPath = existingPath;
                TxtDestPath.Text = existingPath;

                // Disable options editing to protect path integrity
                RadioScopeMe.IsEnabled = false;
                RadioScopeAll.IsEnabled = false;
                BtnBrowse.IsEnabled = false;
                TxtDestPath.IsEnabled = false;

                if (_existingInstallScopeAllUsers)
                {
                    RadioScopeAll.IsChecked = true;
                }
                else
                {
                    RadioScopeMe.IsChecked = true;
                }
            }

            UpdateStepUI();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
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
            CancelSetup();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelSetup();
        }

        private void CancelSetup()
        {
            if (_currentStep == 3) // Installing
            {
                return; // Cannot cancel mid-installation
            }

            if (GridCancelOverlay != null)
            {
                GridCancelOverlay.Visibility = Visibility.Visible;
                if (MainContentGrid != null)
                {
                    MainContentGrid.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 15, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
                }
            }
        }

        private void BtnCancelNo_Click(object sender, RoutedEventArgs e)
        {
            if (GridCancelOverlay != null)
            {
                GridCancelOverlay.Visibility = Visibility.Collapsed;
                if (MainContentGrid != null)
                {
                    MainContentGrid.Effect = null;
                }
            }
        }

        private void BtnCancelYes_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ChkAccept_Click(object sender, RoutedEventArgs e)
        {
            BtnNext.IsEnabled = ChkAccept.IsChecked == true;
        }

        private void RadioScope_Checked(object sender, RoutedEventArgs e)
        {
            if (RadioScopeMe == null || TxtDestPath == null) return;

            if (RadioScopeMe.IsChecked == true)
            {
                TxtDestPath.Text = _justMePath;
            }
            else
            {
                TxtDestPath.Text = _allUsersPath;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Destination Folder",
                InitialDirectory = TxtDestPath.Text
            };

            if (dialog.ShowDialog(this) == true)
            {
                TxtDestPath.Text = dialog.FolderName;
            }
        }

        private void ChkYtdlp_Click(object sender, RoutedEventArgs e)
        {
            bool ytdlpChecked = ChkYtdlp.IsChecked == true;

            if (TxtWarnYtdlp != null)
            {
                TxtWarnYtdlp.Visibility = ytdlpChecked ? Visibility.Collapsed : Visibility.Visible;
            }

            // Dependents require yt-dlp to run
            ChkBrdVisualState(BrdFfmpeg, ChkFfmpeg, ytdlpChecked);
            ChkBrdVisualState(BrdYtdlpejs, ChkYtdlpejs, ytdlpChecked);

            // Update dependent warnings visibility based on yt-dlp check state
            if (TxtWarnFfmpeg != null)
            {
                TxtWarnFfmpeg.Visibility = (ytdlpChecked && ChkFfmpeg.IsChecked == true) ? Visibility.Collapsed : Visibility.Visible;
            }
            if (TxtWarnYtdlpejs != null)
            {
                TxtWarnYtdlpejs.Visibility = (ytdlpChecked && ChkYtdlpejs.IsChecked == true) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void ChkFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            bool checkedState = ChkFfmpeg.IsChecked == true;
            if (TxtWarnFfmpeg != null)
            {
                TxtWarnFfmpeg.Visibility = checkedState ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void ChkYtdlpejs_Click(object sender, RoutedEventArgs e)
        {
            bool checkedState = ChkYtdlpejs.IsChecked == true;
            if (TxtWarnYtdlpejs != null)
            {
                TxtWarnYtdlpejs.Visibility = checkedState ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void ChkBrdVisualState(Border brd, CheckBox chk, bool parentChecked)
        {
            if (parentChecked)
            {
                brd.Opacity = 1.0;
                chk.IsEnabled = true;
                chk.IsChecked = true;
            }
            else
            {
                brd.Opacity = 0.45;
                chk.IsEnabled = false;
                chk.IsChecked = false;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep > 0 && _currentStep < 3)
            {
                _currentStep--;
                UpdateStepUI();
            }
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 0) // Welcome EULA -> Options
            {
                _currentStep = 1;
                UpdateStepUI();
            }
            else if (_currentStep == 1) // Options -> Extras
            {
                _currentStep = 2;
                UpdateStepUI();
            }
            else if (_currentStep == 2) // Extras -> Progress (Start installation)
            {
                _currentStep = 3;
                UpdateStepUI();
                await StartInstallationAsync();
            }
            else if (_currentStep == 4) // Complete -> Exit and launch
            {
                if (ChkLaunchApp.IsChecked == true)
                {
                    string targetExe = Path.Combine(TxtDestPath.Text, "current", "PatreonArchiverBridge.exe");
                    if (File.Exists(targetExe))
                    {
                        var psi = new ProcessStartInfo(targetExe)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.Combine(TxtDestPath.Text, "current")
                        };
                        Process.Start(psi);
                    }
                }
                Close();
            }
        }

        private void UpdateStepUI()
        {
            // Panel visibility
            PanelWelcome.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelOptions.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelExtras.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelProgress.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
            PanelComplete.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;

            // Navigation Buttons states
            BtnBack.Visibility = (_currentStep > 0 && _currentStep < 3) ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.Visibility = _currentStep < 3 ? Visibility.Visible : Visibility.Collapsed;

            if (_currentStep == 0)
            {
                BtnNext.Content = "Next";
                BtnNext.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                BtnNext.IsEnabled = ChkAccept.IsChecked == true;
            }
            else if (_currentStep == 1)
            {
                BtnNext.Content = "Next";
                BtnNext.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                BtnNext.IsEnabled = !string.IsNullOrWhiteSpace(TxtDestPath.Text);
            }
            else if (_currentStep == 2)
            {
                BtnNext.Content = "Install";
                BtnNext.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                BtnNext.IsEnabled = true;
                CheckExistingComponents(TxtDestPath.Text);
            }
            else if (_currentStep == 3)
            {
                BtnNext.Visibility = Visibility.Collapsed;
            }
            else if (_currentStep == 4)
            {
                BtnNext.Visibility = Visibility.Visible;
                BtnNext.Content = "Finish";
                BtnNext.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                BtnNext.IsEnabled = true;

                if (_isExistingInstallation)
                {
                    TxtCompleteMessage.Text = "The Patreon Archiver Bridge has been upgraded successfully.";
                }
                else
                {
                    TxtCompleteMessage.Text = "The Patreon Archiver Bridge has been installed successfully.";
                }
            }

            // Stepper Circles
            UpdateStepCircle(StepCircleWelcome, StepTextWelcome, StepDotWelcome, 0);
            UpdateStepCircle(StepCircleOptions, StepTextOptions, StepDotOptions, 1);
            UpdateStepCircle(StepCircleExtras, StepTextExtras, StepDotExtras, 2);
            UpdateStepCircle(StepCircleInstalling, StepTextInstalling, StepDotInstalling, 3);
            UpdateStepCircle(StepCircleComplete, StepTextComplete, StepDotComplete, 4);
        }

        private void UpdateStepCircle(Border circle, TextBlock text, Ellipse dot, int stepIdx)
        {
            var accentBrush = (Brush)Application.Current.Resources["AccentBrush"];
            var textDimBrush = (Brush)Application.Current.Resources["TextDimBrush"];
            var textBrush = (Brush)Application.Current.Resources["TextBrush"];
            var cardBorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"];
            var cardBgBrush = (Brush)Application.Current.Resources["CardBgBrush"];

            if (stepIdx < _currentStep) // Completed
            {
                circle.Background = accentBrush;
                circle.BorderBrush = accentBrush;
                dot.Visibility = Visibility.Collapsed;
                text.Foreground = textBrush;
                text.FontWeight = FontWeights.Medium;
            }
            else if (stepIdx == _currentStep) // Active
            {
                circle.Background = cardBgBrush;
                circle.BorderBrush = accentBrush;
                dot.Visibility = Visibility.Visible;
                text.Foreground = accentBrush;
                text.FontWeight = FontWeights.SemiBold;
            }
            else // Pending
            {
                circle.Background = cardBgBrush;
                circle.BorderBrush = cardBorderBrush;
                dot.Visibility = Visibility.Collapsed;
                text.Foreground = textDimBrush;
                text.FontWeight = FontWeights.Medium;
            }
        }

        private void CheckExistingComponents(string targetDir)
        {
            bool ytdlpPresent = false;
            bool ffmpegPresent = false;
            bool ytdlpejsPresent = false;

            try
            {
                if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                {
                    string sysDir = Path.Combine(targetDir, "System");
                    if (Directory.Exists(sysDir))
                    {
                        string ytdlpPath = Path.Combine(sysDir, "yt-dlp.exe");
                        if (File.Exists(ytdlpPath))
                        {
                            ytdlpPresent = true;
                        }

                        string ffmpegPath = Path.Combine(sysDir, "ffmpeg.exe");
                        if (File.Exists(ffmpegPath))
                        {
                            ffmpegPresent = true;
                        }

                        string ejsPath = Path.Combine(sysDir, "yt_dlp_ejs");
                        if (Directory.Exists(ejsPath))
                        {
                            ytdlpejsPresent = true;
                        }
                    }
                }
            }
            catch { }

            // Update UI elements for yt-dlp
            if (ytdlpPresent)
            {
                TxtYtdlpTitle.Text = "yt-dlp video engine (Already Present)";
                TxtYtdlpTitle.Foreground = (Brush)Application.Current.Resources["TextDimBrush"];
            }
            else
            {
                TxtYtdlpTitle.Text = "yt-dlp video engine (Required for videos)";
                TxtYtdlpTitle.Foreground = (Brush)Application.Current.Resources["TextBrush"];
            }

            // Update UI elements for ffmpeg
            if (ffmpegPresent)
            {
                TxtFfmpegTitle.Text = "FFmpeg audio/video merger (Already Present)";
                TxtFfmpegTitle.Foreground = (Brush)Application.Current.Resources["TextDimBrush"];
            }
            else
            {
                TxtFfmpegTitle.Text = "FFmpeg audio/video merger (Recommended)";
                TxtFfmpegTitle.Foreground = (Brush)Application.Current.Resources["TextBrush"];
            }

            // Update UI elements for yt-dlp-ejs
            if (ytdlpejsPresent)
            {
                TxtYtdlpejsTitle.Text = "yt-dlp-ejs solver (Already Present)";
                TxtYtdlpejsTitle.Foreground = (Brush)Application.Current.Resources["TextDimBrush"];
            }
            else
            {
                TxtYtdlpejsTitle.Text = "yt-dlp-ejs (Required for YouTube support)";
                TxtYtdlpejsTitle.Foreground = (Brush)Application.Current.Resources["TextBrush"];
            }
        }

        private async Task StartInstallationAsync()
        {
            string targetDir = TxtDestPath.Text;
            bool isAllUsers = RadioScopeAll.IsChecked == true;
            bool createDesktop = ChkShortcutDesktop.IsChecked == true;
            bool createStart = ChkShortcutStart.IsChecked == true;
            bool downloadYtdlp = ChkYtdlp.IsChecked == true;
            bool downloadFfmpeg = ChkFfmpeg.IsChecked == true;
            bool downloadYtdlpejs = ChkYtdlpejs.IsChecked == true;

            try
            {
                UpdateStatus(2, "Closing running instances...");
                KillRunningProcesses();
                await Task.Delay(500);

                UpdateStatus(5, "Creating directory structure...");
                Directory.CreateDirectory(targetDir);
                string sysDir = Path.Combine(targetDir, "current", "System");
                Directory.CreateDirectory(sysDir);
                Directory.CreateDirectory(Path.Combine(sysDir, "logs"));

                UpdateStatus(15, "Extracting application files...");
                await Task.Run(() => CopyAppBinaries(targetDir));
                await Task.Delay(300);

                UpdateStatus(50, "Registering native host...");
                RegisterInstalledHost(targetDir);

                // Write Uninstall registry keys for Add/Remove Programs
                WriteUninstallRegistryKeys(targetDir, isAllUsers);

                UpdateStatus(65, "Creating shortcuts...");
                await Task.Run(() => CreateAppShortcuts(targetDir, isAllUsers, createDesktop, createStart));
                await Task.Delay(300);

                if (downloadYtdlp)
                {
                    string ytdlpPath = Path.Combine(sysDir, "yt-dlp.exe");
                    if (File.Exists(ytdlpPath))
                    {
                        UpdateStatus(70, "yt-dlp is already present, skipping download.");
                        await Task.Delay(200);
                    }
                    else
                    {
                        UpdateStatus(70, "Downloading yt-dlp engine...");
                        var progress = new Progress<long>(totalRead => {
                            // Custom UI feedback if needed, let's keep it clean
                        });
                        await BridgeCore.DownloadFileWithProgressAsync(
                            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
                            ytdlpPath,
                            progress,
                            null
                        );
                    }
                }

                if (downloadFfmpeg)
                {
                    string ffmpegPath = Path.Combine(sysDir, "ffmpeg.exe");
                    if (File.Exists(ffmpegPath))
                    {
                        UpdateStatus(82, "FFmpeg is already present, skipping download.");
                        await Task.Delay(200);
                    }
                    else
                    {
                        UpdateStatus(82, "Downloading FFmpeg bundle...");
                        string zipTemp = Path.Combine(Path.GetTempPath(), "ffmpeg_temp.zip");
                        var progress = new Progress<long>(totalRead => {
                            // Custom UI feedback if needed
                        });
                        await BridgeCore.DownloadFileWithProgressAsync(
                            "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
                            zipTemp,
                            progress,
                            null
                        );

                        UpdateStatus(90, "Extracting FFmpeg binaries...");
                        await Task.Run(() =>
                        {
                            using var zip = ZipFile.OpenRead(zipTemp);
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                                    entry.FullName.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    string dest = Path.Combine(sysDir, entry.Name);
                                    entry.ExtractToFile(dest, overwrite: true);
                                }
                            }
                        });
                        try { File.Delete(zipTemp); } catch { }
                    }
                }

                if (downloadYtdlpejs)
                {
                    string ejsDir = Path.Combine(sysDir, "yt_dlp_ejs");
                    if (Directory.Exists(ejsDir))
                    {
                        UpdateStatus(94, "yt-dlp-ejs is already present, skipping download.");
                        await Task.Delay(200);
                    }
                    else
                    {
                        UpdateStatus(94, "Downloading yt-dlp-ejs solver...");
                        await DownloadYtdlpEjsAsync(sysDir);
                    }
                }

                UpdateStatus(100, "Setup complete!");
                await Task.Delay(400);

                _currentStep = 4;
                UpdateStepUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Setup failed with error: {ex.Message}", "Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void UpdateStatus(int pct, string status)
        {
            // Strip trailing dots
            string cleanStatus = status;
            if (cleanStatus.EndsWith("..."))
            {
                cleanStatus = cleanStatus.Substring(0, cleanStatus.Length - 3);
            }
            cleanStatus = cleanStatus.TrimEnd('.');

            TxtStatusLog.Text = cleanStatus;

            if (PanelJumpingDots != null)
            {
                PanelJumpingDots.Visibility = (pct == 100) ? Visibility.Collapsed : Visibility.Visible;
            }

            if (TxtProgressPercent != null)
            {
                TxtProgressPercent.Text = $"{pct}%";
            }

            var animation = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = pct,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            ProgFill.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
        }

        private void KillRunningProcesses()
        {
            foreach (var p in Process.GetProcessesByName("PatreonArchiverBridge.UI"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            foreach (var p in Process.GetProcessesByName("PatreonArchiverBridge.Host"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        private void CopyAppBinaries(string targetDir)
        {
            var assembly = typeof(MainWindow).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("velopack_setup.exe", StringComparison.OrdinalIgnoreCase));
            
            if (resourceName == null)
            {
                throw new Exception("Embedded Velopack setup engine resource not found!");
            }

            string tempSetup = Path.Combine(Path.GetTempPath(), "velopack_setup_temp.exe");
            
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var fileStream = File.Create(tempSetup))
            {
                stream!.CopyTo(fileStream);
            }

            // Start Velopack setup silently to the custom directory chosen by the user
            var psi = new ProcessStartInfo
            {
                FileName = tempSetup,
                Arguments = $"-s --installto \"{targetDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                process?.WaitForExit();
            }

            try { File.Delete(tempSetup); } catch { }
        }

        private void WriteUninstallRegistryKeys(string targetDir, bool allUsers)
        {
            try
            {
                var baseKey = allUsers ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = baseKey.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PatreonArchiverBridge");
                if (key != null)
                {
                    key.SetValue("DisplayName", "Patreon Archiver Bridge");
                    key.SetValue("DisplayVersion", "1.0.0");
                    key.SetValue("Publisher", "Patreon Archiver");
                    key.SetValue("DisplayIcon", Path.Combine(targetDir, "current", "PatreonArchiverBridge.exe"));
                    key.SetValue("UninstallString", Path.Combine(targetDir, "current", "PatreonArchiverBridge_uninstaller.exe"));
                    key.SetValue("InstallLocation", targetDir);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write uninstall keys: {ex.Message}");
            }
        }

        private void RegisterInstalledHost(string targetDir)
        {
            try
            {
                string hostPath = Path.Combine(targetDir, "current", "PatreonArchiverBridge.Host.exe");
                string systemDir = Path.Combine(targetDir, "current", "System");
                Directory.CreateDirectory(systemDir);
                
                string manifestPath = Path.Combine(systemDir, "bridge_manifest.json");
                var manifestData = new
                {
                    name = "com.patreonarchiver.ytdlp",
                    description = "Patreon Archive Manager bridge",
                    path = hostPath.Replace("\\", "/"),
                    type = "stdio",
                    allowed_origins = new[] { "chrome-extension://pjbbdkkgldalamlfbdahhhjpppiepbjg/" }
                };

                string json = System.Text.Json.JsonSerializer.Serialize(manifestData, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(manifestPath, json);

                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Google\Chrome\NativeMessagingHosts\com.patreonarchiver.ytdlp"))
                {
                    key.SetValue("", manifestPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to register native host: {ex.Message}");
            }
        }

        private void CreateAppShortcuts(string targetDir, bool allUsers, bool desktop, bool startmenu)
        {
            string exePath = Path.Combine(targetDir, "current", "PatreonArchiverBridge.exe");
            string currentDir = Path.Combine(targetDir, "current");

            string desktopFolder;
            string startFolder;

            if (allUsers)
            {
                desktopFolder = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "Desktop");
                startFolder = Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", @"Microsoft\Windows\Start Menu\Programs");
            }
            else
            {
                desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                startFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
            }

            string psTemplate = @"$s=New-Object -ComObject WScript.Shell;$l=$s.CreateShortcut(""{0}"");$l.TargetPath=""{1}"";$l.WorkingDirectory=""{2}"";$l.IconLocation=""{3}"";$l.Save()";

            if (desktop)
            {
                string lnk = Path.Combine(desktopFolder, "Patreon Archiver Bridge.lnk");
                RunPowerShell(string.Format(psTemplate, lnk, exePath, currentDir, exePath));
            }

            if (startmenu)
            {
                Directory.CreateDirectory(startFolder);
                string lnk = Path.Combine(startFolder, "Patreon Archiver Bridge.lnk");
                RunPowerShell(string.Format(psTemplate, lnk, exePath, currentDir, exePath));
            }
        }

        private void RunPowerShell(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch { }
        }

        private async Task DownloadYtdlpEjsAsync(string sysDir)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "PatreonArchiverBridgeSetup");

                string response = await client.GetStringAsync("https://pypi.org/pypi/yt-dlp-ejs/json");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                string? whlUrl = null;
                if (root.TryGetProperty("urls", out var urls))
                {
                    foreach (var urlObj in urls.EnumerateArray())
                    {
                        if (urlObj.TryGetProperty("packagetype", out var pt) && pt.GetString() == "bdist_wheel")
                        {
                            whlUrl = urlObj.GetProperty("url").GetString();
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(whlUrl)) return;

                string whlTemp = Path.Combine(Path.GetTempPath(), "ytdlp_ejs_wheel.zip");
                await BridgeCore.DownloadFileWithProgressAsync(whlUrl, whlTemp, null, null);

                // Extract to system directory
                await Task.Run(() =>
                {
                    string ejsDir = Path.Combine(sysDir, "yt_dlp_ejs");
                    Directory.CreateDirectory(ejsDir);

                    using var zip = ZipFile.OpenRead(whlTemp);
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.Contains("yt_dlp_ejs/") && !entry.FullName.EndsWith("/"))
                        {
                            string relPath = entry.FullName.Split(new[] { "yt_dlp_ejs/" }, StringSplitOptions.None)[1];
                            string dest = Path.Combine(ejsDir, relPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            entry.ExtractToFile(dest, overwrite: true);
                        }
                    }
                });

                try { File.Delete(whlTemp); } catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"yt-dlp-ejs download failed: {ex.Message}");
            }
        }
    }
}
