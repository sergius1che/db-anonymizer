using Bogus;
using Bogus.DataSets;
using Npgsql;

var connectionString = args.Length > 0
    ? args[0]
    : "Host=localhost;Port=5433;Database=db_anonymizer_sandbox;Username=anonymizer;Password=anonymizer";

var userCount = args.Length > 1 ? int.Parse(args[1]) : 500;

await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();

// Применить схему
var schema = await File.ReadAllTextAsync(
    Path.Combine(AppContext.BaseDirectory, "Schema", "demo-schema.sql"));
await using var cmd = new NpgsqlCommand(schema, conn);
await cmd.ExecuteNonQueryAsync();

// Faker-ы для чувствительных данных
var userFaker = new Faker<FakeUser>()
    .RuleFor(u => u.FirstName, f => f.Name.FirstName())
    .RuleFor(u => u.LastName, f => f.Name.LastName())
    .RuleFor(u => u.MiddleName, f => f.Name.FirstName()) // отчество генерим как ещё одно имя
    .RuleFor(u => u.Email, (f, u) => f.Internet.Email(u.FirstName, u.LastName))
    .RuleFor(u => u.Phone, f => f.Phone.PhoneNumber("+7###-###-##-##"))
    .RuleFor(u => u.PasswordHash, f => BCryptHash(f.Internet.Password()))
    .RuleFor(u => u.BirthDate, f => f.Date.Past(50, DateTime.Today.AddYears(-18)))
    .RuleFor(u => u.CreatedAt, f => f.Date.Past(3));

// Генерация пользователей
for (int i = 0; i < userCount; i++)
{
    var user = userFaker.Generate();
    await using var insertUser = new NpgsqlCommand(
        @"INSERT INTO users (first_name, last_name, middle_name, email, phone, password_hash, birth_date, created_at)
          VALUES (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8) RETURNING id", conn);
    insertUser.Parameters.AddWithValue("p1", user.FirstName);
    insertUser.Parameters.AddWithValue("p2", user.LastName);
    insertUser.Parameters.AddWithValue("p3", user.MiddleName);
    insertUser.Parameters.AddWithValue("p4", user.Email);
    insertUser.Parameters.AddWithValue("p5", user.Phone);
    insertUser.Parameters.AddWithValue("p6", user.PasswordHash);
    insertUser.Parameters.AddWithValue("p7", user.BirthDate);
    insertUser.Parameters.AddWithValue("p8", user.CreatedAt);
    var userId = Convert.ToInt32(await insertUser.ExecuteScalarAsync());

    // profile
    var fake = new Faker();
    var metadata = $$"""{"source": "{{fake.Internet.UserAgent()}}", "verified": true}""";
    await using var insertProfile = new NpgsqlCommand(
        @"INSERT INTO profiles (user_id, address, passport, inn, metadata)
          VALUES (@p1, @p2, @p3, @p4, @p5::jsonb)", conn);
    insertProfile.Parameters.AddWithValue("p1", userId);
    insertProfile.Parameters.AddWithValue("p2", fake.Address.FullAddress());
    insertProfile.Parameters.AddWithValue("p3", $"{fake.Random.Int(1000, 9999)} {fake.Random.Int(100000, 999999)}");
    insertProfile.Parameters.AddWithValue("p4", fake.Random.Replace("############"));
    insertProfile.Parameters.AddWithValue("p5", metadata);
    await insertProfile.ExecuteNonQueryAsync();

    // 0-5 orders per user
    var orderCount = fake.Random.Int(0, 5);
    for (int j = 0; j < orderCount; j++)
    {
        await using var insertOrder = new NpgsqlCommand(
            @"INSERT INTO orders (user_id, user_email, card_number, amount, comment, created_at)
              VALUES (@p1, @p2, @p3, @p4, @p5, @p6)", conn);
        insertOrder.Parameters.AddWithValue("p1", userId);
        insertOrder.Parameters.AddWithValue("p2", user.Email);
        insertOrder.Parameters.AddWithValue("p3", fake.Finance.CreditCardNumber(CardType.Visa));
        insertOrder.Parameters.AddWithValue("p4", fake.Finance.Amount(100, 50000));
        insertOrder.Parameters.AddWithValue("p5", fake.Lorem.Sentence());
        insertOrder.Parameters.AddWithValue("p6", fake.Date.Past(1));
        await insertOrder.ExecuteNonQueryAsync();
    }
}

Console.WriteLine($"Generated {userCount} users with profiles and orders.");

static string BCryptHash(string input)
{
    // Простая эмуляция bcrypt-хэша фиксированной длины 60
    var bytes = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(input));
    return "$2a$10$" + Convert.ToBase64String(bytes) + Convert.ToBase64String(bytes)[..9];
}

sealed record FakeUser
{
    public string FirstName { get; init; } = default!;
    public string LastName { get; init; } = default!;
    public string MiddleName { get; init; } = default!;
    public string Email { get; init; } = default!;
    public string Phone { get; init; } = default!;
    public string PasswordHash { get; init; } = default!;
    public DateTime BirthDate { get; init; }
    public DateTime CreatedAt { get; init; }
}
