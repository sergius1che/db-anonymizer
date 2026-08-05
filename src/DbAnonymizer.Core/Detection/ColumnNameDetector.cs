// Detection/ColumnNameDetector.cs
using System.Text.RegularExpressions;
using Npgsql;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public sealed class ColumnNameDetector : IDetector
{
    private readonly DetectorCatalog _catalog;

    public string Name => "column_name";

    public ColumnNameDetector(DetectorCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<ColumnCandidate>> DetectAsync(
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        // Получаем список всех колонок из information_schema
        await using var cmd = new NpgsqlCommand(
            @"SELECT table_schema, table_name, column_name, data_type, character_maximum_length
              FROM information_schema.columns
              WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              ORDER BY table_schema, table_name, ordinal_position", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var candidates = new List<ColumnCandidate>();
        var rules = _catalog.GetRules();

        while (await reader.ReadAsync(cancellationToken))
        {
            var columnName = reader.GetString(reader.GetOrdinal("column_name"));
            foreach (var rule in rules)
            {
                if (Regex.IsMatch(columnName, rule.Pattern, RegexOptions.IgnoreCase))
                {
                    candidates.Add(new ColumnCandidate
                    {
                        SchemaName = reader.GetString(reader.GetOrdinal("table_schema")),
                        TableName = reader.GetString(reader.GetOrdinal("table_name")),
                        ColumnName = columnName,
                        DataType = reader.GetString(reader.GetOrdinal("data_type")),
                        MaxLength = reader.IsDBNull(reader.GetOrdinal("character_maximum_length"))
                            ? null
                            : reader.GetInt32(reader.GetOrdinal("character_maximum_length")),
                        DetectedType = rule.ParsedSemanticType,
                        Confidence = rule.ParsedConfidence,
                        SignalSource = Name
                    });
                    break; // первое совпадение, от более специфичных к общим
                }
            }
        }
        return candidates;
    }
}
