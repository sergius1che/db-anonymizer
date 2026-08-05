// Strategies/IStrategyMaterialBuilder.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Strategies;

public interface IStrategyMaterialBuilder
{
    StrategyType StrategyType { get; }
    Task<string> BuildAndLoadAsync(
        string sandboxConnectionString,
        Plan plan,
        CancellationToken cancellationToken = default);
    // Возвращает хэш загруженных материалов (для MaterialsHash в плане)
}
