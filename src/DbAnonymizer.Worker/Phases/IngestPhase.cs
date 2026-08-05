// Phases/IngestPhase.cs
using DbAnonymizer.Core.Adapters;
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using Hangfire;

namespace DbAnonymizer.Worker.Phases;

public sealed class IngestPhase : IIngestPhase
{
    private readonly IReadOnlyList<ISourceAdapter> _adapters;
    private readonly string _sandboxConnectionString;
    private readonly JobRepository _jobs;

    public IngestPhase(
        IReadOnlyList<ISourceAdapter> adapters,
        string sandboxConnectionString,
        JobRepository jobs)
    {
        _adapters = adapters;
        _sandboxConnectionString = sandboxConnectionString;
        _jobs = jobs;
    }

    public async Task IngestAsync(string jobId, SourceSpec source, CancellationToken cancellationToken = default)
    {
        try
        {
            await JobStatusUpdater.SetStatusAsync(_jobs, jobId, JobStatus.Ingesting, cancellationToken);
            var adapter = SelectAdapter(source);
            await adapter.RestoreAsync(source, _sandboxConnectionString, cancellationToken);
            BackgroundJob.Enqueue<IIntrospectPhase>(p => p.IntrospectAsync(jobId, CancellationToken.None));
        }
        catch (Exception exception)
        {
            // Не бросаем — Hangfire иначе ушёл бы в ретраи
            await JobStatusUpdater.MarkFailedAsync(_jobs, jobId, exception, cancellationToken);
        }
    }

    private ISourceAdapter SelectAdapter(SourceSpec source)
    {
        // Task 9.1: выбор по типу источника — живая БД или файл-дамп
        ISourceAdapter? adapter = source.IsLiveDb
            ? _adapters.FirstOrDefault(a => a is LiveDbSourceAdapter)
            : _adapters.FirstOrDefault(a => a is PgDumpSourceAdapter);

        if (adapter is null)
        {
            var kind = source.IsLiveDb ? "live database" : "dump file";
            throw new InvalidOperationException(
                $"No ISourceAdapter registered for {kind} source");
        }

        return adapter;
    }
}
