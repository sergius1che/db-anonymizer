// Models/SourceSpec.cs
namespace DbAnonymizer.Core.Models;

public sealed class SourceSpec
{
    public string DumpFilePath { get; init; } = string.Empty;
    public string ConnectionString { get; init; } = string.Empty;
    public bool IsLiveDb => !string.IsNullOrEmpty(ConnectionString);
}
