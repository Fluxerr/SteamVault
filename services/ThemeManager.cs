using System.Windows;
using WpfApplication = System.Windows.Application;

namespace SteamVault.Services;

/// <summary>
/// Manages application theme switching at runtime.
/// Themes are defined as separate XAML resource dictionaries in the Themes/ folder.
/// Only the color palette changes — all styles remain from Dark.xaml.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, string> ThemeFiles = new()
    {
        ["Gruvbox"] = "Themes/Gruvbox.xaml",
        ["Dracula"] = "Themes/Dracula.xaml",
        ["TokyoNight"] = "Themes/TokyoNight.xaml",
        ["Nord"] = "Themes/Nord.xaml",
        ["CatppuccinMocha"] = "Themes/CatppuccinMocha.xaml",
        ["CatppuccinLatte"] = "Themes/CatppuccinLatte.xaml",
        ["RosePine"] = "Themes/RosePine.xaml",
        ["Emerald"] = "Themes/Emerald.xaml",
        ["Midnight"] = "Themes/Midnight.xaml",
        ["OLED"] = "Themes/OLED.xaml",
        ["Slate"] = "Themes/Slate.xaml",
        ["Dark"] = "Themes/Dark.xaml",
        ["Metro"] = "Themes/Metro.xaml",
    };

    /// <summary>
    /// Applies the given theme by name. Falls back to "Gruvbox" if the theme is not found.
    /// Rebuilds Application.Resources from scratch — Dark.xaml is always the base (all UI styles),
    /// and additional color themes are layered on top to override color/brush resources.
    /// Uses pack:// URIs so it works in both debug and single-file published EXEs.
    /// </summary>
    public static void ApplyTheme(string themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName) || !ThemeFiles.ContainsKey(themeName))
            themeName = "Gruvbox";

        var app = WpfApplication.Current;
        if (app == null) return;

        try
        {
            // Clear existing merged dictionaries to apply the new ones
            app.Resources.MergedDictionaries.Clear();

            // Dark.xaml always goes first (contains all UI styles like buttons, text, etc.)
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute)
            });

            // If a non-Dark theme is selected, add it as an overlay (colors + brushes are overridden)
            if (ThemeFiles.TryGetValue(themeName, out var themeFile))
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{themeFile}", UriKind.Absolute)
                });
            }

            // If a custom accent is needed, it must be reapplied by the caller.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Theme apply error: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to load the theme specified in settings. Falls back to Gruvbox.
    /// </summary>
    public static void ApplySavedTheme(string? themeName)
    {
        ApplyTheme(themeName ?? "Gruvbox");
    }

    /// <summary>
    /// Applies a custom accent color on top of the current theme.
    /// </summary>
    public static void ApplyCustomAccent(string hexColor)
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
            
            var app = WpfApplication.Current;
            var isLightTheme = false;
            if (app?.Resources["WindowBackgroundColor"] is System.Windows.Media.Color windowBg)
            {
                // Calculate perceived brightness
                isLightTheme = (windowBg.R * 0.299 + windowBg.G * 0.587 + windowBg.B * 0.114) > 128;
            }

            var hoverColor = System.Windows.Media.Color.FromArgb(
                color.A, 
                (byte)Math.Min(255, color.R + 20), 
                (byte)Math.Min(255, color.G + 20), 
                (byte)Math.Min(255, color.B + 20));
            
            // For light themes, blend with white. For dark themes, blend with black.
            System.Windows.Media.Color mutedColor;
            if (isLightTheme)
            {
                mutedColor = System.Windows.Media.Color.FromArgb(
                    color.A,
                    (byte)(color.R * 0.3 + 255 * 0.7),
                    (byte)(color.G * 0.3 + 255 * 0.7),
                    (byte)(color.B * 0.3 + 255 * 0.7));
            }
            else
            {
                mutedColor = System.Windows.Media.Color.FromArgb(
                    color.A, 
                    (byte)(color.R * 0.5), 
                    (byte)(color.G * 0.5), 
                    (byte)(color.B * 0.5));
            }

            var glowColor = System.Windows.Media.Color.FromArgb(
                (byte)128, color.R, color.G, color.B);

            // Create new SolidColorBrushes
            var accentBrush = new System.Windows.Media.SolidColorBrush(color);
            var hoverBrush = new System.Windows.Media.SolidColorBrush(hoverColor);
            var mutedBrush = new System.Windows.Media.SolidColorBrush(mutedColor);
            var glowBrush = new System.Windows.Media.SolidColorBrush(glowColor);

            if (app != null)
            {
                app.Resources["PrimaryAccentColor"] = color;
                app.Resources["PrimaryAccentHoverColor"] = hoverColor;
                app.Resources["PrimaryAccentMutedColor"] = mutedColor;
                app.Resources["PrimaryAccentGlowColor"] = glowColor;

                app.Resources["PrimaryAccent"] = accentBrush;
                app.Resources["PrimaryAccentHover"] = hoverBrush;
                app.Resources["PrimaryAccentMuted"] = mutedBrush;
                app.Resources["PrimaryAccentGlow"] = glowBrush;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ApplyCustomAccent error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the key colors (Background, Surface, Accent, Text) for a given theme to show in the preview grid.
    /// If it fails to load the file without applying it, it returns fallback colors.
    /// </summary>
    public static (System.Windows.Media.Color Background, System.Windows.Media.Color Surface, System.Windows.Media.Color Accent, System.Windows.Media.Color Text) GetThemePreviewColors(string themeName)
    {
        try
        {
            if (themeName == "Dark" || !ThemeFiles.TryGetValue(themeName, out var themeFile))
                themeFile = "Themes/Dark.xaml";

            var tempDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{themeFile}", UriKind.Absolute)
            };

            var bg = tempDict["WindowBackgroundColor"] is System.Windows.Media.Color b ? b : System.Windows.Media.Colors.Black;
            var sf = tempDict["SurfaceBaseColor"] is System.Windows.Media.Color s ? s : System.Windows.Media.Colors.Gray;
            var ac = tempDict["PrimaryAccentColor"] is System.Windows.Media.Color a ? a : System.Windows.Media.Colors.Blue;
            var tx = tempDict["TextColor"] is System.Windows.Media.Color t ? t : System.Windows.Media.Colors.White;

            return (bg, sf, ac, tx);
        }
        catch
        {
            return (System.Windows.Media.Colors.Black, System.Windows.Media.Colors.DarkGray, System.Windows.Media.Colors.Blue, System.Windows.Media.Colors.White);
        }
    }
}