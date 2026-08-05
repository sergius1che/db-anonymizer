# DbAnonymizer

Обезличивание чувствительных данных в PostgreSQL: на входе — дамп
(`pg_dump` custom/directory) или подключение к живой БД, на выходе — та же схема,
где персональные и чувствительные значения заменены правдоподобными синтетическими.
Дампы становятся безопасны для передачи командам разработки.

## Как это работает

Пайплайн обезличивания — 7 шагов:

1. **POST /jobs** — задача с источником: файл-дамп (`pg_dump -Fc`/`-Fd`) или
   connection string живой БД.
2. **Ingest** — воркер восстанавливает данные в изолированную песочницу
   (`pg_restore -j`, параллельно, custom/directory-форматы).
3. **Introspect** — детекторы (имя колонки по каталогу правил) находят
   чувствительные колонки и формируют черновик плана.
4. **Review** — план просматривается в UI: стратегии подтверждаются или
   корректируются, затем план «замораживается» (фризится).
5. **Enrich** — воркер вызывает Python-агента (LangGraph + LiteLLM): тот генерирует
   пулы фейковых значений (имена, города, компании…).
6. **Apply** — подмена выполняется через `CREATE TABLE … AS SELECT` (CTAS)
   с функциями `anonymize.*`, а не через UPDATE: на больших объёмах нет деградации.
7. **Verify + Sink** — пять проверок целостности (число строк, разнообразие,
   уникальность, ссылочная целостность, отсутствие утечек исходных значений),
   затем `pg_dump -Fd` выходного дампа и отчёт.

Подстановка — чистая функция от `(seed, оригинал)`: прогоны с одним планом
воспроизводимы, сквозные связи (например, `orders.user_email` ↔ `users.email`)
сохраняются.

## Требования

- Docker 24+ и Docker Compose v2 (команда `docker compose`).
- ~4 GB свободной памяти.
- .NET SDK на хосте не нужен — всё работает в контейнерах.
- API-ключ LLM-провайдера — **опционально**: по умолчанию встроенный
  fake-провайдер (офлайн, без ключа), см. «Реальный LLM-провайдер».
- `pg_dump`/`pg_restore` (PostgreSQL 16) установлены в образ воркера; для демо
  входной дамп создаётся внутри контейнера `pg-sandbox`, клиент на хосте не нужен.

## Быстрый старт: демо «до/после»

Состав стека: `pg-meta` (метаданные), `pg-sandbox` (песочница), `api` (UI :8080),
`worker` (фоновые фазы), `agents` (генератор значений, :8000), `seed` (демо-данные,
по запросу). Запуск:

```bash
# 1. Сборка и запуск (первый раз — долго: собираются 3 образа)
docker compose up -d --build

# 2. Демо-данные во временную БД — песочница остаётся чистой для ingest
docker compose exec pg-sandbox createdb -U anonymizer db_demo_src
docker compose --profile demo run --rm seed "Host=pg-sandbox;Port=5432;Database=db_demo_src;Username=anonymizer;Password=anonymizer"

# 3. «До» — исходные данные (вывод seed случайный, структура та же)
docker compose exec pg-sandbox psql -U anonymizer -d db_demo_src -c \
  "SELECT id, first_name, email, phone FROM users ORDER BY id LIMIT 3;"
docker compose exec pg-sandbox psql -U anonymizer -d db_demo_src -c \
  "SELECT card_number FROM orders LIMIT 3;"

# 4. Входной дамп (custom-формат — обязателен, см. «Ограничения»)
docker compose exec pg-sandbox pg_dump -U anonymizer -d db_demo_src -Fd -j 4 -f demo
docker compose cp pg-sandbox:/demo ./dumps/demo
docker compose exec pg-sandbox dropdb -U anonymizer db_demo_src

# 5. Создать задачу (идентификатор из ответа запомните)
curl -X POST http://localhost:8080/jobs \
  -H "Content-Type: application/json" \
  -d '{"dumpFilePath": "dumps/demo"}'
# → {"id":"<jobId>","status":0,"source":{...},"createdAt":"…"}

# 6. Ревью плана и заморозка: http://localhost:8080
#    Список задач (jobs.html) → задача → план с детекцией и стратегиями → Freeze.
#    CLI-аналог: PUT /plans/<jobId> со списком колонок (поля ColumnSpec),
#    затем POST /plans/<jobId>/freeze.

# 7. Дождаться завершения (status 7 = Complete, 8 = Failed с полем error)
curl http://localhost:8080/jobs/<jobId>

# 8. Отчёт: результаты проверок и путь к выходному дампу
curl http://localhost:8080/reports/<jobId>
# → "outputDumpPath": "dumps/dbanon-<дата>-<id>" — обезличенный дамп в ./dumps/

# 9. «После» — данные в песочнице уже заменены
docker compose exec pg-sandbox psql -U anonymizer -d db_anonymizer_sandbox -c \
  "SELECT id, first_name, email, phone FROM users ORDER BY id LIMIT 3;"
docker compose exec pg-sandbox psql -U anonymizer -d db_anonymizer_sandbox -c \
  "SELECT card_number FROM orders LIMIT 3;"
```

