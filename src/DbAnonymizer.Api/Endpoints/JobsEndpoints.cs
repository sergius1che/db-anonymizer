// Endpoints/JobsEndpoints.cs
using Microsoft.AspNetCore.Mvc;
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

        // Загрузка дампа с клиента — пользователь без доступа к файловой системе
        // сервера иначе не может передать файл (dumpFilePath у POST /jobs требует
        // путь, уже видимый Worker'у). Файл сохраняется в общий с Worker каталог
        // (см. docker-compose.yml), дальше используется как обычный dumpFilePath.
        app.MapPost("/jobs/upload", async (
            IFormFile file,
            [FromForm] string? seed,
            [FromForm] int? suffixWidth,
            DumpUploadStorage storage,
            JobService service,
            CancellationToken ct) =>
        {
            if (file.Length == 0)
                return Results.BadRequest("Uploaded file is empty");

            string dumpFilePath;
            using (var stream = file.OpenReadStream())
                dumpFilePath = await storage.SaveAsync(stream, ct);

            var job = await service.CreateJobAsync(
                new CreateJobRequest
                {
                    Source = new SourceSpec { DumpFilePath = dumpFilePath },
                    Seed = string.IsNullOrWhiteSpace(seed) ? null : seed,
                    SuffixWidth = suffixWidth
                },
                ct);
            return Results.Created($"/jobs/{job.Id}", job);
        }).DisableAntiforgery(); // API без cookie-сессий — CSRF здесь неприменим (см. «Ограничения»)

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
