// Services/JobService.cs
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using Hangfire;

namespace DbAnonymizer.Api.Services;

public sealed class JobService
{
    // Live-DB источник несёт пароль в connection string (SourceSpec.ConnectionString) —
    // pg-meta.jobs.data хранит его как есть (Worker должен подключаться реальными
    // учётными данными), но наружу (POST/GET /jobs) отдаём план и пул фейков, а не пароль
    private const string RedactedConnectionString = "***REDACTED***";

    private readonly JobRepository _repository;

    public JobService(JobRepository repository)
    {
        _repository = repository;
    }

    public async Task<JobState> CreateJobAsync(
        CreateJobRequest request, CancellationToken cancellationToken = default)
    {
        var job = new JobState
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow,
            Source = request.Source,
            Seed = request.Seed,
            SuffixWidth = request.SuffixWidth
        };

        await _repository.SaveAsync(job, cancellationToken);

        // Поставить фазу Ingest в очередь Hangfire
        BackgroundJob.Enqueue<IIngestPhase>(p =>
            p.IngestAsync(job.Id, request.Source, CancellationToken.None));

        return Redact(job);
    }

    public async Task<IReadOnlyList<JobState>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await _repository.ListAsync(cancellationToken);
        return jobs.Select(Redact).ToList();
    }

    public async Task<JobState?> GetJobAsync(string id, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetAsync(id, cancellationToken);
        return job is null ? null : Redact(job);
    }

    private static JobState Redact(JobState job)
    {
        if (job.Source is null || string.IsNullOrEmpty(job.Source.ConnectionString))
            return job;

        return new JobState
        {
            Id = job.Id,
            Status = job.Status,
            PlanId = job.PlanId,
            Error = job.Error,
            CreatedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
            Source = new SourceSpec
            {
                DumpFilePath = job.Source.DumpFilePath,
                ConnectionString = RedactedConnectionString
            },
            Seed = job.Seed,
            SuffixWidth = job.SuffixWidth
        };
    }
}
