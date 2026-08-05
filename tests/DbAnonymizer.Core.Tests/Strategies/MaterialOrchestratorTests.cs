using System.Security.Cryptography;
using System.Text;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Strategies;

namespace DbAnonymizer.Core.Tests.Strategies;

public sealed class MaterialOrchestratorTests
{
    private static readonly Plan TestPlan = new()
    {
        Id = "test-plan",
        SourceHash = "abc",
        Seed = "seed",
        Version = "1",
        ContentHash = "abc",
        MaterialsHash = "abc",
    };

    [Fact]
    public async Task BuildAll_ShouldCombineBuilderHashes()
    {
        var fake = new StubBuilder(StrategyType.Fake, new string('a', 64));
        var shift = new StubBuilder(StrategyType.Shift, new string('b', 64));
        var orchestrator = new MaterialOrchestrator([fake, shift]);

        var hash = await orchestrator.BuildAllAsync("conn", TestPlan);

        var expected = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"Fake:{new string('a', 64)}|Shift:{new string('b', 64)}")));
        Assert.Equal(expected, hash);
        Assert.Equal(1, fake.CallCount);
        Assert.Equal(1, shift.CallCount);
    }

    private sealed class StubBuilder : IStrategyMaterialBuilder
    {
        private readonly string _hash;

        public StubBuilder(StrategyType strategyType, string hash)
        {
            StrategyType = strategyType;
            _hash = hash;
        }

        public StrategyType StrategyType { get; }

        public int CallCount { get; private set; }

        public Task<string> BuildAndLoadAsync(
            string sandboxConnectionString,
            Plan plan,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_hash);
        }
    }
}
