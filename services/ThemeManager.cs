using System.IO;
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
        ["Dark"] = "Themes/Dark.xaml",
        ["AmoledBlack"] = "Themes/AmoledBlack.xaml",
        ["MidnightBlue"] = "Themes/MidnightBlue.xaml",
        ["SlateGray"] = "Themes/SlateGray.xaml",
        ["EmeraldNight"] = "Themes/EmeraldNight.xaml",
    };

    /// <summary>
    /// Applies the given theme by name. Falls back to "Dark" if the theme is not found.
    /// Rebuilds Application.Resources from scratch — Dark.xaml is always the base (all UI styles),
    /// and additional color themes are layered on top to override color/brush resources.
    /// </summary>
    public static void ApplyTheme(string themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName) || !ThemeFiles.ContainsKey(themeName))
            themeName = "Dark";

        var app = WpfApplication.Current;
        if (app == null) return;

        var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        var darkPath = Path.Combine(basePath, "Dark.xaml");

        if (!File.Exists(darkPath)) return;

        // Build fresh resources
        var resources = new ResourceDictionary();

        // Dark.xaml always goes first (contains all UI styles like buttons, text, etc.)
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(darkPath, UriKind.Absolute)
        });

        // If a non-Dark theme is selected, add it as an overlay (only colors/brushes are overridden)
        if (themeName != "Dark" && ThemeFiles.TryGetValue(themeName, out var themeFile))
        {
            var overlayPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, themeFile);
            if (File.Exists(overlayPath))
            {
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(overlayPath, UriKind.Absolute)
                });
            }
        }

        // Replace the entire Application.Resources — WPF propagates this to all elements
        app.Resources = resources;
    }

    /// <summary>
    /// Attempts to load the theme specified in settings. Falls back to Dark.
    /// </summary>
    public static void ApplySavedTheme(string? themeName)
    {
        ApplyTheme(themeName ?? "Dark");
    }
}