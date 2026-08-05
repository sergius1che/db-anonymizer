// Detection/DetectorCatalogTests.cs
using DbAnonymizer.Core.Detection;
using DbAnonymizer.Core.Models;

public sealed class DetectorCatalogTests
{
    [Fact]
    public void Load_ShouldParseAllRules()
    {
        var catalog = DetectorCatalog.Load();
        var rules = catalog.GetRules();
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.ParsedSemanticType == SemanticType.Email);
    }
}
