using System;
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PatreonArchiverBridge.Uninstaller
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

                ApplyDwmBackdrop(hwnd, false); // Uninstaller uses light mode by default

                var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                source.AddHook(new System.Windows.Interop.HwndSourceHook(WndProc));
            }
            catch { }
        }

        private int _currentStep = 0;
        private string _installDir = "";
        private static System.Threading.Mutex? _mutex;

        public MainWindow()
        {
            if (!CheckSingleInstance())
            {
                Close();
                return;
            }

            InitializeComponent();

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

        private static bool CheckSingleInstance()
        {
            _mutex = new System.Threading.Mutex(true, "Global\\PatreonArchiverBridge_Uninstaller_Mutex", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("The uninstaller is already running.", "Uninstall In Progress", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _installDir = FindInstallDir();
            TxtInstallDir.Text = _installDir;
            UpdateStepUI();
        }

        private string FindInstallDir()
        {
            try
            {
                foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    using var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PatreonArchiverBridge");
                    if (key != null)
                    {
                        string? loc = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                        {
                            return loc;
                        }
                    }
                }
            }
            catch { }

            // Default fallback
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Programs", "Patreon Archiver Bridge");
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
            CancelUninstall();
        }

        private void CancelUninstall()
        {
            if (_currentStep == 1) // Uninstalling
            {
                return; // Cannot cancel mid-uninstall
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

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelUninstall();
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 0) // Start Uninstall
            {
                _currentStep = 1;
                UpdateStepUI();
                await StartUninstallAsync();
            }
            else if (_currentStep == 2) // Close App
            {
                Close();
            }
        }

        private void UpdateStepUI()
        {
            PanelWelcome.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelProgress.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelComplete.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;

            BtnCancel.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_currentStep == 0)
            {
                BtnNext.Content = "Uninstall";
                BtnNext.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }
            else if (_currentStep == 1)
            {
                BtnNext.Visibility = Visibility.Collapsed;
            }
            else if (_currentStep == 2)
            {
                BtnNext.Visibility = Visibility.Visible;
                BtnNext.Content = "Finish";
                BtnNext.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }

            UpdateStepCircle(StepCircleWelcome, StepTextWelcome, StepDotWelcome, 0);
            UpdateStepCircle(StepCircleUninstalling, StepTextUninstalling, StepDotUninstalling, 1);
            UpdateStepCircle(StepCircleComplete, StepTextComplete, StepDotComplete, 2);
        }

        private void UpdateStepCircle(Border circle, TextBlock text, Ellipse dot, int stepIdx)
        {
            var accentBrush = (Brush)Application.Current.Resources["AccentBrush"];
            var textDimBrush = (Brush)Application.Current.Resources["TextDimBrush"];
            var textBrush = (Brush)Application.Current.Resources["TextBrush"];
            var cardBorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"];
            var cardBgBrush = (Brush)Application.Current.Resources["CardBgBrush"];

            if (stepIdx < _currentStep)
            {
                circle.Background = accentBrush;
                circle.BorderBrush = accentBrush;
                dot.Visibility = Visibility.Collapsed;
                text.Foreground = textBrush;
                text.FontWeight = FontWeights.Medium;
            }
            else if (stepIdx == _currentStep)
            {
                circle.Background = cardBgBrush;
                circle.BorderBrush = accentBrush;
                dot.Visibility = Visibility.Visible;
                text.Foreground = accentBrush;
                text.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                circle.Background = cardBgBrush;
                circle.BorderBrush = cardBorderBrush;
                dot.Visibility = Visibility.Collapsed;
                text.Foreground = textDimBrush;
                text.FontWeight = FontWeights.Medium;
            }
        }

        private async Task StartUninstallAsync()
        {
            bool cleanAll = ChkCleanAll.IsChecked == true;

            try
            {
                UpdateStatus(10, "Closing running bridge processes...");
                KillRunningProcesses();
                await Task.Delay(300);

                UpdateStatus(30, "Removing Native Messaging Registry entries...");
                DeleteRegistryEntries();
                await Task.Delay(300);

                UpdateStatus(50, "Deleting Desktop and Start Menu shortcuts...");
                DeleteAppShortcuts();
                await Task.Delay(300);

                UpdateStatus(70, "Scheduling file cleanup sequence...");
                ScheduleSelfDeletion(cleanAll);
                await Task.Delay(300);

                UpdateStatus(100, "Uninstall complete.");
                await Task.Delay(400);

                _currentStep = 2;
                UpdateStepUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Uninstall failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            foreach (var p in Process.GetProcessesByName("PatreonArchiverBridge"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            foreach (var p in Process.GetProcessesByName("PatreonArchiverBridge.Host"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        private void DeleteRegistryEntries()
        {
            const string hostName = "com.patreonarchiver.ytdlp";

            foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    baseKey.DeleteSubKeyTree($@"Software\Google\Chrome\NativeMessagingHosts\{hostName}", throwOnMissingSubKey: false);
                    baseKey.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PatreonArchiverBridge", throwOnMissingSubKey: false);
                }
                catch { }
            }
        }

        private void DeleteAppShortcuts()
        {
            string[] shortcutNames = new[] { "Patreon Archiver Bridge.lnk", "PatreonArchiverBridge.lnk" };

            // User Profile folders
            string userDesktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string userStartFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");

            // Public profile folders
            string publicDesktopFolder = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "Desktop");
            string publicStartFolder = Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", @"Microsoft\Windows\Start Menu\Programs");

            foreach (var folder in new[] { userDesktopFolder, userStartFolder, publicDesktopFolder, publicStartFolder })
            {
                foreach (var name in shortcutNames)
                {
                    try
                    {
                        string file = Path.Combine(folder, name);
                        if (File.Exists(file)) File.Delete(file);
                    }
                    catch { }
                }
            }
        }

        private void ScheduleSelfDeletion(bool cleanAll)
        {
            int pid = Process.GetCurrentProcess().Id;
            string tempDir = Path.GetTempPath();
            string cleanupBat = Path.Combine(tempDir, "uninstall_pam_bridge.bat");
            string systemDir = Path.Combine(_installDir, "current", "System");
 
            string script = $@"@echo off
:loop
tasklist /FI ""PID eq {pid}"" | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto loop
)
if exist ""{systemDir}"" rd /s /q ""{systemDir}""
";
            if (cleanAll)
            {
                script += $@"if exist ""{_installDir}\logs"" rd /s /q ""{_installDir}\logs""
";
            }
 
            script += $@"if exist ""{_installDir}\current\PatreonArchiverBridge_uninstaller.exe"" del /f /q ""{_installDir}\current\PatreonArchiverBridge_uninstaller.exe""
if exist ""{_installDir}"" rd /s /q ""{_installDir}""
del ""%~f0""
";
 
            File.WriteAllText(cleanupBat, script);
 
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{cleanupBat}\"\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
    }
}
