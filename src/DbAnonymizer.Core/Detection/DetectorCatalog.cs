// Detection/DetectorCatalog.cs
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public sealed class DetectionRule
{
    public required string Pattern { get; init; }
    public required string SemanticType { get; init; }
    public required string Confidence { get; init; }

    public SemanticType ParsedSemanticType => Enum.Parse<SemanticType>(SemanticType);
    public Confidence ParsedConfidence => Enum.Parse<Confidence>(Confidence);
}

public sealed class DetectorCatalog
{
    private readonly List<DetectionRule> _rules;

    public DetectorCatalog(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var doc = deserializer.Deserialize<CatalogDocument>(yamlContent);
        _rules = doc.Rules;
    }

    public static DetectorCatalog Load()
    {
        var assembly = typeof(DetectorCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "DbAnonymizer.Core.Detection.detectors.yaml");
        using var reader = new StreamReader(stream!);
        return new DetectorCatalog(reader.ReadToEnd());
    }

    public IReadOnlyList<DetectionRule> GetRules() => _rules;

    private sealed class CatalogDocument
    {
        public List<DetectionRule> Rules { get; set; } = new();
    }
}
