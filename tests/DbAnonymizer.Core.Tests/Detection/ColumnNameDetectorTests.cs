using System;
using System.Threading.Tasks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using DbAnonymizer.Core.Detection;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Tests.Detection;

public sealed class ColumnNameDetectorTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        // Создать тестовую таблицу
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "CREATE TABLE public.users (first_name VARCHAR(100), email VARCHAR(255), note TEXT)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Detect_ShouldFindEmailAndFirstName()
    {
        var catalog = DetectorCatalog.Load();
        var detector = new ColumnNameDetector(catalog);
        var candidates = await detector.DetectAsync(_connectionString);

        Assert.Contains(candidates, c => c.ColumnName == "email" && c.Confidence == Confidence.High);
        Assert.Contains(candidates, c => c.ColumnName == "first_name" && c.Confidence == Confidence.High);
        Assert.DoesNotContain(candidates, c => c.ColumnName == "note");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
