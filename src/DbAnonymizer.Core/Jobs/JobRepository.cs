// Jobs/JobRepository.cs
using System.Text.Json;
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Jobs;

public sealed class JobRepository
{
    private readonly string _connectionString;

    public JobRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task SaveAsync(JobState job, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        // Таблица: jobs(id TEXT PK, data JSONB, created_at TIMESTAMPTZ)
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO jobs (id, data, created_at) VALUES (@id, @data::jsonb, @created_at)
              ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, created_at = EXCLUDED.created_at",
            conn);
        cmd.Parameters.AddWithValue("id", job.Id);
        cmd.Parameters.AddWithValue("data",
            JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = false }));
        cmd.Parameters.AddWithValue("created_at", job.CreatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JobState>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT data FROM jobs ORDER BY created_at DESC", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var jobs = new List<JobState>();
        var dataOrdinal = reader.GetOrdinal("data");
        while (await reader.ReadAsync(cancellationToken))
        {
            var json = reader.GetString(dataOrdinal);
            var job = JsonSerializer.Deserialize<JobState>(json);
            if (job is not null)
                jobs.Add(job);
        }
        return jobs;
    }

    public async Task<JobState?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await EnsureTableAsync(conn, cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT data FROM jobs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var dataOrdinal = reader.GetOrdinal("data");
        var json = reader.GetString(dataOrdinal);
        return JsonSerializer.Deserialize<JobState>(json);
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            @"CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                data JSONB NOT NULL,
                created_at TIMESTAMPTZ NOT NULL)", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
