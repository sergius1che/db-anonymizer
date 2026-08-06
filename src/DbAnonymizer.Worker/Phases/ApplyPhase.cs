// Phases/ApplyPhase.cs
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Worker.Phases;

public sealed class ApplyPhase
{
    private readonly string _sandboxConnectionString;

    public ApplyPhase(string sandboxConnectionString)
    {
        _sandboxConnectionString = sandboxConnectionString;
    }

    public async Task ApplyAsync(
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        // 1. Снять все FK — пересоздание таблиц их не перенесёт
        var foreignKeys = await DropAllForeignKeysAsync(conn, cancellationToken);

        try
        {
            // 2. range_days из материалов (shift_config), а не хардкод
            var shiftRangeDays = await GetBirthdateShiftRangeDaysAsync(conn, cancellationToken);

            // 3. Сгруппировать размеченные колонки по таблицам
            var byTable = plan.Columns
                .Where(c => c.Strategy != StrategyType.Keep)
                .GroupBy(c => (c.SchemaName, c.TableName))
                .ToList();

            // 4. Для каждой таблицы: CTAS + перенос индексов/ограничений + подмена
            foreach (var group in byTable)
            {
                await ApplyTableAsync(
                    conn,
                    group.Key.SchemaName,
                    group.Key.TableName,
                    group.ToList(),
                    plan,
                    shiftRangeDays,
                    cancellationToken);
            }
        }
        finally
        {
            // 5. Вернуть FK — даже если часть таблиц не применилась,
            // sandbox должен остаться со ссылочной целостностью
            await RestoreForeignKeysAsync(conn, foreignKeys, cancellationToken);
        }
    }

