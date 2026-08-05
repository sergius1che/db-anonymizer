using System.Text;
using Npgsql;

namespace DbAnonymizer.Integration.Tests;

internal static class SqlScriptRunner
{
    public static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string script,
        CancellationToken cancellationToken = default)
    {
        foreach (var statement in SplitStatements(script))
        {
            if (string.IsNullOrWhiteSpace(statement))
                continue;
            await using var cmd = new NpgsqlCommand(statement, connection);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // Npgsql: одно выражение на команду. Файлы (deploy.sql, стратегии, demo-schema)
    // содержат multi-statement SQL с dollar-quoted телами функций ($$...$$) —
    // наивный split(';') рвёт тела. Разбиваем на выражения с учётом:
    //   - dollar-quote тегов ($$, $tag$)
    //   - одинарных кавычек
    //   - -- однострочных комментариев
    //
    // Ограничения сплиттера (задокументировано, фикс-раунд 1 ревью 10.1):
    //   (а) ';' внутри двойных кавычек — "..."-идентификаторов — не понимается:
    //       стейтмент оборвётся на середине идентификатора;
    //   (б) backslash-escape в строках E'...' не понимается: '\'' закроет строку
    //       раньше времени;
    //   (в) при появлении таких конструкций сплит будет НЕКОРРЕКТНЫМ (упадёт тест,
    //       а не прод — это тестовая утилита).
    // Проверено: ни один из скриптов, которые исполняет фикстура (deploy.sql,
    // strategies/*, utils/*, demo-schema.sql, seed-код, jsonb-кейс), этих
    // конструкций не содержит — экспозиция нулевая, расширение сплиттера
    // не добавляется.
    public static IReadOnlyList<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var inSingleQuote = false;
        var inLineComment = false;
        string? dollarTag = null;

        for (int i = 0; i < script.Length; i++)
        {
            var c = script[i];

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                    current.Append(c);
                }
                continue;
            }

            if (dollarTag is not null)
            {
                current.Append(c);
                if (c == '$'
                    && i + dollarTag.Length <= script.Length
                    && script.AsSpan(i).StartsWith(dollarTag, StringComparison.Ordinal))
                {
                    current.Append(dollarTag, 1, dollarTag.Length - 1);
                    i += dollarTag.Length - 1;
                    dollarTag = null;
                }
                continue;
            }

            if (inSingleQuote)
            {
                current.Append(c);
                if (c == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                current.Append(c);
                continue;
            }

            if (c == '$')
            {
                var tag = ReadDollarTag(script, i);
                if (tag is not null)
                {
                    dollarTag = tag;
                    current.Append(tag);
                    i += tag.Length - 1;
                    continue;
                }
            }

            if (c == ';')
            {
                statements.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            statements.Add(current.ToString());

        return statements;
    }

    private static string? ReadDollarTag(string script, int start)
    {
        var end = start + 1;
        while (end < script.Length && (char.IsLetterOrDigit(script[end]) || script[end] == '_'))
            end++;
        if (end < script.Length && script[end] == '$')
            return script.Substring(start, end - start + 1);
        return null;
    }
}
