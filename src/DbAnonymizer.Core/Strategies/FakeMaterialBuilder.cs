// Strategies/FakeMaterialBuilder.cs
using Npgsql;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Strategies;

public sealed class FakeMaterialBuilder : IStrategyMaterialBuilder
{
    private readonly IReadOnlyList<PoolComponents> _pools;

    public FakeMaterialBuilder(IReadOnlyList<PoolComponents> pools)
    {
        _pools = pools;
    }

    public StrategyType StrategyType => StrategyType.Fake;

    public async Task<string> BuildAndLoadAsync(
        string sandboxConnectionString,
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var createSchema = new NpgsqlCommand(
            "CREATE SCHEMA IF NOT EXISTS materials", conn);
        await createSchema.ExecuteNonQueryAsync(cancellationToken);

        foreach (var pool in _pools)
        {
            // 1. Создать таблицу-материал
            await using var dropCmd = new NpgsqlCommand(
                $"DROP TABLE IF EXISTS materials.pool_{pool.Component}", conn);
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);

            await using var createCmd = new NpgsqlCommand(
                $"CREATE TABLE materials.pool_{pool.Component} (id BIGINT PRIMARY KEY, value TEXT NOT NULL)", conn);
            await createCmd.ExecuteNonQueryAsync(cancellationToken);

            // 2. Загрузить значения через COPY (binary, быстро)
            await using var writer = await conn.BeginBinaryImportAsync(
                $"COPY materials.pool_{pool.Component} (id, value) FROM STDIN (FORMAT BINARY)",
                cancellationToken);
            for (int i = 0; i < pool.Values.Count; i++)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync((long)i, cancellationToken);
                await writer.WriteAsync(pool.Values[i], cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        // Хэш материалов = SHA256 от всех значений (для детерминизма)
        return ComputeHash();
    }

    private string ComputeHash()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                string.Join("|", _pools.SelectMany(p => p.Values))));
        return Convert.ToHexStringLower(bytes);
    }
}