### Пример «до/после»

Демо-схема (`src/DbAnonymizer.Seed/Schema/demo-schema.sql`): `users` (ФИО, email,
телефон, хэш пароля), `profiles` (адрес, паспорт, ИНН, jsonb-метаданные),
`orders` (карта, денормализованный email).

| Таблица | Колонка | «До» (пример) | Стратегия |
|---|---|---|---|
| users | email | `alexey.petrov@mail.ru` | Derive — из фейкового имени, домен `mail.invalid` |
| users | phone | `+79161234567` | Format — FPE-перестановка, формат сохраняется |
| users | password_hash | `$2a$10$…` | Secret — хэш dev-пароля |
| users | birth_date | `1985-03-12` | Shift — сдвиг в пределах ±5 лет |
| profiles | passport | `1234 567890` | Redact — подстановка из рассказа (`story.txt`) |
| profiles | inn | `770123456789` | Format |
| profiles | metadata (jsonb) | `{"source": "…"}` | Redact по json-путям |
| orders | user_email | `alexey.petrov@mail.ru` | Derive — согласовано с `users.email` |
| orders | card_number | `4532 1234 1234 1234` | Format |

Фактический результат одного из прогонов:

```
ДО:   alexey.petrov@mail.ru | +79161234567 | 4532 1234 1234 1234
ПОСЛЕ: maria.sokolova.k3f9x2@mail.invalid | +79953471256 | 5491 9876 5432 1098
```

Скриншоты интерфейса будут добавлены позже; до этого момента секция заменена
текстовыми примерами — фактические значения зависят от seed и пулов.

> Повторный прогон демо: `docker compose down -v && docker compose up -d`
> (сбрасывает обе БД — песочница должна быть пустой перед ingest;
> каталог `dumps/` на хосте при этом сохраняется).

## Реальный LLM-провайдер

По умолчанию `agents` работает в offline-режиме (`LLM_PROVIDER=fake`): пулы —
детерминированные тестовые значения, ключ не нужен. Для правдоподобных значений
включите реального провайдера:

```bash
# файл .env рядом с docker-compose.yml
LLM_PROVIDER=openai
LLM_MODEL=gpt-4o-mini
OPENAI_API_KEY=sk-...

# затем
docker compose up -d agents
```

LiteLLM поддерживает и другие провайдеры (anthropic, deepseek и т.д.) — достаточно
поменять `LLM_PROVIDER` и, при необходимости, `LLM_MODEL`.

## Ограничения (важно)

1. **DEFAULT/SERIAL не переносятся CTAS**: автоинкремент-последовательности
   умирают вместе с исходными таблицами — для вставок с автоинкрементом
   пересоздайте sequence вручную или вставляйте с явным id.
2. **Живой источник — read-only**: адаптер LiveDbSourceAdapter делает `pg_dump` —
   роль источника должна иметь право чтения, лучше выделить целевую реплику.
3. **Входной дамп — только custom/directory** (не plain SQL): параллельный
   `pg_restore -j` работает лишь с форматами `-Fc`/`-Fd` (именно их даёт
   `pg_dump -Fd` в демо выше).
4. **pg_dump/pg_restore в PATH**: воркер спавнит их — в образе воркера они
   установлены; для локального запуска воркера вне Docker поставьте
   PostgreSQL 16 client.
5. **Схема результата**: помеченные колонки становятся `TEXT` (без обратного
   каста к исходному типу).
6. **Один прогон на план**: повторный Apply не value-стабилен (детерминированный
   сдвиг от нового оригинала) — план применяется один раз.
