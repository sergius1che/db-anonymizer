// Endpoints/ReportEndpoints.cs
using DbAnonymizer.Core.Reporting;

namespace DbAnonymizer.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/reports/{id}", async (
            string id, ReportRepository repo, CancellationToken ct) =>
        {
            var report = await repo.LoadAsync(id, ct);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });
    }
}
