using System;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using DrillFlow.Desktop.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainPageViewModel _mainPageViewModel;
    private readonly LiveInteractionPageViewModel _liveInteractionPageViewModel;
    private bool _allowClose;
    private bool _closePending;

    public MainWindow(
        MainWindowViewModel viewModel,
        MainPageViewModel mainPageViewModel,
        LiveInteractionPageViewModel liveInteractionPageViewModel,
        INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        _mainPageViewModel = mainPageViewModel;
        _liveInteractionPageViewModel = liveInteractionPageViewModel;
        DataContext = viewModel;
        RootNavigation.SetPageProviderService(pageProvider);
        RootNavigation.Navigated += (_, _) => DisableOuterPageScrolling();
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(MainPage));
            DisableOuterPageScrolling();
        };
        Closed += (_, _) => ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        Closing += OnClosing;
    }

    private void OnApplicationThemeChanged(ApplicationTheme theme, Color accent)
    {
        // Theme dictionaries can cause the NavigationView template to be recreated.
        // Run after resource/template invalidation so the new presenter also stays fixed.
        Dispatcher.BeginInvoke(
            new Action(DisableOuterPageScrolling),
            DispatcherPriority.Loaded);
    }

    private void DisableOuterPageScrolling()
    {
        // WPF-UI 4.3 keeps its NavigationViewContentPresenter DynamicScrollViewer
        // enabled even when the navigated Page sets ScrollViewer.CanContentScroll=false.
        // Every DrillFlow page owns the scrollable regions it needs, so keep the shell
        // presenter fixed and let those inner ScrollViewers handle input independently.
        if (RootNavigation.Template?.FindName(
                "PART_NavigationViewContentPresenter",
                RootNavigation) is NavigationViewContentPresenter presenter)
        {
            presenter.SetCurrentValue(
                NavigationViewContentPresenter.IsDynamicScrollViewerEnabledProperty,
                false);
        }
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
                await _liveInteractionPageViewModel.ShutdownAsync();
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
