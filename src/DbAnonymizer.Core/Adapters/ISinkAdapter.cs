// Adapters/ISinkAdapter.cs
namespace DbAnonymizer.Core.Adapters;

public interface ISinkAdapter
{
    Task<string> DumpAsync(
        string sandboxConnectionString,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
