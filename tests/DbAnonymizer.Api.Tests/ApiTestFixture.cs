// ApiTestFixture.cs
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace DbAnonymizer.Api.Tests;

public sealed class ApiTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly object _sync = new();
    private bool _started;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:PgMeta", _container.GetConnectionString());
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
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }
}
