using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DbAnonymizer.Integration.Tests;

[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<TestcontainersFixture>
{
}

// Один fixture на коллекцию: два тяжёлых контейнера стартуют один раз
// (meta — чистый, sandbox — с демо-данными), каждый тест начинает с ResetSandboxAsync.
public sealed class TestcontainersFixture : IAsyncLifetime
{
    // Builder уже ставит команду "postgres" по умолчанию — WithCommand ДОБАВЛЯЕТ
    // аргументы к ней (лидирующий "postgres" в аргументах дал бы postgres postgres).
    // max_wal_senders=0: PG16 по умолчанию держит 10 WAL-слайдеров, а wal_level=minimal
    // с ними несовместим.
    private readonly PostgreSqlContainer _meta = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly PostgreSqlContainer _sandbox = new PostgreSqlBuilder("postgres:16-alpine")
        .WithCommand("-c", "fsync=off", "-c", "max_wal_senders=0", "-c", "wal_level=minimal")
        .Build();

    public string MetaConnectionString => _meta.GetConnectionString();

    public string SandboxConnectionString => _sandbox.GetConnectionString();

    public Task InitializeAsync() => InitializeCoreAsync();

    public Task DisposeAsync() => DisposeCoreAsync();

    // Свежий sandbox: дроп демо-таблиц (вместе со следами Apply — _new/_orig),
    // повторное применение demo-schema.sql и детерминированный seed.
    public async Task ResetSandboxAsync(int userCount = 50, CancellationToken cancellationToken = default)
    {
        var sqlDir = Path.Combine(AppContext.BaseDirectory, "sql");
        await ExecuteSandboxAsync(
            "DROP TABLE IF EXISTS orders, orders_new, orders_orig, profiles, profiles_new, profiles_orig, users, users_new, users_orig CASCADE",
            cancellationToken);
        await ExecuteSandboxFileAsync(Path.Combine(sqlDir, "demo-schema.sql"), cancellationToken);
        await SeedSandboxAsync(userCount, cancellationToken);
    }

    public async Task ExecuteSandboxFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var script = await File.ReadAllTextAsync(path, cancellationToken);
        await ExecuteSandboxAsync(script, cancellationToken);
    }

    public async Task ExecuteSandboxAsync(string script, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(SandboxConnectionString);
        await conn.OpenAsync(cancellationToken);
        await SqlScriptRunner.ExecuteAsync(conn, script, cancellationToken);
    }

    public async Task<T?> ExecuteScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(SandboxConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T?)await cmd.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<long> GetRowCountAsync(string tableName, CancellationToken cancellationToken = default) =>
        await ExecuteScalarAsync<long>($"SELECT count(*) FROM \"public\".\"{tableName}\"", cancellationToken);

    public async Task<(long RowCount, IReadOnlyList<long> DistinctCounts)> GetTableStatsAsync(
        string tableName,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        var distinctParts = columns.Select(c => $"count(DISTINCT \"{c}\")");
        var sql = $"SELECT count(*), {string.Join(", ", distinctParts)} FROM \"public\".\"{tableName}\"";
        await using var conn = new NpgsqlConnection(SandboxConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var distinctCounts = new long[columns.Count];
        for (int i = 0; i < columns.Count; i++)
            distinctCounts[i] = reader.GetInt64(i + 1);
        return (reader.GetInt64(0), distinctCounts);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(
            _meta.StartAsync(cancellationToken),
            _sandbox.StartAsync(cancellationToken));
        await DeployFunctionsAsync(cancellationToken);
        await ResetSandboxAsync(cancellationToken: cancellationToken);
    }

    private async Task DisposeCoreAsync()
    {
        await _sandbox.DisposeAsync();
        await _meta.DisposeAsync();
    }

    // deploy.sql содержит \ir-инклуды (мета-команды psql) — Npgsql их не понимает.
    // Разворачиваем: преамбулу (schemas + extension) выполняем как есть,
    // каждый \ir-файл (utils/*, strategies/*) — отдельным выражением по файлу.
    private async Task DeployFunctionsAsync(CancellationToken cancellationToken = default)
    {
        var sqlDir = Path.Combine(AppContext.BaseDirectory, "sql");
        var deploy = await File.ReadAllTextAsync(Path.Combine(sqlDir, "deploy.sql"), cancellationToken);
        var includes = new List<string>();
        var mainScript = new StringBuilder();
        foreach (var line in deploy.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(@"\ir ", StringComparison.Ordinal))
                includes.Add(Path.Combine(sqlDir, trimmed[4..].Trim()));
            else
                mainScript.AppendLine(line);
        }

        await ExecuteSandboxAsync(mainScript.ToString(), cancellationToken);
        foreach (var include in includes)
            await ExecuteSandboxFileAsync(include, cancellationToken);
    }

    private async Task SeedSandboxAsync(int userCount, CancellationToken cancellationToken)
    {
        await ExecuteSandboxAsync(BuildSeedSql(userCount), cancellationToken);
    }

    // Детерминированный seed: 50 users / 50 profiles / 145 orders
    // (2 заказа для пользователей 1..5, 3 — для остальных; 5*2 + 45*3 = 145).
    private static string BuildSeedSql(int userCount) => $$"""
        INSERT INTO users (first_name, last_name, middle_name, email, phone, password_hash, birth_date, created_at)
        SELECT 'FirstName' || g, 'LastName' || g,
               CASE WHEN g % 3 = 0 THEN NULL ELSE 'MiddleName' || ((g % 30) + 1) END,
               'user' || g || '@example.com',
               '+7' || lpad((9000000000 + g)::TEXT, 10, '0'),
               '$2a$10$' || md5(g::TEXT),
               DATE '1990-01-01' + ((g * 30) % 600)::INT,
               TIMESTAMPTZ '2024-01-01 00:00:00+00' + (g * 3600)::INT * INTERVAL '1 second'
        FROM generate_series(1, {{userCount}}) AS g;

        INSERT INTO profiles (user_id, address, passport, inn, metadata)
        SELECT id,
               'Street ' || (id % 20) || ', bld ' || id,
               '1234 ' || lpad(id::TEXT, 6, '0'),
               lpad((id * 12345)::TEXT, 12, '0'),
               jsonb_build_object('source', 'integration-test', 'uid', id)
        FROM users;

        INSERT INTO orders (user_id, user_email, card_number, amount, comment, created_at)
        SELECT u.id, u.email,
               '4539' || lpad(((u.id::BIGINT * 97 + k * 41) * 1000000 + k * 17)::TEXT, 12, '0'),
               (u.id * 1000 + k * 37)::NUMERIC / 100,
               'order comment ' || u.id || '-' || k,
               TIMESTAMPTZ '2024-06-01 00:00:00+00' + (u.id * 3600 + k * 60)::INT * INTERVAL '1 second'
        FROM users u
        CROSS JOIN LATERAL generate_series(1, CASE WHEN u.id <= 5 THEN 2 ELSE 3 END) AS k;
        """;
}
