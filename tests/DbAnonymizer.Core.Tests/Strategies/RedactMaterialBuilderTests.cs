using Npgsql;
using Testcontainers.PostgreSql;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Core.Tests.Strategies;

public sealed class RedactMaterialBuilderTests : IAsyncLifetime
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
    public async Task BuildAndLoad_ShouldLoadStoryAndWordIndex()
    {
        var storyPath = Path.Combine(AppContext.BaseDirectory, "story.txt");
        var builder = new RedactMaterialBuilder(storyPath);

        var hash = await builder.BuildAndLoadAsync(_connectionString, TestPlan);
        // Second run: DROP + CREATE make the builder idempotent (must not fail on rerun)
        await builder.BuildAndLoadAsync(_connectionString, TestPlan);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var wordsCmd = new NpgsqlCommand(
            "SELECT count(*) FROM materials.story_words", conn);
        var wordCount = (long)(await wordsCmd.ExecuteScalarAsync())!;
        Assert.True(wordCount > 200);

        await using var storyCmd = new NpgsqlCommand(
            "SELECT length(content) FROM materials.story", conn);
        var storyLength = Convert.ToInt64(await storyCmd.ExecuteScalarAsync());
        Assert.True(storyLength >= 10000);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
