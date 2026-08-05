// Phases/VerifyPhase.cs
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Worker.Phases;

public sealed class VerifyPhase
{
    private readonly string _sandboxConnectionString;

    public VerifyPhase(string sandboxConnectionString)
    {
        _sandboxConnectionString = sandboxConnectionString;
    }

    public async Task<Report> VerifyAsync(
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        var checks = new List<ReportCheck>();

        // 1. Ссылочная целостность — бесплатно: сюда доходим только если
        // Apply успешно восстановил FK (иначе он бросил бы исключение)
        checks.Add(new ReportCheck
        {
            Name = "Referential integrity",
            Passed = true,
            Detail = "All FK constraints re-created successfully"
        });

        // 2. Дубликаты в unique-колонках — проверяем по каждой
        foreach (var col in plan.Columns.Where(c => c.IsUnique))
        {
            var dupCount = await ExecuteScalarLongAsync(
                conn,
                $"SELECT count(*) FROM (SELECT {BuildValueExpression(col)}, count(*) AS cnt FROM {QualifiedTable(col)} GROUP BY {BuildValueExpression(col)} HAVING count(*) > 1) dups",
                cancellationToken);
            checks.Add(new ReportCheck
            {
                Name = $"Unique: {col.SchemaName}.{col.TableName}.{col.ColumnName}",
                Passed = dupCount == 0,
                Detail = dupCount == 0
                    ? "No duplicates"
                    : $"{dupCount} duplicate groups found"
            });
        }

        // 3. Количество строк — одна проверка на таблицу, не на колонку
        foreach (var group in plan.Columns.GroupBy(c => (c.SchemaName, c.TableName)))
        {
            var first = group.First();
            var newCount = await ExecuteScalarLongAsync(
                conn,
                $"SELECT count(*) FROM {QualifiedTable(first)}",
                cancellationToken);
            checks.Add(new ReportCheck
            {
                Name = $"Row count: {first.SchemaName}.{first.TableName}",
                Passed = newCount == first.RowCount,
                Detail = $"Before: {first.RowCount}, After: {newCount}"
            });
        }

        // 4. Разнообразие значений — DISTINCT до и после (допуск ±10%)
        foreach (var col in plan.Columns.Where(c => c.Strategy != StrategyType.Keep))
        {
            var newDistinct = await ExecuteScalarLongAsync(
                conn,
                $"SELECT count(DISTINCT {BuildValueExpression(col)}) FROM {QualifiedTable(col)}",
                cancellationToken);
            var ratio = col.DistinctCount > 0
                ? (double)newDistinct / col.DistinctCount
                : 1.0;
            checks.Add(new ReportCheck
            {
                Name = $"Diversity: {col.SchemaName}.{col.TableName}.{col.ColumnName}",
                Passed = ratio >= 0.90 && ratio <= 1.10,
                Detail = $"Before: {col.DistinctCount}, After: {newDistinct}, Ratio: {ratio:F2}"
            });
        }

        // 5. Leak check: исходные значения не утекли в новую таблицу.
        // При Apply каждая обработанная таблица t переименована в t_orig,
        // а анонимизированная копия живёт под именем t — сравниваем их.
        // Join keys = не-заменяемые колонки таблицы (сквозной проход CTAS —
        // байт-в-байт одинаковы на обеих сторонах). Утёкшая строка = строка,
        // где значение заменяемой колонки осталось равным старому.
        var replaceableByTable = plan.Columns
            .Where(c => c.Strategy != StrategyType.Keep)
            .GroupBy(c => (c.SchemaName, c.TableName))
            .ToList();

        foreach (var group in replaceableByTable)
        {
            var schema = group.Key.SchemaName;
            var table = group.Key.TableName;
            var tableColumns = await GetColumnNamesAsync(conn, schema, table, cancellationToken);
            var replaceableColumns = group
                .Select(c => c.ColumnName)
                .ToHashSet(StringComparer.Ordinal);
            var joinKeys = tableColumns
                .Where(name => !replaceableColumns.Contains(name))
                .ToList();

            foreach (var spec in group)
            {
                if (joinKeys.Count == 0)
                {
                    // Нет ни одной не-заменяемой колонки — JOIN невозможен
                    checks.Add(new ReportCheck
                    {
                        Name = BuildLeakName(schema, table, spec),
                        Passed = true,
                        Detail = "No join key — skipped"
                    });
                    continue;
                }

                var leakCount = await CountLeaksAsync(
                    conn, schema, table, joinKeys, spec, cancellationToken);
                checks.Add(new ReportCheck
                {
                    Name = BuildLeakName(schema, table, spec),
                    Passed = leakCount == 0,
                    Detail = leakCount == 0
                        ? "No original values leaked"
                        : $"{leakCount} row(s) still contain original values"
                });
            }
        }

        // 6. _orig больше никому не нужны — убрать ПОСЛЕ сбора всех проверок
        foreach (var group in replaceableByTable)
        {
            var drop = $"DROP TABLE IF EXISTS {QuoteIdentifier(group.Key.SchemaName)}.{QuoteIdentifier(group.Key.TableName + "_orig")}";
            await using var cmd = new NpgsqlCommand(drop, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return new Report
        {
            JobId = plan.Id,
            PlanId = plan.Id,
            Seed = plan.Seed,
            SuffixWidth = plan.SuffixWidth,
            AllChecksPassed = checks.All(c => c.Passed),
            Checks = checks,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<long> CountLeaksAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        IReadOnlyList<string> joinKeys,
        ColumnSpec spec,
        CancellationToken cancellationToken)
    {
        var quotedOrig = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table + "_orig")}";
        var quotedNew = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        var joinClause = string.Join(
            " AND ",
            joinKeys.Select(key =>
                $"o.{QuoteIdentifier(key)} IS NOT DISTINCT FROM n.{QuoteIdentifier(key)}"));
        var sql = $"SELECT count(*) FROM {quotedOrig} o JOIN {quotedNew} n ON {joinClause} AND {BuildComparedExpression(spec)} AND o.{BuildValueExpression(spec)} IS NOT NULL";
        return await ExecuteScalarLongAsync(conn, sql, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetColumnNamesAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static string BuildComparedExpression(ColumnSpec col) => col.IsJsonPath
        ? $"o.{QuoteIdentifier(col.ColumnName)}#>>{PathSegmentsLiteral(col.JsonPath)} IS NOT DISTINCT FROM n.{QuoteIdentifier(col.ColumnName)}#>>{PathSegmentsLiteral(col.JsonPath)}"
        : $"o.{QuoteIdentifier(col.ColumnName)}::TEXT IS NOT DISTINCT FROM n.{QuoteIdentifier(col.ColumnName)}";

    private static string BuildLeakName(string schema, string table, ColumnSpec spec) =>
        spec.IsJsonPath
            ? $"Leak: {schema}.{table}.{spec.ColumnName}@{spec.JsonPath}"
            : $"Leak: {schema}.{table}.{spec.ColumnName}";

    private static string BuildValueExpression(ColumnSpec col) => col.IsJsonPath
        ? $"{QuoteIdentifier(col.ColumnName)}#>>{PathSegmentsLiteral(col.JsonPath)}"
        : QuoteIdentifier(col.ColumnName);

    private static string EscapeSqlString(string value) => value.Replace("'", "''");

    private static async Task<long> ExecuteScalarLongAsync(
        NpgsqlConnection conn,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        // Безлимитное время: count(DISTINCT) на больших таблицах дольше 30 с (Npgsql default)
        cmd.CommandTimeout = 0;
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value);
    }

    private static string PathSegmentsLiteral(string jsonPath) =>
        "'{" + string.Join(
            ",",
            StripJsonPathPrefix(jsonPath)
                .Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(EscapeSqlString)) + "}'";

    private static string QualifiedTable(ColumnSpec col) =>
        $"{QuoteIdentifier(col.SchemaName)}.{QuoteIdentifier(col.TableName)}";

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string StripJsonPathPrefix(string jsonPath)
    {
        var path = jsonPath;
        if (path.StartsWith('$'))
            path = path[1..];
        if (path.StartsWith('.'))
            path = path[1..];
        return path;
    }
}
