// Endpoints/PlanEndpoints.cs
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Phases;
using DbAnonymizer.Core.Planning;
using Hangfire;

namespace DbAnonymizer.Api.Endpoints;

public static class PlanEndpoints
{
    public static void MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/plans/{id}", async (
            string id, PlanRepository repo, CancellationToken ct) =>
        {
            var plan = await repo.LoadAsync(id, ct);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });

        app.MapPut("/plans/{id}", async (
            string id, List<ColumnSpec> columns, PlanRepository repo, CancellationToken ct) =>
        {
            // Валидация тела до работы с БД: пустой план упал бы 500-кой при фризе,
            // мусорная стратегия/семантический тип уехала бы в анонимизацию
            if (columns.Count == 0)
                return Results.BadRequest("Plan must contain at least one column");
            foreach (var column in columns)
            {
                if (!Enum.IsDefined(typeof(SemanticType), column.SemanticType))
                    return Results.BadRequest($"Unknown semanticType: {column.SemanticType}");
                if (!Enum.IsDefined(typeof(StrategyType), column.Strategy))
                    return Results.BadRequest($"Unknown strategy: {column.Strategy}");
            }

            // Ревьюер правит стратегии черновика; замороженный план менять запрещено.
            // Несуществующий план — тоже 409: PUT создавать планы не должен.
            var plan = await repo.LoadAsync(id, ct);
            if (plan is null || plan.FrozenAt != default)
                return Results.Conflict();
            var draft = new Plan
            {
                Id = plan.Id,
                SourceHash = plan.SourceHash,
                Seed = plan.Seed,
                SuffixWidth = plan.SuffixWidth,
                Version = plan.Version,
                ContentHash = string.Empty, // пересчитается при фризе
                MaterialsHash = plan.MaterialsHash,
                Columns = columns,
                Pools = plan.Pools
            };
            // Условный апдейт: если между LoadAsync и SaveDraftAsync лёг freeze,
            // строка уже не черновик → 0 затронутых строк → 409, план не перезаписывается
            if (!await repo.SaveDraftAsync(draft, ct))
                return Results.Conflict();
            return Results.Ok(draft);
        });

        app.MapPost("/plans/{id}/freeze", async (
            string id, PlanFreezer freezer, JobRepository jobs, CancellationToken ct) =>
        {
            var draft = await freezer.LoadDraftAsync(id, ct);
            if (draft is null) return Results.NotFound();
            // Гвард повторного фриза: иначе PlanFreezer замёрз бы уже анонимизированные данные
            // (двойной Enrich/Apply поверх анонимизированных значений)
            if (draft.FrozenAt != default) return Results.Conflict();
            var frozen = await freezer.FreezeAsync(draft, "", ct);

            // Задача → Frozen и фаза Enrich в очередь Hangfire (исполняет Worker)
            await JobStatusUpdater.SetStatusAsync(jobs, id, JobStatus.Frozen, ct);
            BackgroundJob.Enqueue<IEnrichPhase>(p => p.EnrichAsync(id, CancellationToken.None));
            return Results.Ok(frozen);
        });
    }
}
