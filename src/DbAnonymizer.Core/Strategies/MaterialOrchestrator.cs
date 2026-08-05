// Strategies/MaterialOrchestrator.cs
using System.Security.Cryptography;
using System.Text;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Strategies;

public sealed class MaterialOrchestrator
{
    private readonly IReadOnlyList<IStrategyMaterialBuilder> _builders;

    public MaterialOrchestrator(IEnumerable<IStrategyMaterialBuilder> builders)
    {
        _builders = builders.ToList();
    }

    public async Task<string> BuildAllAsync(
        string sandboxConnectionString,
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        var hashes = new List<string>();
        foreach (var builder in _builders)
        {
            var hash = await builder.BuildAndLoadAsync(
                sandboxConnectionString, plan, cancellationToken);
            hashes.Add($"{builder.StrategyType}:{hash}");
        }
        // Комбинированный хэш для Plan.MaterialsHash
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", hashes))));
    }
}
