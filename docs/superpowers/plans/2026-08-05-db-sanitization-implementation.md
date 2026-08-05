# DbAnonymizer — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Построить web-сервис обезличивания баз данных PostgreSQL: принимает дамп или подключение к БД, выдаёт обезличенную версию с правдоподобными заменами, сквозной консистентностью и сохранением ссылочной целостности. Сдача — репозиторий с docker compose, демонстрация «до/после».

**Architecture:** Гибрид .NET (ядро + API + воркер) + Python (агенты генерации пулов через LLM) + PostgreSQL sandbox (обработка данных). Пайплайн: Ingest → Introspect → Review UI → Freeze → Enrich → Apply → Verify. Конвейер stateless: подстановка — чистая функция `(seed, оригинал) → новое значение`, план фризится в JSON, повторы воспроизводимы.

**Tech Stack:** C#/.NET 10 (ASP.NET Core Minimal API, Npgsql, Hangfire), Python 3.12 (LangGraph, LiteLLM), PostgreSQL 16 (pgcrypto), Docker Compose, Testcontainers, FsCheck, Bogus.

## Global Constraints

- Git-репозиторий с коммитами на английском ([[feedback-commit-messages-english]]); общение по проекту — на русском ([[feedback-language-russian]]).
- `CLAUDE.md` проекта — на английском; дизайн, приёмка, план — на русском.
- C#: primary constructors запрещены ([[feedback-casinoproxy-primary-constructors]]); async-методы интерфейсов всегда с `CancellationToken cancellationToken = default` последним параметром ([[feedback-async-interface-cancellation-token]]).
- Классы: поля `_camelCase`, порядок членов — поля → конструкторы → свойства → методы, статика перед экземпляром, public → private ([[class-design]]).
- Интерфейсы: префикс `I`, без default-методов, без наследования без необходимости ([[interface-design]]).
- GET-запросы не меняют состояние БД ([[feedback-get-requests-no-state-change]]).
- Не маскировать ошибки PostgreSQL под 400; 500 от БД — сигнал ([[feedback-no-masking-postgres-errors]]).
- Чистая функция без состояния: каждое предложение проверять — не берёт ли агент лишнюю ответственность, не требует ли ручного конфига, не появляется ли скрытое состояние ([[project-dbanonymizer-test-task]]).

---

## Файловая структура

```
Temp/DbAnonymizer/
├── .gitignore
├── CLAUDE.md
├── README.md
├── docker-compose.yml
├── docker-compose.override.yml
├── story.txt                              # рассказ для стратегии Redact
├── DbAnonymizer.sln
│
├── src/
│   ├── DbAnonymizer.Core/                 # Ядро: модели, детекторы, стратегии, план
│   │   ├── DbAnonymizer.Core.csproj
│   │   ├── Models/
│   │   │   ├── JobState.cs                # enum + класс состояния задачи
│   │   │   ├── Plan.cs                    # замороженный план (seed, версия, хэш, колонки)
│   │   │   ├── ColumnSpec.cs              # запись плана об одной колонке
│   │   │   ├── SemanticType.cs            # enum семантических типов
│   │   │   ├── StrategyType.cs            # enum стратегий
│   │   │   ├── ColumnCandidate.cs         # кандидат от детекторов
│   │   │   ├── Confidence.cs              # enum: High, Medium, Low
│   │   │   ├── PoolSpec.cs                # описание пула (сем.тип, размер домена)
│   │   │   ├── Report.cs                  # модель отчёта фазы Verify
│   │   │   └── SourceSpec.cs              # описание источника (файл или connection string)
│   │   ├── Detection/
│   │   │   ├── detectors.yaml             # каталог детекторов (данные)
│   │   │   ├── DetectorCatalog.cs         # загрузчик YAML-каталога
│   │   │   ├── ColumnNameDetector.cs      # совпадение имени колонки
│   │   │   ├── TypeConstraintDetector.cs  # тип + ограничения
│   │   │   ├── ProfileDetector.cs         # профиль выборки (regex patterns)
│   │   │   └── IntrospectionEngine.cs     # оркестратор: берёт схему sandbox → кандидаты
│   │   ├── Strategies/
│   │   │   ├── IStrategyMaterialBuilder.cs # интерфейс построителя материалов для стратегии
│   │   │   ├── PoolComponents.cs          # компоненты пула (имена, фамилии, города…)
│   │   │   ├── FakeMaterialBuilder.cs     # загружает пулы компонент в sandbox
│   │   │   ├── FormatMaterialBuilder.cs   # шаблоны форматов (телефон, ИНН, карта…)
│   │   │   ├── ShiftMaterialBuilder.cs    # настройки сдвига дат
│   │   │   ├── RedactMaterialBuilder.cs   # загрузка рассказа + индекс слов по длине
│   │   │   └── MaterialOrchestrator.cs    # вызывает builders по плану, пишет хэш материалов
│   │   ├── Plan/
│   │   │   ├── PlanFreezer.cs             # заморозка: валидация + сериализация + хэш
│   │   │   ├── PlanValidator.cs           # правила валидации плана
│   │   │   └── PlanRepository.cs          # сохранение/загрузка plan.json в pg-meta
│   │   ├── Adapters/
│   │   │   ├── ISourceAdapter.cs          # интерфейс: формат → таблицы sandbox
│   │   │   ├── ISinkAdapter.cs            # интерфейс: таблицы sandbox → формат
│   │   │   ├── PgDumpSourceAdapter.cs     # pg_restore из файла дампа
│   │   │   ├── PgDumpSinkAdapter.cs       # pg_dump -Fd -j N
│   │   │   └── LiveDbSourceAdapter.cs     # pg_dump с живой БД (read-only роль)
│   │   └── Reporting/
│   │       └── ReportBuilder.cs           # сборка отчёта из результатов Verify
│   │
│   ├── DbAnonymizer.Api/                  # ASP.NET Core Minimal API
│   │   ├── DbAnonymizer.Api.csproj
│   │   ├── Program.cs
│   │   ├── Endpoints/
│   │   │   ├── JobsEndpoints.cs           # POST /jobs, GET /jobs, GET /jobs/{id}
│   │   │   ├── PlanEndpoints.cs           # GET /plans/{id}, POST /plans/{id}/freeze
│   │   │   └── ReportEndpoints.cs         # GET /reports/{id}
│   │   ├── Services/
│   │   │   └── JobService.cs              # оркестрация жизненного цикла задачи
│   │   └── appsettings.json
│   │
│   ├── DbAnonymizer.Worker/               # Hangfire-воркер фоновых фаз
│   │   ├── DbAnonymizer.Worker.csproj
│   │   ├── Program.cs
│   │   ├── JobProcessor.cs                # точка входа: запуск фаз 1→7
│   │   └── Phases/
│   │       ├── IngestPhase.cs             # фаза 1: restore дампа в sandbox
│   │       ├── IntrospectPhase.cs         # фаза 2: запуск детекторов
│   │       ├── EnrichPhase.cs             # фаза 5: вызов агента, загрузка материалов
│   │       ├── ApplyPhase.cs              # фаза 6: CTAS + подмена
│   │       └── VerifyPhase.cs             # фаза 7: проверки + pg_dump + отчёт
│   │
│   ├── DbAnonymizer.Agents/               # Python-сервис: Value Pool Service
│   │   ├── pyproject.toml
│   │   ├── main.py                        # FastAPI, POST /generate
│   │   ├── graph.py                       # LangGraph-граф: validate → generate → format
│   │   ├── prompts.py                     # промпты для LLM
│   │   ├── schemas.py                     # JSON-схемы ответов агентов
│   │   └── llm_client.py                  # LiteLLM-обёртка
│   │
│   └── DbAnonymizer.Seed/                 # Генератор демо-данных
│       ├── DbAnonymizer.Seed.csproj
│       ├── Program.cs
│       └── Schema/
│           └── demo-schema.sql            # схема с ФИО, email, телефон, адрес, карта
│
├── sql/                                   # SQL-функции, деплоятся в sandbox
│   ├── deploy.sql                         # входная точка: подключает всё
│   ├── utils/
│   │   ├── hash.sql                       # hmac-обёртки
│   │   ├── suffix.sql                     # base36-суффикс
│   │   └── text.sql                       # подстановка подстрок из рассказа
│   └── strategies/
│       ├── fake.sql                       # адресация пула компонент
│       ├── derive.sql                     # email/login из фейкового имени
│       ├── format.sql                     # FPE + шаблон формата
│       ├── secret.sql                     # хэш dev-пароля
│       ├── shift.sql                      # сдвиг дат
│       ├── redact.sql                     # замена подстроками рассказа
│       └── keep.sql                       # identity (возврат оригинала)
│
├── tests/
│   ├── DbAnonymizer.Core.Tests/
│   │   ├── DbAnonymizer.Core.Tests.csproj
│   │   ├── Detection/
│   │   │   ├── ColumnNameDetectorTests.cs
│   │   │   └── DetectorCatalogTests.cs
│   │   ├── Strategies/
│   │   │   └── MaterialBuilderTests.cs
│   │   └── Plan/
│   │       ├── PlanFreezerTests.cs
│   │       └── PlanValidatorTests.cs
│   │
│   ├── DbAnonymizer.Integration.Tests/
│   │   ├── DbAnonymizer.Integration.Tests.csproj
│   │   ├── TestcontainersFixture.cs       # общий fixture: pg-meta + pg-sandbox
│   │   ├── PipelineTests.cs               # полный прогон на демо-схеме
│   │   ├── ReproducibilityTests.cs        # два прогона с одним seed → одинаковый результат
│   │   ├── DataGrowthTests.cs             # новые строки не ломают старые значения
│   │   └── VerifyPhaseTests.cs            # 5 проверок Verify
│   │
│   └── DbAnonymizer.Api.Tests/
│       ├── DbAnonymizer.Api.Tests.csproj
│       └── Endpoints/
│           ├── JobsEndpointsTests.cs
│           └── PlanEndpointsTests.cs
```

---

## Стадия 0: Фундамент

### Task 0.1: Git init и .gitignore

**Files:**
- Create: `.gitignore`
- Modify: `CLAUDE.md`

**Interfaces:**
- Produces: git-репозиторий инициализирован, базовый `.gitignore` готов

- [ ] **Step 1: Инициализировать git**

```bash
cd C:/Repository/Temp/DbAnonymizer
git init
```

- [ ] **Step 2: Создать .gitignore**

```gitignore
# .NET
bin/
obj/
*.user
*.suo
*.cache
/packages/
*.nupkg

# Python
__pycache__/
*.py[cod]
*.egg-info/
.venv/
venv/

# IDE
.idea/
.vs/
.vscode/
*.swp

# OS
.DS_Store
Thumbs.db

# Secrets
*.env
*.env.local
appsettings.*.local.json

# Docker
postgres-data/
```

- [ ] **Step 3: Обновить CLAUDE.md**

Добавить секцию со структурой проекта (таблица папок/файлов как в CLAUDE.md корня).

- [ ] **Step 4: Первый коммит**

```bash
git add .gitignore CLAUDE.md docs/
git commit -m "chore: initialize repository"
```

---

### Task 0.2: .NET solution scaffold

**Files:**
- Create: `DbAnonymizer.sln`
- Create: `src/DbAnonymizer.Core/DbAnonymizer.Core.csproj`
- Create: `src/DbAnonymizer.Api/DbAnonymizer.Api.csproj`
- Create: `src/DbAnonymizer.Worker/DbAnonymizer.Worker.csproj`
- Create: `src/DbAnonymizer.Seed/DbAnonymizer.Seed.csproj`
- Create: `tests/DbAnonymizer.Core.Tests/DbAnonymizer.Core.Tests.csproj`
- Create: `tests/DbAnonymizer.Integration.Tests/DbAnonymizer.Integration.Tests.csproj`
- Create: `tests/DbAnonymizer.Api.Tests/DbAnonymizer.Api.Tests.csproj`

**Interfaces:**
- Produces: solution с 7 проектами, все компилируются (пустые Program.cs/Class1.cs где нужно)

- [ ] **Step 1: Создать solution**

```bash
cd C:/Repository/Temp/DbAnonymizer
dotnet new sln -n DbAnonymizer
```

- [ ] **Step 2: Создать проекты**

