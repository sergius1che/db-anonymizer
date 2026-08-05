// Plan/PlanValidator.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Planning;

public static class PlanValidator
{
    public static IReadOnlyList<string> Validate(Plan plan)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.Seed))
            errors.Add("Seed is required");
        if (plan.SuffixWidth < 0)
            errors.Add("SuffixWidth must be >= 0");
        if (plan.Columns.Count == 0)
            errors.Add("Plan must have at least one column");

        // Проверка: каждая unique-колонка семантического типа → суффикс включён
        var typesNeedingSuffix = plan.Columns
            .Where(c => c.IsUnique)
            .Select(c => c.SemanticType)
            .Distinct()
            .ToList();

        foreach (var col in plan.Columns)
        {
            // Чувствительная колонка без явной стратегии — ошибка (Keep — только осознанно)
            if (col.SemanticType != SemanticType.Unknown
                && !col.IsJsonPath
                && col.SemanticType != SemanticType.Json
                && col.Strategy == StrategyType.Keep)
            {
                errors.Add(
                    $"Column {col.SchemaName}.{col.TableName}.{col.ColumnName} " +
                    $"is detected as {col.SemanticType} but has no explicit strategy");
            }

            if (col.Strategy != StrategyType.Keep
                && col.IsUnique
                && typesNeedingSuffix.Contains(col.SemanticType)
                && plan.SuffixWidth == 0)
            {
                errors.Add(
                    $"Column {col.SchemaName}.{col.TableName}.{col.ColumnName} " +
                    $"is unique but SuffixWidth=0");
            }
        }

        return errors;
    }
}
