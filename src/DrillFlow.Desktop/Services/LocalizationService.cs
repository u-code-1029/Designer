using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using DrillFlow.Desktop.Models;

namespace DrillFlow.Desktop.Services;

public sealed class LocalizationService : ILocalizationService
{
    private const string ResourceMarker = "Strings.";
    private readonly IUserSettingsStore _settingsStore;
    private UserPreferences? _preferences;

    public LocalizationService(IUserSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public event EventHandler? LanguageChanged;

    public string SelectedLanguage { get; private set; } = "Auto";

    public string EffectiveLanguage { get; private set; } = "ko-KR";

    public string this[string key] => System.Windows.Application.Current.TryFindResource(key) as string ?? key;

    public void Initialize()
    {
        _preferences = _settingsStore.Load();
        ApplyLanguage(_preferences.Language, false);
    }

    public void ApplyLanguage(string language, bool persist = true)
    {
        SelectedLanguage = NormalizeSelection(language);
        EffectiveLanguage = ResolveEffectiveLanguage(SelectedLanguage);

        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.IndexOf(ResourceMarker, StringComparison.OrdinalIgnoreCase) >= 0);

        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                $"/DrillFlow.Desktop;component/Resources/Strings.{EffectiveLanguage}.xaml",
                UriKind.Relative)
        };

        if (existing is null)
        {
            dictionaries.Add(replacement);
        }
        else
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = replacement;
        }

        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(EffectiveLanguage);

        if (persist)
        {
            _preferences ??= _settingsStore.Load();
            _preferences.Language = SelectedLanguage;
            _settingsStore.Save(_preferences);
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeSelection(string? language)
    {
        if (string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        if (string.Equals(language, "ko-KR", StringComparison.OrdinalIgnoreCase))
        {
            return "ko-KR";
        }

        return "Auto";
    }

    private static string ResolveEffectiveLanguage(string selection)
    {
        if (!string.Equals(selection, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return selection;
        }

        return string.Equals(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "ko-KR";
    }
}
