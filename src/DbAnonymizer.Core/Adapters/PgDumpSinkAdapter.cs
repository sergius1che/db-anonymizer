// Adapters/PgDumpSinkAdapter.cs
namespace DbAnonymizer.Core.Adapters;

/// <summary>
/// Выгружает анонимизированную песочницу через pg_dump -Fd (directory-формат,
/// параллельный) в новый каталог под outputDirectory и возвращает его путь —
/// он попадает в отчёт (output_dump).
/// </summary>
public sealed class PgDumpSinkAdapter : ISinkAdapter
{
    private const string AnonymizeSchema = "anonymize";
    private const string MaterialsSchema = "materials";
    private const string UtilsSchema = "utils";

    public async Task<string> DumpAsync(
        string sandboxConnectionString,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var (host, port, database, username, password) = PgConnectionInfo.Parse(sandboxConnectionString);
        Directory.CreateDirectory(outputDirectory);
        var dumpPath = Path.Combine(
            outputDirectory,
            $"dbanon-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dumpPath);

        var arguments = new List<string>
        {
            "-h", host,
            "-p", port.ToString(),
            "-U", username,
            "-d", database,
            "-Fd",
            "-j", Environment.ProcessorCount.ToString(),
            // Схемы инструмента — не часть исходных данных, не должны попасть
            // в дамп для команды разработки (materials.* содержит пулы фейков
            // и seed-реконструируемый маппинг на оригиналы)
            "-N", AnonymizeSchema,
            "-N", MaterialsSchema,
            "-N", UtilsSchema,
            "-f", dumpPath
        };
        try
        {
            await PgToolRunner.RunAsync("pg_dump", arguments, password, cancellationToken);
        }
        catch
        {
            TryDeleteDirectory(dumpPath);
            throw;
        }

        return dumpPath;
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
            // Best effort — каталог-остаток безвреден
        }
    }
}
