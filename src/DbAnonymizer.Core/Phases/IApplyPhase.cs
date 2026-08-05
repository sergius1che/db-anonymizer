// Phases/IApplyPhase.cs
namespace DbAnonymizer.Core.Phases;

public interface IApplyPhase
{
    Task ApplyAsync(string jobId, CancellationToken cancellationToken = default);
}
