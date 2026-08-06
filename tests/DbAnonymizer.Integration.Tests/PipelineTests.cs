using System.Text.RegularExpressions;
using DbAnonymizer.Core.Detection;
using DbAnonymizer.Core.Models;
using DbAnonymizer.Core.Planning;

namespace DbAnonymizer.Integration.Tests;

[Collection("Integration")]
public sealed class PipelineTests
{
    private const string PipelineSeed = "pipeline-seed";

    private readonly TestcontainersFixture _fixture;
    private readonly PipelineRunner _runner;

    public PipelineTests(TestcontainersFixture fixture)
    {
        _fixture = fixture;
        _runner = new PipelineRunner(fixture);
    }

    [Fact]
    public async Task FullPipeline_IntrospectThroughVerify_AllChecksPassed()
    {
        await _fixture.ResetSandboxAsync();
        Assert.Equal(50, await _fixture.GetRowCountAsync("users"));
        Assert.Equal(50, await _fixture.GetRowCountAsync("profiles"));
        Assert.Equal(145, await _fixture.GetRowCountAsync("orders"));

        // 1. Интроспекция
        var engine = new IntrospectionEngine(
            new IDetector[] { new ColumnNameDetector(DetectorCatalog.Load()) });
        var candidates = await engine.RunAsync(_fixture.SandboxConnectionString);

        Assert.Contains(candidates, c =>
            c.TableName == "users" && c.ColumnName == "first_name"
            && c.DetectedType == SemanticType.FirstName);
        Assert.Contains(candidates, c =>
            c.TableName == "profiles" && c.ColumnName == "passport"
            && c.DetectedType == SemanticType.PassportNumber);
        Assert.Contains(candidates, c =>
            c.TableName == "orders" && c.ColumnName == "user_email"
            && c.DetectedType == SemanticType.Email);
        Assert.DoesNotContain(candidates, c => c.ColumnName is "amount" or "created_at" or "comment");

        // 2..7. draft → validate → freeze (pg-meta) → enrich (пулы без LLM) → apply → verify
        var result = await _runner.RunDemoPipelineAsync(PipelineSeed);

        ReportAsserts.AllChecksPassed(result.Report);
        Assert.Contains(result.Report.Checks, c => c.Name == "Unique: public.users.email" && c.Passed);
        Assert.Contains(result.Report.Checks, c =>
            c.Name == "Diversity: public.users.first_name" && c.Passed);
        Assert.Contains(result.Report.Checks, c => c.Name == "Row count: public.orders" && c.Passed);

        // Значения реально заменены
        Assert.NotEqual(
            "FirstName1",
            await _fixture.ExecuteScalarAsync<string>("SELECT first_name FROM users WHERE id = 1"));
        var email = await _fixture.ExecuteScalarAsync<string>("SELECT email FROM users WHERE id = 1");
        Assert.NotEqual("user1@example.com", email);
        Assert.Matches("^[a-z0-9.]+@mail\\.invalid$", email);

        // Количество строк сохранилось
        Assert.Equal(50, await _fixture.GetRowCountAsync("users"));
        Assert.Equal(145, await _fixture.GetRowCountAsync("orders"));
    }

