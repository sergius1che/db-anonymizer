// Phases/IIngestPhase.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Phases;

public interface IIngestPhase
{
    Task IngestAsync(string jobId, SourceSpec source, CancellationToken cancellationToken = default);
}
