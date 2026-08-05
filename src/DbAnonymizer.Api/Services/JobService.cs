// Services/JobService.cs
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using Hangfire;

namespace DbAnonymizer.Api.Services;

public sealed class JobService
{
    private readonly JobRepository _repository;

    public JobService(JobRepository repository)
    {
        _repository = repository;
    }

    public async Task<JobState> CreateJobAsync(
        SourceSpec source, CancellationToken cancellationToken = default)
    {
        var job = new JobState
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow,
            Source = source
        };

        await _repository.SaveAsync(job, cancellationToken);

        // Поставить фазу Ingest в очередь Hangfire
        BackgroundJob.Enqueue<IIngestPhase>(p =>
            p.IngestAsync(job.Id, source, CancellationToken.None));

        return job;
    }

    public async Task<IReadOnlyList<JobState>> ListJobsAsync(CancellationToken cancellationToken = default) =>
        await _repository.ListAsync(cancellationToken);

    public async Task<JobState?> GetJobAsync(string id, CancellationToken cancellationToken = default) =>
        await _repository.GetAsync(id, cancellationToken);
}
