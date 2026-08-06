// Program.cs
using Hangfire;
using Hangfire.PostgreSql;
using DbAnonymizer.Core.Adapters;
using DbAnonymizer.Core.Detection;
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Phases;
using DbAnonymizer.Core.Planning;
using DbAnonymizer.Core.Reporting;
using DbAnonymizer.Core.Strategies;
using DbAnonymizer.Worker.Phases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    services.AddHangfire(config => config
        .UsePostgreSqlStorage(options =>
            options.UseNpgsqlConnection(
                context.Configuration.GetConnectionString("PgMeta"))));
    services.AddHangfireServer(options => options.WorkerCount = 1);

    // Репозитории pg-meta
    services.AddSingleton(new JobRepository(context.Configuration.GetConnectionString("PgMeta")!));
    services.AddSingleton(new PlanRepository(context.Configuration.GetConnectionString("PgMeta")!));
    services.AddSingleton(new PoolMaterialsRepository(context.Configuration.GetConnectionString("PgMeta")!));
    services.AddSingleton(new ReportRepository(context.Configuration.GetConnectionString("PgMeta")!));
    services.AddSingleton(sp => new PlanFreezer(
        sp.GetRequiredService<PlanRepository>(),
        context.Configuration.GetValue<int>("Agents:PoolSize")));
    services.AddSingleton(new ReportBuilder());

    // Адаптеры (Task 9.1): ingest — файл-дамп или живая БД, sink — pg_dump из sandbox
    services.AddSingleton<ISinkAdapter>(new PgDumpSinkAdapter());
    services.AddSingleton<IReadOnlyList<ISourceAdapter>>(_ =>
        new ISourceAdapter[]
        {
            new PgDumpSourceAdapter(),
            new LiveDbSourceAdapter()
        });

    // Детекция
    services.AddSingleton(new IntrospectionEngine(new IDetector[]
    {
        new ColumnNameDetector(DetectorCatalog.Load())
    }));

    // Статические билдеры материалов (Task 4.3). Порядок регистрации важен:
    // MaterialsHash детерминирован порядком билдеров в MaterialOrchestrator.
    // story.txt копируется в базовый каталог (csproj), но процесс может стартовать
    // из другого cwd (dotnet run) — путь резолвим от AppContext.BaseDirectory
    services.AddSingleton(new RedactMaterialBuilder(
        Path.Combine(AppContext.BaseDirectory, "story.txt")));
    services.AddSingleton(new FormatMaterialBuilder());
    services.AddSingleton(new ShiftMaterialBuilder());
    services.AddSingleton<IReadOnlyList<IStrategyMaterialBuilder>>(sp =>
        new IStrategyMaterialBuilder[]
        {
            sp.GetRequiredService<RedactMaterialBuilder>(),
            sp.GetRequiredService<FormatMaterialBuilder>(),
            sp.GetRequiredService<ShiftMaterialBuilder>()
        });

    // Чистые фазы (Task 7.1/7.2) — их зовут wrapper-фазы
    var sandbox = context.Configuration.GetConnectionString("Sandbox")!;
    services.AddSingleton(new ApplyPhase(sandbox));
    services.AddSingleton(new VerifyPhase(sandbox));

    // Фазы-статус-машины: цепочка через Hangfire, интерфейсы из Core.
    // Hangfire резолвит интерфейс из контейнера при исполнении задания.
    // Фабрики обязательны: ctor фаз принимают строки конфигурации (sandbox и т.п.),
    // которых нет в DI.
    services.AddSingleton<IIngestPhase>(sp => new IngestPhase(
        sp.GetRequiredService<IReadOnlyList<ISourceAdapter>>(),
        sandbox,
        sp.GetRequiredService<JobRepository>()));
    services.AddSingleton<IIntrospectPhase>(sp => new IntrospectPhase(
        sp.GetRequiredService<IntrospectionEngine>(),
        sp.GetRequiredService<PlanRepository>(),
        sp.GetRequiredService<JobRepository>(),
        sandbox));
    services.AddSingleton<IEnrichPhase>(sp => new EnrichPhase(
        new HttpClient(),
        context.Configuration["Agents:BaseUrl"]!,
        sandbox,
        sp.GetRequiredService<PlanRepository>(),
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<PoolMaterialsRepository>(),
        sp.GetRequiredService<IReadOnlyList<IStrategyMaterialBuilder>>()));
    services.AddSingleton<IApplyPhase>(sp => new ApplyChainPhase(
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<PlanRepository>(),
        sp.GetRequiredService<ApplyPhase>()));
    services.AddSingleton<IVerifyPhase>(sp => new VerifyChainPhase(
        sp.GetRequiredService<JobRepository>(),
        sp.GetRequiredService<PlanRepository>(),
        sp.GetRequiredService<ReportRepository>(),
        sp.GetRequiredService<VerifyPhase>(),
        sp.GetRequiredService<ISinkAdapter>(),
        sp.GetRequiredService<ReportBuilder>(),
        sandbox,
        context.Configuration["Sink:OutputDirectory"]!));
});

var host = builder.Build();
host.Run();
