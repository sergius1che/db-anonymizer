// Plan/PlanRepository.cs
using System.Text.Json;
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Planning;

public sealed class PlanRepository
{
    private readonly string _connectionString;

    public PlanRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task SaveAsync(Plan plan, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        // Таблица: plans(id TEXT PK, data JSONB, frozen_at TIMESTAMPTZ)
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO plans (id, data, frozen_at) VALUES (@id, @data::jsonb, @frozen_at)
              ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, frozen_at = EXCLUDED.frozen_at",
            conn);
        cmd.Parameters.AddWithValue("id", plan.Id);
        cmd.Parameters.AddWithValue("data",
            JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = false }));
        cmd.Parameters.AddWithValue("frozen_at", plan.FrozenAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SaveDraftAsync(
        Plan draft,
        CancellationToken cancellationToken = default)
    {
        // Условный апдейт черновика: строка перезаписывается только пока план не заморожен.
        // Иначе гонка PUT (ревью) vs freeze переписала бы замороженный план обратно
        // в черновик, а Enrich/Apply уже в очереди — двойная обработка анонимизированных данных.
        // Маркер черновика — '-infinity'::timestamptz: именно это значение Npgsql 10 пишет
        // в frozen_at для черновика (DateTime.MinValue маппится в -infinity), и именно его
        // читает LoadAsync как default. Значение '0001-01-01T00:00:00+00' в БД у черновиков
        // не встречается, а text-параметр в сравнении с timestamptz неявно не кастится (42883).
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        await using var cmd = new NpgsqlCommand(
            @"UPDATE plans SET data = @data::jsonb
              WHERE id = @id AND frozen_at = '-infinity'::timestamptz",
            conn);
        cmd.Parameters.AddWithValue("id", draft.Id);
        cmd.Parameters.AddWithValue("data",
            JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = false }));
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<Plan?> LoadAsync(string planId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT data FROM plans WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", planId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var dataOrdinal = reader.GetOrdinal("data");
        var json = reader.GetString(dataOrdinal);
        return JsonSerializer.Deserialize<Plan>(json);
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            @"CREATE TABLE IF NOT EXISTS plans (
                id TEXT PRIMARY KEY,
                data JSONB NOT NULL,
                frozen_at TIMESTAMPTZ NOT NULL)", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
