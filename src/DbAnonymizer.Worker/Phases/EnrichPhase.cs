// Phases/EnrichPhase.cs
using System.Net.Http.Json;
using System.Text.Json;
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using DbAnonymizer.Core.Planning;
using DbAnonymizer.Core.Strategies;
using Hangfire;

namespace DbAnonymizer.Worker.Phases;

public sealed class EnrichPhase : IEnrichPhase
{
    // Контракт агента (schemas.py): snake_case JSON с обеих сторон
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _agentsBaseUrl;
    private readonly string _sandboxConnectionString;
    private readonly PlanRepository _plans;
    private readonly JobRepository _jobs;
    private readonly PoolMaterialsRepository _poolMaterials;
    private readonly IReadOnlyList<IStrategyMaterialBuilder> _staticBuilders;

    public EnrichPhase(
        HttpClient httpClient,
        string agentsBaseUrl,
        string sandboxConnectionString,
        PlanRepository plans,
        JobRepository jobs,
        PoolMaterialsRepository poolMaterials,
        IReadOnlyList<IStrategyMaterialBuilder> staticBuilders)
    {
        _httpClient = httpClient;
        _agentsBaseUrl = agentsBaseUrl;
        _sandboxConnectionString = sandboxConnectionString;
        _plans = plans;
        _jobs = jobs;
        _poolMaterials = poolMaterials;
        _staticBuilders = staticBuilders;
    }

    public async Task EnrichAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await LoadFrozenPlanAsync(jobId, cancellationToken);
            // Статус до работы: «воркер вызывает агента» (см. JobStatus)
            await JobStatusUpdater.SetStatusAsync(_jobs, jobId, JobStatus.Enriching, cancellationToken);

            var pools = await GeneratePoolsAsync(plan, cancellationToken);
            var builders = _staticBuilders.Append(new FakeMaterialBuilder(pools));
            var orchestrator = new MaterialOrchestrator(builders);
            var materialsHash = await orchestrator.BuildAllAsync(
                _sandboxConnectionString, plan, cancellationToken);

            await SaveMaterialsHashAsync(plan.Id, materialsHash, cancellationToken);

            BackgroundJob.Enqueue<IApplyPhase>(p => p.ApplyAsync(jobId, CancellationToken.None));
        }
        catch (Exception exception)
        {
            // Ошибка агента/HTTP/парсинга тоже падает сюда: задача → Failed, без ретраев
            await JobStatusUpdater.MarkFailedAsync(_jobs, jobId, exception, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<PoolComponents>> GeneratePoolsAsync(
        Plan plan,
        CancellationToken cancellationToken)
    {
        // plan.Pools — заморожен при фризе (PlanFreezer + PoolPlanner): состав и размер
        // пулов уже зафиксированы, здесь только материализуем значения
        var pools = new List<PoolComponents>(plan.Pools.Count);
        foreach (var pool in plan.Pools)
        {
            pools.Add(new PoolComponents
            {
                Component = pool.Component,
                Values = await GeneratePoolAsync(
                    plan.Id, pool.SemanticType, pool.Component, pool.Size, cancellationToken)
            });
        }
        return pools;
    }

    private async Task<IReadOnlyList<string>> GeneratePoolAsync(
        string planId,
        SemanticType type,
        string component,
        int requestedSize,
        CancellationToken cancellationToken)
    {
        // Кэш в pg-meta: повторный Enrich того же замороженного плана переиспользует
        // те же значения пула — иначе тот же (seed, original) выбирал бы тот же индекс,
        // но в другое слово (LLM не гарантирует одинаковый ответ на два вызова)
        var cached = await _poolMaterials.TryLoadAsync(planId, component, cancellationToken);
        if (cached is not null)
            return cached;

        var request = new AgentGenerateRequest(type.ToString(), component, requestedSize, "ru");
        using var response = await _httpClient.PostAsJsonAsync(
            $"{_agentsBaseUrl}/generate", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Agent /generate failed for component '{component}': HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadFromJsonAsync<AgentGenerateResponse>(
            JsonOptions, cancellationToken);
        if (body is null || body.Values.Count == 0)
        {
            throw new InvalidOperationException(
                $"Agent /generate returned no values for component '{component}'");
        }
        if (body.Values.Count != requestedSize)
        {
            // Модуль хэширования в fake.sql — размер пула, зафиксированный в плане.
            // Ответ другой длины делает несовпадающим modulus, использованный при
            // следующем Apply, с тем, что реально загружено в materials.pool_<component>
            throw new InvalidOperationException(
                $"Agent /generate returned {body.Values.Count} values for component '{component}', " +
                $"expected {requestedSize} (frozen pool size)");
        }

        await _poolMaterials.SaveAsync(planId, component, body.Values, cancellationToken);
        return body.Values;
    }

    private async Task<Plan> LoadFrozenPlanAsync(string jobId, CancellationToken cancellationToken)
    {
        var plan = await _plans.LoadAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Plan '{jobId}' not found");
        if (plan.FrozenAt == default)
        {
            throw new InvalidOperationException(
                $"Plan '{jobId}' is not frozen — freeze it via the API first");
        }
        return plan;
    }

    private async Task SaveMaterialsHashAsync(string planId, string materialsHash, CancellationToken cancellationToken)
    {
        // Перезагружаем план — репозиторий источник правды; MaterialsHash кладём
        // в новую копию (план иммутабелен), FrozenAt сохраняем
        var latest = await _plans.LoadAsync(planId, cancellationToken)
            ?? throw new InvalidOperationException($"Plan '{planId}' disappeared while enriching");
        var updated = new Plan
        {
            Id = latest.Id,
            SourceHash = latest.SourceHash,
            Seed = latest.Seed,
            SuffixWidth = latest.SuffixWidth,
            Version = latest.Version,
            ContentHash = latest.ContentHash,
            MaterialsHash = materialsHash,
            FrozenAt = latest.FrozenAt,
            Columns = latest.Columns,
            Pools = latest.Pools
        };
        await _plans.SaveAsync(updated, cancellationToken);
    }

    // Контракты агента — зеркало schemas.py (GenerateRequest/GenerateResponse)
    private sealed record AgentGenerateRequest(
        string SemanticType,
        string Component,
        int PoolSize,
        string Language);

    private sealed record AgentGenerateResponse(
        string Component,
        IReadOnlyList<string> Values,
        string ModelUsed,
        int TokensSpent);
}
