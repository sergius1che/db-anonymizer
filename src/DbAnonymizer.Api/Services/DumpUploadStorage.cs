// Services/DumpUploadStorage.cs
namespace DbAnonymizer.Api.Services;

/// <summary>
/// Сохраняет загруженный через POST /jobs/upload файл дампа в каталог, который
/// смонтирован тем же путём в Worker (см. docker-compose.yml) — Ingest читает
/// его оттуда как обычный dumpFilePath, не зная, что файл пришёл по HTTP.
/// Имя генерируется (GUID), исходное имя файла от клиента не используется —
/// не даём пользовательскому вводу попасть в путь на диске.
/// </summary>
public sealed class DumpUploadStorage
{
    private readonly string _directory;

    public DumpUploadStorage(string directory)
    {
        _directory = directory;
    }

    public async Task<string> SaveAsync(Stream content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"upload-{Guid.NewGuid():N}.dump");
        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken);
        return path.Replace('\\', '/');
    }
}
