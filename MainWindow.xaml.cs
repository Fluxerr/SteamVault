using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SteamVault.ViewModels;

namespace SteamVault;

public partial class MainWindow : Window
{
    // Windows 11 native rounded corners via DWM
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyRoundedCorners();
        DataContextChanged += OnDataContextChanged;
    }

    private void ApplyRoundedCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var cornerPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            oldVm.PropertyChanged -= MainViewModel_PropertyChanged;

        if (e.NewValue is InstallationViewModel)
        {
            // Installation is a full-width focused flow.
            SidebarPanel.Visibility = Visibility.Collapsed;
            DiscordButton.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
        }
        else if (e.NewValue is MainViewModel newVm)
        {
            // Restore the main glass shell.
            SidebarPanel.Visibility = Visibility.Visible;
            DiscordButton.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(250);
            newVm.PropertyChanged += MainViewModel_PropertyChanged;
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;

        if (e.PropertyName == nameof(MainViewModel.CurrentView) && IsLoaded)
        {
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromSeconds(0.08));
            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromSeconds(0.15))
            {
                BeginTime = TimeSpan.FromSeconds(0.08),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            MainContentHost.BeginAnimation(OpacityProperty, fadeOut);
            MainContentHost.BeginAnimation(OpacityProperty, fadeIn);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Discord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://discord.gg/kxpRNzqnsX",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open Discord: {ex.Message}");
        }
    }
}
