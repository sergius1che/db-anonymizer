// Detection/IntrospectionEngine.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public sealed class IntrospectionEngine
{
    private readonly IReadOnlyList<IDetector> _detectors;

    public IntrospectionEngine(IEnumerable<IDetector> detectors)
    {
        _detectors = detectors.ToList();
    }

    public async Task<IReadOnlyList<ColumnCandidate>> RunAsync(
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        // Запустить все детекторы параллельно
        var tasks = _detectors.Select(d => d.DetectAsync(sandboxConnectionString, cancellationToken));
        var results = await Task.WhenAll(tasks);

        // Слить: ключ = (schema, table, column), значение = кандидат с максимальной уверенностью
        var merged = new Dictionary<string, ColumnCandidate>();
        foreach (var candidate in results.SelectMany(r => r))
        {
            var key = $"{candidate.SchemaName}.{candidate.TableName}.{candidate.ColumnName}";
            if (!merged.TryGetValue(key, out var existing) || candidate.Confidence > existing.Confidence)
            {
                merged[key] = candidate;
            }
        }

        return merged.Values.ToList();
    }
}
