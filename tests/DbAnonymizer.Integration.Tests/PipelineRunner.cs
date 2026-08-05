using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Planning;
using DbAnonymizer.Core.Strategies;
using DbAnonymizer.Worker.Phases;

namespace DbAnonymizer.Integration.Tests;

internal sealed class PipelineRunner
{
    private const string MaterialsHashPlaceholder = "pending-materials";
    private const string SourceHash = "demo-schema-2026-08-05";
    private const int SuffixWidth = 6;

    private static readonly IReadOnlyList<ColumnMark> DemoMarks = BuildDemoMarks();

    private readonly TestcontainersFixture _fixture;

    public PipelineRunner(TestcontainersFixture fixture)
    {
        _fixture = fixture;
    }

    // Полный конвейер реальными классами: draft → validate → freeze (pg-meta) →
    // enrich (пре-заготовленные пулы, без LLM) → apply → verify.
    public async Task<PipelineResult> RunAsync(
        string seed,
        IReadOnlyList<ColumnSpec> columns,
        CancellationToken cancellationToken = default)
    {
        var draft = new Plan
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceHash = SourceHash,
            Seed = seed,
            SuffixWidth = SuffixWidth,
            Version = "1",
            ContentHash = string.Empty,
            MaterialsHash = MaterialsHashPlaceholder,
            Columns = columns,
            Pools = TestPools.ToPoolSpecs()
        };

        var errors = PlanValidator.Validate(draft);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Draft plan invalid: {string.Join("; ", errors)}");

        var repository = new PlanRepository(_fixture.MetaConnectionString);
        var freezer = new PlanFreezer(repository);
        var frozen = await freezer.FreezeAsync(draft, MaterialsHashPlaceholder, cancellationToken);

        // Материалы: статические билдеры, порядок как в Worker (влияет на MaterialsHash)
        var orchestrator = new MaterialOrchestrator(CreateBuilders());
        var materialsHash = await orchestrator.BuildAllAsync(
            _fixture.SandboxConnectionString, frozen, cancellationToken);

        var final = new Plan
        {
            Id = frozen.Id,
            SourceHash = frozen.SourceHash,
            Seed = frozen.Seed,
            SuffixWidth = frozen.SuffixWidth,
            Version = frozen.Version,
            ContentHash = frozen.ContentHash,
            MaterialsHash = materialsHash,
            FrozenAt = frozen.FrozenAt,
            Columns = frozen.Columns,
            Pools = frozen.Pools
        };
        await repository.SaveAsync(final, cancellationToken);

        await new ApplyPhase(_fixture.SandboxConnectionString).ApplyAsync(final, cancellationToken);
        var report = await new VerifyPhase(_fixture.SandboxConnectionString).VerifyAsync(
            final, cancellationToken);
        return new PipelineResult(final, report);
    }

    // Стандартный план демо-схемы (12 размеченных колонок, RowCount/DistinctCount —
    // реальные значения из sandbox) + опциональные доп. колонки (jsonb и т.п.)
    public async Task<PipelineResult> RunDemoPipelineAsync(
        string seed,
        IReadOnlyList<ColumnSpec>? extraColumns = null,
        CancellationToken cancellationToken = default)
    {
        var columns = new List<ColumnSpec>(await BuildDemoColumnsAsync(cancellationToken));
        if (extraColumns is not null)
            columns.AddRange(extraColumns);
        return await RunAsync(seed, columns, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnSpec>> BuildDemoColumnsAsync(
        CancellationToken cancellationToken = default)
    {
        var specs = new List<ColumnSpec>(DemoMarks.Count);
        foreach (var table in DemoMarks.Select(m => m.Table).Distinct().OrderBy(t => t))
        {
            var marks = DemoMarks.Where(m => m.Table == table).ToList();
            var (rowCount, distinctCounts) = await _fixture.GetTableStatsAsync(
                table, marks.Select(m => m.Column).ToList(), cancellationToken);
            for (int i = 0; i < marks.Count; i++)
            {
                var mark = marks[i];
                specs.Add(new ColumnSpec
                {
                    SchemaName = "public",
                    TableName = mark.Table,
                    ColumnName = mark.Column,
                    IsUnique = mark.IsUnique,
                    SemanticType = mark.Type,
                    Strategy = mark.Strategy,
                    RowCount = rowCount,
                    DistinctCount = distinctCounts[i]
                });
            }
        }
        return specs;
    }

    public static IReadOnlyList<(string Table, string Column)> GetDemoMarkedColumns() =>
        DemoMarks.Select(m => (m.Table, m.Column)).ToList();

    private static IReadOnlyList<IStrategyMaterialBuilder> CreateBuilders() =>
        new IStrategyMaterialBuilder[]
        {
            new RedactMaterialBuilder(Path.Combine(AppContext.BaseDirectory, "story.txt")),
            new FormatMaterialBuilder(),
            new ShiftMaterialBuilder(),
            new FakeMaterialBuilder(TestPools.Create())
        };

    private static IReadOnlyList<ColumnMark> BuildDemoMarks() =>
    [
        new ColumnMark("users", "first_name", SemanticType.FirstName, StrategyType.Fake),
        new ColumnMark("users", "last_name", SemanticType.LastName, StrategyType.Fake),
        new ColumnMark("users", "middle_name", SemanticType.MiddleName, StrategyType.Fake),
        new ColumnMark("users", "email", SemanticType.Email, StrategyType.Derive, IsUnique: true),
        new ColumnMark("users", "phone", SemanticType.Phone, StrategyType.Format),
        new ColumnMark("users", "password_hash", SemanticType.PasswordHash, StrategyType.Secret),
        new ColumnMark("users", "birth_date", SemanticType.BirthDate, StrategyType.Shift),
        new ColumnMark("profiles", "address", SemanticType.Address, StrategyType.Redact),
        new ColumnMark("profiles", "passport", SemanticType.PassportNumber, StrategyType.Redact),
        new ColumnMark("profiles", "inn", SemanticType.Inn, StrategyType.Format),
        new ColumnMark("orders", "user_email", SemanticType.Email, StrategyType.Derive),
        new ColumnMark("orders", "card_number", SemanticType.CardNumber, StrategyType.Format),
    ];

    private sealed record ColumnMark(
        string Table,
        string Column,
        SemanticType Type,
        StrategyType Strategy,
        bool IsUnique = false);
}

internal sealed record PipelineResult(Plan Plan, Report Report);
