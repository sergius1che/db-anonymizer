// Models/ColumnCandidate.cs
namespace DbAnonymizer.Core.Models;

public sealed class ColumnCandidate
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string ColumnName { get; init; }
    public string DataType { get; init; } = string.Empty;
    public bool IsUnique { get; init; }
    public int? MaxLength { get; init; }
    public SemanticType DetectedType { get; init; }
    public Confidence Confidence { get; init; }
    public string SignalSource { get; init; } = string.Empty; // "column_name", "type_constraint", "profile"
}
