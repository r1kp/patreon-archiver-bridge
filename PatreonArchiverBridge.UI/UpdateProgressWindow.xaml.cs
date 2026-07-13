using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PatreonArchiverBridge.UI
{
    public partial class UpdateProgressWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;

        private readonly MainWindow? _parent;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly DispatcherTimer _funnyTimer;
        private bool _isClosingConfirmed = false;
        private bool _updateCompleted = false; // Guard against race condition

        private readonly string[] _funnyMessages = new[]
        {
            "Brewing fresh coffee...",
            "Polishing the download gears...",
            "Cleaning the engine exhaust...",
            "Replacing worn-out flux capacitors...",
            "Downloading shiny new pixels...",
            "Feeding the code hamsters...",
            "Dusting off the mainframe...",
            "Optimizing internet pipe alignment...",
            "Tightening loose nuts and bolts..."
        };
        private int _funnyMessageIndex = 0;

        public UpdateProgressWindow(MainWindow? parent = null)
        {
            InitializeComponent();
            _parent = parent;

            _funnyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _funnyTimer.Tick += FunnyTimer_Tick;
            _funnyTimer.Start();

            Loaded += UpdateProgressWindow_Loaded;
        }

        private void ApplyDwmBackdrop(IntPtr hwnd, bool isDark)
        {
            try
            {
                int useDarkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));

                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                int backdropType = 3; // DWMSBT_TRANSIENTWINDOW = Acrylic
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            catch { }
        }

        private bool ReadThemePreference()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PatreonArchiverBridge");
                if (key != null)
                {
                    object? val = key.GetValue("ThemeDark");
                    if (val != null)
                        return Convert.ToInt32(val) == 1;
                }
            }
            catch { }
            return false;
        }

        private void UpdateProgressWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;

            int cornerPref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            // Follow parent's theme preference
            bool isDark = _parent != null ? _parent.ReadThemePreference() : ReadThemePreference();
            ApplyDwmBackdrop(hwnd, isDark);

            // Start update download simulation
            StartUpdateFlow();
        }

        private void FunnyTimer_Tick(object? sender, EventArgs e)
        {
            _funnyMessageIndex = (_funnyMessageIndex + 1) % _funnyMessages.Length;
            TxtFunnyStatus.Text = _funnyMessages[_funnyMessageIndex];
        }

        private async void StartUpdateFlow()
        {
            try
            {
                // === REAL VELOPACK UPDATE FLOW ===
                var source = new Velopack.Sources.GithubSource("https://github.com/r1kp/patreon-archiver-bridge", null, false);
                var mgr = new Velopack.UpdateManager(source);


                TxtDownloading.Text = "Checking update details...";
                var updateInfo = await mgr.CheckForUpdatesAsync().ConfigureAwait(true);
                if (updateInfo == null || updateInfo.TargetFullRelease == null)
                {
                    MessageBox.Show("Everything is already up to date!", "Patreon Archiver", MessageBoxButton.OK, MessageBoxImage.Information);
                    _isClosingConfirmed = true;
                    Close();
                    return;
                }

                // Phase 1: Real Download
                TxtDownloading.Text = "Downloading update...";
                await mgr.DownloadUpdatesAsync(updateInfo, progress =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        TxtPercent.Text = $"{progress}%";
                    });
                }).ConfigureAwait(true);

                // Phase 2: Real Apply & Restart
                TxtDownloading.Text = "Installing update...";
                _funnyTimer.Stop();
                TxtFunnyStatus.Text = "Restarting application to apply updates...";
                TxtPercent.Text = "100%";
                await Task.Delay(1000).ConfigureAwait(true);

                _updateCompleted = true;
                _isClosingConfirmed = true;

                mgr.ApplyUpdatesAndRestart(updateInfo);
            }
            catch (TaskCanceledException)
            {
                // Handled in cancel flow (BtnCancelYes_Click)
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _isClosingConfirmed = true;
                Close();
            }
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosingConfirmed) return;

            e.Cancel = true; // Stop immediate closing
            ShowCancelOverlay();
        }

        // --- Custom Cancel Confirmation Modal Flows ---
        private void ShowCancelOverlay()
        {
            CancelOverlay.Visibility = Visibility.Visible;
            
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(0.18));
            var scaleUp = new DoubleAnimation(0.96, 1.0, TimeSpan.FromSeconds(0.22)) { EasingFunction = ease };
            var translateUp = new DoubleAnimation(10, 0, TimeSpan.FromSeconds(0.22)) { EasingFunction = ease };

            CancelOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            CancelModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            CancelModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
            CancelModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateUp);
        }

        private void HideCancelOverlay()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.15));
            var scaleDown = new DoubleAnimation(1.0, 0.96, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };
            var translateDown = new DoubleAnimation(0, 10, TimeSpan.FromSeconds(0.15)) { EasingFunction = ease };

            fadeOut.Completed += (s, ev) =>
            {
                CancelOverlay.Visibility = Visibility.Collapsed;
            };

            CancelOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            CancelModalScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            CancelModalScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
            CancelModalTranslate.BeginAnimation(TranslateTransform.YProperty, translateDown);
        }

        private void BtnCancelNo_Click(object sender, RoutedEventArgs e)
        {
            HideCancelOverlay();
        }

        private void BtnCancelYes_Click(object sender, RoutedEventArgs e)
        {
            // If the update already completed while the confirm dialog was open,
            // do nothing — the success flow already created a new MainWindow.
            if (_updateCompleted) return;

            _funnyTimer.Stop();
            _cts.Cancel();
            
            _isClosingConfirmed = true;
            
            // Return to main app window
            var newMain = new MainWindow();
            newMain.Show();
            
            Close();
        }
    }
}
