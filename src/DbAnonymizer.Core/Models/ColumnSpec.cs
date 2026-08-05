// Models/ColumnSpec.cs
namespace DbAnonymizer.Core.Models;

public sealed class ColumnSpec
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string ColumnName { get; init; }
    public string DataType { get; init; } = string.Empty;
    public bool IsUnique { get; init; }
    public SemanticType SemanticType { get; init; }
    public StrategyType Strategy { get; init; } = StrategyType.Keep;
    public bool IsJsonPath { get; init; }  // true если это путь внутри jsonb, а не колонка
    public string JsonPath { get; init; } = string.Empty; // "$.customer.phone"
    public long DistinctCount { get; init; }
    public long RowCount { get; init; }
}
