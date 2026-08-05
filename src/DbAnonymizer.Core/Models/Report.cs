// Models/Report.cs
namespace DbAnonymizer.Core.Models;

public sealed class Report
{
    public required string JobId { get; init; }
    public required string PlanId { get; init; }
    public required string Seed { get; init; }
    public int SuffixWidth { get; init; }
    public bool AllChecksPassed { get; init; }
    public IReadOnlyList<ReportCheck> Checks { get; init; } = Array.Empty<ReportCheck>();
    public string OutputDumpPath { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; }
}

public sealed class ReportCheck
{
    public required string Name { get; init; }
    public bool Passed { get; init; }
    public string Detail { get; init; } = string.Empty;
}
