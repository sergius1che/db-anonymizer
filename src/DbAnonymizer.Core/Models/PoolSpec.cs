// Models/PoolSpec.cs
namespace DbAnonymizer.Core.Models;

public sealed class PoolSpec
{
    public required SemanticType SemanticType { get; init; }
    public required string Component { get; init; } // "first_name", "last_name", "city"...
    public required int Size { get; init; }          // размер компонента пула
    public required string PromptVersion { get; init; }
}
