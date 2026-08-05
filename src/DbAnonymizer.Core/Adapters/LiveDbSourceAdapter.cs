// Adapters/LiveDbSourceAdapter.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Adapters;

/// <summary>
/// Копирует живую БД в песочницу ДВУХШАГОВО: pg_dump -Fd с живой БД (read-only)
/// в temp-каталог, затем pg_restore из этого каталога. Никакого bash-pipe —
/// каждый процесс имеет собственный stderr и exit code. Temp-каталог удаляется
/// в finally.
/// </summary>
public sealed class LiveDbSourceAdapter : ISourceAdapter
{
    public async Task RestoreAsync(
        SourceSpec source,
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.ConnectionString))
            throw new InvalidOperationException(
                "SourceSpec.ConnectionString is empty — live-db source requires a connection string");

        var (liveHost, livePort, liveDatabase, liveUsername, livePassword) =
            PgConnectionInfo.Parse(source.ConnectionString);
        var (sandboxHost, sandboxPort, sandboxDatabase, sandboxUsername, sandboxPassword) =
            PgConnectionInfo.Parse(sandboxConnectionString);
        var tempDir = Path.Combine(Path.GetTempPath(), "dbanon-live-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);

            var dumpArguments = new List<string>
            {
                "-h", liveHost,
                "-p", livePort.ToString(),
                "-U", liveUsername,
                "-d", liveDatabase,
                "-Fd",
                "-j", Environment.ProcessorCount.ToString(),
                "-f", tempDir
            };
            await PgToolRunner.RunAsync("pg_dump", dumpArguments, livePassword, cancellationToken);

            var restoreArguments = new List<string>
            {
                "-h", sandboxHost,
                "-p", sandboxPort.ToString(),
                "-U", sandboxUsername,
                "-d", sandboxDatabase,
                "-j", Environment.ProcessorCount.ToString(),
                "--no-owner",
                "--no-privileges",
                tempDir
            };
            await PgToolRunner.RunAsync(
                "pg_restore", restoreArguments, sandboxPassword, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort — временные остатки безвредны
        }
    }
}
