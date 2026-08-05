// Adapters/ISourceAdapter.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Adapters;

public interface ISourceAdapter
{
    Task RestoreAsync(
        SourceSpec source,
        string sandboxConnectionString,
        CancellationToken cancellationToken = default);
}
