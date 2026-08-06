using DbAnonymizer.Core.Detection;
using DbAnonymizer.Core.Jobs;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Planning;
using DbAnonymizer.Worker.Phases;

namespace DbAnonymizer.Integration.Tests;

[Collection("Integration")]
public sealed class IntrospectPhaseTests
{
    private readonly TestcontainersFixture _fixture;

    public IntrospectPhaseTests(TestcontainersFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IntrospectAsync_JobWithExplicitSeedAndSuffixWidth_UsesThemInDraft()
    {
        // Seed/suffix width — явные входы задачи (спека): если пользователь задал их
        // при создании job, Introspect должен использовать их as-is, а не подставлять
        // свои значения (регрессия — раньше Seed генерировался внутри Introspect всегда)
        await _fixture.ResetSandboxAsync();
        var jobs = new JobRepository(_fixture.MetaConnectionString);
        var jobId = Guid.NewGuid().ToString("N");
        await jobs.SaveAsync(new JobState
        {
            Id = jobId,
            Status = JobStatus.Ingesting,
            CreatedAt = DateTime.UtcNow,
            Seed = "explicit-seed-from-job",
            SuffixWidth = 9
        });
        var plans = new PlanRepository(_fixture.MetaConnectionString);
        var phase = NewIntrospectPhase(plans, jobs);

        await phase.IntrospectAsync(jobId);

        var draft = await plans.LoadAsync(jobId);
        Assert.NotNull(draft);
        Assert.Equal("explicit-seed-from-job", draft!.Seed);
        Assert.Equal(9, draft.SuffixWidth);
    }

    [Fact]
    public async Task IntrospectAsync_JobWithoutSeedOrSuffixWidth_FallsBackToDefaults()
    {
        await _fixture.ResetSandboxAsync();
        var jobs = new JobRepository(_fixture.MetaConnectionString);
        var jobId = Guid.NewGuid().ToString("N");
        await jobs.SaveAsync(new JobState
        {
            Id = jobId,
            Status = JobStatus.Ingesting,
            CreatedAt = DateTime.UtcNow
        });
        var plans = new PlanRepository(_fixture.MetaConnectionString);
        var phase = NewIntrospectPhase(plans, jobs);

        await phase.IntrospectAsync(jobId);

        var draft = await plans.LoadAsync(jobId);
        Assert.NotNull(draft);
        Assert.False(string.IsNullOrWhiteSpace(draft!.Seed));
        Assert.Equal(6, draft.SuffixWidth);
    }

    private IntrospectPhase NewIntrospectPhase(PlanRepository plans, JobRepository jobs) =>
        new(
            new IntrospectionEngine(new IDetector[] { new ColumnNameDetector(DetectorCatalog.Load()) }),
            plans,
            jobs,
            _fixture.SandboxConnectionString);
}
