// Strategies/PoolPlanner.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Strategies;

/// <summary>
/// Единое место, где считается, какие пулы фейков нужны плану — используется и при
/// фризе (PlanFreezer заполняет Plan.Pools), и при генерации материалов (EnrichPhase
/// читает уже замороженный Plan.Pools). Раньше эта логика жила только в EnrichPhase
/// и не совпадала с тем, что реально фризилось — размер пула не был частью плана.
/// </summary>
public static class PoolPlanner
{
    public static IReadOnlyList<(SemanticType SemanticType, string Component)> ComputeComponents(
        IReadOnlyList<ColumnSpec> columns)
    {
        var components = columns
            .Where(c => c.Strategy == StrategyType.Fake)
            .Select(c => (c.SemanticType, Component: c.SemanticType.ToString().ToLowerInvariant()))
            .DistinctBy(c => c.Component)
            .ToList();

        // Derive-стратегия неявно зависит от Fake-пулов firstname/lastname (derive.sql
        // склеивает фейковое имя) — нужны, даже если в плане нет явных Fake(FirstName)/
        // Fake(LastName) колонок
        if (columns.Any(c => c.Strategy == StrategyType.Derive))
        {
            AddIfMissing(components, SemanticType.FirstName);
            AddIfMissing(components, SemanticType.LastName);
        }

        return components;
    }

    private static void AddIfMissing(
        List<(SemanticType SemanticType, string Component)> components,
        SemanticType semanticType)
    {
        var component = semanticType.ToString().ToLowerInvariant();
        if (!components.Any(c => c.Component == component))
            components.Add((semanticType, component));
    }
}
