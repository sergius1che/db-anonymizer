using Npgsql;
using Testcontainers.PostgreSql;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Core.Tests.Strategies;

public sealed class FakeMaterialBuilderTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private PostgreSqlContainer _container = null!;

    private static readonly Plan TestPlan = new()
    {
        Id = "test-plan",
        SourceHash = "abc",
        Seed = "seed",
        Version = "1",
        ContentHash = "abc",
        MaterialsHash = "abc",
    };

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    [Fact]
    public async Task BuildAndLoad_ShouldCreatePoolTables()
    {
        var pools = new List<PoolComponents>
        {
            new() { Component = "first_name", Values = ["Алексей", "Мария", "Дмитрий"] }
        };
        var builder = new FakeMaterialBuilder(pools);
        var hash = await builder.BuildAndLoadAsync(_connectionString, TestPlan);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM materials.pool_first_name", conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(3, count);
        Assert.Equal(64, hash.Length);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
