// Phases/IVerifyPhase.cs
namespace DbAnonymizer.Core.Phases;

public interface IVerifyPhase
{
    Task VerifyAsync(string jobId, CancellationToken cancellationToken = default);
}
