using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Integration.Tests;

// Пре-заготовленные пулы (без LLM): значения фиксированы → MaterialsHash и
// результаты fake-стратегии детерминированы между прогонами.
internal static class TestPools
{
    public const int PoolSize = 500;

    public static IReadOnlyList<PoolComponents> Create() =>
    [
        BuildPool("firstname", i => $"FakeFirst{i:0000}"),
        BuildPool("lastname", i => $"FakeLast{i:0000}"),
        BuildPool("middlename", i => $"FakeMiddle{i:0000}"),
        BuildPool("phone", i => $"+7-9{i % 100:00}-{i % 1000:000}-{i % 10000:0000}"),
    ];

    private static PoolComponents BuildPool(string component, Func<int, string> valueFactory)
    {
        var values = new string[PoolSize];
        for (int i = 0; i < PoolSize; i++)
            values[i] = valueFactory(i);
        return new PoolComponents { Component = component, Values = values };
    }
}
