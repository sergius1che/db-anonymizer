// Plan/PlanFreezerTests.cs
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Planning;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DbAnonymizer.Core.Tests.Planning;

public sealed class PlanFreezerTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    [Fact]
    public void Validate_SeedRequired_ReturnsError()
    {
        var plan = new Plan
        {
            Id = "p1",
            SourceHash = "abc",
            Seed = "",
            Version = "1",
            ContentHash = "abc",
            MaterialsHash = "abc",
            Columns =
            [
                new ColumnSpec
                {
                    SchemaName = "public", TableName = "users", ColumnName = "email"
                }
            ]
        };

        var errors = PlanValidator.Validate(plan);

        Assert.Contains(errors, e => e.Contains("Seed is required"));
    }

    [Fact]
    public void Validate_DetectedColumnWithoutStrategy_ReturnsError()
    {
        var plan = new Plan
        {
            Id = "p1",
            SourceHash = "abc",
            Seed = "seed",
            Version = "1",
            ContentHash = "abc",
            MaterialsHash = "abc",
            Columns =
            [
                new ColumnSpec
                {
                    SchemaName = "public",
                    TableName = "users",
                    ColumnName = "email",
                    SemanticType = SemanticType.Email
                }
            ]
        };

        var errors = PlanValidator.Validate(plan);

        Assert.Contains(errors, e => e.Contains("no explicit strategy"));
    }

    [Fact]
    public void Validate_JsonColumnWithoutStrategy_IsAccepted()
    {
        var plan = new Plan
        {
            Id = "p1",
            SourceHash = "abc",
            Seed = "seed",
            Version = "1",
            ContentHash = "abc",
            MaterialsHash = "abc",
            Columns =
            [
                new ColumnSpec
                {
                    SchemaName = "public",
                    TableName = "customers",
                    ColumnName = "data",
                    SemanticType = SemanticType.Json
                }
            ]
        };

        var errors = PlanValidator.Validate(plan);

        Assert.DoesNotContain(errors, e => e.Contains("no explicit strategy"));
    }

    [Fact]
    public void Validate_NonSensitiveUniqueColumn_NoSuffixWidthError()
    {
        var plan = new Plan
        {
            Id = "p1",
            SourceHash = "abc",
            Seed = "seed",
            Version = "1",
            ContentHash = "abc",
            MaterialsHash = "abc",
            SuffixWidth = 0,
            Columns =
            [
                new ColumnSpec
                {
                    SchemaName = "public",
                    TableName = "orders",
                    ColumnName = "order_number",
                    SemanticType = SemanticType.Unknown,
                    IsUnique = true
                }
            ]
        };

        var errors = PlanValidator.Validate(plan);

        Assert.DoesNotContain(errors, e => e.Contains("SuffixWidth"));
        Assert.DoesNotContain(errors, e => e.Contains("no explicit strategy"));
    }

    [Fact]
    public async Task Freeze_JsonColumnWithoutStrategy_BecomesRedact()
    {
        var plan = new Plan
        {
            Id = "freeze-test",
            SourceHash = "abc",
            Seed = "seed-42",
            Version = "1",
            ContentHash = "not-yet-set",
            MaterialsHash = "materials-hash",
            Columns =
            [
                new ColumnSpec
                {
                    SchemaName = "public",
                    TableName = "customers",
                    ColumnName = "data",
                    SemanticType = SemanticType.Json,
                    Strategy = StrategyType.Keep
                }
            ]
        };

        var repository = new PlanRepository(_connectionString);
        var freezer = new PlanFreezer(repository);
        var frozen = await freezer.FreezeAsync(plan, "materials-hash");

        Assert.Equal(StrategyType.Redact, frozen.Columns[0].Strategy);
        Assert.Matches("^[0-9a-f]{64}$", frozen.ContentHash);
        Assert.NotEqual(default, frozen.FrozenAt);

        // Roundtrip: план сохранён в pg-meta и читается обратно
        var loaded = await repository.LoadAsync(frozen.Id);
        Assert.NotNull(loaded);
        Assert.Equal(frozen.ContentHash, loaded!.ContentHash);
        Assert.Equal(StrategyType.Redact, loaded.Columns[0].Strategy);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
