using System;

namespace DrillFlow.Desktop.Services;

public interface ILocalizationService
{
    event EventHandler? LanguageChanged;

    string SelectedLanguage { get; }

    string EffectiveLanguage { get; }

    string this[string key] { get; }

    void Initialize();

    void ApplyLanguage(string language, bool persist = true);
}
