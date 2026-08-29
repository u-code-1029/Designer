using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    private readonly MainPage _mainPage;
    private readonly LiveInteractionPage _liveInteractionPage;
    private bool _allowClose;
    private bool _closePending;
    private bool _refreshingLayoutButtons;

    public MainWindow(
        MainWindowViewModel viewModel,
        MainPageViewModel mainPageViewModel,
        LiveInteractionPageViewModel liveInteractionPageViewModel,
        MainPage mainPage,
        LiveInteractionPage liveInteractionPage,
        INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        _mainPageViewModel = mainPageViewModel;
        _liveInteractionPageViewModel = liveInteractionPageViewModel;
        _mainPage = mainPage;
        _liveInteractionPage = liveInteractionPage;
        DataContext = viewModel;
        RootNavigation.SetPageProviderService(pageProvider);
        RootNavigation.Navigated += (_, _) =>
        {
            DisableOuterPageScrolling();
            // NavigationView raises Navigated before SelectedItem is guaranteed to expose
            // the destination item. Refresh once layout/selection propagation has completed
            // so page-specific controls (notably validation) never retain the prior page state.
            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    DisableOuterPageScrolling();
                    RefreshLayoutButtonState();
                }),
                DispatcherPriority.Loaded);
        };
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(MainPage));
            DisableOuterPageScrolling();
            RefreshLayoutButtonState();
        };
        Closed += (_, _) => ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        Closing += OnClosing;
    }

    private IEquipmentPanelLayoutHost? ActiveEquipmentPanelLayoutHost
    {
        get
        {
            if (RootNavigation.SelectedItem is not NavigationViewItem selectedItem)
            {
                return null;
            }

            if (selectedItem.TargetPageType == typeof(MainPage))
            {
                return _mainPage;
            }

            return selectedItem.TargetPageType == typeof(LiveInteractionPage)
                ? _liveInteractionPage
                : null;
        }
    }

    private void LayoutToggleButtons_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // The panel itself also has a collapse control. Refresh here so a layout
        // change made inside either page is reflected before the next title-bar click.
        RefreshLayoutButtonState();
    }

    private void CommunicationRegionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingLayoutButtons || ActiveEquipmentPanelLayoutHost is not { } host)
        {
            return;
        }

        var showRegion = !host.IsEquipmentPanelExpanded || !host.IsCommunicationRegionVisible;
        if (showRegion && !host.IsEquipmentPanelExpanded)
        {
            // A direct title-bar toggle represents exactly one region. If the whole
            // panel was collapsed, reopen it with only the requested region visible.
            host.IsCommunicationRegionVisible = true;
            host.IsValidationRegionVisible = false;
            host.IsPreviewRegionVisible = false;
        }
        else
        {
            host.IsCommunicationRegionVisible = showRegion;
        }

        CompleteRegionVisibilityChange(host, showRegion);
    }

    private void ValidationRegionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingLayoutButtons
            || ActiveEquipmentPanelLayoutHost is not { SupportsValidationRegion: true } host)
        {
            return;
        }

        var showRegion = !host.IsEquipmentPanelExpanded || !host.IsValidationRegionVisible;
        if (showRegion && !host.IsEquipmentPanelExpanded)
        {
            host.IsCommunicationRegionVisible = false;
            host.IsValidationRegionVisible = true;
            host.IsPreviewRegionVisible = false;
        }
        else
        {
            host.IsValidationRegionVisible = showRegion;
        }

        CompleteRegionVisibilityChange(host, showRegion);
    }

    private void PreviewRegionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingLayoutButtons || ActiveEquipmentPanelLayoutHost is not { } host)
        {
            return;
        }

        var showRegion = !host.IsEquipmentPanelExpanded || !host.IsPreviewRegionVisible;
        if (showRegion && !host.IsEquipmentPanelExpanded)
        {
            host.IsCommunicationRegionVisible = false;
            host.IsValidationRegionVisible = false;
            host.IsPreviewRegionVisible = true;
        }
        else
        {
            host.IsPreviewRegionVisible = showRegion;
        }

        CompleteRegionVisibilityChange(host, showRegion);
    }

    private void CompleteRegionVisibilityChange(
        IEquipmentPanelLayoutHost host,
        bool regionIsVisible)
    {
        if (regionIsVisible)
        {
            host.IsEquipmentPanelExpanded = true;
        }
        else if (!host.IsCommunicationRegionVisible
                 && (!host.SupportsValidationRegion || !host.IsValidationRegionVisible)
                 && !host.IsPreviewRegionVisible)
        {
            host.IsEquipmentPanelExpanded = false;
        }

        RefreshLayoutButtonState();
    }

    private void RefreshLayoutButtonState()
    {
        var host = ActiveEquipmentPanelLayoutHost;
        LayoutToggleButtons.Visibility = host is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (host is null)
        {
            return;
        }

        _refreshingLayoutButtons = true;
        try
        {
            var communicationActive = host.IsEquipmentPanelExpanded
                                      && host.IsCommunicationRegionVisible;
            var validationActive = host.IsEquipmentPanelExpanded
                                   && host.SupportsValidationRegion
                                   && host.IsValidationRegionVisible;
            var previewActive = host.IsEquipmentPanelExpanded
                                && host.IsPreviewRegionVisible;

            ValidationRegionToggle.Visibility = host.SupportsValidationRegion
                ? Visibility.Visible
                : Visibility.Collapsed;
            SetLayoutButtonState(CommunicationRegionToggle, communicationActive);
            SetLayoutButtonState(ValidationRegionToggle, validationActive);
            SetLayoutButtonState(PreviewRegionToggle, previewActive);
        }
        finally
        {
            _refreshingLayoutButtons = false;
        }
    }

    private static void SetLayoutButtonState(Wpf.Ui.Controls.Button button, bool isActive)
    {
        button.Appearance = isActive
            ? ControlAppearance.Primary
            : ControlAppearance.Secondary;
        System.Windows.Automation.AutomationProperties.SetItemStatus(
            button,
            isActive ? "On" : "Off");
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
                // PrepareForCloseAsync/ShutdownAsync can both complete synchronously. Calling
                // Close() from inside the original Closing event then violates WPF's closing
                // guard. Defer the approved close until this event has returned; the next event
                // observes _allowClose and proceeds without another prompt or shutdown pass.
                _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
            }
        }
        finally
        {
            _closePending = false;
        }
    }
}
