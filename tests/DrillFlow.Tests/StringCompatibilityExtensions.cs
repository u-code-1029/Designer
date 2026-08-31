using System;

namespace DrillFlow.Tests;

/// <summary>
/// Supplies the comparison-aware string helper that net48 does not expose as an instance API.
/// Keeping this test-only compatibility shim lets the solution use a fixed C# 13 compiler
/// baseline instead of relying on newer extension-member lookup behavior.
/// </summary>
internal static class StringCompatibilityExtensions
{
    public static bool Contains(
        this string source,
        string value,
        StringComparison comparisonType)
    {
        return source.IndexOf(value, comparisonType) >= 0;
    }
}
