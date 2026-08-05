// Adapters/PgDumpSourceAdapter.cs
using System.Diagnostics;
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Adapters;

/// <summary>
/// Восстанавливает дамп в песочницу через pg_restore.
/// Параллельный режим (-j) поддерживается только для форматов custom/directory —
/// такой дамп продюсит PgDumpSinkAdapter; plain SQL-дампы так не восстанавливаются.
/// </summary>
public sealed class PgDumpSourceAdapter : ISourceAdapter
{
    public async Task RestoreAsync(
        SourceSpec source,
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.DumpFilePath))
            throw new InvalidOperationException(
                "SourceSpec.DumpFilePath is empty — dump-file source requires a dump path");

        var (host, port, database, username, password) = PgConnectionInfo.Parse(sandboxConnectionString);
        var arguments = new List<string>
        {
            "-h", host,
            "-p", port.ToString(),
            "-U", username,
            "-d", database,
            "-j", Environment.ProcessorCount.ToString(),
            "--no-owner",
            "--no-privileges",
            source.DumpFilePath
        };
        await PgToolRunner.RunAsync("pg_restore", arguments, password, cancellationToken);
    }
}

/// <summary>
/// Разбор connection string для pg-адаптеров: обязательные поля (Host, Database,
/// Username) проверяются, чтобы pg-инструменты получали валидный набор аргументов.
/// Пароль в сообщениях об ошибках не раскрывается.
/// </summary>
internal static class PgConnectionInfo
{
    public static (string Host, int Port, string Database, string Username, string Password) Parse(
        string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(builder.Host)
            || string.IsNullOrEmpty(builder.Database)
            || string.IsNullOrEmpty(builder.Username))
        {
            throw new InvalidOperationException(
                "Connection string must set Host, Database and Username (password is optional)");
        }

        return (
            builder.Host,
            builder.Port,
            builder.Database,
            builder.Username,
            builder.Password ?? string.Empty);
    }
}

/// <summary>
/// Общий запуск pg-инструментов для адаптеров:
/// PGPASSWORD через env, stderr читается асинхронно, при отмене — best-effort Kill,
/// ненулевой exit code → InvalidOperationException с кодом и stderr.
/// </summary>
internal static class PgToolRunner
{
    public static async Task RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string password,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PGPASSWORD"] = password;

        // Уже отменённый токен — процесс не стартуем вовсе (иначе осиротевший
        // pg_dump/pg_restore повис бы с живым соединением к БД)
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"'{tool}' failed to start — is the PostgreSQL client installed on PATH?",
                exception);
        }

        // stderr и ожидание выхода — в одном try: любой OCE (включая синхронный
        // бросок ReadToEndAsync при отменённом токене) проходит через TryKill,
        // чтобы процесс pg не остался висеть
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"'{tool}' exited with code {process.ExitCode}: {stderr}");
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            // Ограниченное ожидание: если Kill не сработал или дерево зависло —
            // OCE всё равно разворачивается, задание не блокируется
            process.WaitForExit(5000);
        }
        catch (Exception)
        {
            // Best effort — процесс pg не должен остаться висеть
        }
    }
}