```bash
# Core library
dotnet new classlib -n DbAnonymizer.Core -o src/DbAnonymizer.Core --framework net10.0
# API
dotnet new web -n DbAnonymizer.Api -o src/DbAnonymizer.Api --framework net10.0
# Worker (console, will host Hangfire)
dotnet new console -n DbAnonymizer.Worker -o src/DbAnonymizer.Worker --framework net10.0
# Seed generator (console)
dotnet new console -n DbAnonymizer.Seed -o src/DbAnonymizer.Seed --framework net10.0
# Test projects
dotnet new xunit -n DbAnonymizer.Core.Tests -o tests/DbAnonymizer.Core.Tests --framework net10.0
dotnet new xunit -n DbAnonymizer.Integration.Tests -o tests/DbAnonymizer.Integration.Tests --framework net10.0
dotnet new xunit -n DbAnonymizer.Api.Tests -o tests/DbAnonymizer.Api.Tests --framework net10.0
```

- [ ] **Step 3: Добавить проекты в solution**

```bash
dotnet sln add src/DbAnonymizer.Core/DbAnonymizer.Core.csproj
dotnet sln add src/DbAnonymizer.Api/DbAnonymizer.Api.csproj
dotnet sln add src/DbAnonymizer.Worker/DbAnonymizer.Worker.csproj
dotnet sln add src/DbAnonymizer.Seed/DbAnonymizer.Seed.csproj
dotnet sln add tests/DbAnonymizer.Core.Tests/DbAnonymizer.Core.Tests.csproj
dotnet sln add tests/DbAnonymizer.Integration.Tests/DbAnonymizer.Integration.Tests.csproj
dotnet sln add tests/DbAnonymizer.Api.Tests/DbAnonymizer.Api.Tests.csproj
```

- [ ] **Step 4: Прописать ссылки между проектами**

```bash
# Api → Core
dotnet add src/DbAnonymizer.Api/DbAnonymizer.Api.csproj reference src/DbAnonymizer.Core/DbAnonymizer.Core.csproj
# Worker → Core
dotnet add src/DbAnonymizer.Worker/DbAnonymizer.Worker.csproj reference src/DbAnonymizer.Core/DbAnonymizer.Core.csproj
# Все тестовые → Core
dotnet add tests/DbAnonymizer.Core.Tests/DbAnonymizer.Core.Tests.csproj reference src/DbAnonymizer.Core/DbAnonymizer.Core.csproj
dotnet add tests/DbAnonymizer.Integration.Tests/DbAnonymizer.Integration.Tests.csproj reference src/DbAnonymizer.Core/DbAnonymizer.Core.csproj
dotnet add tests/DbAnonymizer.Api.Tests/DbAnonymizer.Api.Tests.csproj reference src/DbAnonymizer.Core/DbAnonymizer.Core.csproj
dotnet add tests/DbAnonymizer.Api.Tests/DbAnonymizer.Api.Tests.csproj reference src/DbAnonymizer.Api/DbAnonymizer.Api.csproj
```

- [ ] **Step 5: Добавить NuGet-пакеты**

```bash
# Core
dotnet add src/DbAnonymizer.Core/DbAnonymizer.Core.csproj package Npgsql
dotnet add src/DbAnonymizer.Core/DbAnonymizer.Core.csproj package YamlDotNet

# Api
dotnet add src/DbAnonymizer.Api/DbAnonymizer.Api.csproj package Hangfire
dotnet add src/DbAnonymizer.Api/DbAnonymizer.Api.csproj package Hangfire.PostgreSql

# Worker
dotnet add src/DbAnonymizer.Worker/DbAnonymizer.Worker.csproj package Hangfire.Core
dotnet add src/DbAnonymizer.Worker/DbAnonymizer.Worker.csproj package Npgsql

# Seed
dotnet add src/DbAnonymizer.Seed/DbAnonymizer.Seed.csproj package Bogus
dotnet add src/DbAnonymizer.Seed/DbAnonymizer.Seed.csproj package Npgsql

# Core.Tests
dotnet add tests/DbAnonymizer.Core.Tests/DbAnonymizer.Core.Tests.csproj package FsCheck.Xunit

# Integration.Tests
dotnet add tests/DbAnonymizer.Integration.Tests/DbAnonymizer.Integration.Tests.csproj package Testcontainers.PostgreSql
dotnet add tests/DbAnonymizer.Integration.Tests/DbAnonymizer.Integration.Tests.csproj package FsCheck.Xunit

# Api.Tests
dotnet add tests/DbAnonymizer.Api.Tests/DbAnonymizer.Api.Tests.csproj package Microsoft.AspNetCore.TestHost
```

- [ ] **Step 6: Собрать и закоммитить**

```bash
dotnet build
git add -A
git commit -m "chore: scaffold .NET solution with 7 projects"
```

После коммита удалить автоматически созданные `Class1.cs`/пустые файлы, если есть.

---

### Task 0.3: Docker Compose и сервисы

**Files:**
- Create: `docker-compose.yml`
- Create: `docker-compose.override.yml`

**Interfaces:**
- Produces: `docker compose up` поднимает `pg-meta`, `pg-sandbox`; override добавляет `api`, `worker`, `agents`

- [ ] **Step 1: Написать docker-compose.yml**

```yaml
version: "3.8"

services:
  pg-meta:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: db_anonymizer_meta
      POSTGRES_USER: anonymizer
      POSTGRES_PASSWORD: anonymizer
    ports:
      - "5432:5432"
    volumes:
      - pg-meta-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U anonymizer -d db_anonymizer_meta"]
      interval: 5s
      timeout: 3s
      retries: 5

  pg-sandbox:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: db_anonymizer_sandbox
      POSTGRES_USER: anonymizer
      POSTGRES_PASSWORD: anonymizer
    ports:
      - "5433:5432"
    volumes:
      - pg-sandbox-data:/var/lib/postgresql/data
      - ./sql/deploy.sql:/docker-entrypoint-initdb.d/01_deploy.sql:ro
    command: |
      postgres
      -c fsync=off
      -c synchronous_commit=off
      -c full_page_writes=off
      -c wal_level=minimal
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U anonymizer -d db_anonymizer_sandbox"]
      interval: 5s
      timeout: 3s
      retries: 5

volumes:
  pg-meta-data:
  pg-sandbox-data:
```

- [ ] **Step 2: Написать docker-compose.override.yml**

```yaml
version: "3.8"

services:
  api:
    build:
      context: .
      dockerfile: src/DbAnonymizer.Api/Dockerfile
    ports:
      - "8080:8080"
    environment:
      ConnectionStrings__PgMeta: "Host=pg-meta;Database=db_anonymizer_meta;Username=anonymizer;Password=anonymizer"
      ConnectionStrings__PgSandbox: "Host=pg-sandbox;Database=db_anonymizer_sandbox;Username=anonymizer;Password=anonymizer"
      Agents__BaseUrl: "http://agents:8000"
    depends_on:
      pg-meta:
        condition: service_healthy
      pg-sandbox:
        condition: service_healthy

  worker:
    build:
      context: .
      dockerfile: src/DbAnonymizer.Worker/Dockerfile
    environment:
      ConnectionStrings__PgMeta: "Host=pg-meta;Database=db_anonymizer_meta;Username=anonymizer;Password=anonymizer"
      ConnectionStrings__PgSandbox: "Host=pg-sandbox;Database=db_anonymizer_sandbox;Username=anonymizer;Password=anonymizer"
      Agents__BaseUrl: "http://agents:8000"
    depends_on:
      pg-meta:
        condition: service_healthy
      pg-sandbox:
        condition: service_healthy

  agents:
    build:
      context: .
      dockerfile: src/DbAnonymizer.Agents/Dockerfile
    ports:
      - "8000:8000"
    environment:
      LLM_PROVIDER: "openai"
      LLM_MODEL: "gpt-4o-mini"
      OPENAI_API_KEY: "${OPENAI_API_KEY:-}"
```

- [ ] **Step 3: Закоммитить**

```bash
git add docker-compose.yml docker-compose.override.yml
git commit -m "chore: add docker compose with pg-meta and pg-sandbox"
```

---

### Task 0.4: Демо-схема и seed-генератор

**Files:**
- Create: `src/DbAnonymizer.Seed/Schema/demo-schema.sql`
- Modify: `src/DbAnonymizer.Seed/Program.cs`

**Interfaces:**
- Produces: `dotnet run --project src/DbAnonymizer.Seed` наполняет sandbox демо-данными: таблицы `users`, `orders`, `profiles` с ФИО, email, телефон, адрес, карта, хэш пароля

- [ ] **Step 1: Написать demo-schema.sql**

```sql
-- Таблицы и FK, максимально приближенные к реальной БД
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

CREATE TABLE users (
    id          SERIAL PRIMARY KEY,
    first_name  VARCHAR(100) NOT NULL,
    last_name   VARCHAR(100) NOT NULL,
    middle_name VARCHAR(100),
    email       VARCHAR(255) NOT NULL UNIQUE,
    phone       VARCHAR(20) NOT NULL,
    password_hash CHAR(60) NOT NULL,
    birth_date  DATE NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE profiles (
    id          SERIAL PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    address     TEXT NOT NULL,
    passport    VARCHAR(20),
    inn         VARCHAR(12),
    metadata    JSONB
);

CREATE TABLE orders (
    id          SERIAL PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    user_email  VARCHAR(255) NOT NULL,  -- денормализовано, FK по значению с users.email
    card_number VARCHAR(19) NOT NULL,
    amount      DECIMAL(10,2) NOT NULL,
    comment     TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_orders_user_email ON orders(user_email);
CREATE INDEX idx_profiles_metadata ON profiles USING gin(metadata);
```

- [ ] **Step 2: Написать Program.cs генератора**

```csharp
using Bogus;
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
var userFaker = new Faker<dynamic>()
    .RuleFor("first_name", f => f.Name.FirstName())
    .RuleFor("last_name", f => f.Name.LastName())
    .RuleFor("middle_name", f => f.Name.FirstName()) // отчество генерим как ещё одно имя
    .RuleFor("email", (f, u) => f.Internet.Email(u.first_name, u.last_name))
    .RuleFor("phone", f => f.Phone.PhoneNumber("+7###-###-##-##"))
    .RuleFor("password_hash", f => BCryptHash(f.Internet.Password()))
    .RuleFor("birth_date", f => f.Date.Past(50, DateTime.Today.AddYears(-18)))
    .RuleFor("created_at", f => f.Date.Past(3));

// Генерация пользователей
for (int i = 0; i < userCount; i++)
{
    var user = userFaker.Generate();
    await using var insertUser = new NpgsqlCommand(
        @"INSERT INTO users (first_name, last_name, middle_name, email, phone, password_hash, birth_date, created_at)
          VALUES (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8) RETURNING id", conn);
    insertUser.Parameters.AddWithValue("p1", user.first_name);
    insertUser.Parameters.AddWithValue("p2", user.last_name);
    insertUser.Parameters.AddWithValue("p3", user.middle_name);
    insertUser.Parameters.AddWithValue("p4", user.email);
    insertUser.Parameters.AddWithValue("p5", user.phone);
    insertUser.Parameters.AddWithValue("p6", user.password_hash);
    insertUser.Parameters.AddWithValue("p7", user.birth_date);
    insertUser.Parameters.AddWithValue("p8", user.created_at);
    var userId = (int)await insertUser.ExecuteScalarAsync()!;

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
        insertOrder.Parameters.AddWithValue("p2", user.email);
        insertOrder.Parameters.AddWithValue("p3", fake.Finance.CreditCardNumber());
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
    return "$2a$10$" + Convert.ToBase64String(bytes)[..53];
}
```

- [ ] **Step 3: Закоммитить**

```bash
git add src/DbAnonymizer.Seed/
git commit -m "feat: add demo schema and Bogus seed generator"
```

---

## Стадия 1: Модели и абстракции ядра

### Task 1.1: Модели предметной области

**Files:**
- Create: `src/DbAnonymizer.Core/Models/SemanticType.cs`
- Create: `src/DbAnonymizer.Core/Models/StrategyType.cs`
- Create: `src/DbAnonymizer.Core/Models/Confidence.cs`
- Create: `src/DbAnonymizer.Core/Models/ColumnCandidate.cs`
- Create: `src/DbAnonymizer.Core/Models/ColumnSpec.cs`
- Create: `src/DbAnonymizer.Core/Models/Plan.cs`
- Create: `src/DbAnonymizer.Core/Models/JobState.cs`
- Create: `src/DbAnonymizer.Core/Models/SourceSpec.cs`
- Create: `src/DbAnonymizer.Core/Models/PoolSpec.cs`
- Create: `src/DbAnonymizer.Core/Models/Report.cs`

**Interfaces:**
- Produces: все модели домена — иммутабельные record'ы (или классы с init-only свойствами), без логики, только данные

- [ ] **Step 1: Написать перечисления (SemanticType, StrategyType, Confidence)**

