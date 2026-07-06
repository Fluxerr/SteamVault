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
    /// Uses pack:// URIs so it works in both debug and single-file published EXEs.
    /// </summary>
    public static void ApplyTheme(string themeName)
    {
        if (string.IsNullOrWhiteSpace(themeName) || !ThemeFiles.ContainsKey(themeName))
            themeName = "Dark";

        var app = WpfApplication.Current;
        if (app == null) return;

        try
        {
            // Build fresh resources
            var resources = new ResourceDictionary();

            // Dark.xaml always goes first (contains all UI styles like buttons, text, etc.)
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute)
            });

            // If a non-Dark theme is selected, add it as an overlay (colors + brushes are overridden)
            if (themeName != "Dark" && ThemeFiles.TryGetValue(themeName, out var themeFile))
            {
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{themeFile}", UriKind.Absolute)
                });
            }

            // Replace the entire Application.Resources — WPF propagates this to all elements
            // that use DynamicResource bindings
            app.Resources = resources;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Theme apply error: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to load the theme specified in settings. Falls back to Dark.
    /// </summary>
    public static void ApplySavedTheme(string? themeName)
    {
        ApplyTheme(themeName ?? "Dark");
    }
}