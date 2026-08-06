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

    [Fact]
    public async Task TryFreezeAsync_RowNotYetSaved_InsertsAndSucceeds()
    {
        // TryFreezeAsync теперь один условный upsert — должен заморозить и без
        // предварительного SaveAsync(draft) (иначе для этого нужен был отдельный
        // безусловный шаг перед ним, который и открывал гонку — см. PlanFreezer)
        var repository = new PlanRepository(_connectionString);
        var frozen = NewPlan("never-saved-plan", "hash-a");

        var succeeded = await repository.TryFreezeAsync(frozen);

        Assert.True(succeeded);
        var loaded = await repository.LoadAsync(frozen.Id);
        Assert.NotNull(loaded);
        Assert.Equal(frozen.FrozenAt, loaded!.FrozenAt);
    }

    [Fact]
    public async Task TryFreezeAsync_DraftRow_Succeeds()
    {
        var repository = new PlanRepository(_connectionString);
        var draft = NewDraftPlan("draft-plan", "hash-a");
        await repository.SaveAsync(draft);
        var frozen = NewPlan("draft-plan", "hash-a");

        var succeeded = await repository.TryFreezeAsync(frozen);

        Assert.True(succeeded);
        var loaded = await repository.LoadAsync("draft-plan");
        Assert.Equal(frozen.FrozenAt, loaded!.FrozenAt);
    }

    [Fact]
    public async Task TryFreezeAsync_AlreadyFrozenRow_FailsAndLeavesRowUnchanged()
    {
        var repository = new PlanRepository(_connectionString);
        var firstFreeze = NewPlan("already-frozen-plan", "hash-a");
        await repository.TryFreezeAsync(firstFreeze);
        var secondFreeze = NewPlan("already-frozen-plan", "hash-b");

        var succeeded = await repository.TryFreezeAsync(secondFreeze);

        Assert.False(succeeded);
        var loaded = await repository.LoadAsync("already-frozen-plan");
        Assert.Equal("hash-a", loaded!.ContentHash);
        Assert.Equal(firstFreeze.FrozenAt, loaded.FrozenAt);
    }

    [Fact]
    public async Task TryFreezeAsync_ConcurrentCallsOnDraft_OnlyOneSucceeds()
    {
        // Регрессия: раньше PlanFreezer делал безусловный SaveAsync(draft) перед
        // TryFreezeAsync — окно между ними позволяло ДВУМ параллельным freeze
        // оба пройти условие "план ещё черновик". Один условный upsert закрывает
        // гонку: ровно один из двух конкурентных вызовов должен победить.
        var repository = new PlanRepository(_connectionString);
        var draft = NewDraftPlan("racing-plan", "hash-a");
        await repository.SaveAsync(draft);
        var attempt1 = NewPlan("racing-plan", "hash-attempt-1");
        var attempt2 = NewPlan("racing-plan", "hash-attempt-2");

        var results = await Task.WhenAll(
            repository.TryFreezeAsync(attempt1),
            repository.TryFreezeAsync(attempt2));

        Assert.Equal(1, results.Count(r => r));
    }

    private static Plan NewDraftPlan(string id, string contentHash, string seed = "seed-1")
    {
        var plan = NewPlan(id, contentHash, seed);
        return new Plan
        {
            Id = plan.Id,
            SourceHash = plan.SourceHash,
            Seed = plan.Seed,
            SuffixWidth = plan.SuffixWidth,
            Version = plan.Version,
            ContentHash = plan.ContentHash,
            MaterialsHash = plan.MaterialsHash,
            FrozenAt = default,
            Columns = plan.Columns,
            Pools = plan.Pools
        };
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
