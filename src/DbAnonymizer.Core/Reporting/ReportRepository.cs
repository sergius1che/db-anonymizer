// Reporting/ReportRepository.cs
using System.Text.Json;
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Reporting;

public sealed class ReportRepository
{
    private readonly string _connectionString;

    public ReportRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task SaveAsync(
        Report report,
        string reportJson,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        // Таблица: reports(id TEXT PK, data JSONB, completed_at TIMESTAMPTZ); id = report.JobId.
        // reportJson — отчёт из ReportBuilder (с output_dump), сериализуется обратно в Report
        // при LoadAsync, поэтому должен нести все required-поля модели.
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO reports (id, data, completed_at) VALUES (@id, @data::jsonb, @completed_at)
              ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, completed_at = EXCLUDED.completed_at",
            conn);
        cmd.Parameters.AddWithValue("id", report.JobId);
        cmd.Parameters.AddWithValue("data", reportJson);
        cmd.Parameters.AddWithValue("completed_at", report.CompletedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Report?> LoadAsync(string reportId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT data FROM reports WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", reportId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var dataOrdinal = reader.GetOrdinal("data");
        var json = reader.GetString(dataOrdinal);
        return JsonSerializer.Deserialize<Report>(json);
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            @"CREATE TABLE IF NOT EXISTS reports (
                id TEXT PRIMARY KEY,
                data JSONB NOT NULL,
                completed_at TIMESTAMPTZ NOT NULL)", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
