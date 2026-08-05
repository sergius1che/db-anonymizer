// Detection/IDetector.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public interface IDetector
{
    string Name { get; }
    Task<IReadOnlyList<ColumnCandidate>> DetectAsync(
        string sandboxConnectionString,
        CancellationToken cancellationToken = default);
}
