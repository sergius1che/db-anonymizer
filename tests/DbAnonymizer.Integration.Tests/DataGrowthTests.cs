using Npgsql;

namespace DbAnonymizer.Integration.Tests;

[Collection("Integration")]
public sealed class DataGrowthTests
{
    private const string Seed = "seed-growth";

    private static readonly (string Table, string Key, string Column)[] SnapshotColumns =
    [
        ("users", "id", "first_name"),
        ("users", "id", "last_name"),
        ("users", "id", "middle_name"),
        ("users", "id", "email"),
        ("users", "id", "phone"),
        ("users", "id", "password_hash"),
        ("users", "id", "birth_date"),
        ("profiles", "id", "address"),
        ("profiles", "id", "passport"),
        ("profiles", "id", "inn"),
        ("orders", "comment", "user_email"),
        ("orders", "comment", "card_number"),
    ];

    private readonly TestcontainersFixture _fixture;
    private readonly PipelineRunner _runner;

    public DataGrowthTests(TestcontainersFixture fixture)
    {
        _fixture = fixture;
        _runner = new PipelineRunner(fixture);
    }

    // «Новые строки не ломают старые»: fake/derive/format/shift/redact/secret —
    // чистые функции от (seed, значение), не от номера строки. После добавления
    // 100 новых пользователей (свежий sandbox, тот же seed/план) значения
    // существующих строк должны совпасть с базовым прогоном байт-в-байт.
    [Fact]
    public async Task Growth_Adds100Users_ExistingRowValuesStayIdentical()
    {
        // Базовый прогон
        await _fixture.ResetSandboxAsync();
        var baseline = await _runner.RunDemoPipelineAsync(Seed);
        ReportAsserts.AllChecksPassed(baseline.Report);
        var before = await SnapshotAllMarkedAsync();

        // Рост: те же оригинальные данные + 100 новых пользователей
        // (в т.ч. их profiles и orders) — не double-apply на тот же sandbox.
        await _fixture.ResetSandboxAsync(150);
        var grown = await _runner.RunDemoPipelineAsync(Seed);
        var after = await SnapshotAllMarkedAsync();

        // Grown-отчёт ассертится ПОЛНОСТЬЮ, чтобы regression не проскользнула молча:
        // Verify на выросших данных (150 пользователей) обязан пройти целиком —
        // leak, row-count, unique и diversity по всем 12 колонкам.
        //
        // Почему diversity на grown-данных проходит, хотя пул (500 значений) меньше
        // числа distinct (150): VerifyPhase считает count(DISTINCT APPLIED-значения),
        // а Fake дописывает base36-суффикс (SuffixWidth=6, 36^6 комбинаций) — коллизии
        // практически невозможны, ratio ≈ 1.0 даже при коллизиях в индексе пула.
        // Замер hmac_mod без суффикса для seed-growth: first_name 135/150 = 0.90,
        // last_name 119/150 = 0.79 — с суффиксом оба ~1.0. Ожидание ревью
        // «diversity обязан фейлиться (~0.86)» эмпирически не подтвердилось,
        // поэтому здесь фейл НЕ ожидается, а ассертится полный проход.
        ReportAsserts.AllChecksPassed(grown.Report);

        Assert.Equal(150, await _fixture.GetRowCountAsync("users"));

        // Ключи baseline ⊆ ключи grown (те же заказы пользователей 1..50) —
        // сверяем значения по ключам базового прогона
        foreach (var key in before.Keys)
            Assert.Equal(before[key], after[key]);
    }

    private async Task<IReadOnlyDictionary<string, string?>> SnapshotAllMarkedAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = new Dictionary<string, string?>();
        await using var conn = new NpgsqlConnection(_fixture.SandboxConnectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var (table, key, column) in SnapshotColumns)
        {
            var sql = $"""
                SELECT "{key}"::TEXT, "{column}"::TEXT
                FROM "public"."{table}"
                ORDER BY "{key}"
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rowKey = reader.GetString(0);
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                snapshot[$"{table}.{key}={rowKey}.{column}"] = value;
            }
        }
        return snapshot;
    }
}
