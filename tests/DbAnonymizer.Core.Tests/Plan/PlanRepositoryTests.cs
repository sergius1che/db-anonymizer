// Plan/PlanRepositoryTests.cs
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Planning;
using Testcontainers.PostgreSql;

namespace DbAnonymizer.Core.Tests.Planning;

public sealed class PlanRepositoryTests : IAsyncLifetime
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
    public async Task Save_Then_Load_Roundtrip()
    {
        var repository = new PlanRepository(_connectionString);
        var plan = NewPlan("roundtrip-plan", "hash-a");

        await repository.SaveAsync(plan);

        var loaded = await repository.LoadAsync(plan.Id);
        Assert.NotNull(loaded);
        Assert.Equal(plan.Id, loaded!.Id);
        Assert.Equal(plan.SourceHash, loaded.SourceHash);
        Assert.Equal(plan.Seed, loaded.Seed);
        Assert.Equal(plan.SuffixWidth, loaded.SuffixWidth);
        Assert.Equal(plan.Version, loaded.Version);
        Assert.Equal(plan.ContentHash, loaded.ContentHash);
        Assert.Equal(plan.MaterialsHash, loaded.MaterialsHash);
        Assert.Equal(plan.FrozenAt, loaded.FrozenAt);

        var column = Assert.Single(loaded.Columns);
        Assert.Equal("public", column.SchemaName);
        Assert.Equal("users", column.TableName);
        Assert.Equal("email", column.ColumnName);
        Assert.Equal("varchar", column.DataType);
        Assert.True(column.IsUnique);
        Assert.Equal(SemanticType.Email, column.SemanticType);
        Assert.Equal(StrategyType.Fake, column.Strategy);
        Assert.False(column.IsJsonPath);
        Assert.Equal(string.Empty, column.JsonPath);
        Assert.Equal(100, column.DistinctCount);
        Assert.Equal(1000, column.RowCount);

        var pool = Assert.Single(loaded.Pools);
        Assert.Equal(SemanticType.FirstName, pool.SemanticType);
        Assert.Equal("first_name", pool.Component);
        Assert.Equal(500, pool.Size);
        Assert.Equal("v1", pool.PromptVersion);
    }

    [Fact]
    public async Task Save_Upsert_Overwrites()
    {
        var repository = new PlanRepository(_connectionString);
        var first = NewPlan("upsert-plan", "hash-a");
        var second = NewPlan("upsert-plan", "hash-b", "seed-2");

        await repository.SaveAsync(first);
        await repository.SaveAsync(second);

        var loaded = await repository.LoadAsync("upsert-plan");
        Assert.NotNull(loaded);
        Assert.Equal("hash-b", loaded!.ContentHash);
        Assert.Equal("seed-2", loaded.Seed);
    }

    private static Plan NewPlan(string id, string contentHash, string seed = "seed-1") => new()
    {
        Id = id,
        SourceHash = "src-hash",
        Seed = seed,
        SuffixWidth = 6,
        Version = "1",
        ContentHash = contentHash,
        MaterialsHash = "mat-hash",
        FrozenAt = DateTime.UtcNow,
        Columns =
        [
            new ColumnSpec
            {
                SchemaName = "public",
                TableName = "users",
                ColumnName = "email",
                DataType = "varchar",
                IsUnique = true,
                SemanticType = SemanticType.Email,
                Strategy = StrategyType.Fake,
                DistinctCount = 100,
                RowCount = 1000
            }
        ],
        Pools =
        [
            new PoolSpec
            {
                SemanticType = SemanticType.FirstName,
                Component = "first_name",
                Size = 500,
                PromptVersion = "v1"
            }
        ]
    };

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
