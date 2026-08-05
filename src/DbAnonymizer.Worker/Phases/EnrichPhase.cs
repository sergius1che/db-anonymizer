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
    private readonly int _poolSize;
    private readonly string _sandboxConnectionString;
    private readonly PlanRepository _plans;
    private readonly JobRepository _jobs;
    private readonly IReadOnlyList<IStrategyMaterialBuilder> _staticBuilders;

    public EnrichPhase(
        HttpClient httpClient,
        string agentsBaseUrl,
        int poolSize,
        string sandboxConnectionString,
        PlanRepository plans,
        JobRepository jobs,
        IReadOnlyList<IStrategyMaterialBuilder> staticBuilders)
    {
        _httpClient = httpClient;
        _agentsBaseUrl = agentsBaseUrl;
        _poolSize = poolSize;
        _sandboxConnectionString = sandboxConnectionString;
        _plans = plans;
        _jobs = jobs;
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
        // Конвенция ключей пулов (Task 4.3/7.1): SemanticType.ToString().ToLowerInvariant(),
        // например firstname/cardnumber — их адресует fake.sql через materials.pool_<component>
        var byComponent = plan.Columns
            .Where(c => c.Strategy == StrategyType.Fake)
            .Select(c => (Type: c.SemanticType, Component: c.SemanticType.ToString().ToLowerInvariant()))
            .DistinctBy(x => x.Component)
            .ToList();

        var pools = new List<PoolComponents>(byComponent.Count);
        foreach (var (type, component) in byComponent)
        {
            pools.Add(new PoolComponents
            {
                Component = component,
                Values = await GeneratePoolAsync(type, component, cancellationToken)
            });
        }
        return pools;
    }

    private async Task<IReadOnlyList<string>> GeneratePoolAsync(
        SemanticType type,
        string component,
        CancellationToken cancellationToken)
    {
        var request = new AgentGenerateRequest(type.ToString(), component, _poolSize, "ru");
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
