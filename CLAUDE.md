# DbAnonymizer

Anonymization of sensitive data in databases so that database dumps can be safely
handed over to development teams.

## Stack

| Item | Value |
|------|-------|
| Database | PostgreSQL |
| Main language for new modules | C# / .NET |

## Conventions

- All `CLAUDE.md` documents are written in English.
- Any change to the project structure must be reflected in this `CLAUDE.md`.

## Project Structure

| Path | What it is |
|------|------------|
| `src/DbAnonymizer.Core/` | Core library: domain models, detectors, strategies, plan freeze |
| `src/DbAnonymizer.Api/` | ASP.NET Core Minimal API: jobs, plan review, reports |
| `src/DbAnonymizer.Worker/` | Hangfire worker: ingest, introspect, enrich, apply, verify phases |
| `src/DbAnonymizer.Agents/` | Python service: LangGraph + LiteLLM value pool generation |
| `src/DbAnonymizer.Seed/` | Demo data generator (Bogus) |
| `sql/` | SQL functions deployed into the sandbox |
| `tests/` | Unit (Core), integration (Testcontainers), API tests |
| `docs/` | Design spec and implementation plan |
| `docker-compose.yml` | Services: pg-meta, pg-sandbox, api, worker, agents |
