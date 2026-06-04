using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SteamVault.ViewModels;

namespace SteamVault;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            oldVm.PropertyChanged -= MainViewModel_PropertyChanged;

        if (e.NewValue is InstallationViewModel)
        {
            // Hide sidebar and accent during installation, content fills full width
            SidebarPanel.Visibility = Visibility.Collapsed;
        }
        else if (e.NewValue is MainViewModel newVm)
        {
            // Restore sidebar for main app
            SidebarPanel.Visibility = Visibility.Visible;
            newVm.PropertyChanged += MainViewModel_PropertyChanged;
            AnimateGameCount(newVm.GameCount);
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;

        if (e.PropertyName == nameof(MainViewModel.CurrentView) && IsLoaded)
        {
            var fadeOut = new DoubleAnimation(0.85, TimeSpan.FromSeconds(0.06));
            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromSeconds(0.15))
            {
                BeginTime = TimeSpan.FromSeconds(0.06),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            MainContentHost.BeginAnimation(OpacityProperty, fadeOut);
            MainContentHost.BeginAnimation(OpacityProperty, fadeIn);
        }
        else if (e.PropertyName == nameof(MainViewModel.GameCount))
        {
            AnimateGameCount(vm.GameCount);
        }
    }

    private void AnimateGameCount(string countText)
    {
        if (GameCountText == null || string.IsNullOrEmpty(countText)) return;

        GameCountText.Text = countText;

        var scaleTransform = new ScaleTransform(1, 1);
        GameCountText.RenderTransformOrigin = new System.Windows.Point(0, 0.5);
        GameCountText.RenderTransform = scaleTransform;

        var scaleUp = new DoubleAnimation(1.2, TimeSpan.FromSeconds(0.12))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleDown = new DoubleAnimation(1, TimeSpan.FromSeconds(0.25))
        {
            BeginTime = TimeSpan.FromSeconds(0.12),
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 }
        };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);

        scaleUp.Completed += (_, _) =>
        {
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
        };
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