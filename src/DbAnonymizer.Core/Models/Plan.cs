// Models/Plan.cs
namespace DbAnonymizer.Core.Models;

public sealed class Plan
{
    public required string Id { get; init; }            // GUID
    public required string SourceHash { get; init; }     // хэш схемы источника
    public required string Seed { get; init; }           // seed задачи
    public int SuffixWidth { get; init; } = 6;           // ширина base36-суффикса
    public required string Version { get; init; }        // "1", "2", ...
    public required string ContentHash { get; init; }    // SHA256 всего содержимого
    public required string MaterialsHash { get; init; }  // хэш загруженных материалов
    public DateTime FrozenAt { get; init; }
    public IReadOnlyList<ColumnSpec> Columns { get; init; } = Array.Empty<ColumnSpec>();
    public IReadOnlyList<PoolSpec> Pools { get; init; } = Array.Empty<PoolSpec>();
}
