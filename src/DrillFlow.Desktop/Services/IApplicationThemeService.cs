namespace DrillFlow.Desktop.Services;

public interface IApplicationThemeService
{
    string SelectedTheme { get; }

    void Initialize();

    void ApplyTheme(string selection);
}
