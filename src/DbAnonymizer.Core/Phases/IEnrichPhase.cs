// Phases/IEnrichPhase.cs
namespace DbAnonymizer.Core.Phases;

public interface IEnrichPhase
{
    Task EnrichAsync(string jobId, CancellationToken cancellationToken = default);
}
