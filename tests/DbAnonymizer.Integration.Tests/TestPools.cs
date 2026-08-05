using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Integration.Tests;

// Пре-заготовленные пулы (без LLM): значения фиксированы → MaterialsHash и
// результаты fake-стратегии детерминированы между прогонами.
internal static class TestPools
{
    public static IReadOnlyList<PoolComponents> Create() =>
    [
        BuildPool("firstname", 500, i => $"FakeFirst{i:0000}"),
        BuildPool("lastname", 500, i => $"FakeLast{i:0000}"),
        BuildPool("middlename", 500, i => $"FakeMiddle{i:0000}"),
        BuildPool("phone", 500, i => $"+7-9{i % 100:00}-{i % 1000:000}-{i % 10000:0000}"),
    ];

    public static IReadOnlyList<PoolSpec> ToPoolSpecs() =>
        Create()
            .Select(p => new PoolSpec
            {
                SemanticType = ToSemanticType(p.Component),
                Component = p.Component,
                Size = p.Size,
                PromptVersion = "test-static"
            })
            .ToList();

    private static PoolComponents BuildPool(string component, int size, Func<int, string> valueFactory)
    {
        var values = new string[size];
        for (int i = 0; i < size; i++)
            values[i] = valueFactory(i);
        return new PoolComponents { Component = component, Values = values };
    }

    private static SemanticType ToSemanticType(string component) => component switch
    {
        "firstname" => SemanticType.FirstName,
        "lastname" => SemanticType.LastName,
        "middlename" => SemanticType.MiddleName,
        "phone" => SemanticType.Phone,
        _ => SemanticType.Unknown
    };
}
