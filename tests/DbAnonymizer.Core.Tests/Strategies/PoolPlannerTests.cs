// Strategies/PoolPlannerTests.cs
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Core.Tests.Strategies;

public sealed class PoolPlannerTests
{
    [Fact]
    public void ComputeComponents_FakeColumns_OneComponentPerDistinctSemanticType()
    {
        var columns = new[]
        {
            NewColumn("first_name", SemanticType.FirstName, StrategyType.Fake),
            NewColumn("last_name", SemanticType.LastName, StrategyType.Fake),
            NewColumn("nickname", SemanticType.FirstName, StrategyType.Fake), // дубль типа
        };

        var components = PoolPlanner.ComputeComponents(columns);

        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c.Component == "firstname");
        Assert.Contains(components, c => c.Component == "lastname");
    }

    [Fact]
    public void ComputeComponents_NoFakeOrDeriveColumns_ReturnsEmpty()
    {
        var columns = new[]
        {
            NewColumn("comment", SemanticType.Unknown, StrategyType.Keep),
            NewColumn("card_number", SemanticType.CardNumber, StrategyType.Format),
        };

        var components = PoolPlanner.ComputeComponents(columns);

        Assert.Empty(components);
    }

    [Fact]
    public void ComputeComponents_DeriveColumnWithoutExplicitFakeNames_AddsFirstAndLastName()
    {
        var columns = new[]
        {
            NewColumn("email", SemanticType.Email, StrategyType.Derive),
        };

        var components = PoolPlanner.ComputeComponents(columns);

        Assert.Equal(2, components.Count);
        Assert.Contains(components, c => c.Component == "firstname" && c.SemanticType == SemanticType.FirstName);
        Assert.Contains(components, c => c.Component == "lastname" && c.SemanticType == SemanticType.LastName);
    }

    [Fact]
    public void ComputeComponents_DeriveAndExplicitFakeNames_DoesNotDuplicateComponents()
    {
        var columns = new[]
        {
            NewColumn("first_name", SemanticType.FirstName, StrategyType.Fake),
            NewColumn("last_name", SemanticType.LastName, StrategyType.Fake),
            NewColumn("email", SemanticType.Email, StrategyType.Derive),
        };

        var components = PoolPlanner.ComputeComponents(columns);

        Assert.Equal(2, components.Count);
    }

    private static ColumnSpec NewColumn(
        string columnName, SemanticType semanticType, StrategyType strategy) => new()
    {
        SchemaName = "public",
        TableName = "users",
        ColumnName = columnName,
        SemanticType = semanticType,
        Strategy = strategy
    };
}
