using System;
using System.ComponentModel;
using DrillFlow.Desktop.ViewModels;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainPageViewModel _mainPageViewModel;
    private bool _allowClose;
    private bool _closePending;

    public MainWindow(
        MainWindowViewModel viewModel,
        MainPageViewModel mainPageViewModel,
        INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        _mainPageViewModel = mainPageViewModel;
        DataContext = viewModel;
        RootNavigation.SetPageProviderService(pageProvider);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(MainPage));
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closePending)
        {
            return;
        }

        _closePending = true;
        try
        {
            if (await _mainPageViewModel.PrepareForCloseAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _closePending = false;
        }
    }
}
