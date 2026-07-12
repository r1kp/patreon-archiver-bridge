using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PatreonArchiverBridge.UI.Shared;

namespace PatreonArchiverBridge.UI
{
    public partial class App : Application
    {
        private static Mutex? _mutex;

        // Win32 APIs for bringing the existing window to the foreground
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Set up global crash logger to log all unexpected application thread crashes
            this.DispatcherUnhandledException += (s, ev) =>
            {
                try
                {
                    PatreonArchiverBridge.UI.MainWindow.LogException("Global Unhandled Exception", ev.Exception);
                }
                catch { }
            };

            // Initialise Velopack hooks (shortcuts, installation event etc)
            Velopack.VelopackApp.Build().Run();

            if (e.Args.Length > 0 && e.Args[0] == "--pick-folder")
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select Patreon Archiver Download Folder",
                    Multiselect = false
                };
                
                if (dialog.ShowDialog() == true)
                {
                    Console.WriteLine(dialog.FolderName);
                }
                
                Shutdown(0);
                return;
            }

            // Single Instance check using Mutex
            const string mutexName = "Local\\PatreonArchiverBridgeUIUniqueMutexName";
            _mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // App is already running! Highlight it and quit.
                ActivateExistingInstance();
                Shutdown(0);
                return;
            }

            // Apply theme BEFORE showing the splash so it renders with the correct colors
            bool isDark = ReadSavedThemePreference();
            ThemeManager.SetTheme(isDark);

            // Show Splash Screen
            var splash = new SplashWindow();
            splash.Show();

            await Task.Run(async () =>
            {
                // Simulate background initialization tasks while the bridge animation plays
                await Task.Delay(1200);
            });

            // Start MainWindow
            var main = new MainWindow();
            this.MainWindow = main;
            
            // Turn off Topmost on splash so the main window can sit in front of it during its fade-in animation
            splash.Topmost = false;
            
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(0.3))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            fadeOut.Completed += (s, ev) => splash.Close();
            
            main.Show();
            main.Activate();
            main.Focus();

            splash.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private static void ActivateExistingInstance()
        {
            try
            {
                // Method 1: Find window by precise Title (highly reliable for fully loaded UI)
                IntPtr hwnd = FindWindow(null, "Patreon Archiver - Bridge Control Center");

                // Method 2: Fallback to process search if window name changed or not found yet
                if (hwnd == IntPtr.Zero)
                {
                    var current = Process.GetCurrentProcess();
                    foreach (var process in Process.GetProcessesByName(current.ProcessName))
                    {
                        if (process.Id != current.Id)
                        {
                            hwnd = process.MainWindowHandle;
                            if (hwnd != IntPtr.Zero)
                                break;
                        }
                    }
                }

                if (hwnd != IntPtr.Zero)
                {
                    // Restore window if minimized, otherwise just show it
                    if (IsIconic(hwnd))
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                    }
                    else
                    {
                        ShowWindow(hwnd, SW_SHOW);
                    }
                    SetForegroundWindow(hwnd);
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads the persisted theme preference from the registry without depending on MainWindow.
        /// Registry key: HKCU\Software\PatreonArchiverBridge, Value: ThemeDark (DWORD 0=Light, 1=Dark)
        /// </summary>
        private static bool ReadSavedThemePreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\PatreonArchiverBridge");
                if (key != null)
                {
                    object? val = key.GetValue("ThemeDark");
                    if (val != null)
                        return Convert.ToInt32(val) == 1;
                }
            }
            catch { }
            return false; // Default: Light
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Release the Mutex upon application exit
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
