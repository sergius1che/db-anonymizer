using Npgsql;

namespace DbAnonymizer.Integration.Tests;

[Collection("Integration")]
public sealed class ReproducibilityTests
{
    private const string Seed = "repro-seed-42";

    private readonly TestcontainersFixture _fixture;
    private readonly PipelineRunner _runner;

    public ReproducibilityTests(TestcontainersFixture fixture)
    {
        _fixture = fixture;
        _runner = new PipelineRunner(fixture);
    }

    // Два полных прогона с одним seed на ДВУХ свежих sandbox (truncate + re-seed между
    // прогонами — НЕ double-apply: fake детерминирован от seed+значение, повторный
    // apply по анонимизированным данным нестабилен). Хэши всех размеченных колонок
    // должны совпасть.
    [Fact]
    public async Task TwoRuns_SameSeed_FreshSandboxes_ProduceIdenticalColumnHashes()
    {
        await _fixture.ResetSandboxAsync();
        var first = await _runner.RunDemoPipelineAsync(Seed);
        ReportAsserts.AllChecksPassed(first.Report);
        var firstHashes = await ComputeHashesAsync();

        await _fixture.ResetSandboxAsync();
        var second = await _runner.RunDemoPipelineAsync(Seed);
        ReportAsserts.AllChecksPassed(second.Report);
        var secondHashes = await ComputeHashesAsync();

        Assert.Equal(firstHashes.Keys.OrderBy(k => k), secondHashes.Keys.OrderBy(k => k));
        foreach (var key in firstHashes.Keys)
            Assert.Equal(firstHashes[key], secondHashes[key]);
    }

    private async Task<IReadOnlyDictionary<string, string>> ComputeHashesAsync(
        CancellationToken cancellationToken = default)
    {
        var hashes = new Dictionary<string, string>();
        await using var conn = new NpgsqlConnection(_fixture.SandboxConnectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var (table, column) in PipelineRunner.GetDemoMarkedColumns())
        {
            var sql = $"""
                SELECT COALESCE(md5(string_agg("{column}"::TEXT, '|' ORDER BY id)), '')
                FROM "public"."{table}"
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            var value = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
            hashes[$"{table}.{column}"] = value ?? string.Empty;
        }
        return hashes;
    }
}