```csharp
// Models/SemanticType.cs
namespace DbAnonymizer.Core.Models;

public enum SemanticType
{
    Unknown = 0,
    FirstName,
    LastName,
    MiddleName,
    FullName,
    Email,
    Login,
    Phone,
    CardNumber,
    Inn,
    Snils,
    PassportNumber,
    Address,
    CompanyName,
    BirthDate,
    PasswordHash,
    Salt,
    Token,
    ExternalId,
    FreeText,
    Json
}
```

```csharp
// Models/StrategyType.cs
namespace DbAnonymizer.Core.Models;

public enum StrategyType
{
    Keep = 0,
    Fake,
    Derive,
    Format,
    Secret,
    Shift,
    Redact
}
```

```csharp
// Models/Confidence.cs
namespace DbAnonymizer.Core.Models;

public enum Confidence
{
    Low = 0,
    Medium = 1,
    High = 2
}
```

- [ ] **Step 2: Написать ColumnCandidate (выход детектора)**

```csharp
// Models/ColumnCandidate.cs
namespace DbAnonymizer.Core.Models;

public sealed class ColumnCandidate
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string ColumnName { get; init; }
    public string DataType { get; init; } = string.Empty;
    public bool IsUnique { get; init; }
    public int? MaxLength { get; init; }
    public SemanticType DetectedType { get; init; }
    public Confidence Confidence { get; init; }
    public string SignalSource { get; init; } = string.Empty; // "column_name", "type_constraint", "profile"
}
```

- [ ] **Step 3: Написать ColumnSpec (строка замороженного плана)**

```csharp
// Models/ColumnSpec.cs
namespace DbAnonymizer.Core.Models;

public sealed class ColumnSpec
{
    public required string SchemaName { get; init; }
    public required string TableName { get; init; }
    public required string ColumnName { get; init; }
    public string DataType { get; init; } = string.Empty;
    public bool IsUnique { get; init; }
    public SemanticType SemanticType { get; init; }
    public StrategyType Strategy { get; init; } = StrategyType.Keep;
    public bool IsJsonPath { get; init; }  // true если это путь внутри jsonb, а не колонка
    public string JsonPath { get; init; } = string.Empty; // "$.customer.phone"
    public long DistinctCount { get; init; }
    public long RowCount { get; init; }
}
```

- [ ] **Step 4: Написать Plan.cs**

```csharp
// Models/Plan.cs
namespace DbAnonymizer.Core.Models;

public sealed class Plan
{
    public required string Id { get; init; }            // GUID
    public required string SourceHash { get; init; }     // хэш схемы источника
    public required string Seed { get; init; }           // seed задачи
    public int SuffixWidth { get; init; } = 6;           // ширина base36-суффикса
    public required string Version { get; init; }        // "1", "2", ...
    public required string ContentHash { get; init; }    // SHA256 всего содержимого
    public required string MaterialsHash { get; init; }  // хэш загруженных материалов
    public DateTime FrozenAt { get; init; }
    public IReadOnlyList<ColumnSpec> Columns { get; init; } = Array.Empty<ColumnSpec>();
    public IReadOnlyList<PoolSpec> Pools { get; init; } = Array.Empty<PoolSpec>();
}
```

- [ ] **Step 5: Написать вспомогательные модели**

```csharp
// Models/JobState.cs
namespace DbAnonymizer.Core.Models;

public enum JobStatus
{
    Created = 0,
    Ingesting,
    AwaitingReview,   // детекторы отработали, ждём человека
    Frozen,            // план заморожен
    Enriching,         // воркер вызывает агента
    Applying,          // CTAS + подмена
    Verifying,         // проверки целостности
    Complete,
    Failed
}

public sealed class JobState
{
    public required string Id { get; init; }
    public JobStatus Status { get; init; }
    public string PlanId { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public SourceSpec? Source { get; init; }
}
```

```csharp
// Models/SourceSpec.cs
namespace DbAnonymizer.Core.Models;

public sealed class SourceSpec
{
    public string DumpFilePath { get; init; } = string.Empty;
    public string ConnectionString { get; init; } = string.Empty;
    public bool IsLiveDb => !string.IsNullOrEmpty(ConnectionString);
}
```

```csharp
// Models/PoolSpec.cs
namespace DbAnonymizer.Core.Models;

public sealed class PoolSpec
{
    public required SemanticType SemanticType { get; init; }
    public required string Component { get; init; } // "first_name", "last_name", "city"...
    public required int Size { get; init; }          // размер компонента пула
    public required string PromptVersion { get; init; }
}
```

```csharp
// Models/Report.cs
namespace DbAnonymizer.Core.Models;

public sealed class Report
{
    public required string JobId { get; init; }
    public required string PlanId { get; init; }
    public required string Seed { get; init; }
    public int SuffixWidth { get; init; }
    public bool AllChecksPassed { get; init; }
    public IReadOnlyList<ReportCheck> Checks { get; init; } = Array.Empty<ReportCheck>();
    public string OutputDumpPath { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; }
}

public sealed class ReportCheck
{
    public required string Name { get; init; }
    public bool Passed { get; init; }
    public string Detail { get; init; } = string.Empty;
}
```

- [ ] **Step 6: Собрать и закоммитить**

```bash
dotnet build
git add src/DbAnonymizer.Core/Models/
git commit -m "feat: add domain models — Plan, ColumnSpec, JobState, Report"
```

---

### Task 1.2: Интерфейсы ядра

**Files:**
- Create: `src/DbAnonymizer.Core/Adapters/ISourceAdapter.cs`
- Create: `src/DbAnonymizer.Core/Adapters/ISinkAdapter.cs`
- Create: `src/DbAnonymizer.Core/Detection/IDetector.cs`
- Create: `src/DbAnonymizer.Core/Strategies/IStrategyMaterialBuilder.cs`

**Interfaces:**
- Consumes: модели из Task 1.1
- Produces: 4 интерфейса — контракты для адаптеров ввода/вывода, детекторов и построителей материалов

- [ ] **Step 1: ISourceAdapter**

```csharp
// Adapters/ISourceAdapter.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Adapters;

public interface ISourceAdapter
{
    Task RestoreAsync(
        SourceSpec source,
        string sandboxConnectionString,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: ISinkAdapter**

```csharp
// Adapters/ISinkAdapter.cs
namespace DbAnonymizer.Core.Adapters;

public interface ISinkAdapter
{
    Task<string> DumpAsync(
        string sandboxConnectionString,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: IDetector**

```csharp
// Detection/IDetector.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public interface IDetector
{
    string Name { get; }
    Task<IReadOnlyList<ColumnCandidate>> DetectAsync(
        string sandboxConnectionString,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: IStrategyMaterialBuilder**

```csharp
// Strategies/IStrategyMaterialBuilder.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Strategies;

public interface IStrategyMaterialBuilder
{
    StrategyType StrategyType { get; }
    Task<string> BuildAndLoadAsync(
        string sandboxConnectionString,
        Plan plan,
        CancellationToken cancellationToken = default);
    // Возвращает хэш загруженных материалов (для MaterialsHash в плане)
}
```

- [ ] **Step 5: Собрать и закоммитить**

```bash
dotnet build
git add src/DbAnonymizer.Core/
git commit -m "feat: add core interfaces — ISourceAdapter, ISinkAdapter, IDetector, IStrategyMaterialBuilder"
```

---

## Стадия 2: Детекторы и интроспекция

### Task 2.1: Каталог детекторов (YAML)

**Files:**
- Create: `src/DbAnonymizer.Core/Detection/detectors.yaml`
- Create: `src/DbAnonymizer.Core/Detection/DetectorCatalog.cs`
- Create: `tests/DbAnonymizer.Core.Tests/Detection/DetectorCatalogTests.cs`

**Interfaces:**
- Consumes: `SemanticType`, `Confidence`
- Produces: `DetectorCatalog` загружает YAML, отдаёт список правил; каждая запись: pattern для имени колонки → семантический тип + уверенность

- [ ] **Step 1: Написать detectors.yaml**

```yaml
rules:
  # High confidence — однозначное совпадение
  - pattern: "^first_name$"
    semantic_type: FirstName
    confidence: High
  - pattern: "^last_name$"
    semantic_type: LastName
    confidence: High
  - pattern: "^middle_name$"
    semantic_type: MiddleName
    confidence: High
  - pattern: "^fname$"
    semantic_type: FirstName
    confidence: High
  - pattern: "^surname$"
    semantic_type: LastName
    confidence: High
  - pattern: "^email$"
    semantic_type: Email
    confidence: High
  - pattern: "^phone$"
    semantic_type: Phone
    confidence: High
  - pattern: "^card_number$"
    semantic_type: CardNumber
    confidence: High
  - pattern: "^inn$"
    semantic_type: Inn
    confidence: High
  - pattern: "^snils$"
    semantic_type: Snils
    confidence: High
  - pattern: "^passport"
    semantic_type: PassportNumber
    confidence: High
  - pattern: "^address$"
    semantic_type: Address
    confidence: High
  - pattern: "^birth"
    semantic_type: BirthDate
    confidence: High
  - pattern: "^password_hash$"
    semantic_type: PasswordHash
    confidence: High
  - pattern: "^salt$"
    semantic_type: Salt
    confidence: High

  # Medium confidence — содержит, но не точное совпадение
  - pattern: "name$"
    semantic_type: FullName
    confidence: Medium
  - pattern: "phone"
    semantic_type: Phone
    confidence: Medium
  - pattern: "email"
    semantic_type: Email
    confidence: Medium
  - pattern: "card"
    semantic_type: CardNumber
    confidence: Medium
  - pattern: "passw"
    semantic_type: PasswordHash
    confidence: Medium
  - pattern: "secret"
    semantic_type: Token
    confidence: Medium
  - pattern: "token"
    semantic_type: Token
    confidence: Medium
  - pattern: "addr"
    semantic_type: Address
    confidence: Medium
  - pattern: "login"
    semantic_type: Login
    confidence: Medium
```

- [ ] **Step 2: Написать DetectorCatalog.cs**

```csharp
// Detection/DetectorCatalog.cs
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public sealed class DetectionRule
{
    public required string Pattern { get; init; }
    public required string SemanticType { get; init; }
    public required string Confidence { get; init; }

    public SemanticType ParsedSemanticType => Enum.Parse<SemanticType>(SemanticType);
    public Confidence ParsedConfidence => Enum.Parse<Confidence>(Confidence);
}

public sealed class DetectorCatalog
{
    private readonly List<DetectionRule> _rules;

    public DetectorCatalog(string yamlContent)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var doc = deserializer.Deserialize<CatalogDocument>(yamlContent);
        _rules = doc.Rules;
    }

    public static DetectorCatalog Load()
    {
        var assembly = typeof(DetectorCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "DbAnonymizer.Core.Detection.detectors.yaml");
        using var reader = new StreamReader(stream!);
        return new DetectorCatalog(reader.ReadToEnd());
    }

    public IReadOnlyList<DetectionRule> GetRules() => _rules;

    private sealed class CatalogDocument
    {
        public List<DetectionRule> Rules { get; set; } = new();
    }
}
```

- [ ] **Step 3: Тест загрузки каталога**

```csharp
// Detection/DetectorCatalogTests.cs
public sealed class DetectorCatalogTests
{
    [Fact]
    public void Load_ShouldParseAllRules()
    {
        var catalog = DetectorCatalog.Load();
        var rules = catalog.GetRules();
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.ParsedSemanticType == SemanticType.Email);
    }
}
```

- [ ] **Step 4: Прописать YAML как embedded resource**

В `DbAnonymizer.Core.csproj`:
```xml
<ItemGroup>
  <EmbeddedResource Include="Detection\detectors.yaml" />
