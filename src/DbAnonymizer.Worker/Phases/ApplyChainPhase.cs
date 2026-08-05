// Phases/ApplyChainPhase.cs
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using DbAnonymizer.Core.Planning;
using Hangfire;

namespace DbAnonymizer.Worker.Phases;

/// <summary>
/// Обёртка-статус-машина над чистой фазой ApplyPhase (Task 7.1):
/// статус Applying → вызов ApplyPhase → постановка IVerifyPhase в очередь.
/// Любой сбой → Failed + Error, без ретраев Hangfire.
/// </summary>
public sealed class ApplyChainPhase : IApplyPhase
{
    private readonly JobRepository _jobs;
    private readonly PlanRepository _plans;
    private readonly ApplyPhase _applyPhase;

    public ApplyChainPhase(JobRepository jobs, PlanRepository plans, ApplyPhase applyPhase)
    {
        _jobs = jobs;
        _plans = plans;
        _applyPhase = applyPhase;
    }

    public async Task ApplyAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            await JobStatusUpdater.SetStatusAsync(_jobs, jobId, JobStatus.Applying, cancellationToken);
            var plan = await LoadPlanAsync(jobId, cancellationToken);
            await _applyPhase.ApplyAsync(plan, cancellationToken);
            BackgroundJob.Enqueue<IVerifyPhase>(p => p.VerifyAsync(jobId, CancellationToken.None));
        }
        catch (Exception exception)
        {
            await JobStatusUpdater.MarkFailedAsync(_jobs, jobId, exception, cancellationToken);
        }
    }

    private async Task<Plan> LoadPlanAsync(string jobId, CancellationToken cancellationToken)
    {
        return await _plans.LoadAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Plan '{jobId}' not found");
    }
}