    private async Task ApplyTableAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        IReadOnlyList<ColumnSpec> columns,
        Plan plan,
        int shiftRangeDays,
        CancellationToken cancellationToken)
    {
        var quotedSchema = QuoteIdentifier(schema);
        var quotedTable = QuoteIdentifier(table);
        var quotedNewTable = QuoteIdentifier(table + "_new");

        // Полный список колонок в физическом порядке: CTAS копирует ВСЕ колонки,
        // не только размеченные — иначе неразмеченные молча теряются
        var tableColumns = await GetColumnNamesAsync(conn, schema, table, cancellationToken);
        var firstNameColumn = FindSiblingColumn(plan.Columns, schema, table, SemanticType.FirstName);
        var lastNameColumn = FindSiblingColumn(plan.Columns, schema, table, SemanticType.LastName);
        var selectParts = BuildSelectParts(
            tableColumns, columns, plan, shiftRangeDays, firstNameColumn, lastNameColumn);

        var ctas = $"""
            CREATE TABLE {quotedSchema}.{quotedNewTable} AS
            SELECT {string.Join(", ", selectParts)}
            FROM {quotedSchema}.{quotedTable}
            """;
        await using (var cmd = new NpgsqlCommand(ctas, conn))
        {
            cmd.CommandTimeout = 0; // без таймаута для больших таблиц
            cmd.Parameters.AddWithValue("@seed", plan.Seed);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await TransferIndexesAndConstraintsAsync(conn, schema, table, cancellationToken);
        await SwapTablesAsync(conn, schema, table, cancellationToken);
    }

    private static string BuildAnonymizeCall(
        ColumnSpec col,
        string inputExpression,
        Plan plan,
        int shiftRangeDays,
        string? firstNameColumn,
        string? lastNameColumn) => col.Strategy switch
    {
        StrategyType.Fake => $"anonymize.fake(@seed, {inputExpression}, '{col.SemanticType.ToString().ToLowerInvariant()}', {GetPoolSize(plan, col.SemanticType)}, {plan.SuffixWidth})",
        StrategyType.Format => $"anonymize.format(@seed, {inputExpression}, '{col.SemanticType.ToString().ToLowerInvariant()}', {plan.SuffixWidth})",
        StrategyType.Secret => $"anonymize.secret(@seed, {inputExpression})",
        StrategyType.Shift => $"anonymize.shift(@seed, {inputExpression}, {shiftRangeDays})",
        StrategyType.Redact => $"anonymize.redact(@seed, {inputExpression})",
        StrategyType.Derive => BuildDeriveCall(inputExpression, plan, firstNameColumn, lastNameColumn),
        _ => inputExpression
    };

    // Фейковое имя для email должно совпадать с тем, что показывают Fake(FirstName)/
    // Fake(LastName) колонки той же строки — вход берём из реальных first/last name
    // колонок ТОЙ ЖЕ таблицы, не из original самой email-колонки. Если в таблице нет
    // таких колонок (Derive-колонка в другой таблице, напр. orders.user_email), падаем
    // назад на original email — имя не совпадёт ни с чем, но результат детерминирован
    private static string BuildDeriveCall(
        string inputExpression,
        Plan plan,
        string? firstNameColumn,
        string? lastNameColumn)
    {
        var firstNameFake = BuildFakeNameCall(firstNameColumn, inputExpression, SemanticType.FirstName, plan);
        var lastNameFake = BuildFakeNameCall(lastNameColumn, inputExpression, SemanticType.LastName, plan);
        return $"anonymize.derive(@seed, {inputExpression}, {firstNameFake}, {lastNameFake}, '@mail.invalid', {plan.SuffixWidth})";
    }

    private static string BuildFakeNameCall(
        string? siblingColumn,
        string fallbackInput,
        SemanticType semanticType,
        Plan plan)
    {
        var input = siblingColumn is null ? fallbackInput : $"{QuoteIdentifier(siblingColumn)}::TEXT";
        var component = semanticType.ToString().ToLowerInvariant();
        return $"anonymize.fake(@seed, {input}, '{component}', {GetPoolSize(plan, semanticType)}, 0)";
    }

    private static string? FindSiblingColumn(
        IReadOnlyList<ColumnSpec> allColumns,
        string schema,
        string table,
        SemanticType semanticType) =>
        allColumns
            .FirstOrDefault(c =>
                c.SchemaName == schema
                && c.TableName == table
                && c.SemanticType == semanticType
                && !c.IsJsonPath)
            ?.ColumnName;

    private static long GetPoolSize(Plan plan, SemanticType semanticType)
    {
        var pool = plan.Pools.FirstOrDefault(p => p.SemanticType == semanticType);
        if (pool is null)
            throw new InvalidOperationException(
                $"Plan '{plan.Id}' needs a Fake pool for semantic type '{semanticType}' but none was frozen");
        return pool.Size;
    }

    private static IReadOnlyList<string> BuildSelectParts(
        IReadOnlyList<string> tableColumns,
        IReadOnlyList<ColumnSpec> columns,
        Plan plan,
        int shiftRangeDays,
        string? firstNameColumn,
        string? lastNameColumn)
    {
        var byColumn = columns
            .GroupBy(c => c.ColumnName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var parts = new List<string>(tableColumns.Count);
        foreach (var column in tableColumns)
        {
            if (!byColumn.TryGetValue(column, out var specs))
            {
                // Неразмеченная колонка — сквозной проход
                parts.Add(QuoteIdentifier(column));
                continue;
            }

            var plainSpec = specs.FirstOrDefault(s => !s.IsJsonPath);
            if (plainSpec is not null)
            {
                // Размечена целиком: анонимизация всего значения
                var input = $"{QuoteIdentifier(column)}::TEXT";
                parts.Add($"{BuildAnonymizeCall(plainSpec, input, plan, shiftRangeDays, firstNameColumn, lastNameColumn)} AS {QuoteIdentifier(column)}");
                continue;
            }

            // Размечены пути внутри jsonb: цепочка jsonb_set для всех путей колонки,
            // вход анонимизации — значение пути (column#>>'{segments}'), не весь jsonb
            var jsonbExpr = QuoteIdentifier(column);
            foreach (var spec in specs)
            {
                var path = StripJsonPathPrefix(spec.JsonPath);
                var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var segmentsLiteral = "{" + string.Join(",", segments.Select(EscapeSqlString)) + "}";
                var input = $"{QuoteIdentifier(column)}#>>'{segmentsLiteral}'";
                var call = BuildAnonymizeCall(spec, input, plan, shiftRangeDays, firstNameColumn, lastNameColumn);
                // anonymize.* возвращает TEXT; to_jsonb — в скалярный jsonb.
                // jsonb_set_lax + 'return_target': путь отсутствует или равен JSON null —
                // вход NULL → STRICT-функция → NULL → jsonb_set вернул бы NULL для всей
                // колонки; lax возвращает исходный jsonb нетронутым
                jsonbExpr = $"jsonb_set_lax({jsonbExpr}, '{segmentsLiteral}', to_jsonb({call}), false, 'return_target')";
            }
            parts.Add($"{jsonbExpr} AS {QuoteIdentifier(column)}");
        }
        return parts;
    }

    private static string EscapeSqlString(string value) => value.Replace("'", "''");

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string RewriteIndexDefinition(string definition, string schema, string table)
    {
        var quotedOld = $"\"{schema}\".\"{table}\"";
        var quotedNew = $"\"{schema}\".\"{table}_new\"";
        var plainOld = $"{schema}.{table}";
        var plainNew = $"{schema}.{table}_new";
        return definition
            .Replace(quotedOld, quotedNew)
            .Replace(plainOld, plainNew);
    }

    private static string StripJsonPathPrefix(string jsonPath)
    {
        var path = jsonPath;
        if (path.StartsWith('$'))
            path = path[1..];
        if (path.StartsWith('.'))
            path = path[1..];
        return path;
    }

    private async Task<IReadOnlyList<ForeignKeySpec>> DropAllForeignKeysAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT con.conname AS constraint_name,
                   n.nspname AS schema_name,
                   c.relname AS table_name,
                   pg_get_constraintdef(con.oid) AS definition
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'f'
            ORDER BY n.nspname, c.relname, con.conname
            """;
        var specs = new List<ForeignKeySpec>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                specs.Add(new ForeignKeySpec(
                    reader.GetString(reader.GetOrdinal("constraint_name")),
                    reader.GetString(reader.GetOrdinal("definition")),
                    reader.GetString(reader.GetOrdinal("schema_name")),
                    reader.GetString(reader.GetOrdinal("table_name"))));
            }
        }

        foreach (var spec in specs)
        {
            var drop = $"ALTER TABLE {QuoteIdentifier(spec.SchemaName)}.{QuoteIdentifier(spec.TableName)} DROP CONSTRAINT {QuoteIdentifier(spec.ConstraintName)}";
            await using var cmd = new NpgsqlCommand(drop, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return specs;
    }

    private async Task<int> GetBirthdateShiftRangeDaysAsync(
        NpgsqlConnection conn,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT range_days
            FROM materials.shift_config
            WHERE type = 'birthdate'
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 3650 : Convert.ToInt32(value);
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

    private async Task RestoreForeignKeysAsync(
        NpgsqlConnection conn,
        IReadOnlyList<ForeignKeySpec> foreignKeys,
        CancellationToken cancellationToken)
    {
        foreach (var spec in foreignKeys)
        {
            var restore = $"ALTER TABLE {QuoteIdentifier(spec.SchemaName)}.{QuoteIdentifier(spec.TableName)} ADD CONSTRAINT {QuoteIdentifier(spec.ConstraintName)} {spec.Definition}";
            await using var cmd = new NpgsqlCommand(restore, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task SwapTablesAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var quotedSchema = QuoteIdentifier(schema);
        var quotedOrig = QuoteIdentifier(table + "_orig");
        var quotedTable = QuoteIdentifier(table);
        var quotedNew = QuoteIdentifier(table + "_new");

        // Пере-запуск: убрать _orig от прошлого прохода (Verify сам дропнет после себя)
        await using (var dropOrig = new NpgsqlCommand(
            $"DROP TABLE IF EXISTS {quotedSchema}.{quotedOrig}", conn))
        {
            await dropOrig.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var renameOrig = new NpgsqlCommand(
            $"ALTER TABLE {quotedSchema}.{quotedTable} RENAME TO {quotedOrig}", conn))
        {
            await renameOrig.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var renameNew = new NpgsqlCommand(
            $"ALTER TABLE {quotedSchema}.{quotedNew} RENAME TO {quotedTable}", conn))
        {
            await renameNew.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task TransferIndexesAndConstraintsAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var tableParam = $"{schema}.{table}";

        // Определения снимаем ДО дропа старых ограничений/индексов:
        // после дропа их уже не из чего взять
        var constraints = new List<(char Type, string Name, string Definition)>();
        var notNullColumns = new List<string>();
        var indexDefinitions = new List<string>();

        const string constraintsSql = """
            SELECT contype, conname, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conrelid = to_regclass(@table) AND contype IN ('p', 'u', 'c')
            """;
        await using (var cmd = new NpgsqlCommand(constraintsSql, conn))
        {
            cmd.Parameters.AddWithValue("@table", tableParam);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                constraints.Add((reader.GetChar(0), reader.GetString(1), reader.GetString(2)));
        }

        // NOT NULL живёт в pg_attribute.attnotnull (для обычного CREATE TABLE
        // pg_constraint-строки contype='n' не создаются), CTAS его не переносит
        const string notNullSql = """
            SELECT a.attname
            FROM pg_attribute a
            WHERE a.attrelid = to_regclass(@table)
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND a.attnotnull
            """;
        await using (var cmd = new NpgsqlCommand(notNullSql, conn))
        {
            cmd.Parameters.AddWithValue("@table", tableParam);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                notNullColumns.Add(reader.GetString(0));
        }

        const string indexesSql = """
            SELECT pg_get_indexdef(ix.indexrelid)
            FROM pg_index ix
            WHERE ix.indrelid = to_regclass(@table)
              AND ix.indexrelid NOT IN (
                  SELECT conindid FROM pg_constraint
                  WHERE conindid IS NOT NULL AND contype IN ('p', 'u'))
            """;
        await using (var cmd = new NpgsqlCommand(indexesSql, conn))
        {
            cmd.Parameters.AddWithValue("@table", tableParam);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                indexDefinitions.Add(reader.GetString(0));
        }

        // PK/unique-ограничения старой таблицы снять (вместе с бэкинг-индексом):
        // имя индекса общесхемное — пока старая таблица жива, имя занято
        foreach (var constraint in constraints.Where(c => c.Type == 'p' || c.Type == 'u'))
        {
            var drop = $"ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} DROP CONSTRAINT {QuoteIdentifier(constraint.Name)}";
            await using var cmd = new NpgsqlCommand(drop, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Обычные индексы: имя тоже общесхемное, старые освобождаем
        const string plainIndexesSql = """
            SELECT n.nspname, i.relname
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_namespace n ON n.oid = i.relnamespace
            WHERE ix.indrelid = to_regclass(@table)
              AND ix.indexrelid NOT IN (
                  SELECT conindid FROM pg_constraint
                  WHERE conindid IS NOT NULL AND contype IN ('p', 'u'))
            """;
        var plainIndexes = new List<(string Schema, string Name)>();
        await using (var cmd = new NpgsqlCommand(plainIndexesSql, conn))
        {
            cmd.Parameters.AddWithValue("@table", tableParam);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                plainIndexes.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var (indexSchema, indexName) in plainIndexes)
        {
            var drop = $"DROP INDEX {QuoteIdentifier(indexSchema)}.{QuoteIdentifier(indexName)}";
            await using var cmd = new NpgsqlCommand(drop, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Пересоздать всё на _new
        var quotedSchema = QuoteIdentifier(schema);
        var quotedNewTable = QuoteIdentifier(table + "_new");

        foreach (var (_, name, definition) in constraints)
        {
            var add = $"ALTER TABLE {quotedSchema}.{quotedNewTable} ADD CONSTRAINT {QuoteIdentifier(name)} {definition}";
            await using var cmd = new NpgsqlCommand(add, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // NOT NULL — CTAS не переносит NULL-ограничения
        foreach (var column in notNullColumns)
        {
            var setNotNull = $"ALTER TABLE {quotedSchema}.{quotedNewTable} ALTER COLUMN {QuoteIdentifier(column)} SET NOT NULL";
            await using var cmd = new NpgsqlCommand(setNotNull, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var definition in indexDefinitions)
        {
            var rewritten = RewriteIndexDefinition(definition, schema, table);
            await using var cmd = new NpgsqlCommand(rewritten, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed record ForeignKeySpec(
        string ConstraintName,
        string Definition,
        string SchemaName,
        string TableName);
}
