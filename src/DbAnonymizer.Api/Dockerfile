FROM node:22-alpine AS frontend
WORKDIR /src/ClientApp
COPY src/DbAnonymizer.Api/ClientApp/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/DbAnonymizer.Api/ClientApp/ ./
RUN npm run build

# Multi-stage: SDK собирает и публикует, aspnet-образ — рантайм API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# story.txt — материал стратегии Redact (RedactMaterialBuilder), на который
# DbAnonymizer.Core.csproj ссылается безусловно (нужен для publish, даже
# если сам Api его не использует)
COPY story.txt story.txt
COPY src/DbAnonymizer.Core/ src/DbAnonymizer.Core/
COPY src/DbAnonymizer.Api/ src/DbAnonymizer.Api/
COPY --from=frontend /src/wwwroot/ src/DbAnonymizer.Api/wwwroot/
RUN dotnet publish src/DbAnonymizer.Api/ -c Release -o /app -p:BuildFrontend=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DbAnonymizer.Api.dll"]
