// Strategies/PoolMaterialsRepository.cs
using System.Text.Json;
using Npgsql;

namespace DbAnonymizer.Core.Strategies;

/// <summary>
/// Кэш сгенерированных агентом значений пула в pg-meta, keyed по (planId, component).
/// Без кэша повторный прогон Enrich одного и того же замороженного плана получал бы
/// от LLM новый набор значений того же размера — тот же (seed, original) выбирал бы
/// тот же индекс, но по нему лежало бы другое слово: «стабильность между прогонами»
/// (design intent — размер И содержимое пула фризятся с планом) не держалась бы.
/// </summary>
public sealed class PoolMaterialsRepository
{
    private readonly string _connectionString;

    public PoolMaterialsRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task SaveAsync(
        string planId,
        string component,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO pool_materials (plan_id, component, pool_values) VALUES (@plan_id, @component, @pool_values::jsonb)
              ON CONFLICT (plan_id, component) DO UPDATE SET pool_values = EXCLUDED.pool_values",
            conn);
        cmd.Parameters.AddWithValue("plan_id", planId);
        cmd.Parameters.AddWithValue("component", component);
        cmd.Parameters.AddWithValue("pool_values", JsonSerializer.Serialize(values));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>?> TryLoadAsync(
        string planId,
        string component,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT pool_values FROM pool_materials WHERE plan_id = @plan_id AND component = @component",
            conn);
        cmd.Parameters.AddWithValue("plan_id", planId);
        cmd.Parameters.AddWithValue("component", component);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var json = reader.GetString(0);
        return JsonSerializer.Deserialize<List<string>>(json);
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            @"CREATE TABLE IF NOT EXISTS pool_materials (
                plan_id TEXT NOT NULL,
                component TEXT NOT NULL,
                pool_values JSONB NOT NULL,
                PRIMARY KEY (plan_id, component))",
            conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
