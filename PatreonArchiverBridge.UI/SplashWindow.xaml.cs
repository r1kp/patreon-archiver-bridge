using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PatreonArchiverBridge.UI
{
    public partial class SplashWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private const int GWL_STYLE = -16;
        private const int WS_SYSMENU = 0x80000;
        private const int WS_CAPTION = 0xC00000;

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2; // Round corners

        public SplashWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var helper = new WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;

                // Enable DWM animations and native glassy rendering by adding Caption style
                int style = GetWindowLong(hwnd, GWL_STYLE);
                style |= WS_CAPTION;
                style &= ~WS_SYSMENU;
                SetWindowLong(hwnd, GWL_STYLE, style);

                // Round corners via DWM
                int cornerPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                // Apply Acrylic glass backdrop
                bool isDark = ReadSavedThemePreference();
                ApplyDwmBackdrop(hwnd, isDark);
            }
            catch { }
        }

        private void ApplyDwmBackdrop(IntPtr hwnd, bool isDark)
        {
            // 1. Tell DWM whether we're dark or light (affects backdrop tinting)
            int useDarkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));

            // 2. Extend DWM frame into the entire client area so the backdrop covers everything
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // 3. Request Acrylic backdrop (value 3 = DWMSBT_TRANSIENTWINDOW)
            int backdropType = 3;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
        }

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
            return false;
        }
    }
}
