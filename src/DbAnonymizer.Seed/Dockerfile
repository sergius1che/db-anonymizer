# Генератор демо-данных: консольный проект без ProjectReference,
# запускается через `docker compose run --rm seed "<connection string>"`
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/DbAnonymizer.Seed/ src/DbAnonymizer.Seed/
RUN dotnet publish src/DbAnonymizer.Seed/ -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
# libgssapi-krb5-2: Npgsql ищет Kerberos-библиотеку при каждом соединении;
# без неё в stderr падает безобидное "Cannot load library libgssapi_krb5.so.2"
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "DbAnonymizer.Seed.dll"]
