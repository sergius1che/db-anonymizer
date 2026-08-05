// Phases/IIntrospectPhase.cs
namespace DbAnonymizer.Core.Phases;

public interface IIntrospectPhase
{
    Task IntrospectAsync(string jobId, CancellationToken cancellationToken = default);
}
