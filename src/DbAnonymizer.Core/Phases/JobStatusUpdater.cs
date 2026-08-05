// Phases/JobStatusUpdater.cs
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Phases;

/// <summary>
/// Единый паттерн «загрузить задачу / сменить статус» для всех фаз-статус-машин
/// и endpoint'а фриза. JobState иммутабелен — переход всегда строит новую копию,
/// сохраняя Id/PlanId/CreatedAt/Source и незатронутые поля.
/// </summary>
public static class JobStatusUpdater
{
    private const int MaxErrorLength = 2000;

    private static async Task<JobState> LoadOrThrowAsync(
        JobRepository jobs,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        return job ?? throw new InvalidOperationException($"Job '{jobId}' not found");
    }

    public static async Task MarkCompletedAsync(
        JobRepository jobs,
        string jobId,
        CancellationToken cancellationToken) =>
        await SaveAsync(jobs, jobId, JobStatus.Complete, cancellationToken, null, DateTime.UtcNow);

    public static async Task MarkFailedAsync(
        JobRepository jobs,
        string jobId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Best-effort: если задачи уже нет — нечего помечать, не бросаем
        var job = await jobs.GetAsync(jobId, cancellationToken);
        if (job is null)
            return;

        var message = exception.Message;
        if (message.Length > MaxErrorLength)
            message = message[..MaxErrorLength];
        await SaveAsync(jobs, job, JobStatus.Failed, cancellationToken, message, null);
    }

    public static async Task SetStatusAsync(
        JobRepository jobs,
        string jobId,
        JobStatus status,
        CancellationToken cancellationToken,
        string? planId = null) =>
        await SaveAsync(jobs, jobId, status, cancellationToken, null, null, planId);

    private static async Task SaveAsync(
        JobRepository jobs,
        string jobId,
        JobStatus status,
        CancellationToken cancellationToken,
        string? error,
        DateTime? completedAt,
        string? planId = null)
    {
        var job = await LoadOrThrowAsync(jobs, jobId, cancellationToken);
        await SaveAsync(jobs, job, status, cancellationToken, error, completedAt, planId);
    }

    private static async Task SaveAsync(
        JobRepository jobs,
        JobState job,
        JobStatus status,
        CancellationToken cancellationToken,
        string? error,
        DateTime? completedAt,
        string? planId = null)
    {
        var updated = new JobState
        {
            Id = job.Id,
            Status = status,
            PlanId = planId ?? job.PlanId,
            Error = error ?? job.Error,
            CreatedAt = job.CreatedAt,
            CompletedAt = completedAt ?? job.CompletedAt,
            Source = job.Source
        };
        await jobs.SaveAsync(updated, cancellationToken);
    }
}
