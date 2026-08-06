// Models/CreateJobRequest.cs
namespace DbAnonymizer.Core.Models;

/// <summary>
/// Тело POST /jobs. Seed/SuffixWidth — явные входы задачи (по спеке), опциональные:
/// если не заданы, Introspect подставит значения по умолчанию.
/// </summary>
public sealed class CreateJobRequest
{
    public required SourceSpec Source { get; init; }
    public string? Seed { get; init; }
    public int? SuffixWidth { get; init; }
}
