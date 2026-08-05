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

        // Получаем список всех колонок из information_schema + признак участия в
        // UNIQUE/PRIMARY KEY (для суффикс-гарда и проверки Unique в VerifyPhase).
        await using var cmd = new NpgsqlCommand(
            @"SELECT c.table_schema, c.table_name, c.column_name, c.data_type,
                     c.character_maximum_length,
                     EXISTS (
                         SELECT 1
                         FROM information_schema.table_constraints tc
                         JOIN information_schema.key_column_usage kcu
                           ON tc.constraint_catalog = kcu.constraint_catalog
                           AND tc.constraint_schema = kcu.constraint_schema
                           AND tc.constraint_name = kcu.constraint_name
                         WHERE tc.constraint_type IN ('UNIQUE', 'PRIMARY KEY')
                           AND kcu.table_schema = c.table_schema
                           AND kcu.table_name = c.table_name
                           AND kcu.column_name = c.column_name
                     ) AS is_unique
              FROM information_schema.columns c
              WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
              ORDER BY c.table_schema, c.table_name, c.ordinal_position", conn);
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
                        IsUnique = reader.GetBoolean(reader.GetOrdinal("is_unique")),
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
