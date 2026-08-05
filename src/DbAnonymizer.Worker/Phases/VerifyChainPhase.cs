// Phases/VerifyChainPhase.cs
using DbAnonymizer.Core.Adapters;
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using DbAnonymizer.Core.Planning;
using DbAnonymizer.Core.Reporting;

namespace DbAnonymizer.Worker.Phases;

/// <summary>
/// Обёртка-статус-машина над чистой фазой VerifyPhase (Task 7.2):
/// статус Verifying → проверки → снятие дампа из песочницы (sink, Task 9.1) →
/// отчёт с output_dump в pg-meta → Complete + CompletedAt.
/// Любой сбой → Failed + Error, без ретраев Hangfire.
/// </summary>
public sealed class VerifyChainPhase : IVerifyPhase
{
    private readonly JobRepository _jobs;
    private readonly string _outputDirectory;
    private readonly PlanRepository _plans;
    private readonly ReportRepository _reports;
    private readonly ReportBuilder _reportBuilder;
    private readonly string _sandboxConnectionString;
    private readonly ISinkAdapter _sink;
    private readonly VerifyPhase _verifyPhase;

    public VerifyChainPhase(
        JobRepository jobs,
        PlanRepository plans,
        ReportRepository reports,
        VerifyPhase verifyPhase,
        ISinkAdapter sink,
        ReportBuilder reportBuilder,
        string sandboxConnectionString,
        string outputDirectory)
    {
        _jobs = jobs;
        _plans = plans;
        _reports = reports;
        _verifyPhase = verifyPhase;
        _sink = sink;
        _reportBuilder = reportBuilder;
        _sandboxConnectionString = sandboxConnectionString;
        _outputDirectory = outputDirectory;
    }

    public async Task VerifyAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            await JobStatusUpdater.SetStatusAsync(_jobs, jobId, JobStatus.Verifying, cancellationToken);
            var plan = await LoadPlanAsync(jobId, cancellationToken);
            var report = await _verifyPhase.VerifyAsync(plan, cancellationToken);
            var dumpPath = await _sink.DumpAsync(
                _sandboxConnectionString, _outputDirectory, cancellationToken);
            var reportJson = await _reportBuilder.BuildAsync(report, dumpPath, cancellationToken);
            await _reports.SaveAsync(report, reportJson, cancellationToken);
            await JobStatusUpdater.MarkCompletedAsync(_jobs, jobId, cancellationToken);
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
