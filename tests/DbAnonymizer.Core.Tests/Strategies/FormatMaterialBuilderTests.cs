using Npgsql;
using Testcontainers.PostgreSql;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Core.Tests.Strategies;

public sealed class FormatMaterialBuilderTests : IAsyncLifetime
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
    public async Task BuildAndLoad_ShouldLoadFormatTemplates()
    {
        var builder = new FormatMaterialBuilder();

        var hash = await builder.BuildAndLoadAsync(_connectionString, TestPlan);
        // Second run: DROP + CREATE make the builder idempotent (must not fail on rerun)
        await builder.BuildAndLoadAsync(_connectionString, TestPlan);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT type, template FROM materials.format_templates ORDER BY type", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Type, string Template)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(
        [
            ("cardnumber", @"\d{16}"),
            ("inn", @"\d{10}|\d{12}"),
            ("phone", @"\d{10}"),
        ], rows);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
