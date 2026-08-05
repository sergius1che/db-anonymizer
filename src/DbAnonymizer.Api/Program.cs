using Hangfire;
using Hangfire.PostgreSql;
using DbAnonymizer.Api.Endpoints;
using DbAnonymizer.Api.Services;
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Planning;
using DbAnonymizer.Core.Reporting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHangfire(config => config
    .UsePostgreSqlStorage(options =>
        options.UseNpgsqlConnection(
            builder.Configuration.GetConnectionString("PgMeta"))));
// Сервер Hangfire живёт только в Worker (8.2): контейнер Api не может резолвить
// фазовые интерфейсы Core, и второй сервер на том же сторадже гонялся бы с Worker
builder.Services.AddSingleton(new PlanRepository(builder.Configuration.GetConnectionString("PgMeta")!));
builder.Services.AddSingleton<PlanFreezer>();
builder.Services.AddSingleton(new ReportRepository(builder.Configuration.GetConnectionString("PgMeta")!));
builder.Services.AddSingleton(new JobRepository(builder.Configuration.GetConnectionString("PgMeta")!));
builder.Services.AddSingleton<JobService>();

var app = builder.Build();

// Статичный UI ревью (wwwroot): index.html отдаётся на "/", остальное — по именам
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapJobsEndpoints();
app.MapPlanEndpoints();
app.MapReportEndpoints();
app.UseHangfireDashboard();

app.Run();

public partial class Program
{
}
