using System;
using System.Linq;
using System.Windows;

namespace PatreonArchiverBridge.UI.Shared
{
    public static class ThemeManager
    {
        public static bool IsDarkTheme { get; private set; }

        public static void SetTheme(bool isDark)
        {
            IsDarkTheme = isDark;
            var app = Application.Current;
            if (app == null) return;

            var mergedDicts = app.Resources.MergedDictionaries;
            
            // Find existing theme dictionary
            var existingTheme = mergedDicts.FirstOrDefault(d => 
                d.Source != null && 
                (d.Source.OriginalString.Contains("LightTheme.xaml") || 
                 d.Source.OriginalString.Contains("DarkTheme.xaml")));

            if (existingTheme != null)
            {
                mergedDicts.Remove(existingTheme);
            }

            // Load and insert the new theme dictionary
            var newThemeName = isDark ? "DarkTheme.xaml" : "LightTheme.xaml";
            var newTheme = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/PatreonArchiverBridge.UI.Shared;component/{newThemeName}", UriKind.Absolute)
            };

            mergedDicts.Insert(0, newTheme);
        }
    }
}
