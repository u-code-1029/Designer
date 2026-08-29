using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using DrillFlow.Desktop.ViewModels;

namespace DrillFlow.Desktop.Controls;

public partial class EquipmentCommunicationPanel : UserControl
{
    public static readonly DependencyProperty IsPanelExpandedProperty = DependencyProperty.Register(
        nameof(IsPanelExpanded),
        typeof(bool),
        typeof(EquipmentCommunicationPanel),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnIsPanelExpandedChanged));

    public static readonly DependencyProperty ShowValidationTabProperty = DependencyProperty.Register(
        nameof(ShowValidationTab),
        typeof(bool),
        typeof(EquipmentCommunicationPanel),
        new PropertyMetadata(false, OnRegionVisibilityChanged));

    public static readonly DependencyProperty ValidationDataContextProperty = DependencyProperty.Register(
        nameof(ValidationDataContext),
        typeof(object),
        typeof(EquipmentCommunicationPanel),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsCommunicationRegionVisibleProperty = DependencyProperty.Register(
        nameof(IsCommunicationRegionVisible),
        typeof(bool),
        typeof(EquipmentCommunicationPanel),
        new PropertyMetadata(true, OnRegionVisibilityChanged));

    public static readonly DependencyProperty IsPreviewRegionVisibleProperty = DependencyProperty.Register(
        nameof(IsPreviewRegionVisible),
        typeof(bool),
        typeof(EquipmentCommunicationPanel),
        new PropertyMetadata(true, OnRegionVisibilityChanged));

    public static readonly DependencyProperty IsValidationRegionVisibleProperty = DependencyProperty.Register(
        nameof(IsValidationRegionVisible),
        typeof(bool),
        typeof(EquipmentCommunicationPanel),
        new PropertyMetadata(true, OnRegionVisibilityChanged));

    private INotifyCollectionChanged? _entries;

    public event EventHandler? ExpandedStateChanged;

    public bool IsPanelExpanded
    {
        get => (bool)GetValue(IsPanelExpandedProperty);
        set => SetValue(IsPanelExpandedProperty, value);
    }

    public bool ShowValidationTab
    {
        get => (bool)GetValue(ShowValidationTabProperty);
        set => SetValue(ShowValidationTabProperty, value);
    }

    public object? ValidationDataContext
    {
        get => GetValue(ValidationDataContextProperty);
        set => SetValue(ValidationDataContextProperty, value);
    }

    public bool IsCommunicationRegionVisible
    {
        get => (bool)GetValue(IsCommunicationRegionVisibleProperty);
        set => SetValue(IsCommunicationRegionVisibleProperty, value);
    }

    public bool IsPreviewRegionVisible
    {
        get => (bool)GetValue(IsPreviewRegionVisibleProperty);
        set => SetValue(IsPreviewRegionVisibleProperty, value);
    }

    public bool IsValidationRegionVisible
    {
        get => (bool)GetValue(IsValidationRegionVisibleProperty);
        set => SetValue(IsValidationRegionVisibleProperty, value);
    }

    public EquipmentCommunicationPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachEntries();
        Loaded += (_, _) =>
        {
            AttachEntries(DataContext as EquipmentCommunicationMonitorViewModel);
            UpdateRegionLayout();
        };
    }

    public void ResetLayout()
    {
        IsCommunicationRegionVisible = true;
        IsValidationRegionVisible = true;
        IsPreviewRegionVisible = true;
        UpdateRegionLayout();
        IsPanelExpanded = false;
        if (TerminalList.Items.Count > 0)
        {
            TerminalList.ScrollIntoView(TerminalList.Items[TerminalList.Items.Count - 1]);
        }
    }

    private static void OnRegionVisibilityChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        ((EquipmentCommunicationPanel)dependencyObject).UpdateRegionLayout();
    }

    private void UpdateRegionLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var showCommunication = IsCommunicationRegionVisible;
        var showValidation = ShowValidationTab && IsValidationRegionVisible;
        var showPreview = IsPreviewRegionVisible;

        CommunicationRegion.Visibility = showCommunication
            ? Visibility.Visible
            : Visibility.Collapsed;
        ValidationRegion.Visibility = showValidation
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewRegion.Visibility = showPreview
            ? Visibility.Visible
            : Visibility.Collapsed;

        var visibleRegions = new List<(FrameworkElement Element, double Weight, double MinimumWidth)>();
        if (showCommunication)
        {
            visibleRegions.Add((CommunicationRegion, 2, 260));
        }

        if (showValidation)
        {
            visibleRegions.Add((ValidationRegion, 2, 260));
        }

        if (showPreview)
        {
            visibleRegions.Add((PreviewRegion, 1, 220));
        }

        var contentColumns = new[] { RegionOneColumn, RegionTwoColumn, RegionThreeColumn };
        foreach (var column in contentColumns)
        {
            column.Width = new GridLength(0);
            column.MinWidth = 0;
        }

        for (var index = 0; index < visibleRegions.Count; index++)
        {
            var region = visibleRegions[index];
            var columnIndex = index * 2;
            Grid.SetColumn(region.Element, columnIndex);
            contentColumns[index].MinWidth = region.MinimumWidth;
            contentColumns[index].Width = new GridLength(region.Weight, GridUnitType.Star);
        }

        ConfigureSplitter(
            FirstRegionSplitter,
            FirstSplitterColumn,
            visibleRegions.Count >= 2,
            1);
        ConfigureSplitter(
            SecondRegionSplitter,
            SecondSplitterColumn,
            visibleRegions.Count >= 3,
            3);
    }

    private static void ConfigureSplitter(
        GridSplitter splitter,
        ColumnDefinition column,
        bool isVisible,
        int columnIndex)
    {
        Grid.SetColumn(splitter, columnIndex);
        column.Width = new GridLength(isVisible ? 6 : 0);
        splitter.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void OnIsPanelExpandedChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var panel = (EquipmentCommunicationPanel)dependencyObject;
        panel.ExpandedStateChanged?.Invoke(panel, EventArgs.Empty);

        if ((bool)eventArgs.NewValue)
        {
            panel.ScrollToLatestEntry();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachEntries();
        AttachEntries(e.NewValue as EquipmentCommunicationMonitorViewModel);
    }

    private void AttachEntries(EquipmentCommunicationMonitorViewModel? monitor)
    {
        if (monitor?.Entries is not INotifyCollectionChanged entries || ReferenceEquals(_entries, entries))
        {
            return;
        }

        _entries = entries;
        _entries.CollectionChanged += OnEntriesChanged;
    }

    private void DetachEntries()
    {
        if (_entries is not null)
        {
            _entries.CollectionChanged -= OnEntriesChanged;
            _entries = null;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not EquipmentCommunicationMonitorViewModel monitor
            || !monitor.IsAutoScrollEnabled
            || TerminalList.Items.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            ScrollToLatestEntry();
        }));
    }

    private void ScrollToLatestEntry()
    {
        if (DataContext is EquipmentCommunicationMonitorViewModel monitor
            && monitor.IsAutoScrollEnabled
            && TerminalList.Items.Count > 0)
        {
            TerminalList.ScrollIntoView(TerminalList.Items[TerminalList.Items.Count - 1]);
        }
    }
}
