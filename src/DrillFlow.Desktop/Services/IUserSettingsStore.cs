using DrillFlow.Desktop.Models;

namespace DrillFlow.Desktop.Services;

public interface IUserSettingsStore
{
    UserPreferences Load();

    void Save(UserPreferences preferences);
}