7. **Hangfire Dashboard** (`http://localhost:8080/hangfire`) — dev-инструмент без
   полноценной аутентификации (дефолтный фильтр Hangfire пропускает только
   локальные запросы — из браузера хоста через проброс портов вернёт 401):
   только для локальной разработки, наружу не выставлять.
8. **Песочница — не для прод-данных**: `fsync=off`, `wal_level=minimal`
   (скорость в ущерб durability).
9. **Одна задача за раз**: воркер запущен с `WorkerCount=1` — конкурентные задачи
   на одной песочнице испортят друг другу `materials.*` и `_new`/`_orig`-таблицы.
10. **API без аутентификации и валидации**: `POST /jobs` принимает connection string
    к любому хосту (SSRF-вектор: воркер выполнит `pg_dump` с произвольного адреса).
    Только для локальной разработки, наружу не выставлять.
11. **Реальный LLM — 5 компонентов**: промпты агента покрывают только FirstName,
    LastName, MiddleName, City, CompanyName. Для других Fake-типов агент вернёт
    ошибку; используйте fake-провайдер (по умолчанию) или дополните `prompts.py`.

## Конфигурация

| Переменная | Сервис | По умолчанию | Назначение |
|---|---|---|---|
| `ConnectionStrings__PgMeta` | api, worker | `Host=pg-meta;…` | метаданные |
| `ConnectionStrings__Sandbox` | worker | `Host=pg-sandbox;…` | песочница |
| `Agents__BaseUrl` | api, worker | `http://agents:8000` | адрес агентов |
| `Agents__PoolSize` | worker | `2048` | размер пула значений |
| `Sink__OutputDirectory` | worker | `./dumps` | каталог выходных дампов |
| `LLM_PROVIDER` / `LLM_MODEL` / `OPENAI_API_KEY` | agents | `fake` / `gpt-4o-mini` / — | генератор значений |

Локальный запуск без Docker: `dotnet run --project src/DbAnonymizer.Api`
(pg-meta на :5432) и `dotnet run --project src/DbAnonymizer.Worker`
(pg-sandbox на :5433) — строки подключения в `appsettings.json` каждого проекта.
Каталог `dumps/` в .gitignore — дампы в репозиторий не попадают.

## Тесты

```bash
dotnet build DbAnonymizer.slnx -c Release            # 0 ошибок, 0 предупреждений
dotnet test tests/DbAnonymizer.Core.Tests            # 15/15, нужен Docker (Testcontainers)
dotnet test tests/DbAnonymizer.Integration.Tests     # 11/11, нужен Docker (Testcontainers)
dotnet test tests/DbAnonymizer.Api.Tests             # 3/3, нужен Docker (Testcontainers)
```

## Архитектура

| Компонент | Что делает |
|---|---|
| `src/DbAnonymizer.Core/` | модели домена, детекторы, построители материалов, заморозка плана, адаптеры pg_dump/pg_restore |
| `src/DbAnonymizer.Api/` | ASP.NET Core Minimal API + статичный UI ревью плана |
| `src/DbAnonymizer.Worker/` | Hangfire-воркер: фазы Ingest / Introspect / Enrich / Apply / Verify |
| `src/DbAnonymizer.Agents/` | Python-сервис: FastAPI + LangGraph + LiteLLM, `POST /generate` |
| `src/DbAnonymizer.Seed/` | генератор демо-данных (Bogus) |
| `sql/` | SQL-функции стратегий: fake, derive, format, secret, shift, redact, keep |
| `tests/` | unit (Core), интеграционные (Testcontainers), API-тесты |

Дизайн-спецификация: [`docs/superpowers/specs/2026-07-30-db-sanitization-design.md`](docs/superpowers/specs/2026-07-30-db-sanitization-design.md).<br>
План реализации: [`docs/superpowers/plans/2026-08-05-db-sanitization-implementation.md`](docs/superpowers/plans/2026-08-05-db-sanitization-implementation.md).

## Проверено

- `dotnet build DbAnonymizer.slnx -c Release` — 0 ошибок, 0 предупреждений.
- `dotnet test` — Core 15/15, Api 3/3, Integration 11/11 (Docker).
- `docker compose config` — валиден, без предупреждений.
- `docker compose build` — образы api, worker, agents, seed собираются.
- Демо-прогон «до/после» по инструкции выше — задача доходит до Complete,
  отчёт и выходной дамп формируются.
