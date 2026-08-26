using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DrillFlow.Desktop.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DrillFlow.Desktop.Services;

public sealed class ApplicationThemeService : IApplicationThemeService, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> CustomBrushColorResources =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DrillAccentBrush"] = "AccentFillColorDefault",
            ["DrillAccentSoftBrush"] = "SubtleFillColorSecondary",
            ["DrillSurfaceBrush"] = "CardBackgroundFillColorDefault",
            ["DrillOpaqueSurfaceBrush"] = "SolidBackgroundFillColorQuarternary",
            ["DrillOpaqueSelectedSurfaceBrush"] = "SolidBackgroundFillColorTertiary",
            ["DrillCanvasBrush"] = "ApplicationBackgroundColor",
            ["DrillSubtleBrush"] = "LayerFillColorDefault",
            ["DrillBorderBrush"] = "CardStrokeColorDefault",
            ["DrillTextBrush"] = "TextFillColorPrimary",
            ["DrillSecondaryTextBrush"] = "TextFillColorSecondary",
            ["DrillSuccessBrush"] = "SystemFillColorSuccess",
            ["DrillWarningBrush"] = "SystemFillColorCaution",
            ["DrillDangerBrush"] = "SystemFillColorCritical"
        };

    private readonly IUserSettingsStore _settingsStore;
    private readonly ILogger<ApplicationThemeService> _logger;
    private bool _isInitialized;

    public ApplicationThemeService(
        IUserSettingsStore settingsStore,
        ILogger<ApplicationThemeService> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public string SelectedTheme { get; private set; } = ThemeSelection.System;

    public void Initialize()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplyTheme(_settingsStore.Load().Theme);
    }

    public void ApplyTheme(string selection)
    {
        SelectedTheme = ThemeSelection.Normalize(selection);
        RunOnUiThread(ApplySelectedTheme);
    }

    public void Dispose()
    {
        if (!_isInitialized)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _isInitialized = false;
    }

    private void ApplySelectedTheme()
    {
        try
        {
            ApplicationTheme applicationTheme;
            switch (SelectedTheme)
            {
                case ThemeSelection.Dark:
                    applicationTheme = ApplicationTheme.Dark;
                    break;
                case ThemeSelection.Light:
                    applicationTheme = ApplicationTheme.Light;
                    break;
                default:
                    // ApplySystemTheme currently selects a Mica backdrop internally. Resolve
                    // the system palette ourselves so Windows 7 always stays on the safe,
                    // solid-background path used by the FluentWindow.
                    applicationTheme = ResolveSystemApplicationTheme();
                    break;
            }

            ApplyApplicationTheme(applicationTheme);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not apply application theme {Theme}", SelectedTheme);
            if (SelectedTheme == ThemeSelection.System)
            {
                ApplyApplicationTheme(ApplicationTheme.Light);
            }
        }
    }

    private static void ApplyApplicationTheme(ApplicationTheme applicationTheme)
    {
        ApplicationThemeManager.Apply(
            applicationTheme,
            WindowBackdropType.None,
            updateAccent: true);
        ReplaceCustomBrushResources();
        RefreshOpenWindows();
    }

    private static void RefreshOpenWindows()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        // WPF-UI swaps the application theme dictionary, but controls whose templates
        // have already been materialized (including a hosted ContentDialog) can retain
        // values from the previous dictionary. Re-applying the current resources to
        // every open window refreshes those trees immediately. Popup surfaces use
        // DynamicResource references and therefore follow the same application palette.
        foreach (Window window in application.Windows)
        {
            ApplicationThemeManager.Apply(window);
            window.InvalidateVisual();
        }
    }

    private static void ReplaceCustomBrushResources()
    {
        var application = System.Windows.Application.Current;
        if (application is null)
        {
            return;
        }

        // Brushes already used by a rendered WPF template can be frozen. Mutating their
        // Color then throws and leaves the application half-switched. Publish a fresh,
        // immutable brush object for every semantic key instead; DynamicResource users
        // immediately receive the replacement and code-behind resolves the new object.
        foreach (var pair in CustomBrushColorResources)
        {
            if (application.TryFindResource(pair.Value) is Color color)
            {
                var replacement = new SolidColorBrush(color);
                replacement.Freeze();
                application.Resources[pair.Key] = replacement;
            }
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        if (SelectedTheme != ThemeSelection.System
            || (eventArgs.Category != UserPreferenceCategory.General
                && eventArgs.Category != UserPreferenceCategory.Accessibility
                && eventArgs.Category != UserPreferenceCategory.Color
                && eventArgs.Category != UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        RunOnUiThread(ApplySelectedTheme);
    }

    private static ApplicationTheme ResolveSystemApplicationTheme()
    {
        switch (ApplicationThemeManager.GetSystemTheme())
        {
            case SystemTheme.Dark:
            case SystemTheme.Glow:
            case SystemTheme.CapturedMotion:
                return ApplicationTheme.Dark;
            case SystemTheme.HCWhite:
            case SystemTheme.HCBlack:
            case SystemTheme.HC1:
            case SystemTheme.HC2:
                return ApplicationTheme.HighContrast;
            default:
                // Windows 7 and unrecognized custom themes do not expose the modern
                // app-mode registry value; Light is the predictable fallback.
                return ApplicationTheme.Light;
        }
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
