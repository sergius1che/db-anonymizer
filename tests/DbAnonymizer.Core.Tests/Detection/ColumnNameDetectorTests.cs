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

        // Создать тестовую таблицу: email — single-column UNIQUE (сама по себе
        // уникальна); phone+card_number — composite PRIMARY KEY (уникальна только
        // комбинация, не каждая колонка по отдельности — обе всё равно матчатся
        // детектором по имени, чтобы попасть в candidates)
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            CREATE TABLE public.users (
                first_name VARCHAR(100),
                email VARCHAR(255) UNIQUE,
                note TEXT
            );
            CREATE TABLE public.order_items (
                phone VARCHAR(20),
                card_number VARCHAR(20),
                PRIMARY KEY (phone, card_number)
            );
            """, conn);
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

    [Fact]
    public async Task Detect_SingleColumnUniqueConstraint_MarksColumnUnique()
    {
        var catalog = DetectorCatalog.Load();
        var detector = new ColumnNameDetector(catalog);
        var candidates = await detector.DetectAsync(_connectionString);

        var email = Assert.Single(candidates, c => c.TableName == "users" && c.ColumnName == "email");
        Assert.True(email.IsUnique);
    }

    [Fact]
    public async Task Detect_CompositePrimaryKeyColumns_AreNotMarkedUnique()
    {
        // Регрессия: composite PK/UNIQUE (несколько колонок в одном constraint)
        // раньше помечал КАЖДУЮ участвующую колонку IsUnique=true — false FAIL
        // в Verify на многоколоночных ключах, где ни одна колонка сама по себе
        // не обязана быть уникальной
        var catalog = DetectorCatalog.Load();
        var detector = new ColumnNameDetector(catalog);
        var candidates = await detector.DetectAsync(_connectionString);

        var phone = Assert.Single(candidates, c => c.TableName == "order_items" && c.ColumnName == "phone");
        var cardNumber = Assert.Single(
            candidates, c => c.TableName == "order_items" && c.ColumnName == "card_number");
        Assert.False(phone.IsUnique);
        Assert.False(cardNumber.IsUnique);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
