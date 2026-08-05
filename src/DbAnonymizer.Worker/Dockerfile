# Multi-stage: SDK собирает и публикует, aspnet-образ — рантайм воркера.
# postgresql-client обязателен: адаптеры (Task 9.1) спавнят pg_dump/pg_restore
# (sink — pg_dump -Fd из песочницы, source — pg_restore дампа в песочницу).
# Базовый образ — Ubuntu 24.04 (noble): пакет postgresql-client = PostgreSQL 16,
# совпадает с версией сервера песочницы (postgres:16-alpine).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# story.txt — материал стратегии Redact (RedactMaterialBuilder)
COPY story.txt story.txt
COPY src/DbAnonymizer.Core/ src/DbAnonymizer.Core/
COPY src/DbAnonymizer.Worker/ src/DbAnonymizer.Worker/
RUN dotnet publish src/DbAnonymizer.Worker/ -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "DbAnonymizer.Worker.dll"]
