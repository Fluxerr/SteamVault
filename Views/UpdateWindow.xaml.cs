using System;
using System.Windows;
using System.Windows.Input;
using SteamVault.ViewModels;

namespace SteamVault.Views;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        MouseDown += (s, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        };
    }
}

