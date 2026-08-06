// Plan/PlanFreezer.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Core.Planning;

public sealed class PlanFreezer
{
    // Версии промптов пока не различаются (Task 5.1 — единственный промпт на компонент);
    // поле в PoolSpec зарезервировано под будущую ротацию промптов агента
    private const string PromptVersion = "1";

    private readonly int _poolSize;
    private readonly PlanRepository _repository;

    public PlanFreezer(PlanRepository repository, int poolSize)
    {
        _poolSize = poolSize;
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

        // 4. Пулы фейков: размер и состав фризятся вместе с планом (не пересчитываются
        // при каждом Enrich) — иначе повторный прогон замороженного плана на реальной
        // LLM даёт другой модуль хэширования и, как следствие, другие фейковые значения
        var pools = PoolPlanner.ComputeComponents(columns)
            .Select(c => new PoolSpec
            {
                SemanticType = c.SemanticType,
                Component = c.Component,
                Size = _poolSize,
                PromptVersion = PromptVersion
            })
            .ToList();

        // 5. Создать замороженный план
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
            Pools = pools
        };

        // 6. Атомарно заморозить одним условным upsert'ом: только если план ещё
        // черновик. Раньше здесь был отдельный безусловный SaveAsync(draft) перед
        // TryFreezeAsync — окно между двумя запросами открывало гонку (безусловный
        // SaveAsync второго конкурентного вызова откатывал frozen_at первого обратно
        // в '-infinity', и оба TryFreezeAsync проходили условие, размораживая план,
        // который уже пошёл в Enrich/Apply). Гонка двух параллельных POST /freeze —
        // первый замораживает, второй получает Conflict.
        if (!await _repository.TryFreezeAsync(frozen, cancellationToken))
            throw new InvalidOperationException(
                $"Plan '{draft.Id}' was frozen by a concurrent request — refresh and retry");
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
