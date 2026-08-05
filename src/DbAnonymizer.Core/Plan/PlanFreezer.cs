// Plan/PlanFreezer.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Planning;

public sealed class PlanFreezer
{
    private readonly PlanRepository _repository;

    public PlanFreezer(PlanRepository repository)
    {
        _repository = repository;
    }

    public async Task<Plan> FreezeAsync(
        Plan draft,
        string materialsHash,
        CancellationToken cancellationToken = default)
    {
        // 1. Валидация
        var errors = PlanValidator.Validate(draft);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Plan validation failed: {string.Join("; ", errors)}");

        // 2. Json-колонки без явной стратегии → Redact (ruling Task 1.1)
        var columns = draft.Columns
            .Select(c => (c.IsJsonPath || c.SemanticType == SemanticType.Json)
                    && c.Strategy == StrategyType.Keep
                ? RedactJsonColumn(c)
                : c)
            .ToList();

        // 3. Вычислить ContentHash
        var json = JsonSerializer.Serialize(columns);
        var contentHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        // 4. Создать замороженный план
        var frozen = new Plan
        {
            Id = draft.Id,
            SourceHash = draft.SourceHash,
            Seed = draft.Seed,
            SuffixWidth = draft.SuffixWidth,
            Version = draft.Version,
            ContentHash = contentHash,
            MaterialsHash = materialsHash,
            FrozenAt = DateTime.UtcNow,
            Columns = columns,
            Pools = draft.Pools
        };

        // 5. Сохранить в pg-meta
        await _repository.SaveAsync(frozen, cancellationToken);
        return frozen;
    }

    public async Task<Plan?> LoadDraftAsync(
        string planId,
        CancellationToken cancellationToken = default) =>
        await _repository.LoadAsync(planId, cancellationToken);

    private static ColumnSpec RedactJsonColumn(ColumnSpec c) => new()
    {
        SchemaName = c.SchemaName,
        TableName = c.TableName,
        ColumnName = c.ColumnName,
        DataType = c.DataType,
        IsUnique = c.IsUnique,
        SemanticType = c.SemanticType,
        Strategy = StrategyType.Redact,
        IsJsonPath = c.IsJsonPath,
        JsonPath = c.JsonPath,
        DistinctCount = c.DistinctCount,
        RowCount = c.RowCount
    };
}
