// Phases/IntrospectPhase.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbAnonymizer.Core.Detection;
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using DbAnonymizer.Core.Planning;
using Npgsql;

namespace DbAnonymizer.Worker.Phases;

public sealed class IntrospectPhase : IIntrospectPhase
{
    private readonly IntrospectionEngine _engine;
    private readonly PlanRepository _plans;
    private readonly JobRepository _jobs;
    private readonly string _sandboxConnectionString;

    public IntrospectPhase(
        IntrospectionEngine engine,
        PlanRepository plans,
        JobRepository jobs,
        string sandboxConnectionString)
    {
        _engine = engine;
        _plans = plans;
        _jobs = jobs;
        _sandboxConnectionString = sandboxConnectionString;
    }

    public async Task IntrospectAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var job = await _jobs.GetAsync(jobId, cancellationToken)
                ?? throw new InvalidOperationException($"Job '{jobId}' not found");
            var candidates = await _engine.RunAsync(_sandboxConnectionString, cancellationToken);
            var counts = await LoadCountsAsync(candidates, cancellationToken);

            // Черновик плана: стратегии не выбираем — это дело человека на ревью;
            // RowCount/DistinctCount считаем здесь, иначе Verify-проверки (Task 7.2)
            // на нулях черновика дадут ложные фейлы на непустых таблицах.
            // Seed/SuffixWidth — явные входы задачи (спека): если пользователь задал
            // их при создании job (POST /jobs), используем как есть; иначе подставляем
            // значения по умолчанию — фактически использованные попадут в план/отчёт.
            var draft = new Plan
            {
                Id = jobId,
                SourceHash = ComputeSourceHash(candidates),
                Seed = string.IsNullOrWhiteSpace(job.Seed) ? Guid.NewGuid().ToString("N") : job.Seed,
                SuffixWidth = job.SuffixWidth ?? 6,
                Version = "1",
                ContentHash = string.Empty,   // считается при фризе
                MaterialsHash = string.Empty, // считается при enrich
                FrozenAt = default,
                Columns = candidates
                    .Select(c => new ColumnSpec
                    {
                        SchemaName = c.SchemaName,
                        TableName = c.TableName,
                        ColumnName = c.ColumnName,
                        DataType = c.DataType,
                        IsUnique = c.IsUnique,
                        SemanticType = c.DetectedType,
                        Strategy = StrategyType.Keep, // ревью проставит реальные стратегии
                        IsJsonPath = false,
                        JsonPath = string.Empty,
                        DistinctCount = counts[CountKey(c)].DistinctCount,
                        RowCount = counts[CountKey(c)].RowCount
                    })
                    .ToList(),
                Pools = Array.Empty<PoolSpec>()
            };

            await _plans.SaveAsync(draft, cancellationToken);
            await JobStatusUpdater.SetStatusAsync(
                _jobs, jobId, JobStatus.AwaitingReview, cancellationToken, planId: jobId);
            // Стоп: человек ревьюит черновик и фризит через API
        }
        catch (Exception exception)
        {
            await JobStatusUpdater.MarkFailedAsync(_jobs, jobId, exception, cancellationToken);
        }
    }

    private static string ComputeSourceHash(IReadOnlyList<ColumnCandidate> candidates)
    {
        var json = JsonSerializer.Serialize(candidates);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static string CountKey(ColumnCandidate candidate) =>
        $"{candidate.SchemaName}.{candidate.TableName}.{candidate.ColumnName}";

    private static async Task<long> ExecuteScalarLongAsync(
        NpgsqlConnection conn,
        string sql,
        CancellationToken cancellationToken)
    {
        // count(DISTINCT) на реальном объёме sandbox может превысить дефолтные 30 c —
        // тот же прецедент, что CommandTimeout = 0 на CTAS в ApplyPhase
        await using var cmd = new NpgsqlCommand(sql, conn)
        {
            CommandTimeout = 0
        };
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private async Task<IReadOnlyDictionary<string, ColumnCounts>> LoadCountsAsync(
        IReadOnlyList<ColumnCandidate> candidates,
        CancellationToken cancellationToken)
    {
        // Одна сессия на весь проход: count(*) на таблицу + count(DISTINCT) на колонку.
        // Jsonb-путей в черновиках пока нет (IsJsonPath=false всегда) — plain SQL достаточно.
        // Таблиц в черновике немного — по одной дополнительной команде на таблицу и колонку.
        var counts = new Dictionary<string, ColumnCounts>();
        await using var conn = new NpgsqlConnection(_sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        foreach (var group in candidates.GroupBy(c => (c.SchemaName, c.TableName)))
        {
            var schema = group.Key.SchemaName;
            var table = group.Key.TableName;
            var rowCount = await ExecuteScalarLongAsync(
                conn, $"SELECT count(*) FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}", cancellationToken);
            foreach (var candidate in group)
            {
                var distinctCount = await ExecuteScalarLongAsync(
                    conn,
                    $"SELECT count(DISTINCT {QuoteIdentifier(candidate.ColumnName)}) " +
                    $"FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}",
                    cancellationToken);
                counts[CountKey(candidate)] = new ColumnCounts(rowCount, distinctCount);
            }
        }
        return counts;
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private sealed record ColumnCounts(long RowCount, long DistinctCount);
}
