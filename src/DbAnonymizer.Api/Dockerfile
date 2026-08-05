# Multi-stage: SDK собирает и публикует, aspnet-образ — рантайм API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# story.txt нужен Worker'у (RedactMaterialBuilder); копируем из общего контекста
COPY story.txt story.txt
COPY src/DbAnonymizer.Core/ src/DbAnonymizer.Core/
COPY src/DbAnonymizer.Api/ src/DbAnonymizer.Api/
RUN dotnet publish src/DbAnonymizer.Api/ -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DbAnonymizer.Api.dll"]
