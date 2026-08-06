// Endpoints/JobsEndpoints.cs
using DbAnonymizer.Api.Services;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Api.Endpoints;

public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/jobs", async (
            CreateJobRequest request, JobService service, CancellationToken ct) =>
        {
            var job = await service.CreateJobAsync(request, ct);
            return Results.Created($"/jobs/{job.Id}", job);
        });

        app.MapGet("/jobs", async (JobService service, CancellationToken ct) =>
        {
            var jobs = await service.ListJobsAsync(ct);
            return Results.Ok(jobs);
        });

        app.MapGet("/jobs/{id}", async (
            string id, JobService service, CancellationToken ct) =>
        {
            var job = await service.GetJobAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });
    }
}
