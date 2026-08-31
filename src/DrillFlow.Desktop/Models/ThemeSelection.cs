using System;

namespace DrillFlow.Desktop.Models;

public static class ThemeSelection
{
    public const string System = "System";

    public const string Light = "Light";

    public const string Dark = "Dark";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(value, Dark, StringComparison.OrdinalIgnoreCase))
        {
            return Dark;
        }

        return System;
    }
}