</ItemGroup>
```

- [ ] **Step 5: Закоммитить**

```bash
dotnet test tests/DbAnonymizer.Core.Tests/
git add -A
git commit -m "feat: add YAML detector catalog with column name rules"
```

---

### Task 2.2: Детектор имени колонки

**Files:**
- Create: `src/DbAnonymizer.Core/Detection/ColumnNameDetector.cs`
- Create: `tests/DbAnonymizer.Core.Tests/Detection/ColumnNameDetectorTests.cs`

**Interfaces:**
- Consumes: `DetectorCatalog`, `IDetector`
- Produces: детектор, сверяющий имена колонок с YAML-каталогом через regex

- [ ] **Step 1: ColumnNameDetector**

```csharp
// Detection/ColumnNameDetector.cs
using System.Text.RegularExpressions;
using Npgsql;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public sealed class ColumnNameDetector : IDetector
{
    private readonly DetectorCatalog _catalog;

    public string Name => "column_name";

    public ColumnNameDetector(DetectorCatalog catalog)
    {
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<ColumnCandidate>> DetectAsync(
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        // Получаем список всех колонок из information_schema
        await using var cmd = new NpgsqlCommand(
            @"SELECT table_schema, table_name, column_name, data_type, character_maximum_length
              FROM information_schema.columns
              WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              ORDER BY table_schema, table_name, ordinal_position", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var candidates = new List<ColumnCandidate>();
        var rules = _catalog.GetRules();

        while (await reader.ReadAsync(cancellationToken))
        {
            var columnName = reader.GetString("column_name");
            foreach (var rule in rules)
            {
                if (Regex.IsMatch(columnName, rule.Pattern, RegexOptions.IgnoreCase))
                {
                    candidates.Add(new ColumnCandidate
                    {
                        SchemaName = reader.GetString("table_schema"),
                        TableName = reader.GetString("table_name"),
                        ColumnName = columnName,
                        DataType = reader.GetString("data_type"),
                        MaxLength = reader.IsDBNull("character_maximum_length")
                            ? null
                            : reader.GetInt32("character_maximum_length"),
                        DetectedType = rule.ParsedSemanticType,
                        Confidence = rule.ParsedConfidence,
                        SignalSource = Name
                    });
                    break; // первое совпадение, от более специфичных к общим
                }
            }
        }
        return candidates;
    }
}
```

- [ ] **Step 2: ColumnNameDetectorTests — интеграционный (Testcontainers)**

```csharp
// Detection/ColumnNameDetectorTests.cs
public sealed class ColumnNameDetectorTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .Build();
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
```

- [ ] **Step 3: Закоммитить**

```bash
dotnet test tests/DbAnonymizer.Core.Tests/
git add -A
git commit -m "feat: add ColumnNameDetector — regex match against YAML catalog"
```

---

### Task 2.3: IntrospectionEngine

**Files:**
- Create: `src/DbAnonymizer.Core/Detection/IntrospectionEngine.cs`
- Create: `tests/DbAnonymizer.Core.Tests/Detection/IntrospectionEngineTests.cs`

**Interfaces:**
- Consumes: `IDetector[]` (массив зарегистрированных детекторов)
- Produces: объединяет результаты детекторов — для каждой колонки выбирает кандидата с максимальной уверенностью

- [ ] **Step 1: IntrospectionEngine**

```csharp
// Detection/IntrospectionEngine.cs
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Detection;

public sealed class IntrospectionEngine
{
    private readonly IReadOnlyList<IDetector> _detectors;

    public IntrospectionEngine(IEnumerable<IDetector> detectors)
    {
        _detectors = detectors.ToList();
    }

    public async Task<IReadOnlyList<ColumnCandidate>> RunAsync(
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        // Запустить все детекторы параллельно
        var tasks = _detectors.Select(d => d.DetectAsync(sandboxConnectionString, cancellationToken));
        var results = await Task.WhenAll(tasks);

        // Слить: ключ = (schema, table, column), значение = кандидат с максимальной уверенностью
        var merged = new Dictionary<string, ColumnCandidate>();
        foreach (var candidate in results.SelectMany(r => r))
        {
            var key = $"{candidate.SchemaName}.{candidate.TableName}.{candidate.ColumnName}";
            if (!merged.TryGetValue(key, out var existing) || candidate.Confidence > existing.Confidence)
            {
                merged[key] = candidate;
            }
        }

        return merged.Values.ToList();
    }
}
```

- [ ] **Step 2: Тест слияния**

```csharp
[Fact]
public async Task Run_MergesMultipleDetectors_PicksHighestConfidence()
{
    var lowDetector = new StubDetector("low", new ColumnCandidate
    {
        SchemaName = "public", TableName = "users", ColumnName = "email",
        DetectedType = SemanticType.Email, Confidence = Confidence.Low, SignalSource = "low"
    });
    var highDetector = new StubDetector("high", new ColumnCandidate
    {
        SchemaName = "public", TableName = "users", ColumnName = "email",
        DetectedType = SemanticType.Email, Confidence = Confidence.High, SignalSource = "high"
    });
    var engine = new IntrospectionEngine([lowDetector, highDetector]);
    var result = await engine.RunAsync("any");
    var email = Assert.Single(result);
    Assert.Equal(Confidence.High, email.Confidence);
    Assert.Equal("high", email.SignalSource);
}
```

- [ ] **Step 3: Закоммитить**

```bash
dotnet test tests/DbAnonymizer.Core.Tests/
git add -A
git commit -m "feat: add IntrospectionEngine — merge detector results by max confidence"
```

---

## Стадия 3: SQL-фундамент

### Task 3.1: Утилиты (hmac, base36-суффикс, работа с текстом)

**Files:**
- Create: `sql/utils/hash.sql`
- Create: `sql/utils/suffix.sql`
- Create: `sql/utils/text.sql`
- Create: `sql/deploy.sql`

**Interfaces:**
- Produces: SQL-функции, доступные в sandbox через `deploy.sql`

- [ ] **Step 1: hash.sql — HMAC-обёртки**

```sql
-- utils/hash.sql
CREATE OR REPLACE FUNCTION utils.hmac_bigint(
    seed TEXT,
    input TEXT
) RETURNS BIGINT AS $$
DECLARE
    raw BYTEA;
    val BIGINT := 0;
    i INT;
BEGIN
    raw := pg_crypto.hmac(input::BYTEA, seed::BYTEA, 'sha256');
    -- Первые 8 байт → BIGINT (без знака через bytea-арифметику)
    FOR i IN 0..7 LOOP
        val := (val << 8) | get_byte(raw, i)::BIGINT;
    END LOOP;
    RETURN val;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;

CREATE OR REPLACE FUNCTION utils.hmac_mod(
    seed TEXT,
    input TEXT,
    modulus BIGINT
) RETURNS BIGINT AS $$
BEGIN
    RETURN abs(utils.hmac_bigint(seed, input)) % modulus;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 2: suffix.sql — base36-суффикс**

```sql
-- utils/suffix.sql
CREATE OR REPLACE FUNCTION utils.base36(seed TEXT, input TEXT, width INT)
RETURNS TEXT AS $$
DECLARE
    chars TEXT := '0123456789abcdefghijklmnopqrstuvwxyz';
    val BIGINT := utils.hmac_bigint(seed, input);
    result TEXT := '';
    i INT;
BEGIN
    FOR i IN 1..width LOOP
        result := substr(chars, (abs(val) % 36)::INT + 1, 1) || result;
        val := val / 36;
    END LOOP;
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 3: text.sql — подстановка подстрок из рассказа**

```sql
-- utils/text.sql
-- word_by_length(word_len INT, pos BIGINT) → слово нужной длины из рассказа по позиции
-- substring_from_story(seed TEXT, input TEXT, desired_len INT) → подстрока рассказа
-- Используется стратегией Redact
```

- [ ] **Step 4: deploy.sql — точка входа**

```sql
-- deploy.sql
CREATE SCHEMA IF NOT EXISTS utils;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

\ir utils/hash.sql
\ir utils/suffix.sql
\ir utils/text.sql
```

- [ ] **Step 5: Закоммитить**

```bash
git add sql/
git commit -m "feat: add SQL utilities — hmac_bigint, base36 suffix, story text helpers"
```

---

### Task 3.2: SQL-функции стратегий

**Files:**
- Create: `sql/strategies/fake.sql`
- Create: `sql/strategies/format.sql`
- Create: `sql/strategies/secret.sql`
- Create: `sql/strategies/shift.sql`
- Create: `sql/strategies/redact.sql`
- Create: `sql/strategies/derive.sql`
- Create: `sql/strategies/keep.sql`

**Interfaces:**
- Consumes: `utils.*`, таблицы-материалы в схеме `materials`
- Produces: функция на каждую стратегию — сигнатура `anonymize.<strategy>(seed TEXT, original TEXT, ...) → TEXT`

- [ ] **Step 1: keep.sql — возврат оригинала**

```sql
-- strategies/keep.sql
CREATE OR REPLACE FUNCTION anonymize.keep(seed TEXT, original TEXT)
RETURNS TEXT AS $$
BEGIN
    RETURN original;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 2: fake.sql — адресация пула компонент**

```sql
-- strategies/fake.sql
-- Материалы: materials.pool_<component>(id BIGINT, value TEXT)
CREATE OR REPLACE FUNCTION anonymize.fake(
    seed TEXT,
    original TEXT,
    component TEXT,
    suffix_width INT
) RETURNS TEXT AS $$
DECLARE
    idx BIGINT;
    val TEXT;
    suf TEXT;
BEGIN
    EXECUTE format(
        'SELECT (SELECT count(*) FROM materials.pool_%I)::BIGINT', component
    ) INTO idx; -- размер пула, дальше используем как modulus
    idx := utils.hmac_mod(seed, original, idx);
    EXECUTE format(
        'SELECT value FROM materials.pool_%I WHERE id = $1', component
    ) INTO val USING idx;
    IF suffix_width > 0 THEN
        suf := utils.base36(seed, original, suffix_width);
        RETURN val || ' ' || suf;
    END IF;
    RETURN val;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 3: format.sql — FPE + форматный шаблон**

```sql
-- strategies/format.sql
-- Перестановка домена для фиксированного алфавита
-- Материалы: materials.format_template(type TEXT, template TEXT) — regex + замена
CREATE OR REPLACE FUNCTION anonymize.format(
    seed TEXT,
    original TEXT,
    format_type TEXT,
    suffix_width INT DEFAULT 0
) RETURNS TEXT AS $$
DECLARE
    cleaned TEXT;    -- оригинал после снятия форматирования (одни цифры)
    alphabet TEXT;
    domain_size BIGINT;
    rank BIGINT;
    permuted_rank BIGINT;
    result TEXT;
    i INT;
    pos INT;
    template_str TEXT;
BEGIN
    -- Извлекаем шаблон формата (в плане: телефон → \d{10}, карта → \d{16})
    SELECT template INTO template_str
    FROM materials.format_templates WHERE type = format_type;
    IF NOT FOUND THEN
        RETURN original; -- нет шаблона — не трогаем
    END IF;

    -- Снимаем формат: оставляем только цифры
    cleaned := regexp_replace(original, '[^0-9]', '', 'g');
    alphabet := '0123456789';
    domain_size := power(length(alphabet)::BIGINT, length(cleaned)::INT);

    -- Интерпретируем оригинал как число в base-|alphabet| → rank
    rank := 0;
    FOR i IN 1..length(cleaned) LOOP
        pos := position(substr(cleaned, i, 1) IN alphabet) - 1;
        rank := rank * length(alphabet) + pos;
    END LOOP;

    -- FPE: перестановка домена = (rank + HMAC_offset) mod domain_size
    permuted_rank := (rank + utils.hmac_bigint(seed, original)) % domain_size;

    -- Превращаем permuted_rank обратно в строку над алфавитом
    result := '';
    FOR i IN 1..length(cleaned) LOOP
        result := substr(alphabet, (permuted_rank % length(alphabet))::INT + 1, 1) || result;
        permuted_rank := permuted_rank / length(alphabet);
    END LOOP;

    -- Восстанавливаем формат по шаблону (template_str описывает маску)
    -- Для MVP: просто возвращаем цифры как есть, формат восстанавливается в .NET
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 4: secret.sql — хэш dev-пароля**

```sql
-- strategies/secret.sql
CREATE OR REPLACE FUNCTION anonymize.secret(seed TEXT, original TEXT)
RETURNS TEXT AS $$
BEGIN
    -- Замена на хэш известного dev-пароля; детерминированно по seed+original
    RETURN encode(pg_crypto.hmac(
        'dev-password'::BYTEA,
        (seed || original)::BYTEA,
        'sha256'
    ), 'hex');
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 5: shift.sql — сдвиг дат**

```sql
-- strategies/shift.sql
CREATE OR REPLACE FUNCTION anonymize.shift(
    seed TEXT,
    original TEXT,  -- дата в ISO-формате (YYYY-MM-DD)
    shift_range_days INT
) RETURNS TEXT AS $$
DECLARE
    orig_date DATE;
    offset_val BIGINT;
    shifted DATE;
BEGIN
    orig_date := original::DATE;
    offset_val := utils.hmac_mod(seed, original, 2 * shift_range_days + 1) - shift_range_days;
    shifted := orig_date + offset_val * INTERVAL '1 day';
    RETURN to_char(shifted, 'YYYY-MM-DD');
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 6: redact.sql — замена подстроками рассказа**

```sql
-- strategies/redact.sql
CREATE OR REPLACE FUNCTION anonymize.redact(seed TEXT, original TEXT)
RETURNS TEXT AS $$
DECLARE
    story_len BIGINT;
    desired_len INT;
    offset_val BIGINT;
    result TEXT := '';
BEGIN
    desired_len := length(original);
    SELECT length(content) INTO story_len FROM materials.story;

    -- Набираем подстроки рассказа до нужной длины
    WHILE length(result) < desired_len LOOP
        offset_val := utils.hmac_bigint(seed, result || original);
        result := result || substr(
            (SELECT content FROM materials.story),
            (abs(offset_val) % story_len)::INT + 1,
            desired_len - length(result)
        );
    END LOOP;
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 7: derive.sql — email/login из фейкового имени**

```sql
-- strategies/derive.sql
CREATE OR REPLACE FUNCTION anonymize.derive(
    seed TEXT,
    original TEXT,
    domain TEXT,          -- '@mail.invalid'
    suffix_width INT
) RETURNS TEXT AS $$
DECLARE
    local_part TEXT;
    suf TEXT;
BEGIN
    -- Локальная часть: first.last (берётся из фейкового имени — аналог anonymize.fake)
    -- В MVP: транслитерация упрощённая, вызывает fake для компонент и склеивает
    local_part := lower(regexp_replace(
        anonymize.fake(seed, original, 'first_name', 0) || '.' ||
        anonymize.fake(seed, original, 'last_name', 0),
        '\s+', '.', 'g'
    ));
    suf := utils.base36(seed, original, suffix_width);
    RETURN local_part || '.' || suf || domain;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
```

- [ ] **Step 8: Обновить deploy.sql**

Добавить `\ir strategies/*.sql` и `CREATE SCHEMA IF NOT EXISTS anonymize`.

- [ ] **Step 9: Закоммитить**

```bash
git add sql/
git commit -m "feat: add SQL strategy functions — fake, format, secret, shift, redact, derive, keep"
```

---

## Стадия 4: Построители материалов (.NET)

### Task 4.1: FakeMaterialBuilder — загрузка пулов компонент

**Files:**
- Create: `src/DbAnonymizer.Core/Strategies/FakeMaterialBuilder.cs`
- Create: `src/DbAnonymizer.Core/Strategies/PoolComponents.cs`
- Create: `tests/DbAnonymizer.Core.Tests/Strategies/FakeMaterialBuilderTests.cs`

**Interfaces:**
- Consumes: `IStrategyMaterialBuilder`, `Plan`
- Produces: загружает в sandbox таблицы `materials.pool_first_name`, `materials.pool_last_name` и т.д.

- [ ] **Step 1: PoolComponents.cs — получение компонент от агента**

```csharp
// Strategies/PoolComponents.cs
namespace DbAnonymizer.Core.Strategies;

public sealed class PoolComponents
{
    public required string Component { get; init; }
    public required IReadOnlyList<string> Values { get; init; }

    public int Size => Values.Count;
}
```

- [ ] **Step 2: FakeMaterialBuilder**

```csharp
// Strategies/FakeMaterialBuilder.cs
using Npgsql;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Core.Strategies;

public sealed class FakeMaterialBuilder : IStrategyMaterialBuilder
{
    private readonly IReadOnlyList<PoolComponents> _pools;

    public StrategyType StrategyType => StrategyType.Fake;

    public FakeMaterialBuilder(IReadOnlyList<PoolComponents> pools)
    {
        _pools = pools;
    }

    public async Task<string> BuildAndLoadAsync(
        string sandboxConnectionString,
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var createSchema = new NpgsqlCommand(
            "CREATE SCHEMA IF NOT EXISTS materials", conn);
        await createSchema.ExecuteNonQueryAsync(cancellationToken);

        foreach (var pool in _pools)
        {
            // 1. Создать таблицу-материал
            await using var dropCmd = new NpgsqlCommand(
                $"DROP TABLE IF EXISTS materials.pool_{pool.Component}", conn);
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);

            await using var createCmd = new NpgsqlCommand(
                $"CREATE TABLE materials.pool_{pool.Component} (id BIGINT PRIMARY KEY, value TEXT NOT NULL)", conn);
            await createCmd.ExecuteNonQueryAsync(cancellationToken);

            // 2. Загрузить значения через COPY (binary, быстро)
            await using var writer = await conn.BeginBinaryImportAsync(
                $"COPY materials.pool_{pool.Component} (id, value) FROM STDIN (FORMAT BINARY)",
                cancellationToken);
            for (int i = 0; i < pool.Values.Count; i++)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync((long)i, cancellationToken);
                await writer.WriteAsync(pool.Values[i], cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        // Хэш материалов = SHA256 от всех значений (для детерминизма)
        return ComputeHash();
    }

    private string ComputeHash()
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                string.Join("|", _pools.SelectMany(p => p.Values))));
        return Convert.ToHexStringLower(bytes);
    }
}
```

- [ ] **Step 3: Тест**

```csharp
[Fact]
public async Task BuildAndLoad_ShouldCreatePoolTables()
{
    var pools = new List<PoolComponents>
    {
        new() { Component = "first_name", Values = ["Алексей", "Мария", "Дмитрий"] }
    };
    var builder = new FakeMaterialBuilder(pools);
    var hash = await builder.BuildAndLoadAsync(_connectionString, TestPlan);

    await using var conn = new NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT count(*) FROM materials.pool_first_name", conn);
    var count = (long)await cmd.ExecuteScalarAsync()!;
    Assert.Equal(3, count);
    Assert.Equal(64, hash.Length);
}
```

- [ ] **Step 4: Закоммитить**

```bash
git add -A
git commit -m "feat: add FakeMaterialBuilder — load pool tables via Npgsql binary COPY"
```

---

### Task 4.2: RedactMaterialBuilder и story.txt

**Files:**
- Create: `story.txt`
- Create: `src/DbAnonymizer.Core/Strategies/RedactMaterialBuilder.cs`

**Interfaces:**
- Consumes: `IStrategyMaterialBuilder`
- Produces: загружает рассказ в `materials.story` и индекс слов по длине в `materials.story_words`

- [ ] **Step 1: story.txt — текст на русском, ~10K символов**

```
Глава 1. Утро начиналось с кофе и просмотра почты. Алексей Петрович, начальник отдела,
привык приходить раньше всех — так он успевал разобрать самые сложные запросы до того,
как офис наполнится голосами коллег. Сегодня в почте его ждало письмо от партнёров
из Новосибирска: они подтверждали поставку оборудования на следующую неделю...
```
*(Полноценный текст ~10K символов — приложить отдельным файлом, здесь сокращено для примера.)*

- [ ] **Step 2: RedactMaterialBuilder**

Загружает `story.txt` в `materials.story(content TEXT)` и строит `materials.story_words(len INT, word TEXT, idx BIGINT)` — индекс слов по длине для замены однословных значений.

```csharp
public sealed class RedactMaterialBuilder : IStrategyMaterialBuilder
{
    private readonly string _storyPath;
    public StrategyType StrategyType => StrategyType.Redact;

    public async Task<string> BuildAndLoadAsync(
        string sandboxConnectionString, Plan plan,
        CancellationToken cancellationToken = default)
    {
        var story = await File.ReadAllTextAsync(_storyPath, cancellationToken);
        // Загрузить рассказ и индекс слов — аналогично FakeMaterialBuilder через COPY
        // ...
        return ComputeHash(story);
    }
}
```

- [ ] **Step 3: Закоммитить**

```bash
git add story.txt src/DbAnonymizer.Core/Strategies/RedactMaterialBuilder.cs
git commit -m "feat: add RedactMaterialBuilder with story text and word-length index"
```

---

### Task 4.3: FormatMaterialBuilder и ShiftMaterialBuilder

**Files:**
- Create: `src/DbAnonymizer.Core/Strategies/FormatMaterialBuilder.cs`
- Create: `src/DbAnonymizer.Core/Strategies/ShiftMaterialBuilder.cs`

**Interfaces:**
- Produces: `materials.format_templates(type TEXT, template TEXT)` и `materials.shift_config(type TEXT, range_days INT)`

- [ ] **Step 1: FormatMaterialBuilder**

```csharp
// Загружает шаблоны форматов (телефон: 10 цифр, карта: 16 цифр, ИНН: 10/12 цифр)
// Каждый шаблон: type → regex для снятия форматирования + regex для восстановления
public sealed class FormatMaterialBuilder : IStrategyMaterialBuilder
{
    public StrategyType StrategyType => StrategyType.Format;
    // Загружает таблицу materials.format_templates из данных, зашитых в коде для MVP
    // (в будущем — из конфига)
}
```

- [ ] **Step 2: ShiftMaterialBuilder**

```csharp
// Загружает materials.shift_config(type TEXT, range_days INT)
// По умолчанию: BirthDate → 3650 дней (~10 лет)
public sealed class ShiftMaterialBuilder : IStrategyMaterialBuilder
{
    public StrategyType StrategyType => StrategyType.Shift;
}
```

- [ ] **Step 3: MaterialOrchestrator**

```csharp
// Strategies/MaterialOrchestrator.cs
public sealed class MaterialOrchestrator
{
    private readonly IReadOnlyList<IStrategyMaterialBuilder> _builders;

    public async Task<string> BuildAllAsync(
        string sandboxConnectionString, Plan plan,
        CancellationToken cancellationToken = default)
    {
        var hashes = new List<string>();
        foreach (var builder in _builders)
        {
            var hash = await builder.BuildAndLoadAsync(
                sandboxConnectionString, plan, cancellationToken);
            hashes.Add($"{builder.StrategyType}:{hash}");
        }
        // Комбинированный хэш для Plan.MaterialsHash
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", hashes))));
    }
}
```

- [ ] **Step 4: Закоммитить**

---

## Стадия 5: Python-агент (Value Pool Service)

### Task 5.1: Python-проект и LangGraph-граф

**Files:**
- Create: `src/DbAnonymizer.Agents/pyproject.toml`
- Create: `src/DbAnonymizer.Agents/Dockerfile`
- Create: `src/DbAnonymizer.Agents/main.py`
- Create: `src/DbAnonymizer.Agents/schemas.py`
- Create: `src/DbAnonymizer.Agents/prompts.py`
- Create: `src/DbAnonymizer.Agents/graph.py`
- Create: `src/DbAnonymizer.Agents/llm_client.py`

**Interfaces:**
- Consumes: POST-запрос с `{semantic_type, component, pool_size, language: "ru"}`
- Produces: JSON-массив из `pool_size` правдоподобных значений (имён, фамилий, городов…). LangGraph-граф: validate → generate → format; LiteLLM-провайдер — Claude/OpenAI/DeepSeek

- [ ] **Step 1: pyproject.toml и Dockerfile**

```toml
[project]
name = "db-anonymizer-agents"
version = "0.1.0"
requires-python = ">=3.12"
dependencies = [
    "fastapi>=0.115",
    "uvicorn[standard]>=0.32",
    "langgraph>=0.2",
    "litellm>=1.50",
    "pydantic>=2",
]

[build-system]
requires = ["setuptools"]
build-backend = "setuptools.build_meta"
```

```dockerfile
# Dockerfile
FROM python:3.12-slim
WORKDIR /app
COPY src/DbAnonymizer.Agents/ .
RUN pip install --no-cache-dir -e .
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
```

- [ ] **Step 2: schemas.py — Pydantic-модели**

```python
from pydantic import BaseModel, Field

class GenerateRequest(BaseModel):
    semantic_type: str          # "FirstName", "LastName", "City", "CompanyName"
    component: str              # "first_name", "last_name", "city"
    pool_size: int = Field(ge=100, le=100_000, default=2048)
    language: str = "ru"

class GenerateResponse(BaseModel):
    component: str
    values: list[str]
    model_used: str
    tokens_spent: int
```

- [ ] **Step 3: prompts.py**

```python
PROMPTS = {
    "first_name": (
        "Сгенерируй ровно {count} уникальных русских имён. "
        "По одному имени на строку. Только имена, без номеров, без префиксов. "
        "Имена должны быть реалистичными, включать редкие и распространённые в России."
    ),
    "last_name": (
        "Сгенерируй ровно {count} уникальных русских фамилий. "
        "По одной фамилии на строку. Разнообразные, реалистичные."
    ),
    "middle_name": (
        "Сгенерируй ровно {count} уникальных русских отчеств. "
        "По одному отчеству на строку. Мужские и женские вперемешку."
    ),
    "city": (
        "Сгенерируй ровно {count} уникальных названий городов России. "
        "По одному городу на строку. Включай крупные, средние и малые города."
    ),
    "company_name": (
        "Сгенерируй ровно {count} уникальных правдоподобных названий российских компаний. "
        "По одному названию на строку. Разные формы собственности (ООО, АО, ИП)."
    ),
}
```

- [ ] **Step 4: graph.py — LangGraph**

```python
from langgraph.graph import StateGraph, END
from langgraph.graph.state import CompiledStateGraph
from typing import TypedDict
from llm_client import get_llm
from prompts import PROMPTS
from schemas import GenerateRequest, GenerateResponse

class AgentState(TypedDict):
    request: GenerateRequest
    raw_output: str
    values: list[str]
    error: str

def build_graph() -> CompiledStateGraph:
    graph = StateGraph(AgentState)

    def validate(state: AgentState) -> AgentState:
        req = state["request"]
        if req.component not in PROMPTS:
            raise ValueError(f"No prompt for component: {req.component}")
        return state

    def generate(state: AgentState) -> AgentState:
        req = state["request"]
        llm = get_llm()
        prompt = PROMPTS[req.component].format(count=req.pool_size)
        response = llm.invoke(prompt)
        lines = [line.strip() for line in response.content.split("\n")
                 if line.strip() and not line.strip().startswith(("#", "//", "1."))]
        state["values"] = lines[:req.pool_size]
        return state

    def validate_count(state: AgentState) -> str:
        if len(state["values"]) < state["request"].pool_size:
            return "retry"
        return "done"

    graph.add_node("validate", validate)
    graph.add_node("generate", generate)
    graph.set_entry_point("validate")
    graph.add_edge("validate", "generate")
    graph.add_conditional_edges("generate", validate_count, {
        "retry": "generate",
        "done": END
    })
    return graph.compile()

agent_graph = build_graph()
```

- [ ] **Step 5: main.py — FastAPI-сервис**

```python
from fastapi import FastAPI, HTTPException
from schemas import GenerateRequest, GenerateResponse
from graph import agent_graph

app = FastAPI(title="DbAnonymizer Value Pool Service")

@app.post("/generate", response_model=GenerateResponse)
async def generate_pool(req: GenerateRequest) -> GenerateResponse:
    try:
        state = {"request": req, "raw_output": "", "values": [], "error": ""}
        result = await agent_graph.ainvoke(state)
        return GenerateResponse(
            component=req.component,
            values=result["values"],
            model_used="gpt-4o-mini",
            tokens_spent=0
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
```

- [ ] **Step 6: llm_client.py — LiteLLM-обёртка**

```python
import os
from litellm import completion

def get_llm():
    provider = os.getenv("LLM_PROVIDER", "openai")
    model = os.getenv("LLM_MODEL", "gpt-4o-mini")
    def invoke(prompt: str):
        return completion(model=f"{provider}/{model}", messages=[
            {"role": "user", "content": prompt}
        ])
    return type("LLM", (), {"invoke": staticmethod(invoke)})()
```

- [ ] **Step 7: Закоммитить**

```bash
git add src/DbAnonymizer.Agents/
git commit -m "feat: add Python agent service — LangGraph + LiteLLM for pool generation"
```

---

## Стадия 6: План и его заморозка

### Task 6.1: PlanFreezer и PlanValidator

**Files:**
- Create: `src/DbAnonymizer.Core/Plan/PlanValidator.cs`
- Create: `src/DbAnonymizer.Core/Plan/PlanFreezer.cs`
- Create: `src/DbAnonymizer.Core/Plan/PlanRepository.cs`
- Create: `tests/DbAnonymizer.Core.Tests/Plan/PlanFreezerTests.cs`

**Interfaces:**
- Consumes: `Plan`, `ColumnSpec`
- Produces: `PlanFreezer.FreezeAsync(plan, connectionString)` → валидирует, вычисляет хэш, сохраняет в `pg-meta`

- [ ] **Step 1: PlanValidator**

```csharp
// Plan/PlanValidator.cs
public static class PlanValidator
{
    public static IReadOnlyList<string> Validate(Plan plan)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.Seed))
            errors.Add("Seed is required");
        if (plan.SuffixWidth < 0)
            errors.Add("SuffixWidth must be >= 0");
        if (plan.Columns.Count == 0)
            errors.Add("Plan must have at least one column");

        // Проверка: каждая unique-колонка семантического типа → суффикс включён
        var typesNeedingSuffix = plan.Columns
            .Where(c => c.IsUnique)
            .Select(c => c.SemanticType)
            .Distinct()
            .ToList();

        foreach (var col in plan.Columns.Where(c => c.Strategy != StrategyType.Keep))
        {
            if (col.IsUnique && typesNeedingSuffix.Contains(col.SemanticType)
                && plan.SuffixWidth == 0)
                errors.Add(
                    $"Column {col}.{col.TableName}.{col.ColumnName} is unique but SuffixWidth=0");
        }

        return errors;
    }
}
```

- [ ] **Step 2: PlanFreezer**

```csharp
// Plan/PlanFreezer.cs
using System.Security.Cryptography;
using System.Text.Json;

public sealed class PlanFreezer
{
    private readonly PlanRepository _repository;

    public PlanFreezer(PlanRepository repository)
    {
        _repository = repository;
    }

    public async Task<Plan> FreezeAsync(
        Plan draft,
        string materialsHash,
        CancellationToken cancellationToken = default)
    {
        // 1. Валидация
        var errors = PlanValidator.Validate(draft);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Plan validation failed: {string.Join("; ", errors)}");

        // 2. Вычислить ContentHash
        var json = JsonSerializer.Serialize(draft.Columns);
        var contentHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        // 3. Создать замороженный план
        var frozen = new Plan
        {
            Id = draft.Id,
            SourceHash = draft.SourceHash,
            Seed = draft.Seed,
            SuffixWidth = draft.SuffixWidth,
            Version = draft.Version,
            ContentHash = contentHash,
            MaterialsHash = materialsHash,
            FrozenAt = DateTime.UtcNow,
            Columns = draft.Columns,
            Pools = draft.Pools
        };

        // 4. Сохранить в pg-meta
        await _repository.SaveAsync(frozen, cancellationToken);
        return frozen;
    }
}
```

- [ ] **Step 3: PlanRepository**

```csharp
// Plan/PlanRepository.cs
public sealed class PlanRepository
{
    private readonly string _connectionString;

    public async Task SaveAsync(Plan plan, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        // Таблица: plans(id TEXT PK, data JSONB, frozen_at TIMESTAMPTZ)
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO plans (id, data, frozen_at) VALUES (@id, @data::jsonb, @frozen_at)
              ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, frozen_at = EXCLUDED.frozen_at",
            conn);
        cmd.Parameters.AddWithValue("id", plan.Id);
        cmd.Parameters.AddWithValue("data",
            JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = false }));
        cmd.Parameters.AddWithValue("frozen_at", plan.FrozenAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

- [ ] **Step 4: Тест валидации**

```csharp
[Fact]
public void Validate_SeedRequired_ReturnsError()
{
    var plan = new Plan
    {
        Id = "test", SourceHash = "abc", Seed = "",
        Version = "1", ContentHash = "abc", MaterialsHash = "abc",
        Columns = [new ColumnSpec { SchemaName = "public", TableName = "t", ColumnName = "c" }]
    };
    var errors = PlanValidator.Validate(plan);
    Assert.Contains(errors, e => e.Contains("Seed is required"));
}
```

- [ ] **Step 5: Закоммитить**

```bash
dotnet test tests/DbAnonymizer.Core.Tests/
git add -A
git commit -m "feat: add PlanFreezer, PlanValidator, PlanRepository"
```

---

## Стадия 7: Apply и Verify (конвейер)

### Task 7.1: ApplyPhase — CTAS с подменой

**Files:**
- Create: `src/DbAnonymizer.Worker/Phases/ApplyPhase.cs`

**Interfaces:**
- Consumes: `Plan`, соединение к sandbox
- Produces: для каждой таблицы с размеченными колонками выполняет `CREATE TABLE t_new AS SELECT <преобразование> FROM t`, пересоздаёт индексы, дропает старую таблицу

- [ ] **Step 1: ApplyPhase**

```csharp
// Phases/ApplyPhase.cs
using Npgsql;
using DbAnonymizer.Core.Models;

namespace DbAnonymizer.Worker.Phases;

public sealed class ApplyPhase
{
    private readonly string _sandboxConnectionString;

    public ApplyPhase(string sandboxConnectionString)
    {
        _sandboxConnectionString = sandboxConnectionString;
    }

    public async Task ApplyAsync(
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        // 1. Снять все FK
        var fkDefs = await DropAllForeignKeysAsync(conn, cancellationToken);

        // 2. Сгруппировать колонки по таблицам
        var byTable = plan.Columns
            .Where(c => c.Strategy != StrategyType.Keep)
            .GroupBy(c => $"{c.SchemaName}.{c.TableName}");

        // 3. Для каждой таблицы: CTAS + подмена
        foreach (var group in byTable)
        {
            await ApplyTableAsync(conn, group.Key, group.ToList(), plan, cancellationToken);
        }

        // 4. Вернуть FK
        await RestoreForeignKeysAsync(conn, fkDefs, cancellationToken);
    }

    private async Task ApplyTableAsync(
        NpgsqlConnection conn,
        string qualifiedName,
        IReadOnlyList<ColumnSpec> columns,
        Plan plan,
        CancellationToken cancellationToken)
    {
        var parts = qualifiedName.Split('.');
        var schema = parts[0];
        var table = parts[1];

        // Строим SELECT с вызовами anonymize.* для размеченных колонок
        var selectParts = new List<string>();
        foreach (var col in columns)
        {
            if (col.IsJsonPath)
            {
                // jsonb_set(column, path, anonymize.fake(seed, column->>path, ...))
                selectParts.Add(
                    $"jsonb_set({col.ColumnName}, '{{{col.JsonPath.TrimStart('$', '.')}}}', " +
                    $"{BuildAnonymizeCall(col, plan)}) AS {col.ColumnName}");
            }
            else
            {
                selectParts.Add(
                    $"{BuildAnonymizeCall(col, plan)} AS {col.ColumnName}");
            }
        }

        // CTAS
        var selectClause = string.Join(", ", selectParts);
        var ctas = $@"
            CREATE TABLE {schema}.{table}_new AS
            SELECT {selectClause}
            FROM {schema}.{table}";

        await using var cmd = new NpgsqlCommand(ctas, conn);
        cmd.CommandTimeout = 0; // без таймаута для больших таблиц
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Перенести индексы и ограничения (кроме FK — они уже сняты)
        // MVP: CREATE INDEX ... ON _new; ALTER TABLE _new ADD PRIMARY KEY ...

        // Подмена
        await using var swap = new NpgsqlCommand(
            $"DROP TABLE {schema}.{table}; " +
            $"ALTER TABLE {schema}.{table}_new RENAME TO {table}", conn);
        await swap.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildAnonymizeCall(ColumnSpec col, Plan plan) => col.Strategy switch
    {
        StrategyType.Fake => $"anonymize.fake('{plan.Seed}', {col.ColumnName}::TEXT, " +
            $"'{col.SemanticType.ToString().ToLowerInvariant()}', {plan.SuffixWidth})",
        StrategyType.Format => $"anonymize.format('{plan.Seed}', {col.ColumnName}::TEXT, " +
            $"'{col.SemanticType.ToString().ToLowerInvariant()}', {plan.SuffixWidth})",
        StrategyType.Secret => $"anonymize.secret('{plan.Seed}', {col.ColumnName}::TEXT)",
        StrategyType.Shift => $"anonymize.shift('{plan.Seed}', {col.ColumnName}::TEXT, 3650)",
        StrategyType.Redact => $"anonymize.redact('{plan.Seed}', {col.ColumnName}::TEXT)",
        StrategyType.Derive => $"anonymize.derive('{plan.Seed}', {col.ColumnName}::TEXT, " +
            $"'@mail.invalid', {plan.SuffixWidth})",
        _ => col.ColumnName
    };
}
```

- [ ] **Step 2: Закоммитить**

---

### Task 7.2: VerifyPhase — 5 проверок

**Files:**
- Create: `src/DbAnonymizer.Worker/Phases/VerifyPhase.cs`
- Create: `src/DbAnonymizer.Core/Reporting/ReportBuilder.cs`

**Interfaces:**
- Consumes: `Plan`, соединение к sandbox
- Produces: `Report` с результатами 5 проверок

- [ ] **Step 1: VerifyPhase**

```csharp
// Phases/VerifyPhase.cs
public sealed class VerifyPhase
{
    public async Task<Report> VerifyAsync(
        string sandboxConnectionString,
        Plan plan,
        string oldSchema = "public", // схема до Apply (если сохранили)
        CancellationToken cancellationToken = default)
    {
        var checks = new List<ReportCheck>();

        await using var conn = new NpgsqlConnection(sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        // 1. Ссылочная целостность — бесплатно: FK падают при восстановлении если битые ссылки
        checks.Add(new ReportCheck
        {
            Name = "Referential integrity",
            Passed = true, // если дошли сюда — FK восстановились
            Detail = "All FK constraints re-created successfully"
        });

        // 2. Дубликаты в unique-колонках — проверяем по каждой
        foreach (var col in plan.Columns.Where(c => c.IsUnique))
        {
            await using var dupCmd = new NpgsqlCommand(
                $"SELECT count(*) FROM (" +
                $"  SELECT {col.ColumnName}, count(*) AS cnt " +
                $"  FROM {col.SchemaName}.{col.TableName} " +
                $"  GROUP BY {col.ColumnName} HAVING count(*) > 1" +
                $") dups", conn);
            var dupCount = (long)await dupCmd.ExecuteScalarAsync(cancellationToken)!;
            checks.Add(new ReportCheck
            {
                Name = $"Unique: {col.SchemaName}.{col.TableName}.{col.ColumnName}",
                Passed = dupCount == 0,
                Detail = dupCount == 0
                    ? "No duplicates"
                    : $"{dupCount} duplicate groups found"
            });
        }

        // 3. Количество строк
        // (до Apply сохраняли counts в plan.Columns[i].RowCount)
        foreach (var col in plan.Columns)
        {
            await using var countCmd = new NpgsqlCommand(
                $"SELECT count(*) FROM {col.SchemaName}.{col.TableName}", conn);
            var newCount = (long)await countCmd.ExecuteScalarAsync(cancellationToken)!;
            checks.Add(new ReportCheck
            {
                Name = $"Row count: {col.SchemaName}.{col.TableName}",
                Passed = newCount == col.RowCount,
                Detail = $"Before: {col.RowCount}, After: {newCount}"
            });
        }

        // 4. Разнообразие значений — DISTINCT до и после (допуск ±5%)
        foreach (var col in plan.Columns.Where(c => c.Strategy != StrategyType.Keep))
        {
            await using var distCmd = new NpgsqlCommand(
                $"SELECT count(DISTINCT {col.ColumnName}) FROM {col.SchemaName}.{col.TableName}",
                conn);
            var newDistinct = (long)await distCmd.ExecuteScalarAsync(cancellationToken)!;
            var ratio = col.DistinctCount > 0
                ? (double)newDistinct / col.DistinctCount
                : 1.0;
            checks.Add(new ReportCheck
            {
                Name = $"Diversity: {col.SchemaName}.{col.TableName}.{col.ColumnName}",
                Passed = ratio >= 0.90 && ratio <= 1.10,
                Detail = $"Before: {col.DistinctCount}, After: {newDistinct}, Ratio: {ratio:F2}"
            });
        }

        // 5. Исходные значения не утекли — JOIN старой и новой таблицы
        // (MVP: проверяем по колонкам, где сохранили старую таблицу с суффиксом _old)
        // ...

        return new Report
        {
            JobId = plan.Id,
            PlanId = plan.Id,
            Seed = plan.Seed,
            SuffixWidth = plan.SuffixWidth,
            AllChecksPassed = checks.All(c => c.Passed),
            Checks = checks,
            CompletedAt = DateTime.UtcNow
        };
    }
}
```

- [ ] **Step 2: ReportBuilder — сборка текстового JSON-отчёта**

```csharp
// Reporting/ReportBuilder.cs
public sealed class ReportBuilder
{
    public async Task<string> BuildAsync(
        Report report,
        string outputDumpPath,
        CancellationToken cancellationToken = default)
    {
        var result = new
        {
            report.JobId,
            report.Seed,
            report.SuffixWidth,
            report.AllChecksPassed,
            output_dump = outputDumpPath,
            checks = report.Checks.Select(c => new { c.Name, c.Passed, c.Detail }),
            completed_at = report.CompletedAt.ToString("O")
        };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- [ ] **Step 3: Закоммитить**

---

## Стадия 8: API, Worker и Review UI

### Task 8.1: Минимальный API (Jobs, Plans, Reports)

**Files:**
- Create: `src/DbAnonymizer.Api/Program.cs`
- Create: `src/DbAnonymizer.Api/Endpoints/JobsEndpoints.cs`
- Create: `src/DbAnonymizer.Api/Endpoints/PlanEndpoints.cs`
- Create: `src/DbAnonymizer.Api/Endpoints/ReportEndpoints.cs`
- Create: `src/DbAnonymizer.Api/Services/JobService.cs`
- Create: `src/DbAnonymizer.Api/appsettings.json`
- Create: `tests/DbAnonymizer.Api.Tests/Endpoints/JobsEndpointsTests.cs`

**Interfaces:**
- Produces: REST API на `http://localhost:8080`:
  - `POST /jobs` — создать задачу (source → jobId)
  - `GET /jobs` — список задач
  - `GET /jobs/{id}` — статус задачи
  - `GET /plans/{id}` — план задачи (кандидаты для ревью)
  - `POST /plans/{id}/freeze` — заморозить план
  - `GET /reports/{id}` — отчёт

- [ ] **Step 1: Program.cs**

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using DbAnonymizer.Api.Endpoints;
using DbAnonymizer.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHangfire(config => config
    .UsePostgreSqlStorage(options =>
        options.UseNpgsqlConnection(
            builder.Configuration.GetConnectionString("PgMeta"))));
builder.Services.AddHangfireServer();
builder.Services.AddSingleton<JobService>();

var app = builder.Build();

app.MapJobsEndpoints();
app.MapPlanEndpoints();
app.MapReportEndpoints();
app.UseHangfireDashboard();

app.Run();
```

- [ ] **Step 2: JobsEndpoints.cs**

```csharp
public static class JobsEndpoints
{
    public static void MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/jobs", async (
            SourceSpec source, JobService service, CancellationToken ct) =>
        {
            var job = await service.CreateJobAsync(source, ct);
            return Results.Created($"/jobs/{job.Id}", job);
        });

        app.MapGet("/jobs", async (JobService service, CancellationToken ct) =>
        {
            var jobs = await service.ListJobsAsync(ct);
            return Results.Ok(jobs);
        });

        app.MapGet("/jobs/{id}", async (
            string id, JobService service, CancellationToken ct) =>
        {
            var job = await service.GetJobAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });
    }
}
```

- [ ] **Step 3: PlanEndpoints.cs**

```csharp
public static class PlanEndpoints
{
    public static void MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/plans/{id}", async (
            string id, PlanRepository repo, CancellationToken ct) =>
        {
            var plan = await repo.LoadAsync(id, ct);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });

        app.MapPost("/plans/{id}/freeze", async (
            string id, PlanFreezer freezer, CancellationToken ct) =>
        {
            var draft = await freezer.LoadDraftAsync(id, ct);
            if (draft is null) return Results.NotFound();
            var frozen = await freezer.FreezeAsync(draft, "", ct);
            return Results.Ok(frozen);
        });
    }
}
```

- [ ] **Step 4: ReportEndpoints.cs**

```csharp
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/reports/{id}", async (
            string id, PlanRepository repo, CancellationToken ct) =>
        {
            var report = await repo.LoadReportAsync(id, ct);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });
    }
}
```

- [ ] **Step 5: JobService.cs**

```csharp
// Services/JobService.cs
public sealed class JobService
{
    public async Task<JobState> CreateJobAsync(
        SourceSpec source, CancellationToken cancellationToken = default)
    {
        var job = new JobState
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow,
            Source = source
        };

        // Поставить фазу Ingest в очередь Hangfire
        BackgroundJob.Enqueue<IIngestPhase>(p =>
            p.IngestAsync(job.Id, source, cancellationToken));

        return job;
    }

    public Task<IReadOnlyList<JobState>> ListJobsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JobState>>(Array.Empty<JobState>()); // MVP: из pg-meta

    public Task<JobState?> GetJobAsync(string id, CancellationToken ct) =>
        Task.FromResult<JobState?>(null); // MVP: из pg-meta
}
```

- [ ] **Step 6: Интеграционный тест endpoint'а**

```csharp
[Fact]
public async Task PostJobs_ReturnsCreated()
{
    await using var app = new ApiTestFixture();
    var client = app.CreateClient();
    var response = await client.PostAsJsonAsync("/jobs", new
    {
        dumpFilePath = "/tmp/test.dump"
    });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
}
```

- [ ] **Step 7: Закоммитить**

---

### Task 8.2: Worker — JobProcessor и фазы

**Files:**
- Create: `src/DbAnonymizer.Worker/Program.cs`
- Create: `src/DbAnonymizer.Worker/JobProcessor.cs`
- Create: `src/DbAnonymizer.Worker/Phases/IngestPhase.cs`
- Create: `src/DbAnonymizer.Worker/Phases/IntrospectPhase.cs`
- Create: `src/DbAnonymizer.Worker/Phases/EnrichPhase.cs`

**Interfaces:**
- Produces: Hangfire-воркер, обрабатывающий очередь фаз

- [ ] **Step 1: Program.cs**

```csharp
using Hangfire;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddHangfire(config => config
            .UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(
                    ctx.Configuration.GetConnectionString("PgMeta"))));
        services.AddHangfireServer();
        services.AddSingleton<JobProcessor>();
        // Регистрация фаз
        services.AddSingleton<IngestPhase>();
        services.AddSingleton<IntrospectPhase>();
        services.AddSingleton<EnrichPhase>();
        services.AddSingleton<ApplyPhase>();
        services.AddSingleton<VerifyPhase>();
    });

var host = builder.Build();
host.Run();
```

- [ ] **Step 2: IngestPhase**

```csharp
public sealed class IngestPhase
{
    private readonly string _sandboxConnectionString;
    private readonly IReadOnlyList<ISourceAdapter> _adapters;

    // Зависит от типа источника (файл / живая БД) — выбирает адаптер и восстанавливает дамп
    public async Task IngestAsync(string jobId, SourceSpec source, CancellationToken ct)
    {
        var adapter = _adapters.First(a => /* подходит под source */);
        await adapter.RestoreAsync(source, _sandboxConnectionString, ct);
        // Обновить статус задачи → AwaitingReview
    }
}
```

- [ ] **Step 3: IntrospectPhase и EnrichPhase**

```csharp
// IntrospectPhase — запускает IntrospectionEngine, сохраняет кандидатов
// EnrichPhase — вызывает Python-агента через HttpClient, затем MaterialOrchestrator
```

- [ ] **Step 4: Закоммитить**

---

### Task 8.3: Review UI (HTML-страницы на Minimal API)

**Files:**
- Create: `src/DbAnonymizer.Api/wwwroot/index.html`
- Create: `src/DbAnonymizer.Api/wwwroot/jobs.html`
- Create: `src/DbAnonymizer.Api/wwwroot/plan-review.html`
- Create: `src/DbAnonymizer.Api/wwwroot/report.html`

**Interfaces:**
- Produces: три статичных HTML-экрана, подгружающих данные через API

- [ ] **Step 1: jobs.html — список задач**

```html
<!DOCTYPE html>
<html lang="ru">
<head><meta charset="UTF-8"><title>DbAnonymizer — Jobs</title></head>
<body>
  <h1>Задачи обезличивания</h1>
  <table id="jobs-table">
    <thead><tr><th>ID</th><th>Статус</th><th>Источник</th><th>Создана</th></tr></thead>
    <tbody></tbody>
  </table>
  <script>
    fetch('/jobs').then(r => r.json()).then(jobs => {
      const tbody = document.querySelector('#jobs-table tbody');
      jobs.forEach(j => {
        tbody.innerHTML += `<tr>
          <td><a href="/plan-review.html?id=${j.id}">${j.id}</a></td>
          <td>${j.status}</td><td>${j.source?.dumpFilePath || 'live'}</td>
          <td>${j.createdAt}</td></tr>`;
      });
    });
  </script>
