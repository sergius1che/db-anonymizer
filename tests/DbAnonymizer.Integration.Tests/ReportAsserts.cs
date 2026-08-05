using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Integration.Tests;

internal static class ReportAsserts
{
    public static void AllChecksPassed(Report report)
    {
        var failed = report.Checks
            .Where(c => !c.Passed)
            .Select(c => $"{c.Name}: {c.Detail}")
            .ToList();
        Assert.True(
            report.AllChecksPassed,
            failed.Count == 0 ? "Report has no failed checks" : string.Join(" | ", failed));
    }
}
