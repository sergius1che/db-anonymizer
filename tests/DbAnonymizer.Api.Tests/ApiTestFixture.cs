// ApiTestFixture.cs
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace DbAnonymizer.Api.Tests;

public sealed class ApiTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly string _uploadsDirectory =
        Path.Combine(Path.GetTempPath(), "dbanonymizer-test-uploads-" + Guid.NewGuid().ToString("N"));
    private readonly object _sync = new();
    private bool _started;

    public string UploadsDirectory => _uploadsDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:PgMeta", _container.GetConnectionString());
        // Каталог загрузок — во временную папку теста, не в репозиторий (см. DumpUploadStorage)
        builder.UseSetting("Uploads:Directory", _uploadsDirectory);
    }

    public Task InitializeAsync()
    {
        EnsureStarted();
        return Task.CompletedTask;
    }

    private void EnsureStarted()
    {
        lock (_sync)
        {
            if (!_started)
            {
                _container.StartAsync().GetAwaiter().GetResult();
                _started = true;
            }
        }
    }

    public new async Task DisposeAsync()
    {
        if (Directory.Exists(_uploadsDirectory))
            Directory.Delete(_uploadsDirectory, recursive: true);
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }
}
