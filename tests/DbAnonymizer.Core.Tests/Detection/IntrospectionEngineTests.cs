// Detection/IntrospectionEngineTests.cs
using DbAnonymizer.Core.Detection;
using DbAnonymizer.Core.Models;

public sealed class IntrospectionEngineTests
{
    [Fact]
    public async Task Run_MergesMultipleDetectors_PicksHighestConfidence()
    {
        var lowDetector = new StubDetector("low", new ColumnCandidate
        {
            SchemaName = "public", TableName = "users", ColumnName = "email",
            DetectedType = SemanticType.Email, Confidence = Confidence.Low, SignalSource = "low"
        });
        var highDetector = new StubDetector("high", new ColumnCandidate
        {
            SchemaName = "public", TableName = "users", ColumnName = "email",
            DetectedType = SemanticType.Email, Confidence = Confidence.High, SignalSource = "high"
        });
        var engine = new IntrospectionEngine([lowDetector, highDetector]);
        var result = await engine.RunAsync("any");
        var email = Assert.Single(result);
        Assert.Equal(Confidence.High, email.Confidence);
        Assert.Equal("high", email.SignalSource);
    }

    private sealed class StubDetector : IDetector
    {
        private readonly ColumnCandidate _candidate;

        public StubDetector(string name, ColumnCandidate candidate)
        {
            Name = name;
            _candidate = candidate;
        }

        public string Name { get; }

        public Task<IReadOnlyList<ColumnCandidate>> DetectAsync(
            string sandboxConnectionString,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ColumnCandidate>>([_candidate]);
        }
    }
}