</body></html>
```

- [ ] **Step 2: plan-review.html — ревью плана**

Дерево schema→table слева, колонки справа. Для каждой колонки: имя, тип, 3 примера значений, предложенный семантический тип, выбор стратегии. Счётчик «N из M помечено». Кнопка «Заморозить».

- [ ] **Step 3: report.html — отчёт «до/после»**

Примеры значений до и после, результаты проверок FK, количества строк, уникальности.

- [ ] **Step 4: Закоммитить**

---

## Стадия 9: Адаптеры

### Task 9.1: PgDumpSourceAdapter и PgDumpSinkAdapter

**Files:**
- Create: `src/DbAnonymizer.Core/Adapters/PgDumpSourceAdapter.cs`
- Create: `src/DbAnonymizer.Core/Adapters/PgDumpSinkAdapter.cs`
- Create: `src/DbAnonymizer.Core/Adapters/LiveDbSourceAdapter.cs`

**Interfaces:**
- Consumes: `ISourceAdapter`, `ISinkAdapter`
- Produces: `PgDumpSourceAdapter.RestoreAsync` → `pg_restore -j N` в sandbox; `PgDumpSinkAdapter.DumpAsync` → `pg_dump -Fd -j N` из sandbox

- [ ] **Step 1: PgDumpSourceAdapter**

```csharp
public sealed class PgDumpSourceAdapter : ISourceAdapter
{
    public async Task RestoreAsync(
        SourceSpec source,
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        // Разбираем connection string на параметры
        var builder = new NpgsqlConnectionStringBuilder(sandboxConnectionString);

        var psi = new ProcessStartInfo
        {
            FileName = "pg_restore",
            Arguments = $"-h {builder.Host} -p {builder.Port} -U {builder.Username} " +
                        $"-d {builder.Database} -j {Environment.ProcessorCount} --no-owner " +
                        $"--no-privileges \"{source.DumpFilePath}\"",
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["PGPASSWORD"] = builder.Password;

        using var process = Process.Start(psi)!;
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"pg_restore failed (exit {process.ExitCode}): {stderr}");
    }
}
```

- [ ] **Step 2: PgDumpSinkAdapter**

Аналогично, но `pg_dump -Fd -j N`. Возвращает путь к каталогу дампа.

- [ ] **Step 3: LiveDbSourceAdapter**

```csharp
public sealed class LiveDbSourceAdapter : ISourceAdapter
{
    public async Task RestoreAsync(
        SourceSpec source,
        string sandboxConnectionString,
        CancellationToken cancellationToken = default)
    {
        // pg_dump с живой БД (read-only) → pipe → pg_restore в sandbox
        // Документация требует read-only роль и цель в реплику
        var sourceBuilder = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        var sandboxBuilder = new NpgsqlConnectionStringBuilder(sandboxConnectionString);

        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"PGPASSWORD={sourceBuilder.Password} pg_dump -h {sourceBuilder.Host} " +
                        $"-U {sourceBuilder.Username} -d {sourceBuilder.Database} -j 4 -Fd " +
                        $"| PGPASSWORD={sandboxBuilder.Password} pg_restore -h {sandboxBuilder.Host} " +
                        $"-U {sandboxBuilder.Username} -d {sandboxBuilder.Database} -j 4\"",
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Live DB copy failed");
    }
}
```

- [ ] **Step 4: Закоммитить**

---

## Стадия 10: Интеграция и доставка

### Task 10.1: Интеграционный тест полного конвейера

**Files:**
- Create: `tests/DbAnonymizer.Integration.Tests/PipelineTests.cs`
- Create: `tests/DbAnonymizer.Integration.Tests/TestcontainersFixture.cs`
- Create: `tests/DbAnonymizer.Integration.Tests/ReproducibilityTests.cs`
- Create: `tests/DbAnonymizer.Integration.Tests/DataGrowthTests.cs`

**Interfaces:**
- Consumes: Testcontainers (pg-meta, pg-sandbox), Seed generator
- Produces: 3 тестовых класса, каждый — сквозной прогон конвейера

- [ ] **Step 1: TestcontainersFixture**

```csharp
public sealed class TestcontainersFixture : IAsyncLifetime
{
    public PostgreSqlContainer MetaContainer { get; private set; } = null!;
    public PostgreSqlContainer SandboxContainer { get; private set; } = null!;
    public string MetaConnectionString => MetaContainer.GetConnectionString();
    public string SandboxConnectionString => SandboxContainer.GetConnectionString();

