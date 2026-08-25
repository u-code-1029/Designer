using System;
using System.IO;

namespace DrillFlow.Application.Persistence;

public sealed class CorrelationIdStoreOptions
{
    public const string SectionName = "CorrelationIdStore";

    public string StateFilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DrillFlow Designer",
        "state",
        "correlation-id.txt");
}