    [Fact]
    public void Catalog_LoadsExactly24Rules_AllValid()
    {
        var rules = DetectorCatalog.Load().GetRules();

        Assert.Equal(24, rules.Count);
        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Pattern));
            Assert.False(string.IsNullOrWhiteSpace(rule.SemanticType));
            Assert.False(string.IsNullOrWhiteSpace(rule.Confidence));
            _ = new Regex(rule.Pattern, RegexOptions.IgnoreCase); // не должно бросить
            Assert.True(Enum.TryParse<SemanticType>(rule.SemanticType, out _));
            Assert.True(Enum.TryParse<Confidence>(rule.Confidence, out _));
        }
    }

    [Fact]
    public async Task JsonbPath_ColumnInPlan_AppliesAndVerifies()
    {
        await _fixture.ResetSandboxAsync();

        // demo-schema jsonb-путей не имеет — добавляем колонку в тесте (вне файла схемы).
        // Телефона в profiles нет — в jsonb кладём inn (12 цифр, все строки непустые).
        await _fixture.ExecuteSandboxAsync("""
            ALTER TABLE profiles ADD COLUMN customer JSONB;
            UPDATE profiles SET customer = jsonb_build_object('customer', jsonb_build_object('phone', inn));
            """);

        var profilesRowCount = await _fixture.GetRowCountAsync("profiles");
        var jsonbSpec = new ColumnSpec
        {
            SchemaName = "public",
            TableName = "profiles",
            ColumnName = "customer",
            DataType = "jsonb",
            SemanticType = SemanticType.Phone,
            Strategy = StrategyType.Fake,
            IsJsonPath = true,
            JsonPath = "$.customer.phone",
            RowCount = profilesRowCount,
            DistinctCount = 0 // Diversity ratio = 1.0 (DistinctCount==0 → pass)
        };

        var result = await _runner.RunDemoPipelineAsync(
            "jsonb-seed", extraColumns: new[] { jsonbSpec });

        ReportAsserts.AllChecksPassed(result.Report);
        Assert.Contains(result.Report.Checks, c =>
            c.Name == "Leak: public.profiles.customer@$.customer.phone" && c.Passed);

        // Значение по пути реально заменено (original: inn пользователя id=1)
        var replaced = await _fixture.ExecuteScalarAsync<string>(
            "SELECT customer#>>'{customer,phone}' FROM profiles WHERE id = 1");
        Assert.NotNull(replaced);
        Assert.NotEqual("000000000123", replaced);
        Assert.Matches("^\\+7-[0-9-]+ [0-9a-z]{6}$", replaced);
    }

    [Fact]
    public async Task DeriveStrategy_PlanColumn_AppliesAndVerifies()
    {
        // Регрессия парковки 8.2: derive.sql вызывает anonymize.fake с компонентами
        // 'firstname'/'lastname' (конвенция SemanticType.ToString().ToLowerInvariant()).
        // До фикса Apply падал: relation materials.pool_first_name does not exist.
        await _fixture.ResetSandboxAsync();

        var ordersRowCount = await _fixture.GetRowCountAsync("orders");
        var spec = new ColumnSpec
        {
            SchemaName = "public",
            TableName = "orders",
            ColumnName = "user_email",
            DataType = "character varying",
            SemanticType = SemanticType.Email,
            Strategy = StrategyType.Derive,
            RowCount = ordersRowCount,
            DistinctCount = 0
        };

        var result = await _runner.RunAsync("derive-seed", new[] { spec });

        ReportAsserts.AllChecksPassed(result.Report);
        var email = await _fixture.ExecuteScalarAsync<string>(
            "SELECT user_email FROM orders WHERE id = 1");
        Assert.NotNull(email);
        Assert.Matches("^[a-z0-9.]+@mail\\.invalid$", email);
    }

    [Fact]
    public async Task DeriveStrategy_SameTableAsFakeNames_LocalPartMatchesFakeFirstAndLastName()
    {
        // Регрессия: derive.sql раньше вызывал anonymize.fake(seed, original_email, ...) —
        // фейковое имя в email считалось от ORIGINAL EMAIL, а не от original first_name/
        // last_name строки, поэтому не совпадало с тем, что показывают Fake(FirstName)/
        // Fake(LastName) колонки той же строки. Фикс — Derive берёт вход из реальных
        // first_name/last_name колонок ТОЙ ЖЕ таблицы, если они есть в плане.
        await _fixture.ResetSandboxAsync();

        var usersRowCount = await _fixture.GetRowCountAsync("users");
        var columns = new[]
        {
            new ColumnSpec
            {
                SchemaName = "public",
                TableName = "users",
                ColumnName = "first_name",
                DataType = "character varying",
                SemanticType = SemanticType.FirstName,
                Strategy = StrategyType.Fake,
                RowCount = usersRowCount,
                DistinctCount = 0
            },
            new ColumnSpec
            {
                SchemaName = "public",
                TableName = "users",
                ColumnName = "last_name",
                DataType = "character varying",
                SemanticType = SemanticType.LastName,
                Strategy = StrategyType.Fake,
                RowCount = usersRowCount,
                DistinctCount = 0
            },
            new ColumnSpec
            {
                SchemaName = "public",
                TableName = "users",
                ColumnName = "email",
                DataType = "character varying",
                SemanticType = SemanticType.Email,
                Strategy = StrategyType.Derive,
                IsUnique = true,
                RowCount = usersRowCount,
                DistinctCount = usersRowCount
            }
        };

        var result = await _runner.RunAsync("derive-consistency-seed", columns);

        ReportAsserts.AllChecksPassed(result.Report);
        var firstName = await _fixture.ExecuteScalarAsync<string>(
            "SELECT first_name FROM users WHERE id = 1");
        var lastName = await _fixture.ExecuteScalarAsync<string>(
            "SELECT last_name FROM users WHERE id = 1");
        var email = await _fixture.ExecuteScalarAsync<string>(
            "SELECT email FROM users WHERE id = 1");
        // Fake(FirstName)/Fake(LastName) сами дописывают base36-суффикс через
        // plan.SuffixWidth ("FakeFirst0058 iqmc0y") — derive берёт то же фейковое
        // имя без суффикса (suffix_width=0 в BuildFakeNameCall), сравниваем базовую часть
        var firstNameBase = firstName!.Split(' ')[0];
        var lastNameBase = lastName!.Split(' ')[0];
        var expectedLocalPart = $"{firstNameBase}.{lastNameBase}".ToLowerInvariant();
        Assert.StartsWith(expectedLocalPart, email);
        Assert.Matches("^[a-z0-9.]+@mail\\.invalid$", email);
    }

    [Fact]
    public async Task FormatStrategy_PhoneColumn_ProducesValidRussianPhoneShape()
    {
        // Регрессия: seed генерирует телефон по шаблону "+7###-###-##-##" — сама '7'
        // тоже цифра, поэтому regexp_replace(original,'[^0-9]','','g') давал 11 цифр
        // при маске "+7 000 000-00-00" (10 нулей). Лишняя 11-я цифра дописывалась
        // как есть ПОСЛЕ маски, ломая длину и вид номера.
        await _fixture.ResetSandboxAsync();

        var result = await _runner.RunDemoPipelineAsync("format-phone-seed");

        ReportAsserts.AllChecksPassed(result.Report);
        var phone = await _fixture.ExecuteScalarAsync<string>(
            "SELECT phone FROM users WHERE id = 1");
        Assert.NotNull(phone);
        Assert.Matches(@"^\+7 \d{3} \d{3}-\d{2}-\d{2}$", phone);
    }

    [Fact]
    public async Task FormatStrategy_13DigitCardNumber_ProducesCorrectlyShapedOutput()
    {
        // Регрессия: materials.format_templates раньше держал одну маску на type
        // (16 нулей для карты). Bogus.Finance.CreditCardNumber(Visa) иногда даёт
        // 13-значный номер — под 16-значную маску он не подходил: часть символов
        // маски оставалась незаполненной (найдено живым прогоном demo, id=4:
        // "9589 0595 9636" вместо ожидаемых 19 символов). Фикс — несколько строк
        // на type в materials.format_templates, точный подбор по digit_count.
        await _fixture.ResetSandboxAsync();
        await _fixture.ExecuteSandboxAsync(
            "UPDATE orders SET card_number = '4029163411718' WHERE id = 1");

        var result = await _runner.RunDemoPipelineAsync("format-13digit-card-seed");

        ReportAsserts.AllChecksPassed(result.Report);
        var cardNumber = await _fixture.ExecuteScalarAsync<string>(
            "SELECT card_number FROM orders WHERE id = 1");
        Assert.NotNull(cardNumber);
        Assert.Matches(@"^\d{4} \d{4} \d{5}$", cardNumber);
        Assert.Equal(13, cardNumber!.Count(char.IsDigit));
    }

    [Fact]
    public async Task Verify_DiversityBelowTolerance_ReportsFailedCheck()
    {
        await _fixture.ResetSandboxAsync();

        var columns = await _runner.BuildDemoColumnsAsync();
        var doctored = columns
            .Select(c => c.TableName == "users" && c.ColumnName == "first_name"
                ? WithCounts(c, distinctCount: 1000)
                : c)
            .ToList();

        var result = await _runner.RunAsync("negative-seed", doctored);

        Assert.False(result.Report.AllChecksPassed);
        var check = Assert.Single(
            result.Report.Checks, c => c.Name == "Diversity: public.users.first_name");
        Assert.False(check.Passed);
    }

    [Fact]
    public async Task Verify_RowCountMismatch_ReportsFailedCheck()
    {
        await _fixture.ResetSandboxAsync();

        var columns = await _runner.BuildDemoColumnsAsync();
        var doctored = columns
            .Select(c => c.TableName == "users" ? WithCounts(c, rowCount: 999) : c)
            .ToList();

        var result = await _runner.RunAsync("negative-seed", doctored);

        Assert.False(result.Report.AllChecksPassed);
        var check = Assert.Single(
            result.Report.Checks, c => c.Name == "Row count: public.users");
        Assert.False(check.Passed);
    }

    [Fact]
    public async Task Freeze_PlanWithoutColumns_ThrowsExactValidationMessage()
    {
        var repository = new PlanRepository(_fixture.MetaConnectionString);
        var freezer = new PlanFreezer(repository, poolSize: 500);
        var draft = new Plan
        {
            Id = "freeze-negative-empty",
            SourceHash = "x",
            Seed = "seed",
            Version = "1",
            ContentHash = string.Empty,
            MaterialsHash = string.Empty
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => freezer.FreezeAsync(draft, "materials"));

        Assert.Equal(
            "Plan validation failed: Plan must have at least one column", ex.Message);
    }

    [Fact]
    public async Task Freeze_SensitiveColumnWithKeep_ThrowsExactValidationMessage()
    {
        var repository = new PlanRepository(_fixture.MetaConnectionString);
        var freezer = new PlanFreezer(repository, poolSize: 500);
        var draft = new Plan
        {
            Id = "freeze-negative-keep",
            SourceHash = "x",
            Seed = "seed",
            Version = "1",
            ContentHash = string.Empty,
            MaterialsHash = string.Empty,
            Columns =
            [
                new ColumnSpec
                {
                    SchemaName = "public",
                    TableName = "users",
                    ColumnName = "email",
                    SemanticType = SemanticType.Email,
                    Strategy = StrategyType.Keep
                }
            ]
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => freezer.FreezeAsync(draft, "materials"));

        Assert.Equal(
            "Plan validation failed: Column public.users.email is detected as Email but has no explicit strategy",
            ex.Message);
    }

    [Fact]
    public async Task Introspect_EmptySandbox_ReturnsEmptyCandidateList()
    {
        await _fixture.ResetSandboxAsync();
        await _fixture.ExecuteSandboxAsync(
            "DROP TABLE IF EXISTS orders, profiles, users CASCADE");

        var engine = new IntrospectionEngine(
            new IDetector[] { new ColumnNameDetector(DetectorCatalog.Load()) });
        var candidates = await engine.RunAsync(_fixture.SandboxConnectionString);

        Assert.Empty(candidates);

        // Вернуть sandbox в демо-состояние для остальных тестов
        await _fixture.ResetSandboxAsync();
    }

    private static ColumnSpec WithCounts(
        ColumnSpec spec,
        long? rowCount = null,
        long? distinctCount = null) => new()
    {
        SchemaName = spec.SchemaName,
        TableName = spec.TableName,
        ColumnName = spec.ColumnName,
        DataType = spec.DataType,
        IsUnique = spec.IsUnique,
        SemanticType = spec.SemanticType,
        Strategy = spec.Strategy,
        IsJsonPath = spec.IsJsonPath,
        JsonPath = spec.JsonPath,
        RowCount = rowCount ?? spec.RowCount,
        DistinctCount = distinctCount ?? spec.DistinctCount
    };
}
