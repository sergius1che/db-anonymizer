// Strategies/PoolComponents.cs
namespace DbAnonymizer.Core.Strategies;

public sealed class PoolComponents
{
    public required string Component { get; init; }
    public required IReadOnlyList<string> Values { get; init; }

    public int Size => Values.Count;
}
