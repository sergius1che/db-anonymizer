// Reporting/ReportBuilder.cs
using System.Text.Json;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Reporting;

public sealed class ReportBuilder
{
    public async Task<string> BuildAsync(
        Report report,
        string outputDumpPath,
        CancellationToken cancellationToken = default)
    {
        // Имена членов — PascalCase, чтобы reports.data десериализовалась обратно
        // в Report (Api GET /reports/{id} и report.html читают модель); output_dump
        // остаётся snake_case как поле артефакта отчёта.
        var result = new
        {
            report.JobId,
            report.PlanId,
            report.Seed,
            report.SuffixWidth,
            report.AllChecksPassed,
            OutputDumpPath = outputDumpPath,
            output_dump = outputDumpPath,
            report.Checks,
            report.CompletedAt
        };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }
}