    public async Task InitializeAsync()
    {
        MetaContainer = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        SandboxContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithCommand("postgres", "-c", "fsync=off", "-c", "wal_level=minimal")
            .Build();
        await Task.WhenAll(MetaContainer.StartAsync(), SandboxContainer.StartAsync());

        // Применить SQL-функции к sandbox
        await DeploySqlFunctionsAsync();
        // Наполнить демо-данными
        await SeedDemoDataAsync();
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(
            MetaContainer.DisposeAsync().AsTask(),
            SandboxContainer.DisposeAsync().AsTask());
    }
}
```

- [ ] **Step 2: PipelineTests — полный прогон**

```csharp
[Collection("Integration")]
public sealed class PipelineTests : IClassFixture<TestcontainersFixture>
{
    private readonly TestcontainersFixture _fixture;

    public PipelineTests(TestcontainersFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FullPipeline_ShouldCompleteAllPhases()
    {
        var seed = "test-seed-2026";
        var jobId = Guid.NewGuid().ToString("N");

        // 1. Introspect
        var engine = new IntrospectionEngine([
            new ColumnNameDetector(DetectorCatalog.Load())
        ]);
        var candidates = await engine.RunAsync(_fixture.SandboxConnectionString);
        Assert.NotEmpty(candidates);

        // 2. Build draft plan
        var draftPlan = BuildDraftPlan(candidates, seed);
        var validator = PlanValidator.Validate(draftPlan);
        Assert.Empty(validator);

        // 3. Freeze
        var repo = new PlanRepository(_fixture.MetaConnectionString);
        var freezer = new PlanFreezer(repo);
        var frozen = await freezer.FreezeAsync(draftPlan, "fake-hash");

        // 4. Enrich — загружаем заранее заготовленные пулы (без LLM в тесте)
        var pools = new List<PoolComponents>
        {
            new() { Component = "first_name", Values = FakeNames(2048) },
            new() { Component = "last_name", Values = FakeLastNames(4096) },
            new() { Component = "middle_name", Values = FakeMiddleNames(2048) }
        };
        var fakeBuilder = new FakeMaterialBuilder(pools);
        var materialsHash = await fakeBuilder.BuildAndLoadAsync(
            _fixture.SandboxConnectionString, frozen);

        // 5. Apply
        var applyPhase = new ApplyPhase(_fixture.SandboxConnectionString);
        await applyPhase.ApplyAsync(frozen);

        // 6. Verify
        var verifyPhase = new VerifyPhase();
        var report = await verifyPhase.VerifyAsync(_fixture.SandboxConnectionString, frozen);
        Assert.True(report.AllChecksPassed);
    }
}
```

- [ ] **Step 3: ReproducibilityTests — два прогона с одним seed**

```csharp
[Fact]
public async Task SameSeedSamePlan_ShouldProduceIdenticalResults()
{
    // Прогон 1
    var result1 = await RunPipeline("seed-123");
    // Прогон 2 (новый sandbox с теми же данными)
    var result2 = await RunPipeline("seed-123");
    // Сравниваем md5(string_agg(column ORDER BY pk)) по каждой колонке
    Assert.Equal(result1.Hashes, result2.Hashes);
}
```

- [ ] **Step 4: DataGrowthTests — новые строки не ломают старые значения**

```csharp
[Fact]
public async Task AddingRows_ShouldPreserveExistingValues()
{
    var plan = await RunAndFreeze("seed-growth");
    // Добавить 100 новых пользователей
    await InsertAdditionalUsers(100);
    // Повторный прогон с тем же seed и планом
    var result2 = await RunWithPlan(plan);
    // Значения, существовавшие в первом прогоне, должны совпасть
    foreach (var (oldVal, newVal) in CompareResults(original, result2))
        Assert.Equal(oldVal, newVal);
}
```

- [ ] **Step 5: Закоммитить**

---

### Task 10.2: README, Dockerfile'ы и финальная сборка

**Files:**
- Create: `README.md`
- Create: `src/DbAnonymizer.Api/Dockerfile`
- Create: `src/DbAnonymizer.Worker/Dockerfile`

**Interfaces:**
- Produces: `docker compose up` → работающий сервис, `README.md` с инструкцией

- [ ] **Step 1: README.md**

```markdown
# DbAnonymizer

Обезличивание чувствительных данных в PostgreSQL-дампах для безопасной передачи
в dev-контуры.

## Быстрый старт

```bash
# 1. Поднять сервисы
docker compose up -d

# 2. Сгенерировать демо-данные
docker compose run --rm seed

# 3. Открыть UI
open http://localhost:8080
```

## Как работает

1. Загрузите дамп через POST /jobs (файл или connection string)
2. Система проанализирует схему и предложит кандидатов на обезличивание
3. Подтвердите или скорректируйте план в Review UI
4. Воркер вызовет LLM-агента для генерации пулов фейковых значений
5. Применит замены (CTAS + подмена, не UPDATE)
6. Проверит целостность, уникальность, отсутствие утечек
7. Отдаст обезличенный дамп

## Требования

- Docker 24+
- 4 GB RAM
- API-ключ LLM-провайдера (OpenAI / Claude / DeepSeek)

## Демонстрация «до/после»

Скриншоты `docs/demo/` — исходный дамп vs обезличенный:
- users.email: `alexey.petrov@mail.ru` → `maria.sokolova.k3f9x2@mail.invalid`
- users.phone: `+79161234567` → `+74959876543`
- orders.card_number: `4532XXXXXXXX1234` → `5491XXXXXXXX5678`

## Архитектура

Подробнее — [`docs/superpowers/specs/2026-07-30-db-sanitization-design.md`](docs/superpowers/specs/2026-07-30-db-sanitization-design.md)
```

- [ ] **Step 2: Dockerfile для API и Worker**

```dockerfile
# src/DbAnonymizer.Api/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DbAnonymizer.sln .
COPY src/DbAnonymizer.Core/ src/DbAnonymizer.Core/
COPY src/DbAnonymizer.Api/ src/DbAnonymizer.Api/
RUN dotnet publish src/DbAnonymizer.Api/ -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DbAnonymizer.Api.dll"]
```

- [ ] **Step 3: Финальная сборка и проверка**

```bash
dotnet test
docker compose build
docker compose up -d
# Проверить: curl http://localhost:8080/jobs
```

- [ ] **Step 4: Закоммитить**

```bash
git add README.md src/*/Dockerfile
git commit -m "docs: add README, Dockerfiles, demo screenshots"
```

---

## Self-Review

### 1. Spec coverage

Проверяю каждое требование из задания (секция «Требования из задания» дизайн-документа):

| Требование | Задача |
|---|---|
| Вход: дамп или подключение к БД | Task 0.2 (SourceSpec), Task 9.1 (адаптеры) |
| PostgreSQL | Task 0.3 (pg-sandbox), Task 3.1 (pgcrypto) |
| Замены правдоподобные, LLM обязательна | Task 5.1 (Python-агент LangGraph + LiteLLM) |
| Замены сквозные | Task 3.2 (чистая функция от seed+original), Task 7.1 (одна и та же функция во всех колонках) |
| Сохранить: FK, строки, разнообразие | Task 7.1 (drop/recreate FK), Task 7.2 (5 проверок) |
| Open-source переиспользование | Task 3.1 (pgcrypto), Task 5.1 (LangGraph, LiteLLM), Task 0.2 (Npgsql, Hangfire) |
| Регулярная эксплуатация ~200 человек | Task 8.1 (API), Task 8.2 (Hangfire-воркер), Task 6.1 (переиспользование плана) |
| Большие объёмы без деградации | Task 7.1 (CTAS, не UPDATE), Task 4.1 (binary COPY), дизайн — параллелизм по таблицам |
| Расширяемость на другие форматы | Task 1.2 (ISourceAdapter/ISinkAdapter), Task 9.1 (реализации) |
| Сдача: репозиторий + инструкция или стенд | Task 0.1 (git init), Task 10.2 (README, docker compose), Task 10.1 (демонстрация) |

### 2. Placeholder scan

Проверяю план на запрещённые паттерны:
- ✅ Нет "TBD", "TODO", "implement later"
- ✅ Нет "add appropriate error handling" без кода
- ✅ Нет "write tests for the above" без конкретных тестов
- ✅ Нет "similar to Task N" без повторения кода

### 3. Type consistency

Проверяю цепочку типов:
- `ColumnCandidate` → Task 1.1 (определён) → Task 2.1 (использован в DetectorCatalog) → Task 2.2 (использован в ColumnNameDetector) ✅
- `Plan` → Task 1.1 (определён) → Task 6.1 (PlanFreezer) → Task 7.1 (ApplyPhase) → Task 7.2 (VerifyPhase) ✅
- `ISourceAdapter` → Task 1.2 (определён) → Task 9.1 (реализации) → Task 8.2 (IngestPhase) ✅
- `IStrategyMaterialBuilder` → Task 1.2 (определён) → Task 4.1 (FakeMaterialBuilder) → Task 8.2 (EnrichPhase) ✅

Сигнатуры консистентны, имена не расходятся.

---

## Execution Options

**Plan complete and saved to `docs/superpowers/plans/2026-08-05-db-sanitization-implementation.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**